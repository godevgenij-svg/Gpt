using System.Runtime.CompilerServices;
using GreyVPN.Models;
using GreyVPN.Services;

internal static class V09Smoke
{
    [ModuleInitializer]
    internal static void Run()
    {
        TestConfigVault();
        TestAmneziaWgTestConfig();
        TestXrayRemovedAllowInsecure();
        Console.WriteLine("OK GreyVPN v0.9 regression smoke");
    }

    private static void TestConfigVault()
    {
        var temp = Path.Combine(Path.GetTempPath(), "GreyVPN-v09-vault-" + Guid.NewGuid().ToString("N") + ".conf");
        const string config = "[Interface]\nPrivateKey = AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=\nAddress = 10.0.0.2/32\n[Peer]\nPublicKey = BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB=\nAllowedIPs = 0.0.0.0/0\nEndpoint = 192.0.2.1:51820\n";
        File.WriteAllText(temp, config);
        var profile = new VpnProfile { Name = "vault", Type = "WireGuard", SourcePath = temp };
        try
        {
            Must(ConfigVault.EnsureStoredAsync(profile).GetAwaiter().GetResult(), "WG config is copied to internal vault");
            Must(!string.IsNullOrWhiteSpace(profile.StoredConfigFile), "vault file reference is persisted in model");
            File.Delete(temp);
            var restored = ConfigVault.ReadTextAsync(profile).GetAwaiter().GetResult();
            Must(restored.Contains("Endpoint = 192.0.2.1:51820", StringComparison.Ordinal), "vault remains usable after original file deletion");
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

    private static void TestAmneziaWgTestConfig()
    {
        const string source = "[Interface]\nPrivateKey = AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=\nAddress = 10.0.0.2/32\nDNS = 8.8.8.8\nJc = 5\nJmin = 40\nJmax = 70\nS1 = 12\nS2 = 12\nH1 = 123\n[Peer]\nPublicKey = BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB=\nAllowedIPs = 0.0.0.0/0, ::/0\nEndpoint = 192.0.2.1:51820\n";
        var test = AmneziaWgTestConfigBuilder.Build(source);
        Must(!test.Contains("DNS =", StringComparison.OrdinalIgnoreCase), "AWG test does not change system DNS");
        Must(test.Contains("AllowedIPs = " + AmneziaWgTestConfigBuilder.ProbeAllowedIps, StringComparison.Ordinal), "AWG test routes only probe IPs");
        Must(test.Contains("Jc = 5", StringComparison.Ordinal) && test.Contains("H1 = 123", StringComparison.Ordinal), "AWG obfuscation parameters are preserved");
        Must(!test.Contains("0.0.0.0/0", StringComparison.Ordinal), "default route removed from AWG test config");
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

    private static void Must(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("V0.9 SMOKE FAILED: " + message);
    }
}