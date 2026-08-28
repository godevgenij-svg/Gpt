using System.Text;
using GreyVPN.Models;

namespace GreyVPN.Services;

public static class OpenVpnConfigSanitizer
{
    private static readonly HashSet<string> RemoveDirectives = new(StringComparer.OrdinalIgnoreCase)
    {
        "up", "down", "route-up", "route-pre-down", "ipchange", "tls-verify", "plugin",
        "script-security", "management", "management-client", "management-external-key",
        "management-query-passwords", "auth-user-pass-verify", "learn-address", "client-connect",
        "client-disconnect", "daemon", "log", "log-append", "status", "askpass",
        "route", "route-ipv6", "redirect-gateway", "redirect-private", "block-outside-dns",
        "dhcp-option", "windows-driver", "dev-node"
    };

    private static readonly HashSet<string> ExternalFileDirectives = new(StringComparer.OrdinalIgnoreCase)
    {
        "ca", "cert", "key", "pkcs12", "tls-auth", "tls-crypt", "tls-crypt-v2", "auth-user-pass"
    };

    private static readonly HashSet<string> AllowedInlineBlocks = new(StringComparer.OrdinalIgnoreCase)
    {
        "ca", "cert", "key", "pkcs12", "tls-auth", "tls-crypt", "tls-crypt-v2", "auth-user-pass"
    };

    public static bool TryBuildSafeConfig(VpnProfile profile, out string config, out string error)
    {
        config = string.Empty;
        error = string.Empty;

        if (!profile.Type.Equals("OpenVPN", StringComparison.OrdinalIgnoreCase))
        {
            error = "Это не OpenVPN-профиль.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(profile.SourcePath) || !File.Exists(profile.SourcePath))
        {
            error = "Исходный .ovpn файл не найден.";
            return false;
        }

        string source;
        try
        {
            source = File.ReadAllText(profile.SourcePath, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            error = $"Не удалось прочитать .ovpn: {ex.Message}";
            return false;
        }

        var inlineBlocks = FindInlineBlocks(source);
        var output = new StringBuilder();
        var insideBlock = false;
        string? currentBlock = null;
        var hasDev = false;

        foreach (var rawLine in source.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var trimmed = rawLine.Trim();

            if (trimmed.StartsWith('<') && trimmed.EndsWith('>'))
            {
                var tag = trimmed.Trim('<', '>', '/').Trim();
                if (trimmed.StartsWith("</", StringComparison.Ordinal))
                {
                    if (insideBlock && currentBlock is not null && tag.Equals(currentBlock, StringComparison.OrdinalIgnoreCase))
                    {
                        output.AppendLine(rawLine);
                        insideBlock = false;
                        currentBlock = null;
                    }
                    continue;
                }

                if (AllowedInlineBlocks.Contains(tag))
                {
                    insideBlock = true;
                    currentBlock = tag;
                    output.AppendLine(rawLine);
                }
                continue;
            }

            if (insideBlock)
            {
                output.AppendLine(rawLine);
                continue;
            }

            if (trimmed.Length == 0 || trimmed.StartsWith('#') || trimmed.StartsWith(';'))
            {
                output.AppendLine(rawLine);
                continue;
            }

            var parts = SplitDirective(trimmed);
            var directive = parts.Directive;
            var argument = parts.Argument;

            if (directive.Equals("dev", StringComparison.OrdinalIgnoreCase))
            {
                if (!argument.StartsWith("tun", StringComparison.OrdinalIgnoreCase))
                {
                    error = "OpenVPN real-test поддерживает только TUN-профили; TAP-профиль пропущен.";
                    return false;
                }
                hasDev = true;
                output.AppendLine("dev tun");
                continue;
            }

            if (ExternalFileDirectives.Contains(directive))
            {
                if (inlineBlocks.Contains(directive))
                    continue;

                if (directive.Equals("auth-user-pass", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(argument))
                {
                    error = "Профиль требует интерактивный логин/пароль (auth-user-pass).";
                    return false;
                }

                error = $"Безопасный real-test не открывает внешние файлы из .ovpn: {directive} {argument}".TrimEnd();
                return false;
            }

            if (RemoveDirectives.Contains(directive))
                continue;

            output.AppendLine(rawLine);
        }

        if (!hasDev)
            output.AppendLine("dev tun");

        // OpenVPN 2.6 + Wintun is used deliberately: it can create the adapter on demand
        // when the process is elevated, without installing a persistent TAP adapter.
        output.AppendLine("windows-driver wintun");
        output.AppendLine("route-nopull");
        output.AppendLine("route 1.1.1.1 255.255.255.255 vpn_gateway");
        output.AppendLine("auth-nocache");
        output.AppendLine("connect-retry-max 1");
        output.AppendLine("connect-timeout 8");
        output.AppendLine("resolv-retry 2");
        output.AppendLine("verb 3");

        config = output.ToString();
        return true;
    }

    private static HashSet<string> FindInlineBlocks(string source)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var block in AllowedInlineBlocks)
        {
            if (source.Contains($"<{block}>", StringComparison.OrdinalIgnoreCase) &&
                source.Contains($"</{block}>", StringComparison.OrdinalIgnoreCase))
            {
                result.Add(block);
            }
        }
        return result;
    }

    private static (string Directive, string Argument) SplitDirective(string line)
    {
        var i = 0;
        while (i < line.Length && !char.IsWhiteSpace(line[i])) i++;
        var directive = line[..i];
        var argument = i < line.Length ? line[i..].Trim() : string.Empty;
        return (directive, argument);
    }
}
