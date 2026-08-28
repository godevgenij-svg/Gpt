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
        profile.Type.Equals("HY2", StringComparison.OrdinalIgnoreCase);

    public static bool TryBuild(VpnProfile profile, int localPort, out string json, out string error)
    {
        json = string.Empty;
        error = string.Empty;
        if (!Supports(profile))
        {
            error = "Для этого протокола реальный тест через sing-box пока не реализован.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(profile.RawValue))
        {
            error = "В базе нет исходной URI-ссылки этого профиля.";
            return false;
        }

        try
        {
            JsonObject? outbound = profile.Type.ToUpperInvariant() switch
            {
                "VLESS" => BuildVless(profile.RawValue),
                "VMESS" => BuildVmess(profile.RawValue),
                "TROJAN" => BuildTrojan(profile.RawValue),
                "HYSTERIA2" or "HY2" => BuildHysteria2(profile.RawValue),
                _ => null
            };

            if (outbound is null)
            {
                error = "Формат профиля не поддержан конвертером.";
                return false;
            }

            outbound["tag"] = "proxy";
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
                ["outbounds"] = new JsonArray(outbound),
                ["route"] = new JsonObject { ["final"] = "proxy" }
            };

            json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
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
        var json = DecodeBase64(payload.Trim());
        using var doc = JsonDocument.Parse(json);
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
        else if (mod == 1) throw new InvalidDataException("Некорректный VMess Base64.");
        return Encoding.UTF8.GetString(Convert.FromBase64String(s));
    }
}
