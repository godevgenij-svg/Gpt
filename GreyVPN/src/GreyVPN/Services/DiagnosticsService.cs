using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GreyVPN.Models;

namespace GreyVPN.Services;

public static class DiagnosticsService
{
    private static readonly object Sync = new();
    private static readonly Regex PemPrivateKey = new(
        @"-----BEGIN(?: [A-Z0-9]+)? PRIVATE KEY-----.*?-----END(?: [A-Z0-9]+)? PRIVATE KEY-----",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex InlineSecretBlock = new(
        @"<(?<tag>key|auth-user-pass)>.*?</\k<tag>>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex SecretAssignment = new(
        @"(?im)^(?<prefix>\s*(?:PrivateKey|PresharedKey|password|passwd|pass|token|secret|uuid|user_id|client_secret|api_key)\s*[:=]\s*).+$",
        RegexOptions.Compiled);
    private static readonly Regex InlineSecretAssignment = new(
        @"(?i)(?<prefix>\b(?:PrivateKey|PresharedKey|password|passwd|pass|token|secret|uuid|user_id|client_secret|api_key)\s*[:=]\s*)(?<value>[^\s|,;]+)",
        RegexOptions.Compiled);
    private static readonly Regex GuidSecret = new(
        @"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b",
        RegexOptions.Compiled);
    private static readonly Regex UriUserInfo = new(
        @"(?i)\b(?<scheme>vless|trojan|ss|socks|http|https)://(?<userinfo>[^@\s/]+)@",
        RegexOptions.Compiled);
    private static readonly Regex VmessUri = new(
        @"(?i)\bvmess://[A-Za-z0-9_\-+/=]+",
        RegexOptions.Compiled);
    private static readonly Regex QuerySecret = new(
        @"(?i)(?<prefix>[?&](?:password|passwd|pass|token|secret|uuid|id|key|psk|privatekey|presharedkey)=)[^&#\s]+",
        RegexOptions.Compiled);
    private static readonly Regex WindowsUserRoot = new(
        @"(?i)(?<prefix>\b[A-Z]:\\Users\\)[^\\\r\n\t|\"']+",
        RegexOptions.Compiled);
    private static readonly Regex UnixUserRoot = new(
        @"(?i)(?<prefix>/(?:home|Users)/)[^/\s|\"']+",
        RegexOptions.Compiled);

    private static string? _sessionRoot;
    private static string? _appLogPath;
    private static long _engineLogSequence;

    public static string SessionRoot
    {
        get
        {
            EnsureInitialized();
            return _sessionRoot!;
        }
    }

    public static void Initialize()
    {
        lock (Sync)
        {
            if (_sessionRoot is not null) return;

            var sessionId = $"{DateTime.Now:yyyyMMdd_HHmmss}_{Environment.ProcessId}";
            var diagnosticsRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GreyVPN", "Diagnostics");
            _sessionRoot = Path.Combine(diagnosticsRoot, sessionId);
            Directory.CreateDirectory(_sessionRoot);
            Directory.CreateDirectory(Path.Combine(_sessionRoot, "Engines"));
            _appLogPath = Path.Combine(_sessionRoot, "GreyVPN.log");
        }

        Log("APP", $"GreyVPN diagnostics session started. Version={GetVersion()}, OS={RuntimeInformation.OSDescription}, Arch={RuntimeInformation.ProcessArchitecture}, Admin={IsAdministrator()}");
    }

    public static void Log(string area, string message, VpnProfile? profile = null)
    {
        try
        {
            EnsureInitialized();
            var profileText = profile is null
                ? string.Empty
                : $" | Profile={SafeText(profile.Name)} | Type={SafeText(profile.Type)} | Endpoint={SafeText(profile.Endpoint)}";
            var line = $"{DateTimeOffset.Now:O} | T{Environment.CurrentManagedThreadId} | {SafeText(area)} | {SafeText(message)}{profileText}{Environment.NewLine}";
            line = RedactForDiagnostics(line);
            lock (Sync) File.AppendAllText(_appLogPath!, line, new UTF8Encoding(false));
        }
        catch
        {
            // Diagnostics must never break VPN testing.
        }
    }

    public static void WriteEngineLog(VpnProfile profile, string engine, string content, string phase = "runtime")
    {
        try
        {
            EnsureInitialized();
            var dir = Path.Combine(_sessionRoot!, "Engines", SafeFilePart(engine));
            Directory.CreateDirectory(dir);
            var seq = Interlocked.Increment(ref _engineLogSequence);
            var name = $"{seq:D5}_{DateTime.Now:HHmmssfff}_{SafeFilePart(profile.Type)}_{SafeFilePart(profile.Name, 60)}_{SafeFilePart(phase, 24)}.log";
            var header = $"Time: {DateTimeOffset.Now:O}{Environment.NewLine}Engine: {engine}{Environment.NewLine}Profile: {profile.Name}{Environment.NewLine}Type: {profile.Type}{Environment.NewLine}Endpoint: {profile.Endpoint}{Environment.NewLine}Transport: {profile.Transport}{Environment.NewLine}Status: {profile.RealStatus}{Environment.NewLine}{Environment.NewLine}";
            File.WriteAllText(Path.Combine(dir, name), RedactForDiagnostics(header + content), new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            Log("DIAG", $"Failed to write engine log: {ex.GetType().Name}: {ex.Message}", profile);
        }
    }

    public static async Task<string> CreateChatGptReportAsync(
        IEnumerable<VpnProfile> profiles,
        string trigger,
        string? reportsRootOverride = null)
    {
        var snapshot = profiles.ToList();
        return await Task.Run(() => CreateChatGptReport(snapshot, trigger, reportsRootOverride)).ConfigureAwait(false);
    }

    public static string RedactForDiagnostics(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;

        var text = value;
        text = PemPrivateKey.Replace(text, "[PRIVATE KEY REDACTED]");
        text = InlineSecretBlock.Replace(text, m => $"<{m.Groups["tag"].Value}>[REDACTED]</{m.Groups["tag"].Value}>");
        text = SecretAssignment.Replace(text, "${prefix}[REDACTED]");
        text = InlineSecretAssignment.Replace(text, "${prefix}[REDACTED]");
        text = GuidSecret.Replace(text, "[UUID REDACTED]");
        text = UriUserInfo.Replace(text, "${scheme}://[CREDENTIAL REDACTED]@");
        text = VmessUri.Replace(text, "vmess://[REDACTED]");
        text = QuerySecret.Replace(text, "${prefix}[REDACTED]");
        text = WindowsUserRoot.Replace(text, "${prefix}[USER]");
        text = UnixUserRoot.Replace(text, "${prefix}[USER]");
        return text;
    }

    private static string CreateChatGptReport(IReadOnlyList<VpnProfile> profiles, string trigger, string? reportsRootOverride)
    {
        EnsureInitialized();
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var reportsRoot = reportsRootOverride ?? ResolveReportsRoot();
        Directory.CreateDirectory(reportsRoot);

        var stage = Path.Combine(Path.GetTempPath(), "GreyVPN", "reports", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            var rows = profiles.Select(ToReportRow).ToList();
            File.WriteAllText(Path.Combine(stage, "README_SEND_TO_CHATGPT.txt"),
                "Пришлите REPORT_FOR_CHATGPT_LATEST.zip в чат.\r\n" +
                "Архив не содержит исходные VPN-конфиги, RawValue, имена пользовательских каталогов, приватные ключи, UUID и пароли.\r\n" +
                "В нём находятся результаты тестов, системная информация без имени пользователя и подробные очищенные логи движков.\r\n",
                new UTF8Encoding(false));

            File.WriteAllText(Path.Combine(stage, "summary.txt"), BuildSummary(rows, trigger), new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(stage, "profiles.tsv"), BuildTsv(rows), new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(stage, "profiles.json"),
                RedactForDiagnostics(JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true })),
                new UTF8Encoding(false));

            if (File.Exists(_appLogPath))
                File.Copy(_appLogPath!, Path.Combine(stage, "GreyVPN.log"), overwrite: true);

            var engineSource = Path.Combine(_sessionRoot!, "Engines");
            if (Directory.Exists(engineSource))
                CopyDirectory(engineSource, Path.Combine(stage, "EngineLogs"));

            foreach (var file in Directory.EnumerateFiles(stage, "*", SearchOption.AllDirectories))
            {
                if (Path.GetExtension(file).Equals(".log", StringComparison.OrdinalIgnoreCase) ||
                    Path.GetExtension(file).Equals(".txt", StringComparison.OrdinalIgnoreCase) ||
                    Path.GetExtension(file).Equals(".tsv", StringComparison.OrdinalIgnoreCase) ||
                    Path.GetExtension(file).Equals(".json", StringComparison.OrdinalIgnoreCase))
                {
                    var text = File.ReadAllText(file);
                    File.WriteAllText(file, RedactForDiagnostics(text), new UTF8Encoding(false));
                }
            }

            var timestamped = Path.Combine(reportsRoot, $"REPORT_FOR_CHATGPT_GreyVPN_{stamp}.zip");
            var latest = Path.Combine(reportsRoot, "REPORT_FOR_CHATGPT_LATEST.zip");
            if (File.Exists(timestamped)) File.Delete(timestamped);
            ZipFile.CreateFromDirectory(stage, timestamped, CompressionLevel.Optimal, includeBaseDirectory: false);
            File.Copy(timestamped, latest, overwrite: true);
            Log("REPORT", $"ChatGPT report created: {Path.GetFileName(timestamped)}; profiles={profiles.Count}; trigger={trigger}");
            return latest;
        }
        finally
        {
            try { Directory.Delete(stage, recursive: true); } catch { }
        }
    }

