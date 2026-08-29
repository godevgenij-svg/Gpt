using System.Diagnostics;
using System.Text;
using System.Text.Json;
using GreyVPN.Models;
using GreyVPN.Services;

if (args.Length < 2)
    throw new ArgumentException("Usage: GreyVPN.Smoke <xray.exe> <sing-box.exe>");

var xray = Path.GetFullPath(args[0]);
var singBox = Path.GetFullPath(args[1]);
if (!File.Exists(xray)) throw new FileNotFoundException("xray.exe", xray);
if (!File.Exists(singBox)) throw new FileNotFoundException("sing-box.exe", singBox);

var temp = Path.Combine(Path.GetTempPath(), "GreyVPN-smoke-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temp);
try
{
    TestEndpointParser();
    await TestSubscriptionImport(temp);
    await TestGenericImportSafety(temp);
    TestOpenVpnSanitizer(temp);
    await TestProfileStoreConcurrency();
    await TestSynchronousStoreSave();
    await TestXrayBuilders(xray, temp);
    await TestSingBoxBuilders(singBox, temp);
    Console.WriteLine("ALL GREYVPN SMOKE TESTS PASSED");
}
finally
{
    try { Directory.Delete(temp, true); } catch { }
}

static void TestEndpointParser()
{
    Must(ProfileImporter.TrySplitEndpoint("2001:db8::1", out var host1, out var port1) && host1 == "2001:db8::1" && port1 == 0,
        "bare IPv6 endpoint parsing");
    Must(ProfileImporter.TrySplitEndpoint("[2001:db8::1]:443", out var host2, out var port2) && host2 == "2001:db8::1" && port2 == 443,
        "bracketed IPv6 endpoint parsing");
    Must(!ProfileImporter.TrySplitEndpoint("example.com:70000", out _, out _), "port range validation");
    Console.WriteLine("OK endpoint parser");
}

static async Task TestSubscriptionImport(string temp)
{
    var links = string.Join('\n', new[]
    {
        "vless://00000000-0000-0000-0000-000000000001@example.com?encryption=none&security=tls&type=ws#one",
        "trojan://secret@example.net?type=tcp#two"
    });
    var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(links));
    var path = Path.Combine(temp, "subscription.txt");
    await File.WriteAllTextAsync(path, b64);
    var profiles = await ProfileImporter.ImportFilesAsync(new[] { path });
    Must(profiles.Count == 2, "Base64 subscription expands into two profiles");
    Must(profiles.Any(p => p.Type == "VLESS" && p.Endpoint.EndsWith(":443")), "VLESS default 443");
    Must(profiles.Any(p => p.Type == "TROJAN" && p.Endpoint.EndsWith(":443")), "Trojan default 443");
    Console.WriteLine("OK subscription importer");
}

static async Task TestGenericImportSafety(string temp)
{
    var vpn = Path.Combine(temp, "sensitive.vpn");
    var conf = Path.Combine(temp, "not-wireguard.conf");
    var huge = Path.Combine(temp, "too-large.txt");
    await File.WriteAllTextAsync(vpn, "server=127.0.0.1\nrootPassword=DO_NOT_COPY_THIS_SECRET\n");
    await File.WriteAllTextAsync(conf, "ordinary_setting=true\n");
    await using (var fs = new FileStream(huge, FileMode.Create, FileAccess.Write, FileShare.None))
        fs.SetLength(16L * 1024 * 1024 + 1);

    var profiles = await ProfileImporter.ImportFilesAsync(new[] { vpn, conf, huge });
    var vpnProfile = profiles.Single(p => p.SourcePath == vpn);
    var confProfile = profiles.Single(p => p.SourcePath == conf);
    var hugeProfile = profiles.Single(p => p.SourcePath == huge);

    Must(vpnProfile.Type == "Amnezia backup/config" && string.IsNullOrEmpty(vpnProfile.RawValue),
        "generic .vpn secrets are not copied into persistent RawValue");
    Must(confProfile.Type == "CONF", "unrelated .conf is not mislabeled as WireGuard");
    Must(hugeProfile.Type == "Ошибка импорта" && hugeProfile.Error.Contains("слишком большой", StringComparison.OrdinalIgnoreCase),
        "oversized config is rejected before full read");
    Console.WriteLine("OK generic import safety");
}

