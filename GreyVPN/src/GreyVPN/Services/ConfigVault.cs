using System.Text;
using GreyVPN.Models;

namespace GreyVPN.Services;

public static class ConfigVault
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private static string VaultDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GreyVPN", "Configs");

    public static bool Supports(VpnProfile profile) =>
        profile.Type.Equals("WireGuard", StringComparison.OrdinalIgnoreCase) ||
        profile.Type.Equals("AmneziaWG", StringComparison.OrdinalIgnoreCase);

    public static async Task<bool> EnsureStoredAsync(VpnProfile profile, CancellationToken ct = default)
    {
        if (!Supports(profile)) return false;

        var current = ResolveStoredPath(profile);
        if (current is not null && File.Exists(current)) return true;

        if (string.IsNullOrWhiteSpace(profile.SourcePath) || !File.Exists(profile.SourcePath))
            return false;

        var text = await File.ReadAllTextAsync(profile.SourcePath, Encoding.UTF8, ct).ConfigureAwait(false);
        await StoreTextAsync(profile, text, ct).ConfigureAwait(false);
        return true;
    }

    public static async Task<string> ReadTextAsync(VpnProfile profile, CancellationToken ct = default)
    {
        if (!Supports(profile))
            throw new InvalidDataException($"Внутреннее хранилище не используется для {profile.Type}.");

        var stored = ResolveStoredPath(profile);
        if (stored is not null && File.Exists(stored))
            return await File.ReadAllTextAsync(stored, Encoding.UTF8, ct).ConfigureAwait(false);

        if (await EnsureStoredAsync(profile, ct).ConfigureAwait(false))
        {
            stored = ResolveStoredPath(profile);
            if (stored is not null && File.Exists(stored))
                return await File.ReadAllTextAsync(stored, Encoding.UTF8, ct).ConfigureAwait(false);
        }

        throw new FileNotFoundException(
            "Исходный WG/AWG .conf не найден и ещё не был сохранён во внутреннее хранилище GreyVPN. Импортируй профиль один раз заново.",
            profile.SourcePath);
    }

    public static async Task StoreTextAsync(VpnProfile profile, string text, CancellationToken ct = default)
    {
        if (!Supports(profile))
            throw new InvalidDataException($"Нельзя сохранить {profile.Type} в WG/AWG vault.");
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidDataException("WG/AWG конфигурация пустая.");

        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(VaultDir);
            var fileName = profile.Id.ToString("N") + ".conf";
            var target = Path.Combine(VaultDir, fileName);
            var temp = target + ".tmp." + Guid.NewGuid().ToString("N");
            try
            {
                await File.WriteAllTextAsync(temp, text, new UTF8Encoding(false), ct).ConfigureAwait(false);
                File.Move(temp, target, overwrite: true);
                profile.StoredConfigFile = fileName;
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            }
        }
        finally
        {
            Gate.Release();
        }
    }

    public static string? ResolveStoredPath(VpnProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.StoredConfigFile)) return null;
        var fileName = Path.GetFileName(profile.StoredConfigFile);
        if (!fileName.Equals(profile.StoredConfigFile, StringComparison.Ordinal)) return null;
        if (!fileName.EndsWith(".conf", StringComparison.OrdinalIgnoreCase)) return null;
        return Path.Combine(VaultDir, fileName);
    }
}