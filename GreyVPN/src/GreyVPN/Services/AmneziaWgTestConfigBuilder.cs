namespace GreyVPN.Services;

public static class AmneziaWgTestConfigBuilder
{
    public static string Build(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
            throw new InvalidDataException("WG/AWG конфигурация пустая.");

        var lines = source.Replace("\r", string.Empty).Split('\n');
        var output = new List<string>(lines.Length);
        var section = string.Empty;
        var peerCount = 0;

        foreach (var raw in lines)
        {
            var trimmed = raw.Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                section = trimmed[1..^1].Trim();
                if (section.Equals("Peer", StringComparison.OrdinalIgnoreCase)) peerCount++;
                output.Add(raw);
                continue;
            }

            // The real-test must exercise the same tunnel semantics as the official client.
            // In particular, keep DNS and AllowedIPs unchanged: WireGuard/AmneziaWG on Windows
            // may install full-tunnel routing/firewall rules based on those fields.
            output.Add(raw);
        }

        if (peerCount == 0)
            throw new InvalidDataException("WG/AWG: секция [Peer] отсутствует.");
        if (peerCount != 1)
            throw new InvalidDataException("WG/AWG real-test поддерживает один [Peer] на профиль.");

        var result = string.Join(Environment.NewLine, output).Trim() + Environment.NewLine;
        if (!result.Contains("[Interface]", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("WG/AWG: секция [Interface] отсутствует.");
        if (!result.Contains("PrivateKey", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("WG/AWG: PrivateKey отсутствует.");
        if (!result.Contains("AllowedIPs", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("WG/AWG: AllowedIPs отсутствует.");
        if (!result.Contains("Endpoint", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("WG/AWG: Endpoint отсутствует.");

        return result;
    }
}
