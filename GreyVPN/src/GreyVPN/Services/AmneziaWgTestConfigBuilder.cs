using System.Text;

namespace GreyVPN.Services;

public static class AmneziaWgTestConfigBuilder
{
    public const string ProbeAllowedIps = "1.1.1.1/32, 1.0.0.1/32";

    public static string Build(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
            throw new InvalidDataException("WG/AWG конфигурация пустая.");

        var lines = source.Replace("\r", string.Empty).Split('\n');
        var output = new List<string>(lines.Length + 4);
        var section = string.Empty;
        var peerCount = 0;
        var peerHasAllowedIps = false;

        void FinishPeerIfNeeded()
        {
            if (section.Equals("Peer", StringComparison.OrdinalIgnoreCase) && !peerHasAllowedIps)
                output.Add("AllowedIPs = " + ProbeAllowedIps);
        }

        foreach (var raw in lines)
        {
            var trimmed = raw.Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                FinishPeerIfNeeded();
                section = trimmed[1..^1].Trim();
                peerHasAllowedIps = false;
                if (section.Equals("Peer", StringComparison.OrdinalIgnoreCase)) peerCount++;
                output.Add(raw);
                continue;
            }

            var eq = trimmed.IndexOf('=');
            var key = eq > 0 ? trimmed[..eq].Trim() : string.Empty;

            if (section.Equals("Interface", StringComparison.OrdinalIgnoreCase) &&
                key.Equals("DNS", StringComparison.OrdinalIgnoreCase))
            {
                // A mass real-test must not replace the user's system DNS.
                continue;
            }

            if (section.Equals("Peer", StringComparison.OrdinalIgnoreCase) &&
                key.Equals("AllowedIPs", StringComparison.OrdinalIgnoreCase))
            {
                if (!peerHasAllowedIps)
                {
                    output.Add("AllowedIPs = " + ProbeAllowedIps);
                    peerHasAllowedIps = true;
                }
                continue;
            }

            output.Add(raw);
        }

        FinishPeerIfNeeded();

        if (peerCount == 0)
            throw new InvalidDataException("WG/AWG: секция [Peer] отсутствует.");
        if (peerCount != 1)
            throw new InvalidDataException("WG/AWG real-test v0.9 поддерживает один [Peer] на профиль; multi-peer профиль не изменяется автоматически.");

        var result = string.Join(Environment.NewLine, output).Trim() + Environment.NewLine;
        if (!result.Contains("[Interface]", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("WG/AWG: секция [Interface] отсутствует.");
        if (!result.Contains("PrivateKey", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("WG/AWG: PrivateKey отсутствует.");
        if (!result.Contains("Endpoint", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("WG/AWG: Endpoint отсутствует.");

        return result;
    }
}