using System.IO.Compression;
using System.Runtime.CompilerServices;
using GreyVPN.Models;
using GreyVPN.Services;

internal static class DiagnosticsSmoke
{
    [ModuleInitializer]
    internal static void Run()
    {
        var temp = Path.Combine(Path.GetTempPath(), "GreyVPN-diag-smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            const string uuid = "12345678-1234-1234-1234-123456789abc";
            const string password = "SUPER_SECRET_PASSWORD_987";
            const string privateKey = "SUPER_PRIVATE_KEY_654";
            const string token = "SUPER_TOKEN_321";

            DiagnosticsService.Initialize();
            var profile = new VpnProfile
            {
                Name = "diagnostic-profile",
                Type = "VLESS",
                Endpoint = "example.com:443",
                Transport = "ws",
                SourcePath = @"C:\Users\SensitiveUser\configs\private-name.ovpn",
                RawValue = $"vless://{uuid}@example.com:443?token={token}",
                Status = "ENDPOINT OK",
                TcpConnectMs = 41,
                PingMs = 39,
                RealStatus = "AUTH ERROR",
                RealError = $"authentication failed uuid={uuid} password={password} token={token} PrivateKey={privateKey}",
                LastRealTested = DateTimeOffset.Now
            };

            DiagnosticsService.Log("SMOKE", $"password={password} uuid={uuid} token={token} PrivateKey={privateKey}", profile);
            DiagnosticsService.WriteEngineLog(profile, "smoke-engine",
                $"password: {password}\nUUID={uuid}\nPrivateKey = {privateKey}\nhttps://user:{password}@example.com/\nvmess://eyJwcyI6IntentionallySensitivePayload" );

            var report = DiagnosticsService.CreateChatGptReportAsync(new[] { profile }, "smoke-test", temp)
                .GetAwaiter().GetResult();
            if (!File.Exists(report)) throw new InvalidOperationException("Diagnostics report ZIP was not created.");

            using var zip = ZipFile.OpenRead(report);
            var text = string.Join("\n", zip.Entries
                .Where(e => e.Length < 2_000_000 && IsText(e.FullName))
                .Select(ReadEntry));

            MustNotContain(text, uuid, "UUID leaked into diagnostic report");
            MustNotContain(text, password, "password leaked into diagnostic report");
            MustNotContain(text, privateKey, "private key leaked into diagnostic report");
            MustNotContain(text, token, "token leaked into diagnostic report");
            MustNotContain(text, @"C:\Users\SensitiveUser", "full source path leaked into diagnostic report");
            if (!text.Contains("example.com:443", StringComparison.Ordinal))
                throw new InvalidOperationException("Diagnostic report lost endpoint information needed for debugging.");
            if (!text.Contains("AUTH ERROR", StringComparison.Ordinal))
                throw new InvalidOperationException("Diagnostic report lost real-test status.");

            Console.WriteLine("OK diagnostic report redaction");
        }
        finally
        {
            try { Directory.Delete(temp, true); } catch { }
        }
    }

    private static bool IsText(string name)
    {
        var ext = Path.GetExtension(name);
        return ext.Equals(".txt", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".log", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".tsv", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".json", StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadEntry(ZipArchiveEntry entry)
    {
        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }

    private static void MustNotContain(string text, string secret, string message)
    {
        if (text.Contains(secret, StringComparison.Ordinal)) throw new InvalidOperationException(message);
    }
}
