using System.Diagnostics;
using System.Net;
using System.Security.Principal;
using System.Text;
using GreyVPN.Models;

namespace GreyVPN.Services;

public static class OpenVpnRealTester
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(18);
    private static readonly SemaphoreSlim OpenVpnGate = new(1, 1);
    private const string ProbeUrl = "https://1.1.1.1/cdn-cgi/trace";

    public static bool Supports(VpnProfile profile) =>
        profile.Type.Equals("OpenVPN", StringComparison.OrdinalIgnoreCase);

    public static async Task TestAsync(VpnProfile profile, CancellationToken ct)
    {
        await OpenVpnGate.WaitAsync(ct);
        try
        {
            await TestCoreAsync(profile, ct);
        }
        finally
        {
            OpenVpnGate.Release();
        }
    }

    private static async Task TestCoreAsync(VpnProfile profile, CancellationToken ct)
    {
        profile.RealStatus = "OpenVPN test";
        profile.RealError = string.Empty;
        profile.ExitIp = string.Empty;
        profile.RealTestMs = null;
        profile.LastRealTested = DateTimeOffset.Now;

        if (!IsAdministrator())
        {
            profile.RealStatus = "NEED ADMIN";
            profile.RealError = "Для Wintun OpenVPN real-test запусти GreyVPN от имени администратора.";
            return;
        }

        var engineDir = Path.Combine(AppContext.BaseDirectory, "engines", "openvpn");
        var engine = Path.Combine(engineDir, "openvpn.exe");
        if (!File.Exists(engine))
        {
            profile.RealStatus = "ENGINE ERROR";
            profile.RealError = "Не найден engines\\openvpn\\openvpn.exe.";
            return;
        }

        if (!OpenVpnConfigSanitizer.TryBuildSafeConfig(profile, out var safeConfig, out var sanitizeError))
        {
            profile.RealStatus = sanitizeError.Contains("логин/пароль", StringComparison.OrdinalIgnoreCase)
                ? "AUTH NEEDED"
                : "CONFIG BLOCKED";
            profile.RealError = sanitizeError;
            return;
        }

        string baselineIp;
        try
        {
            baselineIp = await GetCloudflareIpAsync(ct);
        }
        catch (Exception ex)
        {
            profile.RealStatus = "BASELINE ERROR";
            profile.RealError = $"Не удалось получить исходный внешний IP: {Shorten(ex.Message)}";
            return;
        }

        using var overall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        overall.CancelAfter(TimeSpan.FromSeconds(32));
        var token = overall.Token;
        var tempDir = Path.Combine(Path.GetTempPath(), "GreyVPN", "openvpn", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var configPath = Path.Combine(tempDir, "profile.ovpn");
        Process? process = null;
        var log = new StringBuilder();
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            await File.WriteAllTextAsync(configPath, safeConfig, new UTF8Encoding(false), token);

            var psi = new ProcessStartInfo
            {
                FileName = engine,
                Arguments = $"--config \"{configPath}\"",
                WorkingDirectory = engineDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) => HandleLine(e.Data, log, connected);
            process.ErrorDataReceived += (_, e) => HandleLine(e.Data, log, connected);

            if (!process.Start())
                throw new InvalidOperationException("Не удалось запустить openvpn.exe.");

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            connectCts.CancelAfter(ConnectTimeout);
            var exitTask = process.WaitForExitAsync(connectCts.Token);
            var winner = await Task.WhenAny(connected.Task, exitTask, Task.Delay(ConnectTimeout, connectCts.Token));

            if (winner != connected.Task || !connected.Task.IsCompletedSuccessfully)
            {
                var text = Snapshot(log);
                ClassifyFailure(profile, text, process.HasExited ? process.ExitCode : null);
                return;
            }

            var sw = Stopwatch.StartNew();
            var exitIp = await GetCloudflareIpAsync(token);
            sw.Stop();

            if (!IPAddress.TryParse(exitIp, out _))
            {
                profile.RealStatus = "NO INTERNET";
                profile.RealError = "OpenVPN подключился, но внешний IP не распознан.";
                return;
            }

            if (exitIp.Equals(baselineIp, StringComparison.OrdinalIgnoreCase))
            {
                profile.RealStatus = "NO TUNNEL";
                profile.RealError = $"OpenVPN сообщил подключение, но probe вышел через прежний IP {baselineIp}.";
                return;
            }

            profile.RealStatus = "РАБОТАЕТ";
            profile.ExitIp = exitIp;
            profile.RealTestMs = sw.ElapsedMilliseconds;
            profile.RealError = string.Empty;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            profile.RealStatus = "TIMEOUT";
            profile.RealError = "OpenVPN real-test превысил таймаут.";
        }
        catch (OperationCanceledException)
        {
            profile.RealStatus = "ОТМЕНЕНО";
        }
        catch (HttpRequestException ex)
        {
            profile.RealStatus = "NO INTERNET";
            profile.RealError = Shorten(ex.Message);
        }
        catch (Exception ex)
        {
            profile.RealStatus = "ENGINE ERROR";
            profile.RealError = Shorten(ex.Message);
        }
        finally
        {
            if (process is { HasExited: false })
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                try { await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
            }
            process?.Dispose();
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    private static void HandleLine(string? line, StringBuilder log, TaskCompletionSource connected)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        lock (log)
        {
            if (log.Length < 24_000) log.AppendLine(line);
        }
        if (line.Contains("Initialization Sequence Completed", StringComparison.OrdinalIgnoreCase))
            connected.TrySetResult();
    }

    private static async Task<string> GetCloudflareIpAsync(CancellationToken ct)
    {
        using var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
        var text = await client.GetStringAsync(ProbeUrl, ct);
        foreach (var line in text.Split('\n'))
        {
            if (line.StartsWith("ip=", StringComparison.OrdinalIgnoreCase))
                return line[3..].Trim();
        }
        throw new InvalidDataException("Cloudflare trace не вернул поле ip.");
    }

    private static void ClassifyFailure(VpnProfile profile, string log, int? exitCode)
    {
        var suffix = exitCode is null ? string.Empty : $" ExitCode={exitCode}.";
        if (log.Contains("AUTH_FAILED", StringComparison.OrdinalIgnoreCase))
        {
            profile.RealStatus = "AUTH ERROR";
            profile.RealError = "OpenVPN: AUTH_FAILED." + suffix;
        }
        else if (log.Contains("There are no TAP-Windows adapters", StringComparison.OrdinalIgnoreCase) ||
                 log.Contains("wintun", StringComparison.OrdinalIgnoreCase) &&
                 (log.Contains("failed", StringComparison.OrdinalIgnoreCase) || log.Contains("error", StringComparison.OrdinalIgnoreCase)))
        {
            profile.RealStatus = "DRIVER ERROR";
            profile.RealError = Shorten(LastUsefulLines(log)) + suffix;
        }
        else if (log.Contains("TLS Error", StringComparison.OrdinalIgnoreCase) ||
                 log.Contains("TLS key negotiation failed", StringComparison.OrdinalIgnoreCase))
        {
            profile.RealStatus = "TLS ERROR";
            profile.RealError = Shorten(LastUsefulLines(log)) + suffix;
        }
        else
        {
            profile.RealStatus = exitCode is null ? "TIMEOUT" : "CONNECT ERROR";
            profile.RealError = Shorten(LastUsefulLines(log)) + suffix;
        }
    }

    private static string LastUsefulLines(string value)
    {
        var lines = value.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .TakeLast(6);
        var text = string.Join(" | ", lines);
        return string.IsNullOrWhiteSpace(text) ? "OpenVPN не завершил подключение." : text;
    }

    private static string Snapshot(StringBuilder log)
    {
        lock (log) return log.ToString();
    }

    private static bool IsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private static string Shorten(string value)
    {
        value = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return value.Length <= 700 ? value : value[..700];
    }
}
