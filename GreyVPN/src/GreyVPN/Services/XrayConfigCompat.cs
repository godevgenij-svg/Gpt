using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using GreyVPN.Models;

namespace GreyVPN.Services;

public sealed record XrayNormalizedConfig(string Json, string Warning);

public static class XrayConfigCompat
{
    public static XrayNormalizedConfig Normalize(VpnProfile profile, string json)
    {
        var root = JsonNode.Parse(json) ?? throw new InvalidDataException("Xray config JSON пуст.");
        var removedAllowInsecure = RemovePropertyRecursive(root, "allowInsecure");

        var query = ParseShareQuery(profile.RawValue);
        var pcs = Get(query, "pcs", Get(query, "pinnedPeerCertSha256"));
        var vcn = Get(query, "vcn", Get(query, "verifyPeerCertByName"));
        if (!string.IsNullOrWhiteSpace(pcs) || !string.IsNullOrWhiteSpace(vcn))
            ApplyTlsVerificationFields(root, pcs, vcn);

        var warnings = new List<string>();
        if (removedAllowInsecure || IsTrue(Get(query, "allowInsecure")) || IsTrue(Get(query, "insecure")))
            warnings.Add("Xray 26.7.28 удалил allowInsecure; GreyVPN не отключает проверку сертификата автоматически.");
        if (!string.IsNullOrWhiteSpace(pcs)) warnings.Add("Применён pinnedPeerCertSha256 (pcs).");
        if (!string.IsNullOrWhiteSpace(vcn)) warnings.Add("Применён verifyPeerCertByName (vcn).");

        return new XrayNormalizedConfig(
            root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
            string.Join(" ", warnings));
    }

    private static bool RemovePropertyRecursive(JsonNode node, string propertyName)
    {
        var removed = false;
        if (node is JsonObject obj)
        {
            if (obj.Remove(propertyName)) removed = true;
            foreach (var child in obj.Select(x => x.Value).Where(x => x is not null).ToArray())
                removed |= RemovePropertyRecursive(child!, propertyName);
        }
        else if (node is JsonArray array)
        {
            foreach (var child in array.Where(x => x is not null).ToArray())
                removed |= RemovePropertyRecursive(child!, propertyName);
        }
        return removed;
    }

    private static void ApplyTlsVerificationFields(JsonNode node, string pcs, string vcn)
    {
        if (node is JsonObject obj)
        {
            if (obj["tlsSettings"] is JsonObject tls)
            {
                if (!string.IsNullOrWhiteSpace(pcs)) tls["pinnedPeerCertSha256"] = pcs;
                if (!string.IsNullOrWhiteSpace(vcn)) tls["verifyPeerCertByName"] = vcn;
            }
            foreach (var child in obj.Select(x => x.Value).Where(x => x is not null).ToArray())
                ApplyTlsVerificationFields(child!, pcs, vcn);
        }
        else if (node is JsonArray array)
        {
            foreach (var child in array.Where(x => x is not null).ToArray())
                ApplyTlsVerificationFields(child!, pcs, vcn);
        }
    }

    private static Dictionary<string, string> ParseShareQuery(string raw)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(raw) || !raw.Contains("://", StringComparison.Ordinal)) return result;
        if (!Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var uri)) return result;
        foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var split = part.Split('=', 2);
            var key = WebUtility.UrlDecode(split[0]);
            var value = split.Length > 1 ? WebUtility.UrlDecode(split[1]) : string.Empty;
            if (!string.IsNullOrWhiteSpace(key)) result[key] = value;
        }
        return result;
    }

    private static string Get(IReadOnlyDictionary<string, string> values, string key, string fallback = "") =>
        values.TryGetValue(key, out var value) ? value : fallback;

    private static bool IsTrue(string value) =>
        value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("yes", StringComparison.OrdinalIgnoreCase);
}