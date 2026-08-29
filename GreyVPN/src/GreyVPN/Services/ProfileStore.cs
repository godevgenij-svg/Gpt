using System.IO;
using System.Text.Json;
using GreyVPN.Models;

namespace GreyVPN.Services;

public static class ProfileStore
{
    private static readonly SemaphoreSlim SaveGate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static string DataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GreyVPN");

    private static string ProfilesPath => Path.Combine(DataDir, "profiles.json");
    private static string BackupPath => Path.Combine(DataDir, "profiles.json.bak");

    public static async Task SaveAsync(IEnumerable<VpnProfile> profiles)
    {
        var snapshot = Deduplicate(profiles.ToList());
        foreach (var profile in snapshot)
            await TryEnsureVaultAsync(profile).ConfigureAwait(false);

        await SaveGate.WaitAsync().ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(DataDir);
            var tempPath = ProfilesPath + ".tmp." + Guid.NewGuid().ToString("N");
            try
            {
                await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, useAsync: true))
                {
                    await JsonSerializer.SerializeAsync(stream, snapshot, JsonOptions).ConfigureAwait(false);
                    await stream.FlushAsync().ConfigureAwait(false);
                }

                if (File.Exists(ProfilesPath))
                {
                    try
                    {
                        File.Replace(tempPath, ProfilesPath, BackupPath, ignoreMetadataErrors: true);
                    }
                    catch (PlatformNotSupportedException)
                    {
                        File.Copy(ProfilesPath, BackupPath, overwrite: true);
                        File.Move(tempPath, ProfilesPath, overwrite: true);
                    }
                }
                else
                {
                    File.Move(tempPath, ProfilesPath);
                }
            }
            finally
            {
                foreach (var stale in Directory.EnumerateFiles(DataDir, "profiles.json.tmp.*"))
                {
                    try { File.Delete(stale); } catch { }
                }
            }
        }
        finally
        {
            SaveGate.Release();
        }
    }

    public static async Task<IReadOnlyList<VpnProfile>> LoadAsync()
    {
        Directory.CreateDirectory(DataDir);
        var primary = await TryLoadAsync(ProfilesPath).ConfigureAwait(false);
        if (primary is not null)
            return primary;

        var backup = await TryLoadAsync(BackupPath).ConfigureAwait(false);
        return backup ?? Array.Empty<VpnProfile>();
    }

    private static async Task<IReadOnlyList<VpnProfile>?> TryLoadAsync(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
            var profiles = await JsonSerializer.DeserializeAsync<List<VpnProfile>>(stream, JsonOptions).ConfigureAwait(false);
            if (profiles is null) return null;

            foreach (var profile in profiles)
                await TryEnsureVaultAsync(profile).ConfigureAwait(false);

            var deduped = Deduplicate(profiles);
            if (deduped.Count != profiles.Count)
                DiagnosticsService.Log("STORE", $"Removed exact duplicate profiles on load: {profiles.Count - deduped.Count}; remaining={deduped.Count}");
            return deduped;
        }
        catch
        {
            return null;
        }
    }

    private static async Task TryEnsureVaultAsync(VpnProfile profile)
    {
        try
        {
            if (ConfigVault.Supports(profile))
            {
                await ConfigVault.EnsureStoredAsync(profile).ConfigureAwait(false);
                return;
            }

            if (OpenVpnConfigVault.Supports(profile))
                await OpenVpnConfigVault.EnsureStoredAsync(profile).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            DiagnosticsService.Log("STORE", $"Config vault migration skipped: {ex.GetType().Name}: {ex.Message}", profile);
        }
    }

    internal static List<VpnProfile> Deduplicate(IReadOnlyList<VpnProfile> profiles)
    {
        var result = new List<VpnProfile>();
        var indexByKey = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var profile in profiles)
        {
            var key = StableIdentity(profile);
            if (!indexByKey.TryGetValue(key, out var existingIndex))
            {
                indexByKey[key] = result.Count;
                result.Add(profile);
                continue;
            }

            var existing = result[existingIndex];
            if (Score(profile) > Score(existing) ||
                (Score(profile) == Score(existing) && profile.LastRealTested > existing.LastRealTested))
            {
                result[existingIndex] = profile;
            }
        }

        return result;
    }

    private static string StableIdentity(VpnProfile profile)
    {
        if (!string.IsNullOrWhiteSpace(profile.RawValue) && profile.RawValue.Contains("://", StringComparison.Ordinal))
            return "uri|" + profile.RawValue.Trim();

        if (ConfigVault.Supports(profile) || OpenVpnConfigVault.Supports(profile))
            return $"vpn-file|{profile.Type.Trim()}|{profile.Name.Trim()}|{profile.Endpoint.Trim()}";

        var path = string.IsNullOrWhiteSpace(profile.SourcePath) ? string.Empty : Path.GetFullPath(profile.SourcePath);
        return $"file|{path}|{profile.Type.Trim()}|{profile.Name.Trim()}";
    }

    private static int Score(VpnProfile profile)
    {
        var score = 0;
        if (HasStoredConfig(profile)) score += 100;
        if (!string.IsNullOrWhiteSpace(profile.SourcePath) && File.Exists(profile.SourcePath)) score += 40;
        if (profile.RealStatus.Equals("РАБОТАЕТ", StringComparison.OrdinalIgnoreCase)) score += 30;
        if (profile.LastRealTested is not null) score += 10;
        if (!string.IsNullOrWhiteSpace(profile.RealStatus) &&
            !profile.RealStatus.Equals("Не проверен", StringComparison.OrdinalIgnoreCase)) score += 5;
        return score;
    }

    private static bool HasStoredConfig(VpnProfile profile)
    {
        var path = ConfigVault.Supports(profile)
            ? ConfigVault.ResolveStoredPath(profile)
            : OpenVpnConfigVault.Supports(profile)
                ? OpenVpnConfigVault.ResolveStoredPath(profile)
                : null;
        return path is not null && File.Exists(path);
    }
}
