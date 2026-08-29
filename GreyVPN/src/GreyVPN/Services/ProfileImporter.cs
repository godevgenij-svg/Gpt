using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GreyVPN.Models;

namespace GreyVPN.Services;

public static partial class ProfileImporter
{
    private const long MaxConfigBytes = 16L * 1024 * 1024;

    private static readonly HashSet<string> ProxySchemes = new(StringComparer.OrdinalIgnoreCase)
    {
        "vless", "vmess", "trojan", "hysteria2", "hy2", "ss", "socks", "http", "https"
    };

    public static IEnumerable<string> EnumerateSupportedFiles(string folder)
    {
        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".ovpn", ".conf", ".txt", ".json", ".yaml", ".yml", ".vpn"
        };
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false,
            AttributesToSkip = FileAttributes.ReparsePoint
        };

        return Directory.EnumerateFiles(folder, "*", options)
            .Where(path => extensions.Contains(Path.GetExtension(path)));
    }

    public static async Task<IReadOnlyList<VpnProfile>> ImportFilesAsync(IEnumerable<string> files, CancellationToken ct = default)
    {
        var result = new List<VpnProfile>();

        foreach (var file in files.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var info = new FileInfo(file);
                if (!info.Exists) throw new FileNotFoundException("Файл не найден.", file);
                if (info.Length > MaxConfigBytes)
                    throw new InvalidDataException($"Файл слишком большой для VPN-конфигурации: {info.Length:N0} байт (лимит {MaxConfigBytes:N0}).");

                var extension = Path.GetExtension(file).ToLowerInvariant();
                var text = await File.ReadAllTextAsync(file, Encoding.UTF8, ct);

                switch (extension)
                {
                    case ".ovpn":
                        result.Add(ParseOpenVpn(file, text));
                        break;
                    case ".conf":
                        result.Add(LooksLikeWireGuardFamily(text)
                            ? ParseWireGuardFamily(file, text)
                            : ParseGeneric(file, "CONF"));
                        break;
                    case ".txt":
                        result.AddRange(ParseUriList(file, text));
                        break;
                    case ".json":
                        result.Add(ParseGeneric(file, "Xray/JSON"));
                        break;
                    case ".yaml":
                    case ".yml":
                        result.Add(ParseGeneric(file, "Clash/Mihomo"));
                        break;
                    case ".vpn":
                        result.Add(ParseGeneric(file, "Amnezia backup/config"));
                        break;
                }
            }
            catch (Exception ex)
            {
                result.Add(new VpnProfile
                {
                    Name = Path.GetFileName(file),
                    Type = "Ошибка импорта",
                    SourcePath = file,
                    Status = "Ошибка",
                    Error = ex.Message
                });
            }
        }

        return Deduplicate(result);
    }

    public static void RefreshParsedFields(VpnProfile profile)
    {
        if (profile.PingMs is null && profile.LatencyMs is not null) profile.PingMs = profile.LatencyMs;
        if (string.IsNullOrWhiteSpace(profile.RawValue) || !profile.RawValue.Contains("://", StringComparison.Ordinal)) return;
        if (!TryParseProxyUri(profile.RawValue.Trim(), out var parsed)) return;
        if (!string.IsNullOrWhiteSpace(parsed.Name)) profile.Name = parsed.Name;
        profile.Type = parsed.Type;
        profile.Endpoint = parsed.Endpoint;
        profile.Transport = parsed.Transport;
    }

    private static VpnProfile ParseOpenVpn(string path, string text)
    {
        var remote = RemoteRegex().Match(text);
        var proto = ProtoRegex().Match(text).Groups[1].Value.Trim();
        var host = remote.Success ? remote.Groups[1].Value.Trim() : string.Empty;
        var port = remote.Success ? remote.Groups[2].Value.Trim() : string.Empty;
        return new VpnProfile
        {
            Name = Path.GetFileNameWithoutExtension(path),
            Type = "OpenVPN",
            Endpoint = JoinEndpoint(host, port),
            Transport = string.IsNullOrWhiteSpace(proto) ? "unknown" : proto,
            SourcePath = path,
            RawValue = string.Empty
        };
    }

    private static bool LooksLikeWireGuardFamily(string text) =>
        text.Contains("[Interface]", StringComparison.OrdinalIgnoreCase) &&
        PrivateKeyRegex().IsMatch(text) &&
        text.Contains("[Peer]", StringComparison.OrdinalIgnoreCase);

    private static VpnProfile ParseWireGuardFamily(string path, string text)
    {
        var endpointMatch = EndpointRegex().Match(text);
        var endpoint = endpointMatch.Success ? endpointMatch.Groups[1].Value.Trim() : string.Empty;
        var isAwg = AwgMarkerRegex().IsMatch(text);
        return new VpnProfile
        {
            Name = Path.GetFileNameWithoutExtension(path),
            Type = isAwg ? "AmneziaWG" : "WireGuard",
            Endpoint = endpoint,
            Transport = "udp",
            SourcePath = path,
            RawValue = string.Empty
        };
    }

    private static IEnumerable<VpnProfile> ParseUriList(string path, string text)
    {
        var parsed = ParseUriLines(text).ToList();
        if (parsed.Count == 0 && TryDecodeBase64(text.Trim(), out var decoded))
            parsed = ParseUriLines(decoded).ToList();

        if (parsed.Count == 0)
        {
            yield return ParseGeneric(path, "TXT");
            yield break;
        }

        foreach (var profile in parsed)
        {
            profile.SourcePath = path;
            yield return profile;
        }
    }

    private static IEnumerable<VpnProfile> ParseUriLines(string text)
    {
        foreach (var line in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                     .Select(x => x.Trim())
                     .Where(x => x.Length > 0 && !x.StartsWith('#')))
        {
            if (TryParseProxyUri(line, out var profile)) yield return profile;
        }
    }

    private static bool TryParseProxyUri(string raw, out VpnProfile profile)
    {
        profile = new VpnProfile();
        var schemeEnd = raw.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd <= 0) return false;
        var scheme = raw[..schemeEnd];
        if (!ProxySchemes.Contains(scheme)) return false;

        if (scheme.Equals("vmess", StringComparison.OrdinalIgnoreCase) && TryParseVmess(raw, out profile))
            return true;

        string name = scheme.ToUpperInvariant();
        string endpoint;
        if (Uri.TryCreate(raw, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host))
        {
            var port = uri.Port > 0 ? uri.Port : DefaultPortForScheme(scheme);
            endpoint = port > 0 ? JoinEndpoint(uri.Host, port.ToString()) : uri.Host;
            if (!string.IsNullOrWhiteSpace(uri.Fragment)) name = Uri.UnescapeDataString(uri.Fragment.TrimStart('#'));
        }
        else
        {
            endpoint = TryExtractAuthority(raw);
        }

        profile = new VpnProfile
        {
            Name = string.IsNullOrWhiteSpace(name) ? scheme.ToUpperInvariant() : name,
            Type = scheme.ToUpperInvariant(),
            Endpoint = endpoint,
            Transport = GuessTransport(raw),
            RawValue = raw
        };
        return true;
    }

    private static bool TryParseVmess(string raw, out VpnProfile profile)
    {
        profile = new VpnProfile();
        try
        {
            var payload = raw["vmess://".Length..];
            var hash = payload.IndexOf('#');
            if (hash >= 0) payload = payload[..hash];
            payload = payload.Trim();
            if (!TryDecodeBase64(payload, out var json)) return false;

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var host = GetJsonText(root, "add");
            var port = GetJsonText(root, "port");
            var name = GetJsonText(root, "ps");
            var transport = GetJsonText(root, "net");
            if (string.IsNullOrWhiteSpace(host)) return false;

            profile = new VpnProfile
            {
                Name = string.IsNullOrWhiteSpace(name) ? "VMESS" : name,
                Type = "VMESS",
                Endpoint = JoinEndpoint(host, port),
                Transport = string.IsNullOrWhiteSpace(transport) ? "tcp" : transport,
                RawValue = raw
            };
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string GetJsonText(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var value)) return string.Empty;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.GetRawText(),
            _ => string.Empty
        };
    }

    private static bool TryDecodeBase64(string value, out string text)
    {
        text = string.Empty;
        try
        {
            var normalized = WebUtility.UrlDecode(value).Trim().Replace('-', '+').Replace('_', '/');
            var mod = normalized.Length % 4;
            if (mod == 2) normalized += "==";
            else if (mod == 3) normalized += "=";
            else if (mod == 1) return false;
            text = Encoding.UTF8.GetString(Convert.FromBase64String(normalized));
            return true;
        }
        catch
        {
            return false;
        }
    }

    // Unsupported generic formats keep only their source path. This intentionally avoids
    // copying whole .vpn/.json/.yaml files (which may contain credentials) into profiles.json.
    private static VpnProfile ParseGeneric(string path, string type) => new()
    {
        Name = Path.GetFileNameWithoutExtension(path),
        Type = type,
        SourcePath = path,
        RawValue = string.Empty,
        Status = "Импортирован"
    };

    private static IReadOnlyList<VpnProfile> Deduplicate(IEnumerable<VpnProfile> profiles)
    {
        var result = new List<VpnProfile>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in profiles)
        {
            var identity = BuildIdentity(profile);
            if (seen.Add(identity)) result.Add(profile);
        }
        return result;
    }

    private static string BuildIdentity(VpnProfile p)
    {
        if (!string.IsNullOrWhiteSpace(p.RawValue) && p.RawValue.Contains("://", StringComparison.Ordinal))
            return $"uri|{p.RawValue.Trim()}";
        return $"file|{Path.GetFullPath(p.SourcePath)}|{p.Type}|{p.Name}";
    }

    public static bool TrySplitEndpoint(string endpoint, out string host, out int port)
    {
        host = string.Empty;
        port = 0;
        if (string.IsNullOrWhiteSpace(endpoint)) return false;
        var value = endpoint.Trim();

        if (value.StartsWith('['))
        {
            var close = value.IndexOf(']');
            if (close <= 0) return false;
            host = value[1..close];
            if (!IPAddress.TryParse(host, out _)) return false;
            if (close == value.Length - 1) return true;
            if (close + 2 < value.Length && value[close + 1] == ':' &&
                int.TryParse(value[(close + 2)..], out port) && port is > 0 and <= 65535) return true;
            return false;
        }

        if (IPAddress.TryParse(value, out _))
        {
            host = value;
            return true;
        }

        var colon = value.LastIndexOf(':');
        if (colon > 0 && int.TryParse(value[(colon + 1)..], out port))
        {
            if (port is < 1 or > 65535) return false;
            host = value[..colon];
            return !string.IsNullOrWhiteSpace(host);
        }

        host = value;
        port = 0;
        return true;
    }

    private static string TryExtractAuthority(string raw)
    {
        var at = raw.LastIndexOf('@');
        var start = at >= 0 ? at + 1 : raw.IndexOf("://", StringComparison.Ordinal) + 3;
        if (start < 3 || start >= raw.Length) return string.Empty;
        var endCandidates = new[] { raw.IndexOf('/', start), raw.IndexOf('?', start), raw.IndexOf('#', start) }
            .Where(x => x >= 0)
            .DefaultIfEmpty(raw.Length);
        return raw[start..endCandidates.Min()];
    }

    private static string GuessTransport(string raw)
    {
        if (raw.Contains("type=xhttp", StringComparison.OrdinalIgnoreCase) || raw.Contains("type=splithttp", StringComparison.OrdinalIgnoreCase)) return "xhttp";
        if (raw.Contains("type=httpupgrade", StringComparison.OrdinalIgnoreCase) || raw.Contains("type=http-upgrade", StringComparison.OrdinalIgnoreCase)) return "httpupgrade";
        if (raw.Contains("type=grpc", StringComparison.OrdinalIgnoreCase)) return "grpc";
        if (raw.Contains("type=ws", StringComparison.OrdinalIgnoreCase) || raw.Contains("type=websocket", StringComparison.OrdinalIgnoreCase)) return "ws";
        if (raw.Contains("type=tcp", StringComparison.OrdinalIgnoreCase) || raw.Contains("type=raw", StringComparison.OrdinalIgnoreCase)) return "tcp";
        if (raw.StartsWith("hysteria2://", StringComparison.OrdinalIgnoreCase) || raw.StartsWith("hy2://", StringComparison.OrdinalIgnoreCase)) return "udp";
        return "tcp";
    }

    private static int DefaultPortForScheme(string scheme) => scheme.ToLowerInvariant() switch
    {
        "vless" or "trojan" or "hysteria2" or "hy2" or "https" => 443,
        "http" => 80,
        _ => 0
    };

    private static string JoinEndpoint(string host, string port)
    {
        if (string.IsNullOrWhiteSpace(host)) return string.Empty;
        if (string.IsNullOrWhiteSpace(port)) return host;
        return host.Contains(':') && !host.StartsWith('[') ? $"[{host}]:{port}" : $"{host}:{port}";
    }

    [GeneratedRegex(@"(?im)^\s*remote\s+([^\s#;]+)(?:\s+(\d+))?")]
    private static partial Regex RemoteRegex();

    [GeneratedRegex(@"(?im)^\s*proto\s+([^\s#;]+)")]
    private static partial Regex ProtoRegex();

    [GeneratedRegex(@"(?im)^\s*PrivateKey\s*=\s*\S+")]
    private static partial Regex PrivateKeyRegex();

    [GeneratedRegex(@"(?im)^\s*Endpoint\s*=\s*([^\r\n#;]+)")]
    private static partial Regex EndpointRegex();

    [GeneratedRegex(@"(?im)^\s*(?:Jc|Jmin|Jmax|S1|S2|S3|S4|H1|H2|H3|H4|I1|I2|I3|I4|I5|HeaderProtectionKey|ContentPaddingAddition|RekeyAfterTime|RekeyTimeout|RejectAfterTime|KeepaliveTimeout|MaxHandshakeAttempts|RandomTrailers|DisableCookies)\s*=")]
    private static partial Regex AwgMarkerRegex();
}
