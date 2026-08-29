using System.Net;

namespace GreyVPN.Services;

public static class ProxyEgressProbe
{
    private static readonly (string Url, bool CloudflareTrace)[] Targets =
    {
        ("https://1.1.1.1/cdn-cgi/trace", true),
        ("https://api.ipify.org", false),
        ("https://ifconfig.me/ip", false)
    };

    public static async Task<string> GetExitIpAsync(IWebProxy proxy, CancellationToken ct)
    {
        var failures = new List<string>();

        foreach (var target in Targets)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var handler = new HttpClientHandler
                {
                    Proxy = proxy,
                    UseProxy = true,
                    AllowAutoRedirect = false
                };
                using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(7) };
                using var response = await client.GetAsync(target.Url, HttpCompletionOption.ResponseContentRead, ct);
                response.EnsureSuccessStatusCode();
                var text = (await response.Content.ReadAsStringAsync(ct)).Trim();
                var ip = target.CloudflareTrace ? ParseCloudflareTrace(text) : text.Split('\n', '\r')[0].Trim();
                if (IPAddress.TryParse(ip, out _))
                    return ip;
                failures.Add($"{new Uri(target.Url).Host}: неверный IP");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                failures.Add($"{new Uri(target.Url).Host}: {Flatten(ex)}");
            }
        }

        throw new HttpRequestException("Ни один независимый HTTPS probe не подтвердил интернет через профиль: " + string.Join(" | ", failures));
    }

    private static string ParseCloudflareTrace(string text)
    {
        foreach (var line in text.Split('\n'))
            if (line.StartsWith("ip=", StringComparison.OrdinalIgnoreCase))
                return line[3..].Trim();
        return string.Empty;
    }

    private static string Flatten(Exception ex)
    {
        var parts = new List<string>();
        for (Exception? cur = ex; cur is not null && parts.Count < 3; cur = cur.InnerException)
            if (!string.IsNullOrWhiteSpace(cur.Message) && !parts.Contains(cur.Message)) parts.Add(cur.Message);
        var result = string.Join(" -> ", parts).Replace('\r', ' ').Replace('\n', ' ').Trim();
        return result.Length <= 220 ? result : result[..220];
    }
}
