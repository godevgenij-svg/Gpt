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
        var snapshot = profiles.ToList();
        await SaveGate.WaitAsync();
        try
        {
            Directory.CreateDirectory(DataDir);
            var tempPath = ProfilesPath + ".tmp." + Guid.NewGuid().ToString("N");
            try
            {
                await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, useAsync: true))
                {
                    await JsonSerializer.SerializeAsync(stream, snapshot, JsonOptions);
                    await stream.FlushAsync();
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
        var primary = await TryLoadAsync(ProfilesPath);
        if (primary is not null)
            return primary;

        var backup = await TryLoadAsync(BackupPath);
        return backup ?? Array.Empty<VpnProfile>();
    }

    private static async Task<IReadOnlyList<VpnProfile>?> TryLoadAsync(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
            var profiles = await JsonSerializer.DeserializeAsync<List<VpnProfile>>(stream, JsonOptions);
            return profiles;
        }
        catch
        {
            return null;
        }
    }
}