static void TestOpenVpnSanitizer(string temp)
{
    var safePath = Path.Combine(temp, "legacy.ovpn");
    File.WriteAllText(safePath, "client\ndev tun\nproto udp\nremote 127.0.0.1 1194\ncipher AES-128-CBC\n<ca>\nDUMMY\n</ca>\n");
    var safe = new VpnProfile { Type = "OpenVPN", SourcePath = safePath };
    Must(OpenVpnConfigSanitizer.TryBuildSafeConfig(safe, out var sanitized, out var safeError), "safe OpenVPN sanitize: " + safeError);
    Must(sanitized.Contains("compat-mode 2.4.0", StringComparison.OrdinalIgnoreCase), "legacy cipher compatibility insertion");
    Must(sanitized.Contains("route-nopull", StringComparison.OrdinalIgnoreCase), "route isolation insertion");

    var evilPath = Path.Combine(temp, "evil.ovpn");
    File.WriteAllText(evilPath, "client\ndev tun\nremote 127.0.0.1 1194\n--config evil-extra.ovpn\n");
    var evil = new VpnProfile { Type = "OpenVPN", SourcePath = evilPath };
    Must(!OpenVpnConfigSanitizer.TryBuildSafeConfig(evil, out _, out var evilError) && evilError.Contains("заблокирована", StringComparison.OrdinalIgnoreCase),
        "nested OpenVPN config injection blocked");

    var unknownBlockPath = Path.Combine(temp, "unknown-block.ovpn");
    File.WriteAllText(unknownBlockPath, "client\ndev tun\n<connection>\nremote 127.0.0.1 1194\n</connection>\n");
    var unknown = new VpnProfile { Type = "OpenVPN", SourcePath = unknownBlockPath };
    Must(!OpenVpnConfigSanitizer.TryBuildSafeConfig(unknown, out _, out _), "unsupported inline block blocked");
    Console.WriteLine("OK OpenVPN sanitizer");
}

static async Task TestProfileStoreConcurrency()
{
    var saves = Enumerable.Range(0, 12).Select(i => ProfileStore.SaveAsync(new[]
    {
        new VpnProfile { Name = "smoke-" + i, Type = "VLESS", Endpoint = "example.com:443" }
    }));
    await Task.WhenAll(saves);
    var loaded = await ProfileStore.LoadAsync();
    Must(loaded.Count == 1 && loaded[0].Name.StartsWith("smoke-", StringComparison.Ordinal), "atomic concurrent profile store");
    Console.WriteLine("OK profile store concurrency");
}

static async Task TestSynchronousStoreSave()
{
    var blockedContextSave = Task.Run(() =>
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new BlackHoleSynchronizationContext());
        try
        {
            ProfileStore.SaveAsync(new[]
            {
                new VpnProfile { Name = "sync-close", Type = "VLESS", Endpoint = "example.com:443" }
            }).GetAwaiter().GetResult();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    });

    await blockedContextSave.WaitAsync(TimeSpan.FromSeconds(5));
    Console.WriteLine("OK synchronous shutdown save");
}

static async Task TestXrayBuilders(string xray, string temp)
{
    const string id = "00000000-0000-0000-0000-000000000001";
    const string pbk = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    var samples = new List<VpnProfile>
    {
        P("vless-raw", "VLESS", $"vless://{id}@example.com:443?encryption=none&security=tls&type=tcp&sni=example.com"),
        P("vless-ws", "VLESS", $"vless://{id}@example.com:443?encryption=none&security=tls&type=ws&host=example.com&path=%2Fws&sni=example.com"),
        P("vless-grpc", "VLESS", $"vless://{id}@example.com:443?encryption=none&security=tls&type=grpc&serviceName=test&sni=example.com"),
        P("vless-httpupgrade", "VLESS", $"vless://{id}@example.com:443?encryption=none&security=tls&type=httpupgrade&host=example.com&path=%2Fup&sni=example.com"),
        P("vless-xhttp", "VLESS", $"vless://{id}@example.com:443?encryption=none&security=tls&type=xhttp&host=example.com&path=%2Fx&mode=stream-up&sni=example.com"),
        P("vless-reality", "VLESS", $"vless://{id}@example.com:443?encryption=none&security=reality&type=tcp&sni=example.com&fp=chrome&pbk={pbk}&sid=0123456789abcdef"),
        P("trojan-implicit-tls", "TROJAN", "trojan://secret@example.com:443?type=tcp")
    };

    var vmessPayload = JsonSerializer.Serialize(new
    {
        v = "2", ps = "smoke", add = "example.com", port = "443", id,
        aid = "0", scy = "auto", net = "ws", type = "none", host = "example.com", path = "/ws", tls = "tls", sni = "example.com"
    });
    samples.Add(P("vmess-ws", "VMESS", "vmess://" + Convert.ToBase64String(Encoding.UTF8.GetBytes(vmessPayload))));

    var i = 0;
    foreach (var profile in samples)
    {
        var json = XrayConfigBuilder.Build(profile, 19000 + i);
        if (profile.Name == "trojan-implicit-tls")
            Must(json.Contains("\"security\": \"tls\"", StringComparison.Ordinal), "Trojan implicit TLS in generated Xray config");
        var path = Path.Combine(temp, $"xray-{i++}.json");
        await File.WriteAllTextAsync(path, json, new UTF8Encoding(false));
        var run = await Run(xray, $"run -test -c \"{path}\"");
        Must(run.ExitCode == 0, $"Xray config {profile.Name}: {run.Text}");
    }
    Console.WriteLine($"OK Xray builders: {samples.Count}");
}

