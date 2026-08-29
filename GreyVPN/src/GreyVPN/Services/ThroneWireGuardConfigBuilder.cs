using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using GreyVPN.Models;

namespace GreyVPN.Services;

public sealed record ThroneWireGuardConfig(string Json, string ResolvedEndpoint, bool IsAmnezia);

public static class ThroneWireGuardConfigBuilder
{
    private static readonly HashSet<string> AmneziaKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "Jc", "Jmin", "Jmax", "S1", "S2", "S3", "S4", "H1", "H2", "H3", "H4",
        "I1", "I2", "I3", "I4", "I5", "HeaderProtectionKey", "ContentPaddingAddition",
        "RekeyAfterTime", "RekeyTimeout", "RejectAfterTime", "KeepaliveTimeout",
        "MaxHandshakeAttempts", "RandomTrailers", "DisableCookies"
    };

    public static bool Supports(VpnProfile profile) =>
        profile.Type.Equals("WireGuard", StringComparison.OrdinalIgnoreCase) ||
        profile.Type.Equals("AmneziaWG", StringComparison.OrdinalIgnoreCase);

    public static async Task<ThroneWireGuardConfig> BuildAsync(VpnProfile profile, int mixedPort, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(profile.SourcePath) || !File.Exists(profile.SourcePath))
            throw new FileNotFoundException("Исходный .conf не найден. Импортируй профиль заново.", profile.SourcePath);

        var text = await File.ReadAllTextAsync(profile.SourcePath, ct).ConfigureAwait(false);
        return await BuildFromTextAsync(text, mixedPort, resolveEndpoint: true, ct).ConfigureAwait(false);
    }

    public static async Task<ThroneWireGuardConfig> BuildFromTextAsync(
        string text,
        int mixedPort,
        bool resolveEndpoint,
        CancellationToken ct = default)
    {
        if (mixedPort is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(mixedPort));

        var parsed = Parse(text);
        if (!ProfileImporter.TrySplitEndpoint(parsed.Endpoint, out var endpointHost, out var endpointPort) || endpointPort <= 0)
            throw new InvalidDataException("WireGuard Endpoint отсутствует или имеет неверный порт.");

        var resolvedHost = endpointHost;
        if (resolveEndpoint && !IPAddress.TryParse(endpointHost, out _))
        {
            var addresses = await Dns.GetHostAddressesAsync(endpointHost).WaitAsync(ct).ConfigureAwait(false);
            var selected = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                           ?? addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetworkV6)
                           ?? throw new InvalidDataException($"DNS не вернул адрес для WireGuard endpoint {endpointHost}.");
            resolvedHost = selected.ToString();
        }

        var peer = new Dictionary<string, object?>
        {
            ["address"] = resolvedHost,
            ["port"] = endpointPort,
            ["public_key"] = parsed.PublicKey,
            ["allowed_ips"] = new[] { "0.0.0.0/0", "::/0" }
        };
        if (!string.IsNullOrWhiteSpace(parsed.PresharedKey)) peer["pre_shared_key"] = parsed.PresharedKey;
        if (!string.IsNullOrWhiteSpace(parsed.PersistentKeepalive))
        {
            if (int.TryParse(parsed.PersistentKeepalive, out var keepalive) && keepalive > 0)
                peer["persistent_keepalive_interval"] = keepalive;
            else if (System.Text.RegularExpressions.Regex.IsMatch(parsed.PersistentKeepalive, @"^\d+-\d+$"))
                peer["persistent_keepalive_interval"] = parsed.PersistentKeepalive;
        }

        var endpoint = new Dictionary<string, object?>
        {
            ["type"] = "wireguard",
            ["tag"] = "proxy",
            ["system"] = false,
            ["address"] = parsed.Addresses,
            ["private_key"] = parsed.PrivateKey,
            ["peers"] = new[] { peer }
        };
        if (parsed.Mtu > 0) endpoint["mtu"] = parsed.Mtu;

        var awg = BuildAmneziaObject(parsed.InterfaceValues);
        if (awg.Count > 0) endpoint["amnezia_wg"] = awg;

        var config = new Dictionary<string, object?>
        {
            ["log"] = new Dictionary<string, object?> { ["level"] = "info", ["timestamp"] = true },
            ["inbounds"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["type"] = "mixed",
                    ["tag"] = "mixed-in",
                    ["listen"] = "127.0.0.1",
                    ["listen_port"] = mixedPort
                }
            },
            ["endpoints"] = new object[] { endpoint },
            ["outbounds"] = new object[]
            {
                new Dictionary<string, object?> { ["type"] = "direct", ["tag"] = "direct" }
            },
            ["route"] = new Dictionary<string, object?>
            {
                ["final"] = "proxy",
                ["auto_detect_interface"] = true
            }
        };

        return new ThroneWireGuardConfig(
            JsonSerializer.Serialize(config),
            FormatEndpoint(resolvedHost, endpointPort),
            awg.Count > 0);
    }

    public static bool LooksLikeAmnezia(string text)
    {
        var parsed = ParseIni(text);
        return parsed.Interface.Keys.Any(AmneziaKeys.Contains);
    }

    private static ParsedWireGuard Parse(string text)
    {
        var ini = ParseIni(text);
        if (!ini.Interface.TryGetValue("PrivateKey", out var privateKey) || string.IsNullOrWhiteSpace(privateKey))
            throw new InvalidDataException("WireGuard PrivateKey отсутствует.");
        if (!ini.Interface.TryGetValue("Address", out var addressText) || string.IsNullOrWhiteSpace(addressText))
            throw new InvalidDataException("WireGuard Address отсутствует.");
        if (ini.Peers.Count == 0) throw new InvalidDataException("WireGuard [Peer] отсутствует.");

        var peer = ini.Peers[0];
        if (!peer.TryGetValue("PublicKey", out var publicKey) || string.IsNullOrWhiteSpace(publicKey))
            throw new InvalidDataException("WireGuard peer PublicKey отсутствует.");
        if (!peer.TryGetValue("Endpoint", out var endpoint) || string.IsNullOrWhiteSpace(endpoint))
            throw new InvalidDataException("WireGuard peer Endpoint отсутствует.");

        var addresses = addressText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(FixCidr)
            .ToArray();
        if (addresses.Length == 0) throw new InvalidDataException("WireGuard Address пуст.");

        var mtu = ini.Interface.TryGetValue("MTU", out var mtuText) && int.TryParse(mtuText, out var m) ? m : 0;
        return new ParsedWireGuard(
            privateKey.Trim(), addresses, mtu, endpoint.Trim(), publicKey.Trim(),
            peer.GetValueOrDefault("PresharedKey", string.Empty).Trim(),
            peer.GetValueOrDefault("PersistentKeepalive", string.Empty).Trim(),
            ini.Interface);
    }

    private static IniDocument ParseIni(string text)
    {
        var interfaceValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var peers = new List<Dictionary<string, string>>();
        Dictionary<string, string>? current = null;

        foreach (var rawLine in text.Replace("\r", string.Empty).Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';')) continue;
            if (line.Equals("[Interface]", StringComparison.OrdinalIgnoreCase))
            {
                current = interfaceValues;
                continue;
            }
            if (line.Equals("[Peer]", StringComparison.OrdinalIgnoreCase))
            {
                current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                peers.Add(current);
                continue;
            }
            if (current is null) continue;
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();
            if (key.Length > 0) current[key] = value;
        }
        return new IniDocument(interfaceValues, peers);
    }

    private static Dictionary<string, object?> BuildAmneziaObject(IReadOnlyDictionary<string, string> values)
    {
        var result = new Dictionary<string, object?>();
        AddPositiveInt(values, result, "Jc", "jc");
        AddPositiveInt(values, result, "Jmin", "jmin");
        AddPositiveInt(values, result, "Jmax", "jmax");
        AddPositiveInt(values, result, "S1", "s1");
        AddPositiveInt(values, result, "S2", "s2");
        AddPositiveInt(values, result, "S3", "s3");
        AddPositiveInt(values, result, "S4", "s4");
        AddString(values, result, "H1", "h1");
        AddString(values, result, "H2", "h2");
        AddString(values, result, "H3", "h3");
        AddString(values, result, "H4", "h4");
        AddString(values, result, "I1", "i1");
        AddString(values, result, "I2", "i2");
        AddString(values, result, "I3", "i3");
        AddString(values, result, "I4", "i4");
        AddString(values, result, "I5", "i5");
        AddString(values, result, "HeaderProtectionKey", "header_protection_key");
        AddRange(values, result, "ContentPaddingAddition", "content_padding_addition");
        AddRange(values, result, "RekeyAfterTime", "rekey_after_time");
        AddRange(values, result, "RekeyTimeout", "rekey_timeout");
        AddRange(values, result, "RejectAfterTime", "reject_after_time");
        AddRange(values, result, "KeepaliveTimeout", "keepalive_timeout");
        AddRange(values, result, "MaxHandshakeAttempts", "max_handshake_attempts");
        AddBool(values, result, "RandomTrailers", "random_trailers");
        AddBool(values, result, "DisableCookies", "disable_cookies");
        return result;
    }

    private static void AddPositiveInt(IReadOnlyDictionary<string, string> src, IDictionary<string, object?> dst, string input, string output)
    {
        if (src.TryGetValue(input, out var value) && int.TryParse(value, out var number) && number > 0) dst[output] = number;
    }

    private static void AddString(IReadOnlyDictionary<string, string> src, IDictionary<string, object?> dst, string input, string output)
    {
        if (src.TryGetValue(input, out var value) && !string.IsNullOrWhiteSpace(value)) dst[output] = value.Trim();
    }

    private static void AddRange(IReadOnlyDictionary<string, string> src, IDictionary<string, object?> dst, string input, string output)
    {
        if (!src.TryGetValue(input, out var value) || string.IsNullOrWhiteSpace(value)) return;
        value = value.Trim();
        dst[output] = int.TryParse(value, out var number) ? number : value;
    }

    private static void AddBool(IReadOnlyDictionary<string, string> src, IDictionary<string, object?> dst, string input, string output)
    {
        if (!src.TryGetValue(input, out var value)) return;
        if (value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase) || value.Trim() == "1") dst[output] = true;
    }

    private static string FixCidr(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Contains('/')) return trimmed;
        return trimmed.Contains(':') ? trimmed + "/128" : trimmed + "/32";
    }

    private static string FormatEndpoint(string host, int port) => host.Contains(':') ? $"[{host}]:{port}" : $"{host}:{port}";

    private sealed record IniDocument(
        Dictionary<string, string> Interface,
        List<Dictionary<string, string>> Peers);

    private sealed record ParsedWireGuard(
        string PrivateKey,
        string[] Addresses,
        int Mtu,
        string Endpoint,
        string PublicKey,
        string PresharedKey,
        string PersistentKeepalive,
        Dictionary<string, string> InterfaceValues);
}
