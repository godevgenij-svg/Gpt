using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using GreyVPN.Models;

namespace GreyVPN.Services;

public static class ProfileTester
{
    public static async Task TestAsync(VpnProfile profile, CancellationToken ct)
    {
        profile.Status = "Проверка";
        profile.Error = string.Empty;
        profile.LatencyMs = null;
        profile.LastTested = DateTimeOffset.Now;

        if (!ProfileImporter.TrySplitEndpoint(profile.Endpoint, out var host, out var port) || string.IsNullOrWhiteSpace(host))
        {
            profile.Status = "Нет endpoint";
            profile.Error = "Из конфигурации не удалось определить адрес сервера.";
            return;
        }

        try
        {
            ct.ThrowIfCancellationRequested();

            var sw = Stopwatch.StartNew();
            if (port > 0 && IsTcpCandidate(profile))
            {
                using var tcp = new TcpClient();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                await tcp.ConnectAsync(host, port, timeout.Token);
                sw.Stop();
                profile.LatencyMs = sw.ElapsedMilliseconds;
                profile.Status = "TCP доступен";
                return;
            }

            using var ping = new Ping();
            var reply = await ping.SendPingAsync(host, 4000).WaitAsync(ct);
            sw.Stop();

            if (reply.Status == IPStatus.Success)
            {
                profile.LatencyMs = reply.RoundtripTime;
                profile.Status = "Хост доступен";
            }
            else
            {
                profile.Status = "Нет ответа";
                profile.Error = reply.Status.ToString();
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            profile.Status = "Timeout";
            profile.Error = "Превышено время предварительной проверки.";
        }
        catch (OperationCanceledException)
        {
            profile.Status = "Отменено";
        }
        catch (Exception ex)
        {
            profile.Status = "Недоступен";
            profile.Error = ex.Message;
        }
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

        return true;
    }
}
