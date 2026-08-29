using System.Runtime.CompilerServices;
using GreyVPN.Models;
using GreyVPN.Services;

internal static class V09Smoke
{
    [ModuleInitializer]
    internal static void Run()
    {
        TestConfigVault();
        TestOpenVpnVault();
        TestAmneziaWgTestConfig();
        TestXrayRemovedAllowInsecure();
        TestVmessOpaqueBase64();
        TestDoubleEncodedXhttpExtra();
        Console.WriteLine("OK GreyVPN v0.9.1.2 regression smoke");
    }

    private static void TestConfigVault()
    {
        var temp = Path.Combine(Path.GetTempPath(), "GreyVPN-v091-vault-" + Guid.NewGuid().ToString("N") + ".conf");
        const string config = "[Interface]\nPrivateKey = AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=\nAddress = 10.0.0.2/32\n[Peer]\nPublicKey = BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB=\nAllowedIPs = 0.0.0.0/0\nEndpoint = 192.0.2.1:51820\n";
        File.WriteAllText(temp, config);
        var profile = new VpnProfile { Name = "vault", Type = "WireGuard", SourcePath = temp };
        try
        {
            Must(ConfigVault.EnsureStoredAsync(profile).GetAwaiter().GetResult(), "WG config is copied to internal vault");
            Must(!string.IsNullOrWhiteSpace(profile.StoredConfigFile), "WG vault file reference is persisted in model");
            File.Delete(temp);
            var restored = ConfigVault.ReadTextAsync(profile).GetAwaiter().GetResult();
            Must(restored.Contains("Endpoint = 192.0.2.1:51820", StringComparison.Ordinal), "WG vault remains usable after original file deletion");
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            try
            {
                var stored = ConfigVault.ResolveStoredPath(profile);
                if (stored is not null && File.Exists(stored)) File.Delete(stored);
            }
            catch { }
        }
    }

    private static void TestOpenVpnVault()
    {
        var temp = Path.Combine(Path.GetTempPath(), "GreyVPN-v091-openvpn-" + Guid.NewGuid().ToString("N") + ".ovpn");
        const string config = "client\ndev tun\nproto udp\nremote 192.0.2.1 1194\n<ca>\nDUMMY\n</ca>\n";
        File.WriteAllText(temp, config);
        var profile = new VpnProfile { Name = "ovpn-vault", Type = "OpenVPN", SourcePath = temp };
        try
        {
            Must(OpenVpnConfigVault.EnsureStoredAsync(profile).GetAwaiter().GetResult(), "OpenVPN config is copied to internal vault");
            Must(!string.IsNullOrWhiteSpace(profile.StoredConfigFile), "OpenVPN vault file reference is persisted in model");
            File.Delete(temp);
            var restoredPath = OpenVpnConfigVault.ResolveUsablePathAsync(profile).GetAwaiter().GetResult();
            Must(restoredPath is not null && File.Exists(restoredPath), "OpenVPN vault remains usable after original file deletion");
            Must(File.ReadAllText(restoredPath).Contains("remote 192.0.2.1 1194", StringComparison.Ordinal), "OpenVPN vaulted text is intact");
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            try
            {
                var stored = OpenVpnConfigVault.ResolveStoredPath(profile);
                if (stored is not null && File.Exists(stored)) File.Delete(stored);
            }
            catch { }
        }
    }

    private static void TestAmneziaWgTestConfig()
    {
        const string source = "[Interface]\nPrivateKey = AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=\nAddress = 10.0.0.2/32\nDNS = 8.8.8.8\nJc = 5\nJmin = 40\nJmax = 70\nS1 = 12\nS2 = 12\nH1 = 123\n[Peer]\nPublicKey = BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB=\nAllowedIPs = 0.0.0.0/0, ::/0\nEndpoint = 192.0.2.1:51820\n";
        var test = AmneziaWgTestConfigBuilder.Build(source);
        Must(!test.Contains("DNS =", StringComparison.OrdinalIgnoreCase), "AWG test does not replace system DNS");
        Must(test.Contains("AllowedIPs = 0.0.0.0/0, ::/0", StringComparison.Ordinal), "AWG test preserves original routing semantics");
        Must(test.Contains("Jc = 5", StringComparison.Ordinal) && test.Contains("H1 = 123", StringComparison.Ordinal), "AWG obfuscation parameters are preserved");
    }

    private static void TestXrayRemovedAllowInsecure()
    {
        const string id = "00000000-0000-0000-0000-000000000001";
        var profile = new VpnProfile
        {
            Name = "xray-insecure",
            Type = "VLESS",
            RawValue = $"vless://{id}@example.com:443?encryption=none&security=tls&type=ws&sni=example.com&allowInsecure=1"
        };
        var built = XrayConfigBuilder.Build(profile, 19991);
        Must(built.Contains("allowInsecure", StringComparison.Ordinal), "legacy builder input reproduces removed field before compatibility normalization");
        var normalized = XrayConfigCompat.Normalize(profile, built);
        Must(!normalized.Json.Contains("allowInsecure", StringComparison.OrdinalIgnoreCase), "removed Xray allowInsecure is stripped");
        Must(normalized.Warning.Contains("удалил allowInsecure", StringComparison.OrdinalIgnoreCase), "Xray compatibility warning is recorded");
    }

    private static void TestVmessOpaqueBase64()
    {
        const string payload = "eyJ2IjoiMiIsInBzIjoidGVzdNC+IiwiYWRkIjoiZXhhbXBsZS5jb20iLCJwb3J0IjoiNDQzIiwiaWQiOiIwMDAwMDAwMC0wMDAwLTAwMDAtMDAwMC0wMDAwMDAwMDAwMDEiLCJhaWQiOiIwIiwibmV0Ijoid3MiLCJ0eXBlIjoibm9uZSIsImhvc3QiOiJleGFtcGxlLmNvbSIsInBhdGgiOiIvIiwidGxzIjoidGxzIiwic2N5IjoiYXV0byJ9";
        Must(payload.Contains('+'), "VMess regression payload actually contains plus");
        var temp = Path.Combine(Path.GetTempPath(), "GreyVPN-v0912-vmess-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            File.WriteAllText(temp, "vmess://" + payload);
            var imported = ProfileImporter.ImportFilesAsync(new[] { temp }).GetAwaiter().GetResult();
            Must(imported.Count == 1, "VMess plus payload imports as one profile");
            var p = imported[0];
            Must(p.Type == "VMESS", "VMess plus payload keeps VMESS type");
            Must(p.Endpoint == "example.com:443", "VMess plus payload decodes endpoint");
            Must(p.Transport == "ws", "VMess plus payload decodes transport");
            var built = XrayConfigBuilder.Build(p, 19992);
            Must(built.Contains("example.com", StringComparison.Ordinal), "Xray builder decodes the same VMess plus payload");
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }

    private static void TestDoubleEncodedXhttpExtra()
    {
        const string id = "00000000-0000-0000-0000-000000000001";
        const string extra = "{\"noGRPCHeader\":true}";
        var twice = Uri.EscapeDataString(Uri.EscapeDataString(extra));
        var profile = new VpnProfile
        {
            Name = "xhttp-double-extra",
            Type = "VLESS",
            RawValue = $"vless://{id}@example.com:443?encryption=none&security=tls&type=xhttp&sni=example.com&extra={twice}"
        };
        var built = XrayConfigBuilder.Build(profile, 19993);
        Must(built.Contains("noGRPCHeader", StringComparison.Ordinal), "double URL-encoded XHTTP extra is parsed");
    }

    private static void Must(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("V0.9.1.2 SMOKE FAILED: " + message);
    }
}
