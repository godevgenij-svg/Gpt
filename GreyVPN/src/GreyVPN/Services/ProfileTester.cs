using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using GreyVPN.Models;

namespace GreyVPN.Services;

public static class ProfileTester
{
    private const int Attempts = 2;
    private static readonly TimeSpan TcpTimeout = TimeSpan.FromMilliseconds(3500);

    public static bool IsResponsive(VpnProfile profile) =>
        profile.Status.Equals("ENDPOINT OK", StringComparison.OrdinalIgnoreCase) ||
        profile.Status.Equals("HOST ONLY", StringComparison.OrdinalIgnoreCase);

    public static async Task TestAsync(VpnProfile profile, CancellationToken ct)
    {
        profile.Status = "Проверка";
        profile.Error = string.Empty;
        profile.PingMs = null;
        profile.TcpConnectMs = null;
        profile.LatencyMs = null;
        profile.TestAttempts = 0;
        profile.LastTested = DateTimeOffset.Now;

        if (!ProfileImporter.TrySplitEndpoint(profile.Endpoint, out var host, out var port) || string.IsNullOrWhiteSpace(host))
        {
            profile.Status = "NO ENDPOINT";
            profile.Error = "Из конфигурации не удалось определить адрес сервера.";
            return;
        }

        try
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var addresses = await Dns.GetHostAddressesAsync(host).WaitAsync(TimeSpan.FromSeconds(4), ct);
                if (addresses.Length == 0)
                {
                    profile.Status = "DNS ERROR";
                    profile.Error = "DNS не вернул адресов.";
                    return;
                }
            }
            catch (Exception ex) when (ex is SocketException or TimeoutException)
            {
                profile.Status = "DNS ERROR";
                profile.Error = ex.Message;
                return;
            }

            profile.PingMs = await TryPingAsync(host, ct);
            profile.LatencyMs = profile.PingMs;

            if (!IsTcpCandidate(profile))
            {
                profile.TestAttempts = Attempts;
                if (profile.PingMs is not null)
                {
                    profile.Status = "HOST ONLY";
                    profile.Error = "UDP-сервис не проверен: подтверждена только доступность хоста.";
                }
                else
                {
                    profile.Status = "UDP UNVERIFIED";
                    profile.Error = "DNS работает, но ICMP не ответил. Для UDP нужна реальная проверка протокола.";
                }
                return;
            }

            if (port <= 0)
            {
                profile.Status = "NO PORT";
                profile.Error = "Для TCP-проверки не определён порт.";
                return;
            }

            var successful = new List<long>(Attempts);
            string? lastError = null;
            var timedOut = false;

            for (var attempt = 0; attempt < Attempts; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                profile.TestAttempts = attempt + 1;

                try
                {
                    var elapsed = await TcpConnectOnceAsync(host, port, ct);
                    successful.Add(elapsed);
                }
                catch (TimeoutException)
                {
                    timedOut = true;
                    lastError = $"TCP connect timeout ({TcpTimeout.TotalMilliseconds:0} ms).";
                }
                catch (SocketException ex)
                {
                    lastError = ex.Message;
                }
            }

            if (successful.Count > 0)
            {
                profile.TcpConnectMs = (long)Math.Round(successful.Average());
                profile.Status = "ENDPOINT OK";
                profile.Error = successful.Count < Attempts
                    ? $"Успешно {successful.Count}/{Attempts} TCP-попыток. Это ещё не проверка VPN-авторизации."
                    : "TCP endpoint отвечает. VPN-авторизация ещё не проверена.";
                return;
            }

            profile.Status = timedOut ? "TIMEOUT" : "ENDPOINT FAIL";
            profile.Error = lastError ?? "TCP endpoint не ответил.";
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            profile.Status = "TIMEOUT";
            profile.Error = "Превышено время предварительной проверки.";
        }
        catch (OperationCanceledException)
        {
            profile.Status = "Отменено";
        }
        catch (Exception ex)
        {
            profile.Status = "ERROR";
            profile.Error = ex.Message;
        }
    }

    private static async Task<long> TcpConnectOnceAsync(string host, int port, CancellationToken ct)
    {
        using var tcp = new TcpClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TcpTimeout);
        var sw = Stopwatch.StartNew();

        try
        {
            await tcp.ConnectAsync(host, port, timeout.Token);
            sw.Stop();
            return sw.ElapsedMilliseconds;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException();
        }
    }

    private static async Task<long?> TryPingAsync(string host, CancellationToken ct)
    {
        var values = new List<long>(Attempts);

        for (var i = 0; i < Attempts; i++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var ping = new Ping();
                var reply = await ping.SendPingAsync(host, 1800).WaitAsync(ct);
                if (reply.Status == IPStatus.Success)
                    values.Add(reply.RoundtripTime);
            }
            catch (PingException)
            {
                // ICMP may be blocked while the service itself is healthy.
            }
        }

        return values.Count == 0 ? null : (long)Math.Round(values.Average());
    }

    private static bool IsTcpCandidate(VpnProfile p)
    {
        if (p.Type.Equals("OpenVPN", StringComparison.OrdinalIgnoreCase))
            return p.Transport.Contains("tcp", StringComparison.OrdinalIgnoreCase);

        if (p.Type.Equals("WireGuard", StringComparison.OrdinalIgnoreCase) ||
            p.Type.Equals("AmneziaWG", StringComparison.OrdinalIgnoreCase) ||
            p.Type.Equals("HYSTERIA2", StringComparison.OrdinalIgnoreCase) ||
            p.Type.Equals("HY2", StringComparison.OrdinalIgnoreCase))
            return false;

        return !p.Transport.Equals("udp", StringComparison.OrdinalIgnoreCase);
    }
}