    private static ReportRow ToReportRow(VpnProfile p) => new()
    {
        Name = SafeText(p.Name),
        Type = SafeText(p.Type),
        Endpoint = SafeText(p.Endpoint),
        Transport = SafeText(p.Transport),
        SourceFile = string.IsNullOrWhiteSpace(p.SourcePath) ? string.Empty : SafeText(Path.GetFileName(p.SourcePath)),
        PreStatus = SafeText(p.Status),
        TcpMs = p.TcpConnectMs,
        IcmpMs = p.PingMs,
        PreAttempts = p.TestAttempts,
        LastPreTest = p.LastTested,
        PreError = RedactForDiagnostics(SafeText(p.Error)),
        RealStatus = SafeText(p.RealStatus),
        ExitIp = SafeText(p.ExitIp),
        RealMs = p.RealTestMs,
        LastRealTest = p.LastRealTested,
        RealError = RedactForDiagnostics(SafeText(p.RealError))
    };

    private static string BuildSummary(IReadOnlyList<ReportRow> rows, string trigger)
    {
        var sb = new StringBuilder();
        sb.AppendLine("GreyVPN diagnostic report");
        sb.AppendLine($"Created: {DateTimeOffset.Now:O}");
        sb.AppendLine($"Trigger: {SafeText(trigger)}");
        sb.AppendLine($"GreyVPN version: {GetVersion()}");
        sb.AppendLine($"OS: {RuntimeInformation.OSDescription}");
        sb.AppendLine($"Architecture: {RuntimeInformation.ProcessArchitecture}");
        sb.AppendLine($".NET: {RuntimeInformation.FrameworkDescription}");
        sb.AppendLine($"Administrator: {IsAdministrator()}");
        sb.AppendLine($"Profiles: {rows.Count}");
        sb.AppendLine();
        sb.AppendLine("REAL STATUS:");
        foreach (var g in rows.GroupBy(x => EmptyAs(x.RealStatus, "<empty>"), StringComparer.OrdinalIgnoreCase).OrderByDescending(x => x.Count()).ThenBy(x => x.Key))
            sb.AppendLine($"  {g.Key}: {g.Count()}");
        sb.AppendLine();
        sb.AppendLine("PROFILE TYPES:");
        foreach (var g in rows.GroupBy(x => EmptyAs(x.Type, "Unknown"), StringComparer.OrdinalIgnoreCase).OrderByDescending(x => x.Count()).ThenBy(x => x.Key))
            sb.AppendLine($"  {g.Key}: {g.Count()}");
        sb.AppendLine();
        sb.AppendLine("PRETEST STATUS:");
        foreach (var g in rows.GroupBy(x => EmptyAs(x.PreStatus, "<empty>"), StringComparer.OrdinalIgnoreCase).OrderByDescending(x => x.Count()).ThenBy(x => x.Key))
            sb.AppendLine($"  {g.Key}: {g.Count()}");
        return RedactForDiagnostics(sb.ToString());
    }