static async Task TestSingBoxBuilders(string singBox, string temp)
{
    var key1 = Convert.ToBase64String(Enumerable.Range(1, 32).Select(x => (byte)x).ToArray());
    var key2 = Convert.ToBase64String(Enumerable.Range(33, 32).Select(x => (byte)x).ToArray());
    var wgPath = Path.Combine(temp, "wg.conf");
    await File.WriteAllTextAsync(wgPath, $"[Interface]\nPrivateKey = {key1}\nAddress = 10.0.0.2/32\nMTU = 1280\n[Peer]\nPublicKey = {key2}\nAllowedIPs = 0.0.0.0/0\nEndpoint = example.com:51820\nPersistentKeepalive = 20\n");

    var ssCredentials = Convert.ToBase64String(Encoding.UTF8.GetBytes("aes-128-gcm:password")).TrimEnd('=');
    var samples = new List<VpnProfile>
    {
        P("hy2-default-port", "HYSTERIA2", "hysteria2://secret@example.com?sni=example.com&insecure=1"),
        new() { Name = "wireguard", Type = "WireGuard", SourcePath = wgPath },
        P("ss", "SS", $"ss://{ssCredentials}@example.com:8388"),
        P("socks", "SOCKS", "socks://user:pass@example.com:1080"),
        P("http", "HTTP", "http://user:pass@example.com:8080"),
        P("https", "HTTPS", "https://user:pass@example.com")
    };

    var i = 0;
    foreach (var profile in samples)
    {
        Must(SingBoxConfigBuilder.TryBuild(profile, 20000 + i, out var json, out var error), $"sing-box builder {profile.Name}: {error}");
        var path = Path.Combine(temp, $"singbox-{i++}.json");
        await File.WriteAllTextAsync(path, json, new UTF8Encoding(false));
        var run = await Run(singBox, $"check -c \"{path}\"");
        Must(run.ExitCode == 0, $"sing-box config {profile.Name}: {run.Text}");
    }
    Console.WriteLine($"OK sing-box builders: {samples.Count}");
}

static VpnProfile P(string name, string type, string raw) => new() { Name = name, Type = type, RawValue = raw };

static async Task<(int ExitCode, string Text)> Run(string file, string arguments)
{
    var psi = new ProcessStartInfo
    {
        FileName = file,
        Arguments = arguments,
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true
    };
    using var p = Process.Start(psi) ?? throw new InvalidOperationException("Cannot start " + file);
    var stdout = p.StandardOutput.ReadToEndAsync();
    var stderr = p.StandardError.ReadToEndAsync();
    await p.WaitForExitAsync();
    return (p.ExitCode, ((await stdout) + Environment.NewLine + (await stderr)).Trim());
}

static void Must(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException("SMOKE FAILED: " + message);
}

sealed class BlackHoleSynchronizationContext : SynchronizationContext
{
    public override void Post(SendOrPostCallback d, object? state)
    {
        // Deliberately never execute posted continuations. SaveAsync must use ConfigureAwait(false)
        // internally so MainWindow_Closing can synchronously wait without a WPF UI deadlock.
    }
}
