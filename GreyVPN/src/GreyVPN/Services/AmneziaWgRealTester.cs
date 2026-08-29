using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using GreyVPN.Models;

namespace GreyVPN.Services;

public static class AmneziaWgRealTester
{
    private const string TestTunnelName = "GreyVPNTest";
    private const string TestServiceName = "AmneziaWGTunnel$" + TestTunnelName;
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly Uri[] ProbeUris =
    {
        new("https://1.1.1.1/cdn-cgi/trace"),
        new("https://1.0.0.1/cdn-cgi/trace")
    };

    public static bool Supports(VpnProfile profile) => ConfigVault.Supports(profile);
    public static string EnginePath => Path.Combine(AppContext.BaseDirectory, "engines", "amneziawg", "amneziawg.exe");

    public static async Task TestAsync(VpnProfile profile, CancellationToken ct)
    {
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        var sw = Stopwatch.StartNew();
        var log = new StringBuilder();
        var tempDir = Path.Combine(Path.GetTempPath(), "GreyVPN", "amneziawg", Guid.NewGuid().ToString("N"));
        var testConfigPath = Path.Combine(tempDir, TestTunnelName + ".conf");

        profile.ExitIp = string.Empty;
        profile.RealError = string.Empty;
        profile.LastRealTested = DateTimeOffset.Now;

        try
        {
            if (!OperatingSystem.IsWindows())
            {
                profile.RealStatus = "ENGINE ERROR";
                profile.RealError = "Официальный AmneziaWG backend v0.9 предназначен для Windows.";
                return;
            }
            if (!File.Exists(EnginePath))
            {
                profile.RealStatus = "ENGINE ERROR";
                profile.RealError = "Не найден engines\\amneziawg\\amneziawg.exe.";
                return;
            }
            if (!IsAdministrator())
            {
                profile.RealStatus = "ADMIN REQUIRED";
                profile.RealError = "WG/AWG real-test использует официальный Windows tunnel service и требует запуск GreyVPN от администратора.";
                return;
            }

            Directory.CreateDirectory(tempDir);
            var source = await ConfigVault.ReadTextAsync(profile, ct).ConfigureAwait(false);
            var testConfig = AmneziaWgTestConfigBuilder.Build(source);
            await File.WriteAllTextAsync(testConfigPath, testConfig, new UTF8Encoding(false), ct).ConfigureAwait(false);

            string baselineIp = string.Empty;
            try
            {
                baselineIp = await ProbeCloudflareAsync(ct, TimeSpan.FromSeconds(6)).ConfigureAwait(false);
                log.AppendLine("Baseline exit IP: " + baselineIp);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.AppendLine("Baseline probe unavailable: " + Flatten(ex));
            }

            await CleanupStaleTunnelAsync(log).ConfigureAwait(false);

            var install = await RunEngineAsync(new[] { "/installtunnelservice", testConfigPath }, TimeSpan.FromSeconds(12), ct).ConfigureAwait(false);
            log.AppendLine("[INSTALL]");
            log.AppendLine(install.Text);
            log.AppendLine("ExitCode=" + install.ExitCode);
            if (install.ExitCode != 0)
            {
                profile.RealStatus = ClassifyEngineError(install.Text);
                profile.RealError = ShortError(install.Text);
                return;
            }

            var service = await WaitForServiceRunningAsync(TestServiceName, TimeSpan.FromSeconds(8), ct).ConfigureAwait(false);
            log.AppendLine($"Service state={service.State}; Win32Exit={service.Win32ExitCode}; ServiceExit={service.ServiceSpecificExitCode}");
            if (service.State != ServiceState.Running)
            {
                var dump = await TryDumpLogAsync().ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(dump))
                {
                    log.AppendLine("[AMNEZIAWG LOG]");
                    log.AppendLine(dump);
                }
                profile.RealStatus = ClassifyEngineError(dump);
                profile.RealError = ShortError(string.IsNullOrWhiteSpace(dump)
                    ? $"AmneziaWG tunnel service не перешёл в RUNNING. Win32={service.Win32ExitCode}, Service={service.ServiceSpecificExitCode}."
                    : dump);
                return;
            }

            string exitIp;
            try
            {
                exitIp = await ProbeCloudflareAsync(ct, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                profile.RealStatus = "TIMEOUT";
                profile.RealError = "AmneziaWG tunnel RUNNING, но тестовые HTTPS-маршруты не дали ответа.";
                return;
            }
            catch (HttpRequestException ex)
            {
                profile.RealStatus = "NO INTERNET";
                profile.RealError = ShortError(ex.Message);
                return;
            }

            profile.ExitIp = exitIp;
            profile.RealStatus = "РАБОТАЕТ";
            profile.RealError = string.Empty;
            log.AppendLine("Tunnel exit IP: " + exitIp);
            if (!string.IsNullOrWhiteSpace(baselineIp) && baselineIp.Equals(exitIp, StringComparison.OrdinalIgnoreCase))
                log.AppendLine("NOTE: tunnel exit IP equals baseline IP; status remains working because the exact /32 probe routes were installed by a RUNNING tunnel service.");
        }
        catch (FileNotFoundException ex)
        {
            profile.RealStatus = "CONFIG ERROR";
            profile.RealError = ShortError(ex.Message);
        }
        catch (InvalidDataException ex)
        {
            profile.RealStatus = "CONFIG ERROR";
            profile.RealError = ShortError(ex.Message);
        }
        catch (OperationCanceledException)
        {
            profile.RealStatus = "ОТМЕНЕНО";
            profile.RealError = "WG/AWG real-test отменён.";
            throw;
        }
        catch (Exception ex)
        {
            profile.RealStatus = "ENGINE ERROR";
            profile.RealError = ShortError(Flatten(ex));
            log.AppendLine("Unhandled: " + ex);
        }
        finally
        {
            try { await CleanupStaleTunnelAsync(log).ConfigureAwait(false); } catch { }
            try { Directory.Delete(tempDir, recursive: true); } catch { }
            sw.Stop();
            profile.RealTestMs = (long)Math.Min(int.MaxValue, sw.ElapsedMilliseconds);
            profile.LastRealTested = DateTimeOffset.Now;
            DiagnosticsService.WriteEngineLog(profile, "AmneziaWG", log.ToString(), "wg-awg-real-test");
            Gate.Release();
        }
    }