    private static string BuildTsv(IEnumerable<ReportRow> rows)
    {
        var sb = new StringBuilder("Name\tType\tEndpoint\tTransport\tSourceFile\tPreStatus\tTCP_ms\tICMP_ms\tAttempts\tLastPreTest\tPreError\tRealStatus\tExitIP\tReal_ms\tLastRealTest\tRealError\r\n");
        foreach (var r in rows)
        {
            sb.Append(Tsv(r.Name)).Append('\t').Append(Tsv(r.Type)).Append('\t').Append(Tsv(r.Endpoint)).Append('\t').Append(Tsv(r.Transport)).Append('\t')
              .Append(Tsv(r.SourceFile)).Append('\t').Append(Tsv(r.PreStatus)).Append('\t').Append(r.TcpMs).Append('\t').Append(r.IcmpMs).Append('\t')
              .Append(r.PreAttempts).Append('\t').Append(r.LastPreTest?.ToString("O")).Append('\t').Append(Tsv(r.PreError)).Append('\t')
              .Append(Tsv(r.RealStatus)).Append('\t').Append(Tsv(r.ExitIp)).Append('\t').Append(r.RealMs).Append('\t')
              .Append(r.LastRealTest?.ToString("O")).Append('\t').Append(Tsv(r.RealError)).Append("\r\n");
        }
        return RedactForDiagnostics(sb.ToString());
    }

