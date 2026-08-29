using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GreyVPN.Models;

namespace GreyVPN.Services;

public static class SingBoxConfigBuilder
{
    private static readonly JsonSerializerOptions PrettyJson = new(JsonSerializerOptions.Default)
    {
        WriteIndented = true
    };

    public static bool Supports(VpnProfile profile)
    {
        var type = profile.Type.ToUpperInvariant();
        return type is "HYSTERIA2" or "HY2" or "WIREGUARD" or "SS" or "SOCKS" or "HTTP" or "HTTPS";
    }

    public static bool TryBuild(VpnProfile profile, int localPort, out string json, out string error)
    {
        json = string.Empty;
        error = string.Empty;

        if (!Supports(profile))
        {
            error = "Для этого протокола real-test через sing-box не реализован.";
            return false;
        }

        if (localPort is < 1 or > 65535)
        {
            error = "Некорректный локальный порт real-test.";
            return false;
        }

        try
        {
            var type = profile.Type.ToUpperInvariant();
            var isEndpoint = type == "WIREGUARD";
            JsonObject target = type switch
            {
                "HYSTERIA2" or "HY2" => BuildHysteria2(RequireRawUri(profile)),
                "WIREGUARD" => BuildWireGuard(ReadConfigText(profile)),
                "SS" => BuildShadowsocks(RequireRawUri(profile)),
                "SOCKS" => BuildSocks(RequireRawUri(profile)),
                "HTTP" or "HTTPS" => BuildHttp(RequireRawUri(profile)),
                _ => throw new InvalidDataException("Формат профиля не поддержан конвертером.")
            };

            target["tag"] = "proxy";
            var root = new JsonObject
            {
                ["log"] = new JsonObject
                {
                    ["level"] = "warn",
                    ["timestamp"] = true
                },
                ["inbounds"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "mixed",
                        ["tag"] = "local-test",
                        ["listen"] = "127.0.0.1",
                        ["listen_port"] = localPort
                    }
                },
                ["route"] = new JsonObject
                {
                    ["auto_detect_interface"] = true,
                    ["final"] = "proxy"
                }
            };

            if (isEndpoint)
                root["endpoints"] = new JsonArray(target);
            else
                root["outbounds"] = new JsonArray(target);

            json = root.ToJsonString(PrettyJson);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static string RequireRawUri(VpnProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.RawValue) || !profile.RawValue.Contains("://", StringComparison.Ordinal))
            throw new InvalidDataException("В базе нет исходной URI-ссылки этого профиля.");
        return profile.RawValue.Trim();
    }

    private static string ReadConfigText(VpnProfile profile)
    {
        if (!string.IsNullOrWhiteSpace(profile.RawValue) && !profile.RawValue.Contains("://", StringComparison.Ordinal))
            return profile.RawValue;

        if (!string.IsNullOrWhiteSpace(profile.SourcePath) && File.Exists(profile.SourcePath))
            return File.ReadAllText(profile.SourcePath, Encoding.UTF8);

        throw new InvalidDataException("Исходный WireGuard .conf не найден. Импортируйте файл заново или верните его по исходному пути.");
    }

    private static JsonObject BuildHysteria2(string raw)
    {
        var expectedScheme = raw.StartsWith("hy2://", StringComparison.OrdinalIgnoreCase) ? "hy2" : "hysteria2";
        var uri = RequireUri(raw, expectedScheme);
        var q = ParseQuery(uri.Query);
        var password = Uri.UnescapeDataString(uri.UserInfo);
        if (password.StartsWith(':')) password = password[1..];
        if (string.IsNullOrWhiteSpace(password))
            throw new InvalidDataException("Hysteria2: отсутствует пароль.");

        var o = BaseServer("hysteria2", uri, 443);
        o["password"] = password;
        o["tls"] = BuildTls(q, uri.Host, forceTls: true);

        var obfs = Get(q, "obfs");
        if (!string.IsNullOrWhiteSpace(obfs))
        {
            if (!obfs.Equals("salamander", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Hysteria2 obfs '{obfs}' не поддерживается текущим конвертером.");

            var obfsPassword = Get(q, "obfs-password", Get(q, "obfs_password"));
            if (string.IsNullOrWhiteSpace(obfsPassword))
                throw new InvalidDataException("Hysteria2 salamander: отсутствует obfs password.");

            o["obfs"] = new JsonObject
            {
                ["type"] = "salamander",
                ["password"] = obfsPassword
            };
        }

        return o;
    }

    private static JsonObject BuildWireGuard(string text)
    {
        var privateKey = string.Empty;
        var addresses = new List<string>();
        var mtu = 0;
        var peers = new List<WgPeer>();
        WgPeer? currentPeer = null;
        var section = string.Empty;

        foreach (var rawLine in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';')) continue;

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1].Trim();
                if (section.Equals("Peer", StringComparison.OrdinalIgnoreCase))
                {
                    currentPeer = new WgPeer();
                    peers.Add(currentPeer);
                }
                continue;
            }

            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();

            if (section.Equals("Interface", StringComparison.OrdinalIgnoreCase))
            {
                if (key.Equals("PrivateKey", StringComparison.OrdinalIgnoreCase)) privateKey = value;
                else if (key.Equals("Address", StringComparison.OrdinalIgnoreCase)) AddCsv(addresses, value);
                else if (key.Equals("MTU", StringComparison.OrdinalIgnoreCase)) int.TryParse(value, out mtu);
            }
            else if (section.Equals("Peer", StringComparison.OrdinalIgnoreCase) && currentPeer is not null)
            {
                if (key.Equals("PublicKey", StringComparison.OrdinalIgnoreCase)) currentPeer.PublicKey = value;
                else if (key.Equals("PresharedKey", StringComparison.OrdinalIgnoreCase)) currentPeer.PreSharedKey = value;
                else if (key.Equals("Endpoint", StringComparison.OrdinalIgnoreCase)) currentPeer.Endpoint = value;
                else if (key.Equals("AllowedIPs", StringComparison.OrdinalIgnoreCase)) AddCsv(currentPeer.AllowedIps, value);
                else if (key.Equals("PersistentKeepalive", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out var keepalive)) currentPeer.PersistentKeepalive = keepalive;
            }
        }

        if (string.IsNullOrWhiteSpace(privateKey))
            throw new InvalidDataException("WireGuard: отсутствует Interface/PrivateKey.");
        if (addresses.Count == 0)
            throw new InvalidDataException("WireGuard: отсутствует Interface/Address.");
        if (peers.Count == 0)
            throw new InvalidDataException("WireGuard: отсутствует секция Peer.");

        var peerNodes = new JsonArray();
        foreach (var peer in peers)
        {
            if (string.IsNullOrWhiteSpace(peer.PublicKey))
                throw new InvalidDataException("WireGuard: у Peer отсутствует PublicKey.");
            if (string.IsNullOrWhiteSpace(peer.Endpoint) ||
                !ProfileImporter.TrySplitEndpoint(peer.Endpoint, out var host, out var port) || port <= 0)
                throw new InvalidDataException($"WireGuard: некорректный Peer/Endpoint '{peer.Endpoint}'.");
            if (peer.AllowedIps.Count == 0)
                throw new InvalidDataException("WireGuard: у Peer отсутствует AllowedIPs.");

            var node = new JsonObject
            {
                ["address"] = host,
                ["port"] = port,
                ["public_key"] = peer.PublicKey,
                ["allowed_ips"] = ToJsonArray(peer.AllowedIps)
            };
            if (!string.IsNullOrWhiteSpace(peer.PreSharedKey)) node["pre_shared_key"] = peer.PreSharedKey;
            if (peer.PersistentKeepalive > 0) node["persistent_keepalive_interval"] = peer.PersistentKeepalive;
            peerNodes.Add(node);
        }

        var endpoint = new JsonObject
        {
            ["type"] = "wireguard",
            ["system"] = false,
            ["address"] = ToJsonArray(addresses),
            ["private_key"] = privateKey,
            ["peers"] = peerNodes
        };
        if (mtu > 0) endpoint["mtu"] = mtu;
        return endpoint;
    }

    private static JsonObject BuildShadowsocks(string raw)
    {
        if (!raw.StartsWith("ss://", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Некорректная Shadowsocks URI.");

        var body = raw["ss://".Length..];
        var hash = body.IndexOf('#');
        if (hash >= 0) body = body[..hash];

        var query = string.Empty;
        var qmark = body.IndexOf('?');
        if (qmark >= 0)
        {
            query = body[qmark..];
            body = body[..qmark];
        }

        string credentials;
        string authority;
        var at = body.LastIndexOf('@');
        if (at >= 0)
        {
            var encodedCredentials = Uri.UnescapeDataString(body[..at]);
            credentials = encodedCredentials.Contains(':') ? encodedCredentials : DecodeBase64(encodedCredentials);
            authority = body[(at + 1)..];
        }
        else
        {
            var decoded = DecodeBase64(Uri.UnescapeDataString(body));
            at = decoded.LastIndexOf('@');
            if (at <= 0)
                throw new InvalidDataException("Shadowsocks: не удалось разобрать method/password/server.");
            credentials = decoded[..at];
            authority = decoded[(at + 1)..];
        }

        var colon = credentials.IndexOf(':');
        if (colon <= 0)
            throw new InvalidDataException("Shadowsocks: отсутствуют method/password.");

        var method = credentials[..colon];
        var password = credentials[(colon + 1)..];
        if (!ProfileImporter.TrySplitEndpoint(authority, out var host, out var port) || port <= 0)
            throw new InvalidDataException("Shadowsocks: некорректный server:port.");

        var o = new JsonObject
        {
            ["type"] = "shadowsocks",
            ["server"] = host,
            ["server_port"] = port,
            ["method"] = method,
            ["password"] = password
        };

        var pluginSpec = Get(ParseQuery(query), "plugin");
        if (!string.IsNullOrWhiteSpace(pluginSpec))
        {
            var split = pluginSpec.Split(';', 2);
            o["plugin"] = split[0];
            if (split.Length > 1) o["plugin_opts"] = split[1];
        }

        return o;
    }

    private static JsonObject BuildSocks(string raw)
    {
        var uri = RequireUri(raw, "socks");
        var o = BaseServer("socks", uri);
        o["version"] = "5";
        ApplyUserInfo(o, uri.UserInfo);
        return o;
    }

    private static JsonObject BuildHttp(string raw)
    {
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) ||
            (!uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) &&
             !uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Некорректная HTTP/HTTPS proxy URI.");

        var o = BaseServer("http", uri, uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? 443 : 80);
        ApplyUserInfo(o, uri.UserInfo);
        if (uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
            o["tls"] = BuildTls(ParseQuery(uri.Query), uri.Host, forceTls: true);
        return o;
    }

    private static void ApplyUserInfo(JsonObject o, string userInfo)
    {
        if (string.IsNullOrWhiteSpace(userInfo)) return;
        var decoded = Uri.UnescapeDataString(userInfo);
        var colon = decoded.IndexOf(':');
        if (colon < 0)
        {
            o["username"] = decoded;
            return;
        }
        o["username"] = decoded[..colon];
        o["password"] = decoded[(colon + 1)..];
    }

    private static JsonObject BaseServer(string type, Uri uri, int fallbackPort = 0)
    {
        if (string.IsNullOrWhiteSpace(uri.Host))
            throw new InvalidDataException($"{type}: отсутствует server.");
        var port = uri.Port > 0 ? uri.Port : fallbackPort;
        if (port is < 1 or > 65535)
            throw new InvalidDataException($"{type}: отсутствует или некорректен server_port.");
        return new JsonObject
        {
            ["type"] = type,
            ["server"] = uri.Host,
            ["server_port"] = port
        };
    }

    private static JsonObject BuildTls(IReadOnlyDictionary<string, string> q, string serverHost, bool forceTls)
    {
        var tls = new JsonObject
        {
            ["enabled"] = forceTls
        };

        var sni = Get(q, "sni", Get(q, "peer", serverHost));
        if (!string.IsNullOrWhiteSpace(sni)) tls["server_name"] = sni;
        if (IsTrue(Get(q, "insecure")) || IsTrue(Get(q, "allowInsecure"))) tls["insecure"] = true;

        return tls;
    }

    private static Uri RequireUri(string raw, string expected)
    {
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) ||
            !uri.Scheme.Equals(expected, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(uri.Host))
            throw new InvalidDataException($"Некорректная {expected} URI.");
        return uri;
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var p = part.Split('=', 2);
            var key = WebUtility.UrlDecode(p[0]);
            var value = p.Length > 1 ? WebUtility.UrlDecode(p[1]) : string.Empty;
            result[key] = value;
        }
        return result;
    }

    private static void AddCsv(List<string> target, string value)
    {
        foreach (var item in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (item.Length > 0) target.Add(item);
    }

    private static JsonArray ToJsonArray(IEnumerable<string> values)
    {
        var result = new JsonArray();
        foreach (var value in values) result.Add(value);
        return result;
    }

    private static string Get(IReadOnlyDictionary<string, string> q, string key, string fallback = "") =>
        q.TryGetValue(key, out var value) ? value : fallback;

    private static bool IsTrue(string value) =>
        value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("yes", StringComparison.OrdinalIgnoreCase);

    private static string DecodeBase64(string value)
    {
        var s = value.Trim().Replace('-', '+').Replace('_', '/');
        var mod = s.Length % 4;
        if (mod == 2) s += "==";
        else if (mod == 3) s += "=";
        else if (mod == 1) throw new InvalidDataException("Некорректный Base64.");

        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(s));
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException("Некорректный Base64.", ex);
        }
    }

    private sealed class WgPeer
    {
        public string PublicKey { get; set; } = string.Empty;
        public string PreSharedKey { get; set; } = string.Empty;
        public string Endpoint { get; set; } = string.Empty;
        public List<string> AllowedIps { get; } = new();
        public int PersistentKeepalive { get; set; }
    }
}
