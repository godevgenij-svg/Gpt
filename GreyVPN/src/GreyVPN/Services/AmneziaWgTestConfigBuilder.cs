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

            var eq = trimmed.IndexOf('=');
            var key = eq > 0 ? trimmed[..eq].Trim() : string.Empty;

            if (section.Equals("Interface", StringComparison.OrdinalIgnoreCase) &&
                key.Equals("DNS", StringComparison.OrdinalIgnoreCase))
            {
                // The checker probes numeric IPs and must not replace the user's system DNS.
                continue;
            }

            // v0.9.1 intentionally preserves the profile's original AllowedIPs. Rewriting them
            // to two /32 routes caused WSAEHOSTUNREACH on Windows even while the official
            // AmneziaWG tunnel service was RUNNING. The real checker must exercise the same
            // routing semantics the profile would use in the official client.
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
