using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using GreyVPN.Models;

namespace GreyVPN.Services;

public static class RealProxyTester
{
    private static readonly TimeSpan OverallTimeout = TimeSpan.FromSeconds(22);

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
            if (profile.Type.Equals("AmneziaWG", StringComparison.OrdinalIgnoreCase))
                profile.RealError = "AmneziaWG не проверяется стандартным WireGuard: нужен отдельный AWG-движок, иначе результат был бы ложным.";
            else if (profile.Type.Equals("OpenVPN", StringComparison.OrdinalIgnoreCase))
                profile.RealError = "OpenVPN real-test не включён: стабильный sing-box 1.13.19 ещё не содержит OpenVPN client endpoint.";
            else
                profile.RealError = "Real v0.4: VLESS / VMESS / TROJAN / HYSTERIA2 / WireGuard / SS / SOCKS / HTTP(S).";
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
        var tempDir = Path.Combine(Path.GetTempPath(), "GreyVPN", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var configPath = Path.Combine(tempDir, "config.json");
        Process? process = null;

        try
        {
            var localPort = GetFreeTcpPort();
            if (!SingBoxConfigBuilder.TryBuild(profile, localPort, out var config, out var buildError))
            {
                profile.RealStatus = "CONFIG ERROR";
                profile.RealError = buildError;
                return;
            }

            await File.WriteAllTextAsync(configPath, config, token);
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
            process = Process.Start(psi) ?? throw new InvalidOperationException("Не удалось запустить sing-box.");

            var stderrTask = process.StandardError.ReadToEndAsync(token);
            var stdoutTask = process.StandardOutput.ReadToEndAsync(token);

            if (!await WaitForLocalPortAsync(localPort, process, token))
            {
                var err = await SafeReadAsync(stderrTask);
                profile.RealStatus = "ENGINE ERROR";
                profile.RealError = string.IsNullOrWhiteSpace(err) ? "sing-box не открыл локальный proxy port." : Shorten(err);
                return;
            }

            var sw = Stopwatch.StartNew();
            using var handler = new HttpClientHandler
            {
                Proxy = new WebProxy($"http://127.0.0.1:{localPort}"),
                UseProxy = true,
                AllowAutoRedirect = false
            };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(12) };

            using var probe = await client.GetAsync("https://www.gstatic.com/generate_204", token);
            if ((int)probe.StatusCode < 200 || (int)probe.StatusCode >= 400)
            {
                profile.RealStatus = "NO INTERNET";
                profile.RealError = $"HTTPS через профиль вернул {(int)probe.StatusCode}.";
                return;
            }

            var exitIp = (await client.GetStringAsync("https://api.ipify.org", token)).Trim();
            sw.Stop();
            if (!IPAddress.TryParse(exitIp, out _))
            {
                profile.RealStatus = "NO INTERNET";
                profile.RealError = "HTTPS прошёл, но внешний IP не распознан.";
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
            profile.RealError = $"Реальный тест превысил {OverallTimeout.TotalSeconds:0} с.";
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
        for (var i = 0; i < 40; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (process.HasExited) return false;
            try
            {
                using var tcp = new TcpClient();
                using var shortTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                shortTimeout.CancelAfter(150);
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

    private static async Task<string> SafeReadAsync(Task<string> task)
    {
        try { return await task.WaitAsync(TimeSpan.FromMilliseconds(500)); }
        catch { return string.Empty; }
    }

    private static string TrimError(string error, string output)
    {
        var text = string.IsNullOrWhiteSpace(error) ? output : error;
        return string.IsNullOrWhiteSpace(text) ? "sing-box check завершился с ошибкой." : Shorten(text);
    }

    private static string Shorten(string value)
    {
        value = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return value.Length <= 500 ? value : value[..500];
    }
}
