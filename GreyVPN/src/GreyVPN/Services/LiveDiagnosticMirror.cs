using System.Runtime.CompilerServices;
using System.Text;

namespace GreyVPN.Services;

/// <summary>
/// Keeps an easy-to-find, sanitized, live copy of the current GreyVPN session log.
/// DiagnosticsService already redacts secrets before writing GreyVPN.log; this class
/// only mirrors those bytes to a user-visible Logs directory.
/// </summary>
internal static class LiveDiagnosticMirror
{
    private static readonly object Sync = new();
    private static Timer? _timer;
    private static string _source = string.Empty;
    private static string _sessionTarget = string.Empty;
    private static string _latestTarget = string.Empty;
    private static long _copiedBytes;
    private static int _copying;

    [ModuleInitializer]
    internal static void Start()
    {
        try
        {
            DiagnosticsService.Initialize();
            _source = Path.Combine(DiagnosticsService.SessionRoot, "GreyVPN.log");
            var root = ResolveVisibleLogRoot();
            Directory.CreateDirectory(root);

            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            _sessionTarget = Path.Combine(root, $"GreyVPN_DIAGNOSTIC_{stamp}.log");
            _latestTarget = Path.Combine(root, "GreyVPN_DIAGNOSTIC_LATEST.log");

            File.WriteAllText(_sessionTarget,
                $"GreyVPN live diagnostic log\r\nStarted: {DateTimeOffset.Now:O}\r\n" +
                "This log is sanitized by GreyVPN before mirroring.\r\n\r\n",
                new UTF8Encoding(false));
            File.Copy(_sessionTarget, _latestTarget, overwrite: true);

            _timer = new Timer(_ => CopyNewBytes(), null, TimeSpan.Zero, TimeSpan.FromMilliseconds(750));
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                try { CopyNewBytes(); } catch { }
                try { _timer?.Dispose(); } catch { }
            };

            DiagnosticsService.Log("DIAG", $"Live diagnostic mirror enabled: {Path.GetFileName(_latestTarget)}");
        }
        catch
        {
            // A logging helper must never stop GreyVPN from starting.
        }
    }

    private static void CopyNewBytes()
    {
        if (Interlocked.Exchange(ref _copying, 1) != 0) return;
        try
        {
            lock (Sync)
            {
                if (string.IsNullOrWhiteSpace(_source) || !File.Exists(_source)) return;
                if (string.IsNullOrWhiteSpace(_sessionTarget) || string.IsNullOrWhiteSpace(_latestTarget)) return;

                using var input = new FileStream(_source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (input.Length < _copiedBytes) _copiedBytes = 0;
                if (input.Length == _copiedBytes) return;

                input.Position = _copiedBytes;
                using var buffer = new MemoryStream();
                input.CopyTo(buffer);
                var bytes = buffer.ToArray();
                _copiedBytes = input.Position;

                AppendBytes(_sessionTarget, bytes);
                AppendBytes(_latestTarget, bytes);
            }
        }
        catch
        {
            // Best effort only. The authoritative session log remains in LocalAppData.
        }
        finally
        {
            Volatile.Write(ref _copying, 0);
        }
    }

    private static void AppendBytes(string path, byte[] bytes)
    {
        using var output = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        output.Write(bytes, 0, bytes.Length);
        output.Flush();
    }

    private static string ResolveVisibleLogRoot()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Logs"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "GreyVPN Logs"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GreyVPN", "Logs")
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

        throw new IOException("GreyVPN could not create a writable live log directory.");
    }
}
