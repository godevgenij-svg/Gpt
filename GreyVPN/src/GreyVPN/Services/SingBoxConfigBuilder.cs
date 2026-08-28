using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GreyVPN.Models;

namespace GreyVPN.Services;

public static class SingBoxConfigBuilder
{
    public static bool Supports(VpnProfile profile) =>
        profile.Type.Equals("VLESS", StringComparison.OrdinalIgnoreCase) ||
        profile.Type.Equals("VMESS", StringComparison.OrdinalIgnoreCase) ||
        profile.Type.Equals("TROJAN", StringComparison.OrdinalIgnoreCase) ||
        profile.Type.Equals("HYSTERIA2", StringComparison.OrdinalIgnoreCase) ||
        profile.Type.Equals("HY2", StringComparison.OrdinalIgnoreCase) ||
        profile.Type.Equals("WIREGUARD", StringComparison.OrdinalIgnoreCase) ||
        profile.Type.Equals("SS", StringComparison.OrdinalIgnoreCase) ||
        profile.Type.Equals("SOCKS", StringComparison.OrdinalIgnoreCase) ||
        profile.Type.Equals("HTTP", StringComparison.OrdinalIgnoreCase) ||
        profile.Type.Equals("HTTPS", StringComparison.OrdinalIgnoreCase);

