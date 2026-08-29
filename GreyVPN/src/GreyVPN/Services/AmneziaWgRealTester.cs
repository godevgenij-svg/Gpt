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
    public static string AwgToolPath => Path.Combine(AppContext.BaseDirectory, "engines", "amneziawg", "awg.exe");

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
                profile.RealError = "Официальный AmneziaWG backend предназначен для Windows.";
                return;
            }
            if (!File.Exists(EnginePath) || !File.Exists(AwgToolPath))
            {
                profile.RealStatus = "ENGINE ERROR";
                profile.RealError = "Не найдены engines\\amneziawg\\amneziawg.exe и/или awg.exe.";
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

            // Give the official service time to finish interface/route installation before the first packet.
            await Task.Delay(700, ct).ConfigureAwait(false);
            var before = await ReadTelemetryAsync(ct).ConfigureAwait(false);
            AppendTelemetry(log, "Before probe", before);

            string? probeIp = null;
            string? probeError = null;
            try
            {
                probeIp = await ProbeCloudflareAsync(ct, TimeSpan.FromSeconds(8)).ConfigureAwait(false);
                log.AppendLine("HTTPS probe IP: " + probeIp);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                probeError = Flatten(ex);
                log.AppendLine("HTTPS probe failed: " + probeError);
            }

            // Read the official userspace telemetry instead of assuming RUNNING == connected.
            // Poll for a short period because the first data packet may initiate the handshake.
            var after = before;
            var telemetryUntil = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            do
            {
                after = await ReadTelemetryAsync(ct).ConfigureAwait(false);
                if (after.HandshakeUnix > 0 || after.RxBytes > before.RxBytes)
                    break;
                await Task.Delay(450, ct).ConfigureAwait(false);
            }
            while (DateTime.UtcNow < telemetryUntil);
            AppendTelemetry(log, "After probe", after);

            var hasFreshHandshake = after.HandshakeUnix > 0 &&
                                    DateTimeOffset.UtcNow.ToUnixTimeSeconds() - after.HandshakeUnix <= 180;
            var receivedPayload = after.RxBytes > before.RxBytes || after.RxBytes > 0;
            var sentPayload = after.TxBytes > before.TxBytes || after.TxBytes > 0;

            if (!string.IsNullOrWhiteSpace(probeIp))
            {
                // A changed public IP is sufficient proof of a working tunnel. When the IP is
                // unchanged, require AmneziaWG telemetry to prove that the probe actually used it.
                var changedIp = string.IsNullOrWhiteSpace(baselineIp) ||
                                !baselineIp.Equals(probeIp, StringComparison.OrdinalIgnoreCase);
                if (changedIp || (hasFreshHandshake && (receivedPayload || sentPayload)))
                {
                    profile.ExitIp = probeIp;
                    profile.RealStatus = "РАБОТАЕТ";
                    profile.RealError = string.Empty;
                    return;
                }

                profile.RealStatus = "ROUTE NOT USED";
                profile.RealError = $"HTTPS доступен, но probe вышел через прежний IP {probeIp}, а свежий WG/AWG handshake не подтверждён.";
                return;
            }

            var dumpLog = await TryDumpLogAsync().ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(dumpLog))
            {
                log.AppendLine("[AMNEZIAWG LOG AFTER FAILED PROBE]");
                log.AppendLine(dumpLog);
            }

            if (!hasFreshHandshake)
            {
                profile.RealStatus = "NO HANDSHAKE";
                profile.RealError = ShortError($"Служба AmneziaWG RUNNING, но свежего handshake с peer нет. TX={after.TxBytes}, RX={after.RxBytes}. {probeError}");
                return;
            }

            if (!receivedPayload)
            {
                profile.RealStatus = "NO RX";
                profile.RealError = ShortError($"Handshake есть, но полезных входящих данных не получено. TX={after.TxBytes}, RX={after.RxBytes}. {probeError}");
                return;
            }

            profile.RealStatus = IsHostUnreachable(probeError) ? "ROUTE ERROR" : "NO INTERNET";
            profile.RealError = ShortError($"Handshake подтверждён, RX={after.RxBytes}, TX={after.TxBytes}, но HTTPS probe не прошёл. {probeError}");
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

    private static async Task<TunnelTelemetry> ReadTelemetryAsync(CancellationToken ct)
    {
        long handshake = 0;
        long rx = 0;
        long tx = 0;
        var errors = new List<string>();

        var hs = await RunAwgToolAsync(new[] { "show", TestTunnelName, "latest-handshakes" }, TimeSpan.FromSeconds(4), ct).ConfigureAwait(false);
        if (hs.ExitCode == 0)
        {
            foreach (var line in SplitLines(hs.Text))
            {
                var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && long.TryParse(parts[^1], out var value)) handshake = Math.Max(handshake, value);
            }
        }
        else if (!string.IsNullOrWhiteSpace(hs.Text)) errors.Add("handshake: " + ShortError(hs.Text));

        var transfer = await RunAwgToolAsync(new[] { "show", TestTunnelName, "transfer" }, TimeSpan.FromSeconds(4), ct).ConfigureAwait(false);
        if (transfer.ExitCode == 0)
        {
            foreach (var line in SplitLines(transfer.Text))
            {
                var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 3 && long.TryParse(parts[^2], out var rxValue) && long.TryParse(parts[^1], out var txValue))
                {
                    rx = Math.Max(rx, rxValue);
                    tx = Math.Max(tx, txValue);
                }
            }
        }
        else if (!string.IsNullOrWhiteSpace(transfer.Text)) errors.Add("transfer: " + ShortError(transfer.Text));

        return new TunnelTelemetry(handshake, rx, tx, string.Join(" | ", errors));
    }

    private static IEnumerable<string> SplitLines(string value) =>
        value.Replace("\r", string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static void AppendTelemetry(StringBuilder log, string label, TunnelTelemetry t)
    {
        var age = t.HandshakeUnix > 0 ? Math.Max(0, DateTimeOffset.UtcNow.ToUnixTimeSeconds() - t.HandshakeUnix).ToString() + "s" : "none";
        log.AppendLine($"Telemetry {label}: handshakeAge={age}; RX={t.RxBytes}; TX={t.TxBytes}" +
                       (string.IsNullOrWhiteSpace(t.Error) ? string.Empty : "; error=" + t.Error));
    }

    private static async Task CleanupStaleTunnelAsync(StringBuilder log)
    {
        if (!File.Exists(EnginePath)) return;
        var service = QueryService(TestServiceName);
        if (service.State == ServiceState.Missing) return;

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

    private static Task<(int ExitCode, string Text)> RunAwgToolAsync(string[] args, TimeSpan timeout, CancellationToken ct) =>
        RunProcessAsync(AwgToolPath, Path.GetDirectoryName(AwgToolPath)!, args, timeout, ct);

    private static Task<(int ExitCode, string Text)> RunEngineAsync(string[] args, TimeSpan timeout, CancellationToken ct) =>
        RunProcessAsync(EnginePath, Path.GetDirectoryName(EnginePath)!, args, timeout, ct);

    private static async Task<(int ExitCode, string Text)> RunProcessAsync(string file, string workingDirectory, string[] args, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = file,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Не удалось запустить " + Path.GetFileName(file) + ".");
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
            throw new TimeoutException($"{Path.GetFileName(file)} не завершил команду за {timeout.TotalSeconds:0} с.");
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
                request.Headers.UserAgent.ParseAdd("GreyVPN/0.9.1-real-test");
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

    private static bool IsHostUnreachable(string? value) =>
        !string.IsNullOrWhiteSpace(value) && ContainsAny(value, "unreachable host", "недоступного хоста", "no route to host", "network is unreachable");

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
        return oneLine.Length <= 1400 ? oneLine : oneLine[..1400] + "…";
    }

    private readonly record struct TunnelTelemetry(long HandshakeUnix, long RxBytes, long TxBytes, string Error);
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
