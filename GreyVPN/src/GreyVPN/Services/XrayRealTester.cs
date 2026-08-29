using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using GreyVPN.Models;

namespace GreyVPN.Services;

public static class XrayRealTester
{
    private static readonly TimeSpan OverallTimeout = TimeSpan.FromSeconds(32);
    private static readonly TimeSpan LocalStartTimeout = TimeSpan.FromSeconds(6);

    public static bool Supports(VpnProfile profile) => XrayConfigBuilder.Supports(profile);
    public static bool IsRealWorking(VpnProfile profile) => profile.RealStatus == "РАБОТАЕТ";

    public static async Task TestAsync(VpnProfile profile, CancellationToken ct)
    {
        profile.RealStatus = "XRAY TEST";
        profile.RealError = string.Empty;
        profile.ExitIp = string.Empty;
        profile.RealTestMs = null;
        profile.LastRealTested = DateTimeOffset.Now;

        var engineDir = Path.Combine(AppContext.BaseDirectory, "engines", "xray");
        var engine = Path.Combine(engineDir, "xray.exe");
        if (!File.Exists(engine))
        {
            profile.RealStatus = "ENGINE ERROR";
            profile.RealError = "Не найден engines\\xray\\xray.exe.";
            return;
        }

        using var overall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        overall.CancelAfter(OverallTimeout);
        var token = overall.Token;

        var tempDir = Path.Combine(Path.GetTempPath(), "GreyVPN", "xray", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var configPath = Path.Combine(tempDir, "config.json");
        Process? process = null;
        var log = new StringBuilder();
        var validationLog = string.Empty;

        try
        {
            var localPort = GetFreeTcpPort();
            var builtConfig = XrayConfigBuilder.Build(profile, localPort);
            var normalized = XrayConfigCompat.Normalize(profile, builtConfig);
            var config = normalized.Json;
            if (!string.IsNullOrWhiteSpace(normalized.Warning))
            {
                AppendLog(log, "COMPAT: " + normalized.Warning);
                DiagnosticsService.Log("XRAY", normalized.Warning, profile);
            }
            await File.WriteAllTextAsync(configPath, config, new UTF8Encoding(false), token);

            DiagnosticsService.Log("XRAY", "Config check start", profile);
            var validation = await ValidateConfigAsync(engine, engineDir, configPath, token);
            validationLog = validation.Log;
            DiagnosticsService.Log("XRAY", $"Config check end. Ok={validation.Ok}; Log={validation.Log}", profile);
            if (!validation.Ok)
            {
                profile.RealStatus = "CONFIG ERROR";
                profile.RealError = Shorten(validation.Log);
                return;
            }

            var psi = new ProcessStartInfo
            {
                FileName = engine,
                Arguments = $"run -c \"{configPath}\"",
                WorkingDirectory = engineDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) => AppendLog(log, e.Data);
            process.ErrorDataReceived += (_, e) => AppendLog(log, e.Data);

            if (!process.Start()) throw new InvalidOperationException("Не удалось запустить xray.exe.");
            DiagnosticsService.Log("XRAY", $"Runtime started. PID={process.Id}", profile);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            var started = await WaitForLocalPortAsync(localPort, process, LocalStartTimeout, token);
            if (!started)
            {
                profile.RealStatus = process.HasExited ? "ENGINE ERROR" : "START TIMEOUT";
                profile.RealError = Shorten(LastUsefulLines(Snapshot(log), "Xray не открыл локальный HTTP proxy."));
                return;
            }

            DiagnosticsService.Log("XRAY", $"Local proxy ready on 127.0.0.1:{localPort}; starting HTTPS egress probe", profile);
            var sw = Stopwatch.StartNew();
            var exitIp = await ProxyEgressProbe.GetExitIpAsync(new WebProxy($"http://127.0.0.1:{localPort}"), token);
            sw.Stop();

            profile.RealStatus = "РАБОТАЕТ";
            profile.ExitIp = exitIp;
            profile.RealTestMs = sw.ElapsedMilliseconds;
            profile.RealError = string.Empty;
        }
        catch (InvalidDataException ex)
        {
            profile.RealStatus = "CONFIG ERROR";
            profile.RealError = Shorten(ex.Message);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            profile.RealStatus = "TIMEOUT";
            profile.RealError = $"Xray real-test превысил {OverallTimeout.TotalSeconds:0} с. " + Shorten(LastUsefulLines(Snapshot(log), string.Empty));
        }
        catch (OperationCanceledException)
        {
            profile.RealStatus = "ОТМЕНЕНО";
        }
        catch (HttpRequestException ex)
        {
            profile.RealStatus = ClassifyRuntimeFailure(Snapshot(log));
            var details = FlattenException(ex);
            var engineLog = LastUsefulLines(Snapshot(log), string.Empty);
            profile.RealError = Shorten(string.IsNullOrWhiteSpace(engineLog) ? details : $"{details} | XRAY: {engineLog}");
        }
        catch (Exception ex)
        {
            profile.RealStatus = "ENGINE ERROR";
            var engineLog = LastUsefulLines(Snapshot(log), string.Empty);
            profile.RealError = Shorten(string.IsNullOrWhiteSpace(engineLog) ? FlattenException(ex) : $"{FlattenException(ex)} | XRAY: {engineLog}");
        }
        finally
        {
            if (process is { HasExited: false })
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                try { await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
            }

            var exitCode = process is { HasExited: true } ? process.ExitCode.ToString() : "n/a";
            DiagnosticsService.WriteEngineLog(profile, "xray",
                $"[CONFIG CHECK]\r\n{validationLog}\r\n\r\n[RUNTIME]\r\n{Snapshot(log)}\r\n\r\n[FINAL]\r\nStatus={profile.RealStatus}\r\nError={profile.RealError}\r\nExitIP={profile.ExitIp}\r\nRealMs={profile.RealTestMs}\r\nProcessExitCode={exitCode}\r\n");

            process?.Dispose();
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    private static async Task<(bool Ok, string Log)> ValidateConfigAsync(string engine, string engineDir, string configPath, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = engine,
            Arguments = $"run -test -c \"{configPath}\"",
            WorkingDirectory = engineDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Не удалось запустить проверку Xray-конфига.");
        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        var stderrTask = p.StandardError.ReadToEndAsync();
        try
        {
            await p.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            if (!p.HasExited)
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                try { await p.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
            }
            throw;
        }

        var text = ((await stdoutTask) + Environment.NewLine + (await stderrTask)).Trim();
        return (p.ExitCode == 0, LastUsefulLines(text, "Xray: config check завершился без текста."));
    }

    private static async Task<bool> WaitForLocalPortAsync(int port, Process process, TimeSpan timeout, CancellationToken ct)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            ct.ThrowIfCancellationRequested();
            if (process.HasExited) return false;
            try
            {
                using var tcp = new TcpClient();
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
                attempt.CancelAfter(TimeSpan.FromMilliseconds(250));
                await tcp.ConnectAsync(IPAddress.Loopback, port, attempt.Token);
                return true;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            catch (SocketException) { }
            await Task.Delay(100, ct);
        }
        return false;
    }

    private static int GetFreeTcpPort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static string ClassifyRuntimeFailure(string log)
    {
        if (ContainsAny(log, "invalid user", "authentication failed", "rejected proxy", "invalid account", "invalid password")) return "AUTH ERROR";
        if (ContainsAny(log, "reality", "tls handshake", "certificate", "server name", "x509")) return "TLS ERROR";
        if (ContainsAny(log, "timeout", "deadline exceeded", "i/o timeout", "context deadline")) return "TIMEOUT";
        if (ContainsAny(log, "connection refused", "connection reset", "connection closed", "no route to host", "network is unreachable")) return "CONNECT ERROR";
        return "NO INTERNET";
    }

    private static bool ContainsAny(string value, params string[] needles) =>
        needles.Any(x => value.Contains(x, StringComparison.OrdinalIgnoreCase));

    private static void AppendLog(StringBuilder log, string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        lock (log)
        {
            if (log.Length < 96_000) log.AppendLine(line);
        }
    }

    private static string Snapshot(StringBuilder log)
    {
        lock (log) return log.ToString();
    }

    private static string LastUsefulLines(string value, string fallback)
    {
        var lines = value.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .TakeLast(10);
        var text = string.Join(" | ", lines);
        return string.IsNullOrWhiteSpace(text) ? fallback : text;
    }

    private static string FlattenException(Exception ex)
    {
        var parts = new List<string>();
        for (Exception? cur = ex; cur is not null && parts.Count < 5; cur = cur.InnerException)
        {
            if (!string.IsNullOrWhiteSpace(cur.Message) && !parts.Contains(cur.Message)) parts.Add(cur.Message);
        }
        return string.Join(" -> ", parts);
    }

    private static string Shorten(string value)
    {
        value = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return value.Length <= 1400 ? value : value[..1400];
    }
}