    public static bool TryBuild(VpnProfile profile, int localPort, out string json, out string error)
    {
        json = string.Empty;
        error = string.Empty;
        if (!Supports(profile))
        {
            error = "Для этого протокола реальный тест через стабильный sing-box пока не реализован.";
            return false;
        }

        try
        {
            var isEndpoint = profile.Type.Equals("WIREGUARD", StringComparison.OrdinalIgnoreCase);
            JsonObject? target = profile.Type.ToUpperInvariant() switch
            {
                "VLESS" => BuildVless(RequireRawUri(profile)),
                "VMESS" => BuildVmess(RequireRawUri(profile)),
                "TROJAN" => BuildTrojan(RequireRawUri(profile)),
                "HYSTERIA2" or "HY2" => BuildHysteria2(RequireRawUri(profile)),
                "WIREGUARD" => BuildWireGuard(ReadConfigText(profile)),
                "SS" => BuildShadowsocks(RequireRawUri(profile)),
                "SOCKS" => BuildSocks(RequireRawUri(profile)),
                "HTTP" or "HTTPS" => BuildHttp(RequireRawUri(profile)),
                _ => null
            };

            if (target is null)
            {
                error = "Формат профиля не поддержан конвертером.";
                return false;
            }

            target["tag"] = "proxy";
            var root = new JsonObject
            {
                ["log"] = new JsonObject { ["level"] = "warn", ["timestamp"] = true },
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

            json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
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

    private static JsonObject BuildVless(string raw)
    {
        var uri = RequireUri(raw, "vless");
        var q = ParseQuery(uri.Query);
        var uuid = Uri.UnescapeDataString(uri.UserInfo);
        if (string.IsNullOrWhiteSpace(uuid)) throw new InvalidDataException("VLESS: отсутствует UUID.");

        var o = BaseServer("vless", uri);
        o["uuid"] = uuid;
        var flow = Get(q, "flow");
        if (!string.IsNullOrWhiteSpace(flow)) o["flow"] = flow;

        ApplyTls(o, q, Get(q, "security"));
        ApplyTransport(o, q, Get(q, "type"));
        return o;
    }

    private static JsonObject BuildTrojan(string raw)
    {
        var uri = RequireUri(raw, "trojan");
        var q = ParseQuery(uri.Query);
        var password = Uri.UnescapeDataString(uri.UserInfo);
        if (string.IsNullOrWhiteSpace(password)) throw new InvalidDataException("Trojan: отсутствует пароль.");

        var o = BaseServer("trojan", uri);
        o["password"] = password;
        ApplyTls(o, q, Get(q, "security", "tls"), forceTls: true);
        ApplyTransport(o, q, Get(q, "type"));
        return o;
    }

    private static JsonObject BuildHysteria2(string raw)
    {
        var uri = RequireUri(raw, raw.StartsWith("hy2://", StringComparison.OrdinalIgnoreCase) ? "hy2" : "hysteria2");
        var q = ParseQuery(uri.Query);
        var password = Uri.UnescapeDataString(uri.UserInfo);
        if (password.Contains(':') && password.StartsWith(':')) password = password[1..];
        if (string.IsNullOrWhiteSpace(password)) throw new InvalidDataException("Hysteria2: отсутствует пароль.");

        var o = BaseServer("hysteria2", uri);
        o["password"] = password;
        ApplyTls(o, q, "tls", forceTls: true);

        var obfs = Get(q, "obfs");
        var obfsPassword = Get(q, "obfs-password", Get(q, "obfs_password"));
        if (!string.IsNullOrWhiteSpace(obfs))
        {
            if (!obfs.Equals("salamander", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Hysteria2 obfs '{obfs}' не поддерживается sing-box 1.13 конвертером.");
            o["obfs"] = new JsonObject { ["type"] = "salamander", ["password"] = obfsPassword };
        }
        return o;
    }

    private static JsonObject BuildVmess(string raw)
    {
        var payload = raw["vmess://".Length..];
        var hash = payload.IndexOf('#');
        if (hash >= 0) payload = payload[..hash];
        var decoded = DecodeBase64(payload.Trim());
        using var doc = JsonDocument.Parse(decoded);
        var r = doc.RootElement;

        var host = Text(r, "add");
        var port = Int(r, "port");
        var uuid = Text(r, "id");
        if (string.IsNullOrWhiteSpace(host) || port <= 0 || string.IsNullOrWhiteSpace(uuid))
            throw new InvalidDataException("VMess: отсутствуют add/port/id.");

        var o = new JsonObject
        {
            ["type"] = "vmess",
            ["server"] = host,
            ["server_port"] = port,
            ["uuid"] = uuid,
            ["security"] = EmptyAs(Text(r, "scy"), "auto"),
            ["alter_id"] = Int(r, "aid")
        };

        var tlsMode = Text(r, "tls");
        var q = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["sni"] = Text(r, "sni"),
            ["host"] = Text(r, "host"),
            ["path"] = Text(r, "path"),
            ["serviceName"] = Text(r, "path")
        };
        ApplyTls(o, q, tlsMode);
        ApplyTransport(o, q, Text(r, "net"));
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

        if (string.IsNullOrWhiteSpace(privateKey)) throw new InvalidDataException("WireGuard: отсутствует Interface/PrivateKey.");
        if (addresses.Count == 0) throw new InvalidDataException("WireGuard: отсутствует Interface/Address.");
        if (peers.Count == 0) throw new InvalidDataException("WireGuard: отсутствует секция Peer.");

        var peerNodes = new JsonArray();
        foreach (var peer in peers)
        {
            if (string.IsNullOrWhiteSpace(peer.PublicKey)) throw new InvalidDataException("WireGuard: у Peer отсутствует PublicKey.");
            if (string.IsNullOrWhiteSpace(peer.Endpoint) || !ProfileImporter.TrySplitEndpoint(peer.Endpoint, out var host, out var port) || port <= 0)
                throw new InvalidDataException($"WireGuard: некорректный Peer/Endpoint '{peer.Endpoint}'.");
            if (peer.AllowedIps.Count == 0) throw new InvalidDataException("WireGuard: у Peer отсутствует AllowedIPs.");

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
        if (!raw.StartsWith("ss://", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Некорректная Shadowsocks URI.");
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
            if (at <= 0) throw new InvalidDataException("Shadowsocks: не удалось разобрать method/password/server.");
            credentials = decoded[..at];
            authority = decoded[(at + 1)..];
        }

        var colon = credentials.IndexOf(':');
        if (colon <= 0) throw new InvalidDataException("Shadowsocks: отсутствуют method/password.");
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
            (!uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) && !uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Некорректная HTTP/HTTPS proxy URI.");

        var o = BaseServer("http", uri);
        ApplyUserInfo(o, uri.UserInfo);
        if (uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
        {
            o["tls"] = new JsonObject
            {
                ["enabled"] = true,
                ["server_name"] = uri.Host
            };
        }
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

    private static JsonObject BaseServer(string type, Uri uri)
    {
        if (string.IsNullOrWhiteSpace(uri.Host) || uri.Port <= 0)
            throw new InvalidDataException($"{type}: отсутствует server:port.");
        return new JsonObject { ["type"] = type, ["server"] = uri.Host, ["server_port"] = uri.Port };
    }

    private static void ApplyTls(JsonObject o, IReadOnlyDictionary<string, string> q, string mode, bool forceTls = false)
    {
        mode = mode?.Trim() ?? string.Empty;
        var enabled = forceTls || mode.Equals("tls", StringComparison.OrdinalIgnoreCase) || mode.Equals("reality", StringComparison.OrdinalIgnoreCase);
        if (!enabled) return;

        var tls = new JsonObject { ["enabled"] = true };
        var sni = Get(q, "sni", Get(q, "peer"));
        if (!string.IsNullOrWhiteSpace(sni)) tls["server_name"] = sni;
        if (IsTrue(Get(q, "insecure")) || IsTrue(Get(q, "allowInsecure"))) tls["insecure"] = true;

        var fp = Get(q, "fp");
        if (!string.IsNullOrWhiteSpace(fp) && !fp.Equals("none", StringComparison.OrdinalIgnoreCase))
            tls["utls"] = new JsonObject { ["enabled"] = true, ["fingerprint"] = fp };

        if (mode.Equals("reality", StringComparison.OrdinalIgnoreCase))
        {
            var publicKey = Get(q, "pbk");
            if (string.IsNullOrWhiteSpace(publicKey)) throw new InvalidDataException("Reality: отсутствует public key (pbk).");
            tls["reality"] = new JsonObject
            {
                ["enabled"] = true,
                ["public_key"] = publicKey,
                ["short_id"] = Get(q, "sid")
            };
        }
        o["tls"] = tls;
    }

    private static void ApplyTransport(JsonObject o, IReadOnlyDictionary<string, string> q, string transport)
    {
        transport = (transport ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(transport) || transport is "tcp" or "none") return;

        if (transport is "ws" or "websocket")
        {
            var t = new JsonObject { ["type"] = "ws", ["path"] = EmptyAs(Get(q, "path"), "/") };
            var host = Get(q, "host");
            if (!string.IsNullOrWhiteSpace(host)) t["headers"] = new JsonObject { ["Host"] = host };
            o["transport"] = t;
            return;
        }

        if (transport == "grpc")
        {
            var service = Get(q, "serviceName", Get(q, "service_name"));
            o["transport"] = new JsonObject { ["type"] = "grpc", ["service_name"] = service };
            return;
        }

        if (transport is "http" or "h2")
        {
            var t = new JsonObject { ["type"] = "http", ["path"] = Get(q, "path") };
            var host = Get(q, "host");
            if (!string.IsNullOrWhiteSpace(host)) t["host"] = new JsonArray(host);
            o["transport"] = t;
            return;
        }

        if (transport is "httpupgrade" or "http-upgrade")
        {
            o["transport"] = new JsonObject
            {
                ["type"] = "httpupgrade",
                ["host"] = Get(q, "host"),
                ["path"] = EmptyAs(Get(q, "path"), "/")
            };
            return;
        }

        throw new InvalidDataException($"V2Ray transport '{transport}' пока не поддерживается реальным тестом.");
    }

    private static Uri RequireUri(string raw, string expected)
    {
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) || !uri.Scheme.Equals(expected, StringComparison.OrdinalIgnoreCase))
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

    private static bool IsTrue(string value) => value is "1" or "true" or "True" or "TRUE";
    private static string EmptyAs(string value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value;

    private static string Text(JsonElement r, string name)
    {
        if (!r.TryGetProperty(name, out var v)) return string.Empty;
        return v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : v.GetRawText().Trim('"');
    }

    private static int Int(JsonElement r, string name)
    {
        var s = Text(r, name);
        return int.TryParse(s, out var n) ? n : 0;
    }

    private static string DecodeBase64(string value)
    {
        var s = value.Replace('-', '+').Replace('_', '/');
        var mod = s.Length % 4;
        if (mod == 2) s += "==";
        else if (mod == 3) s += "=";
        else if (mod == 1) throw new InvalidDataException("Некорректный Base64.");
        return Encoding.UTF8.GetString(Convert.FromBase64String(s));
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
