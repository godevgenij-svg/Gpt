using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using GreyVPN.Models;

namespace GreyVPN.Services;

public static class RealProxyTester
{
    private static readonly TimeSpan OverallTimeout = TimeSpan.FromSeconds(30);

    public static bool IsRealWorking(VpnProfile p) => p.RealStatus.Equals("РАБОТАЕТ", StringComparison.OrdinalIgnoreCase);

    public static async Task TestAsync(VpnProfile profile, CancellationToken ct)
    {
        profile.RealStatus = "Реальный тест";
        profile.RealError = string.Empty;
        profile.ExitIp = string.Empty;
        profile.RealTestMs = null;
        profile.LastRealTested = DateTimeOffset.Now;

        if (!SingBoxConfigBuilder.Supports(profile))
        {
            profile.RealStatus = "НЕ ПОДДЕРЖАН";
            profile.RealError = profile.Type.Equals("AmneziaWG", StringComparison.OrdinalIgnoreCase)
                ? "AmneziaWG нельзя достоверно проверить стандартным WireGuard: нужен AWG-движок."
                : "Для этого формата real-test через sing-box не реализован.";
            return;
        }

        var engine = Path.Combine(AppContext.BaseDirectory, "engines", "sing-box.exe");
        if (!File.Exists(engine))
        {
            profile.RealStatus = "ENGINE ERROR";
            profile.RealError = "Не найден engines\\sing-box.exe.";
            return;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(OverallTimeout);
        var token = timeout.Token;
        var tempDir = Path.Combine(Path.GetTempPath(), "GreyVPN", "sing-box", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var configPath = Path.Combine(tempDir, "config.json");
        Process? process = null;
        var log = new StringBuilder();

        try
        {
            var localPort = GetFreeTcpPort();
            if (!SingBoxConfigBuilder.TryBuild(profile, localPort, out var config, out var buildError))
            {
                profile.RealStatus = "CONFIG ERROR";
                profile.RealError = buildError;
                return;
            }

            await File.WriteAllTextAsync(configPath, config, new UTF8Encoding(false), token);
            var check = await RunAndCaptureAsync(engine, $"check -c \"{configPath}\"", tempDir, token);
            if (check.ExitCode != 0)
            {
                profile.RealStatus = "CONFIG ERROR";
                profile.RealError = TrimError(check.Error, check.Output);
                return;
            }

            var psi = new ProcessStartInfo
            {
                FileName = engine,
                Arguments = $"run -c \"{configPath}\"",
                WorkingDirectory = tempDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) => AppendLog(log, e.Data);
            process.ErrorDataReceived += (_, e) => AppendLog(log, e.Data);
            if (!process.Start()) throw new InvalidOperationException("Не удалось запустить sing-box.");
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            if (!await WaitForLocalPortAsync(localPort, process, token))
            {
                profile.RealStatus = process.HasExited ? "ENGINE ERROR" : "START TIMEOUT";
                profile.RealError = Shorten(LastUsefulLines(Snapshot(log), "sing-box не открыл локальный proxy port."));
                return;
            }

            var sw = Stopwatch.StartNew();
            var exitIp = await ProxyEgressProbe.GetExitIpAsync(new WebProxy($"http://127.0.0.1:{localPort}"), token);
            sw.Stop();

            profile.RealStatus = "РАБОТАЕТ";
            profile.ExitIp = exitIp;
            profile.RealTestMs = sw.ElapsedMilliseconds;
            profile.RealError = string.Empty;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            profile.RealStatus = "TIMEOUT";
            profile.RealError = $"Реальный тест превысил {OverallTimeout.TotalSeconds:0} с. " + Shorten(LastUsefulLines(Snapshot(log), string.Empty));
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
            profile.RealError = Shorten(string.IsNullOrWhiteSpace(engineLog) ? details : $"{details} | SING-BOX: {engineLog}");
        }
        catch (Exception ex)
        {
            profile.RealStatus = "ENGINE ERROR";
            var engineLog = LastUsefulLines(Snapshot(log), string.Empty);
            profile.RealError = Shorten(string.IsNullOrWhiteSpace(engineLog) ? FlattenException(ex) : $"{FlattenException(ex)} | SING-BOX: {engineLog}");
        }
        finally
        {
            if (process is { HasExited: false })
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                try { await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
            }
            process?.Dispose();
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    private static int GetFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task<bool> WaitForLocalPortAsync(int port, Process process, CancellationToken ct)
    {
        for (var i = 0; i < 50; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (process.HasExited) return false;
            try
            {
                using var tcp = new TcpClient();
                using var shortTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                shortTimeout.CancelAfter(200);
                await tcp.ConnectAsync(IPAddress.Loopback, port, shortTimeout.Token);
                return true;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            catch (SocketException) { }
            await Task.Delay(100, ct);
        }
        return false;
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunAndCaptureAsync(string file, string args, string cwd, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = file,
            Arguments = args,
            WorkingDirectory = cwd,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Не удалось запустить sing-box check.");
        var outputTask = process.StandardOutput.ReadToEndAsync(ct);
        var errorTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        return (process.ExitCode, await outputTask, await errorTask);
    }

    private static string ClassifyRuntimeFailure(string log)
    {
        if (ContainsAny(log, "authentication failed", "invalid password", "bad credentials", "unauthorized")) return "AUTH ERROR";
        if (ContainsAny(log, "tls handshake", "certificate", "x509", "reality", "server name")) return "TLS ERROR";
        if (ContainsAny(log, "timeout", "deadline exceeded", "i/o timeout", "context deadline")) return "TIMEOUT";
        if (ContainsAny(log, "connection refused", "connection reset", "connection closed", "network is unreachable", "no route to host")) return "CONNECT ERROR";
        return "NO INTERNET";
    }

    private static bool ContainsAny(string value, params string[] needles) =>
        needles.Any(x => value.Contains(x, StringComparison.OrdinalIgnoreCase));

    private static void AppendLog(StringBuilder log, string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        lock (log)
        {
            if (log.Length < 48_000) log.AppendLine(line);
        }
    }

    private static string Snapshot(StringBuilder log)
    {
        lock (log) return log.ToString();
    }

    private static string LastUsefulLines(string value, string fallback)
    {
        var lines = value.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(x => !string.IsNullOrWhiteSpace(x)).TakeLast(10);
        var text = string.Join(" | ", lines);
        return string.IsNullOrWhiteSpace(text) ? fallback : text;
    }

    private static string TrimError(string error, string output)
    {
        var text = string.IsNullOrWhiteSpace(error) ? output : error;
        return string.IsNullOrWhiteSpace(text) ? "sing-box check завершился с ошибкой." : Shorten(text);
    }

    private static string FlattenException(Exception ex)
    {
        var parts = new List<string>();
        for (Exception? cur = ex; cur is not null && parts.Count < 5; cur = cur.InnerException)
            if (!string.IsNullOrWhiteSpace(cur.Message) && !parts.Contains(cur.Message)) parts.Add(cur.Message);
        return string.Join(" -> ", parts);
    }

    private static string Shorten(string value)
    {
        value = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return value.Length <= 1400 ? value : value[..1400];
    }
}
