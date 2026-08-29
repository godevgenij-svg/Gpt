using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GreyVPN.Models;

namespace GreyVPN.Services;

public static class XrayConfigBuilder
{
    public static bool Supports(VpnProfile profile)
    {
        var type = profile.Type.ToUpperInvariant();
        return type is "VLESS" or "VMESS" or "TROJAN";
    }

    public static string Build(VpnProfile profile, int localPort)
    {
        if (!Supports(profile))
            throw new InvalidDataException($"Xray real-test не поддерживает {profile.Type}.");

        var outbound = profile.Type.ToUpperInvariant() switch
        {
            "VLESS" => BuildVless(profile.RawValue),
            "VMESS" => BuildVmess(profile.RawValue),
            "TROJAN" => BuildTrojan(profile.RawValue),
            _ => throw new InvalidDataException($"Xray real-test не поддерживает {profile.Type}.")
        };

        var root = new JsonObject
        {
            ["log"] = new JsonObject { ["loglevel"] = "warning" },
            ["inbounds"] = new JsonArray
            {
                new JsonObject
                {
                    ["listen"] = "127.0.0.1",
                    ["port"] = localPort,
                    ["protocol"] = "http",
                    ["settings"] = new JsonObject(),
                    ["tag"] = "local-probe"
                }
            },
            ["outbounds"] = new JsonArray(outbound)
        };

        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static JsonObject BuildVless(string raw)
    {
        var uri = ParseUri(raw, "vless");
        var q = ParseQuery(uri.Query);
        var id = Uri.UnescapeDataString(uri.UserInfo);
        if (string.IsNullOrWhiteSpace(id)) throw new InvalidDataException("VLESS: отсутствует UUID.");

        var settings = new JsonObject
        {
            ["address"] = uri.Host,
            ["port"] = EffectivePort(uri, 443),
            ["id"] = id,
            ["encryption"] = Get(q, "encryption", "none")
        };
        AddIf(settings, "flow", Get(q, "flow"));

        return new JsonObject
        {
            ["protocol"] = "vless",
            ["tag"] = "proxy",
            ["settings"] = settings,
            ["streamSettings"] = BuildStreamSettings(q, uri.Host)
        };
    }

    private static JsonObject BuildTrojan(string raw)
    {
        var uri = ParseUri(raw, "trojan");
        var q = ParseQuery(uri.Query);
        var password = Uri.UnescapeDataString(uri.UserInfo);
        if (string.IsNullOrWhiteSpace(password)) throw new InvalidDataException("Trojan: отсутствует пароль.");

        return new JsonObject
        {
            ["protocol"] = "trojan",
            ["tag"] = "proxy",
            ["settings"] = new JsonObject
            {
                ["address"] = uri.Host,
                ["port"] = EffectivePort(uri, 443),
                ["password"] = password
            },
            ["streamSettings"] = BuildStreamSettings(q, uri.Host)
        };
    }

    private static JsonObject BuildVmess(string raw)
    {
        if (!raw.StartsWith("vmess://", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("VMESS: неверная ссылка.");

        var payload = raw[8..].Trim();
        var hash = payload.IndexOf('#');
        if (hash >= 0) payload = payload[..hash];
        var json = Encoding.UTF8.GetString(DecodeBase64(payload));
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;

        var host = ReadString(r, "add");
        var id = ReadString(r, "id");
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(id))
            throw new InvalidDataException("VMESS: отсутствует add или id.");

        var portText = ReadString(r, "port");
        if (!int.TryParse(portText, out var port) || port is < 1 or > 65535)
            throw new InvalidDataException("VMESS: неверный port.");

        var q = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["type"] = ReadString(r, "net"),
            ["host"] = ReadString(r, "host"),
            ["path"] = ReadString(r, "path"),
            ["security"] = ReadString(r, "tls"),
            ["sni"] = ReadString(r, "sni"),
            ["fp"] = ReadString(r, "fp"),
            ["alpn"] = ReadString(r, "alpn"),
            ["serviceName"] = ReadString(r, "path")
        };

        var settings = new JsonObject
        {
            ["address"] = host,
            ["port"] = port,
            ["id"] = id,
            ["security"] = NormalizeVmessSecurity(ReadString(r, "scy"))
        };

        return new JsonObject
        {
            ["protocol"] = "vmess",
            ["tag"] = "proxy",
            ["settings"] = settings,
            ["streamSettings"] = BuildStreamSettings(q, host)
        };
    }

    private static JsonObject BuildStreamSettings(IReadOnlyDictionary<string, string> q, string address)
    {
        var requested = Get(q, "type");
        if (string.IsNullOrWhiteSpace(requested)) requested = Get(q, "net");
        var method = NormalizeMethod(requested);

        var stream = new JsonObject { ["method"] = method };

        switch (method)
        {
            case "websocket":
                var ws = new JsonObject { ["path"] = DefaultPath(Get(q, "path")) };
                AddIf(ws, "host", Get(q, "host"));
                stream["wsSettings"] = ws;
                break;

            case "grpc":
                var grpc = new JsonObject();
                var service = Get(q, "serviceName");
                if (string.IsNullOrWhiteSpace(service)) service = Get(q, "service_name");
                if (string.IsNullOrWhiteSpace(service)) service = Get(q, "path").TrimStart('/');
                AddIf(grpc, "serviceName", service);
                AddIf(grpc, "authority", Get(q, "authority"));
                stream["grpcSettings"] = grpc;
                break;

            case "httpupgrade":
                var hu = new JsonObject { ["path"] = DefaultPath(Get(q, "path")) };
                AddIf(hu, "host", Get(q, "host"));
                stream["httpupgradeSettings"] = hu;
                break;

            case "xhttp":
                var xhttp = new JsonObject { ["path"] = DefaultPath(Get(q, "path")) };
                AddIf(xhttp, "host", Get(q, "host"));
                AddIf(xhttp, "mode", Get(q, "mode"));
                var extra = Get(q, "extra");
                if (!string.IsNullOrWhiteSpace(extra))
                {
                    try { xhttp["extra"] = JsonNode.Parse(extra); }
                    catch { throw new InvalidDataException("XHTTP: параметр extra содержит неверный JSON."); }
                }
                stream["xhttpSettings"] = xhttp;
                break;
        }

        var security = Get(q, "security").ToLowerInvariant();
        if (security == "tls" || security == "reality")
            stream["security"] = security;
        else
            stream["security"] = "none";

        if (security == "tls")
        {
            var tls = new JsonObject();
            var sni = Get(q, "sni");
            if (string.IsNullOrWhiteSpace(sni)) sni = Get(q, "peer");
            if (string.IsNullOrWhiteSpace(sni)) sni = address;
            AddIf(tls, "serverName", sni);
            AddIf(tls, "fingerprint", Get(q, "fp"));
            if (IsTrue(Get(q, "allowInsecure")) || IsTrue(Get(q, "insecure"))) tls["allowInsecure"] = true;
            var alpn = SplitList(Get(q, "alpn"));
            if (alpn.Count > 0) tls["alpn"] = new JsonArray(alpn.Select(x => (JsonNode?)x).ToArray());
            stream["tlsSettings"] = tls;
        }
        else if (security == "reality")
        {
            var reality = new JsonObject();
            var sni = Get(q, "sni");
            if (string.IsNullOrWhiteSpace(sni)) sni = address;
            reality["serverName"] = sni;
            reality["fingerprint"] = string.IsNullOrWhiteSpace(Get(q, "fp")) ? "chrome" : Get(q, "fp");
            var publicKey = Get(q, "pbk");
            if (string.IsNullOrWhiteSpace(publicKey)) publicKey = Get(q, "publicKey");
            if (string.IsNullOrWhiteSpace(publicKey)) throw new InvalidDataException("REALITY: отсутствует pbk/publicKey.");
            reality["password"] = publicKey;
            AddIf(reality, "shortId", Get(q, "sid"));
            AddIf(reality, "spiderX", Get(q, "spx"));
            stream["realitySettings"] = reality;
        }

        return stream;
    }

    private static string NormalizeMethod(string value)
    {
        value = value.Trim().ToLowerInvariant();
        return value switch
        {
            "" or "tcp" or "raw" => "raw",
            "ws" or "websocket" => "websocket",
            "grpc" => "grpc",
            "httpupgrade" => "httpupgrade",
            "xhttp" or "splithttp" => "xhttp",
            "http" or "h2" or "quic" => throw new InvalidDataException($"Xray {value}: этот старый transport удалён из актуального ядра; нужен эквивалентный XHTTP-профиль."),
            _ => throw new InvalidDataException($"Xray transport '{value}' пока не поддержан импортёром.")
        };
    }

    private static Uri ParseUri(string raw, string scheme)
    {
        if (!Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var uri) || !uri.Scheme.Equals(scheme, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{scheme.ToUpperInvariant()}: неверная URI-ссылка.");
        if (string.IsNullOrWhiteSpace(uri.Host)) throw new InvalidDataException($"{scheme.ToUpperInvariant()}: отсутствует host.");
        return uri;
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(query)) return result;
        foreach (var item in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var p = item.IndexOf('=');
            var key = p >= 0 ? item[..p] : item;
            var value = p >= 0 ? item[(p + 1)..] : string.Empty;
            try
            {
                key = Uri.UnescapeDataString(key);
                value = Uri.UnescapeDataString(value);
            }
            catch { }
            result[key] = value;
        }
        return result;
    }

    private static int EffectivePort(Uri uri, int fallback) => uri.Port is > 0 and <= 65535 ? uri.Port : fallback;

    private static string Get(IReadOnlyDictionary<string, string> q, string key, string fallback = "") =>
        q.TryGetValue(key, out var value) ? value : fallback;

    private static void AddIf(JsonObject obj, string key, string value)
    {
        if (!string.IsNullOrWhiteSpace(value)) obj[key] = value;
    }

    private static string DefaultPath(string value) => string.IsNullOrWhiteSpace(value) ? "/" : value;

    private static bool IsTrue(string value) =>
        value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("yes", StringComparison.OrdinalIgnoreCase);

    private static List<string> SplitList(string value) => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static string ReadString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var e)) return string.Empty;
        return e.ValueKind switch
        {
            JsonValueKind.String => e.GetString() ?? string.Empty,
            JsonValueKind.Number => e.GetRawText(),
            _ => string.Empty
        };
    }

    private static string NormalizeVmessSecurity(string value)
    {
        value = value.Trim().ToLowerInvariant();
        return value is "aes-128-gcm" or "chacha20-poly1305" ? value : "auto";
    }

    private static byte[] DecodeBase64(string value)
    {
        value = WebUtility.UrlDecode(value).Trim().Replace('-', '+').Replace('_', '/');
        value = value.PadRight(value.Length + ((4 - value.Length % 4) % 4), '=');
        try { return Convert.FromBase64String(value); }
        catch (FormatException ex) { throw new InvalidDataException("VMESS: неверный Base64.", ex); }
    }
}
