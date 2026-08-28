using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using GreyVPN.Models;

namespace GreyVPN.Services;

public static partial class ProfileImporter
{
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

        return Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
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
                var extension = Path.GetExtension(file).ToLowerInvariant();
                var text = await File.ReadAllTextAsync(file, Encoding.UTF8, ct);

                switch (extension)
                {
                    case ".ovpn":
                        result.Add(ParseOpenVpn(file, text));
                        break;
                    case ".conf":
                        result.Add(ParseWireGuardFamily(file, text));
                        break;
                    case ".txt":
                        result.AddRange(ParseUriList(file, text));
                        break;
                    case ".json":
                        result.Add(ParseGeneric(file, "Xray/JSON", text));
                        break;
                    case ".yaml":
                    case ".yml":
                        result.Add(ParseGeneric(file, "Clash/Mihomo", text));
                        break;
                    case ".vpn":
                        result.Add(ParseGeneric(file, "Amnezia backup/config", text));
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
        var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .Where(x => x.Length > 0 && !x.StartsWith('#'));

        var any = false;
        foreach (var line in lines)
        {
            if (!TryParseProxyUri(line, out var profile))
                continue;

            any = true;
            profile.SourcePath = path;
            yield return profile;
        }

        if (!any)
            yield return ParseGeneric(path, "TXT", text);
    }

    private static bool TryParseProxyUri(string raw, out VpnProfile profile)
    {
        profile = new VpnProfile();

        var schemeEnd = raw.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd <= 0)
            return false;

        var scheme = raw[..schemeEnd];
        if (!ProxySchemes.Contains(scheme))
            return false;

        string name = scheme.ToUpperInvariant();
        string endpoint = string.Empty;

        if (Uri.TryCreate(raw, UriKind.Absolute, out var uri))
        {
            var host = uri.Host;
            var port = uri.IsDefaultPort ? string.Empty : uri.Port.ToString();
            endpoint = JoinEndpoint(host, port);
            if (!string.IsNullOrWhiteSpace(uri.Fragment))
                name = Uri.UnescapeDataString(uri.Fragment.TrimStart('#'));
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

    private static VpnProfile ParseGeneric(string path, string type, string text) => new()
    {
        Name = Path.GetFileNameWithoutExtension(path),
        Type = type,
        SourcePath = path,
        RawValue = text.Length <= 64_000 ? text : string.Empty,
        Status = "Импортирован"
    };

    private static IReadOnlyList<VpnProfile> Deduplicate(IEnumerable<VpnProfile> profiles)
    {
        var result = new List<VpnProfile>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var profile in profiles)
        {
            var identity = BuildIdentity(profile);
            if (seen.Add(identity))
                result.Add(profile);
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
        if (string.IsNullOrWhiteSpace(endpoint))
            return false;

        var value = endpoint.Trim();
        if (value.StartsWith('['))
        {
            var close = value.IndexOf(']');
            if (close <= 0)
                return false;

            host = value[1..close];
            if (close + 2 <= value.Length && value[close + 1] == ':' && int.TryParse(value[(close + 2)..], out port))
                return true;

            return IPAddress.TryParse(host, out _);
        }

        var colon = value.LastIndexOf(':');
        if (colon > 0 && int.TryParse(value[(colon + 1)..], out port))
        {
            host = value[..colon];
            return !string.IsNullOrWhiteSpace(host);
        }

        host = value;
        return true;
    }

    private static string TryExtractAuthority(string raw)
    {
        var at = raw.LastIndexOf('@');
        var start = at >= 0 ? at + 1 : raw.IndexOf("://", StringComparison.Ordinal) + 3;
        if (start < 3 || start >= raw.Length)
            return string.Empty;

        var endCandidates = new[] { raw.IndexOf('/', start), raw.IndexOf('?', start), raw.IndexOf('#', start) }
            .Where(x => x >= 0)
            .DefaultIfEmpty(raw.Length);
        var end = endCandidates.Min();
        return raw[start..end];
    }

    private static string GuessTransport(string raw)
    {
        if (raw.Contains("type=grpc", StringComparison.OrdinalIgnoreCase)) return "grpc";
        if (raw.Contains("type=ws", StringComparison.OrdinalIgnoreCase)) return "ws";
        if (raw.Contains("type=tcp", StringComparison.OrdinalIgnoreCase)) return "tcp";
        if (raw.StartsWith("hysteria2://", StringComparison.OrdinalIgnoreCase) || raw.StartsWith("hy2://", StringComparison.OrdinalIgnoreCase)) return "udp";
        return "tcp";
    }

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

    [GeneratedRegex(@"(?im)^\s*Endpoint\s*=\s*([^\r\n#;]+)")]
    private static partial Regex EndpointRegex();

    [GeneratedRegex(@"(?im)^\s*(?:Jc|Jmin|Jmax|S1|S2|H1|H2|H3|H4|I1|I2|I3|I4|I5)\s*=")]
    private static partial Regex AwgMarkerRegex();
}