    private static string ResolveReportsRoot()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Reports"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "GreyVPN Reports"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GreyVPN", "Reports")
        };

        foreach (var candidate in candidates)
        {
            try
            {
                Directory.CreateDirectory(candidate);
                var probe = Path.Combine(candidate, $".write_{Guid.NewGuid():N}.tmp");
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
                return candidate;
            }
            catch { }
        }
        throw new IOException("Не удалось найти доступную для записи папку отчётов GreyVPN.");
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        foreach (var dir in Directory.EnumerateDirectories(source))
            CopyDirectory(dir, Path.Combine(destination, Path.GetFileName(dir)));
    }

    private static void EnsureInitialized()
    {
        if (_sessionRoot is null) Initialize();
    }

    private static string GetVersion() => Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";

    private static bool IsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    private static string SafeText(string? value) => RedactForDiagnostics(value ?? string.Empty);
    private static string EmptyAs(string value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value;
    private static string Tsv(string? value) => SafeText(value).Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');

    private static string SafeFilePart(string? value, int max = 40)
    {
        var text = value ?? "unknown";
        foreach (var c in Path.GetInvalidFileNameChars()) text = text.Replace(c, '_');
        text = GuidSecret.Replace(text, "UUID");
        if (text.Length > max) text = text[..max];
        return string.IsNullOrWhiteSpace(text) ? "unknown" : text;
    }

    private sealed class ReportRow
    {
        public string Name { get; init; } = string.Empty;
        public string Type { get; init; } = string.Empty;
        public string Endpoint { get; init; } = string.Empty;
        public string Transport { get; init; } = string.Empty;
        public string SourceFile { get; init; } = string.Empty;
        public string PreStatus { get; init; } = string.Empty;
        public long? TcpMs { get; init; }
        public long? IcmpMs { get; init; }
        public int PreAttempts { get; init; }
        public DateTimeOffset? LastPreTest { get; init; }
        public string PreError { get; init; } = string.Empty;
        public string RealStatus { get; init; } = string.Empty;
        public string ExitIp { get; init; } = string.Empty;
        public long? RealMs { get; init; }
        public DateTimeOffset? LastRealTest { get; init; }
        public string RealError { get; init; } = string.Empty;
    }
}