    private static async Task CleanupStaleTunnelAsync(StringBuilder log)
    {
        if (!File.Exists(EnginePath)) return;
        var cleanup = await RunEngineAsync(new[] { "/uninstalltunnelservice", TestTunnelName }, TimeSpan.FromSeconds(8), CancellationToken.None).ConfigureAwait(false);
        if (cleanup.ExitCode == 0)
            log.AppendLine("Cleanup tunnel service: OK");
        else if (!string.IsNullOrWhiteSpace(cleanup.Text))
            log.AppendLine("Cleanup tunnel service: " + ShortError(cleanup.Text));
        await Task.Delay(250).ConfigureAwait(false);
    }

    private static async Task<string> TryDumpLogAsync()
    {
        try
        {
            var result = await RunEngineAsync(new[] { "/dumplog", "/tail" }, TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
            return result.Text;
        }
        catch (Exception ex)
        {
            return "Не удалось получить AmneziaWG log: " + ex.Message;
        }
    }

    private static async Task<(int ExitCode, string Text)> RunEngineAsync(string[] args, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = EnginePath,
            WorkingDirectory = Path.GetDirectoryName(EnginePath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Не удалось запустить amneziawg.exe.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timer.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timer.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                try { await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
            }
            if (ct.IsCancellationRequested) throw;
            throw new TimeoutException($"amneziawg.exe не завершил команду за {timeout.TotalSeconds:0} с.");
        }
        var text = ((await stdout.ConfigureAwait(false)) + Environment.NewLine + (await stderr.ConfigureAwait(false))).Trim();
        return (process.ExitCode, text);
    }

    private static async Task<string> ProbeCloudflareAsync(CancellationToken ct, TimeSpan timeout)
    {
        var errors = new List<string>();
        foreach (var uri in ProbeUris)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var handler = new HttpClientHandler
                {
                    UseProxy = false,
                    Proxy = null,
                    AutomaticDecompression = DecompressionMethods.All,
                    ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
                };
                using var client = new HttpClient(handler) { Timeout = timeout };
                using var request = new HttpRequestMessage(HttpMethod.Get, uri)
                {
                    Version = HttpVersion.Version11,
                    VersionPolicy = HttpVersionPolicy.RequestVersionOrLower
                };
                request.Headers.UserAgent.ParseAdd("GreyVPN/0.9-real-test");
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
                var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    errors.Add($"{uri.Host}: HTTP {(int)response.StatusCode}");
                    continue;
                }
                foreach (var line in text.Replace("\r", string.Empty).Split('\n'))
                {
                    if (!line.StartsWith("ip=", StringComparison.OrdinalIgnoreCase)) continue;
                    var value = line[3..].Trim();
                    if (IPAddress.TryParse(value, out _)) return value;
                }
                errors.Add($"{uri.Host}: trace без ip=");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                errors.Add($"{uri.Host}: {Flatten(ex)}");
            }
        }
        throw new HttpRequestException("HTTPS probe не подтвердил интернет через WG/AWG: " + string.Join(" | ", errors));
    }

    private static async Task<ServiceStatusResult> WaitForServiceRunningAsync(string serviceName, TimeSpan timeout, CancellationToken ct)
    {
        var until = DateTime.UtcNow + timeout;
        ServiceStatusResult last = default;
        while (DateTime.UtcNow < until)
        {
            ct.ThrowIfCancellationRequested();
            last = QueryService(serviceName);
            if (last.State == ServiceState.Running || last.State == ServiceState.Stopped) return last;
            await Task.Delay(150, ct).ConfigureAwait(false);
        }
        return QueryService(serviceName);
    }

    private static ServiceStatusResult QueryService(string serviceName)
    {
        var scm = OpenSCManager(null, null, 0x0001);
        if (scm == IntPtr.Zero) return new ServiceStatusResult(ServiceState.Unknown, (uint)Marshal.GetLastWin32Error(), 0);
        try
        {
            var service = OpenService(scm, serviceName, 0x0004);
            if (service == IntPtr.Zero) return new ServiceStatusResult(ServiceState.Missing, (uint)Marshal.GetLastWin32Error(), 0);
            try
            {
                var status = new SERVICE_STATUS_PROCESS();
                var size = (uint)Marshal.SizeOf<SERVICE_STATUS_PROCESS>();
                if (!QueryServiceStatusEx(service, 0, ref status, size, out _))
                    return new ServiceStatusResult(ServiceState.Unknown, (uint)Marshal.GetLastWin32Error(), 0);
                return new ServiceStatusResult((ServiceState)status.dwCurrentState, status.dwWin32ExitCode, status.dwServiceSpecificExitCode);
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(scm); }
    }

    private static bool IsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    private static string ClassifyEngineError(string value)
    {
        if (ContainsAny(value, "access is denied", "access denied", "отказано в доступе", "privilege")) return "ADMIN REQUIRED";
        if (ContainsAny(value, "wintun", "driver", "adapter")) return "DRIVER ERROR";
        if (ContainsAny(value, "parse", "invalid", "configuration", "config", "tunnel name")) return "CONFIG ERROR";
        if (ContainsAny(value, "dns", "resolve", "lookup")) return "DNS ERROR";
        return "CONNECT ERROR";
    }

    private static bool ContainsAny(string value, params string[] needles) =>
        needles.Any(n => value.Contains(n, StringComparison.OrdinalIgnoreCase));

    private static string Flatten(Exception ex)
    {
        var parts = new List<string>();
        for (Exception? cur = ex; cur is not null && parts.Count < 4; cur = cur.InnerException)
            if (!string.IsNullOrWhiteSpace(cur.Message) && !parts.Contains(cur.Message)) parts.Add(cur.Message);
        return string.Join(" -> ", parts);
    }

    private static string ShortError(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var oneLine = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return oneLine.Length <= 1200 ? oneLine : oneLine[..1200] + "…";
    }

    private readonly record struct ServiceStatusResult(ServiceState State, uint Win32ExitCode, uint ServiceSpecificExitCode);

    private enum ServiceState : uint
    {
        Unknown = 0,
        Stopped = 1,
        StartPending = 2,
        StopPending = 3,
        Running = 4,
        ContinuePending = 5,
        PausePending = 6,
        Paused = 7,
        Missing = 0xFFFFFFFF
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_STATUS_PROCESS
    {
        public uint dwServiceType;
        public uint dwCurrentState;
        public uint dwControlsAccepted;
        public uint dwWin32ExitCode;
        public uint dwServiceSpecificExitCode;
        public uint dwCheckPoint;
        public uint dwWaitHint;
        public uint dwProcessId;
        public uint dwServiceFlags;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenSCManager(string? machineName, string? databaseName, uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenService(IntPtr scm, string serviceName, uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatusEx(IntPtr service, int infoLevel, ref SERVICE_STATUS_PROCESS buffer, uint bufferSize, out uint bytesNeeded);

    [DllImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr handle);
}