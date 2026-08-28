using System.Text.Json;
using GreyVPN.Models;

namespace GreyVPN.Services;

public static class ProfileStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private static string DataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GreyVPN");

    private static string ProfilesPath => Path.Combine(DataDir, "profiles.json");

    public static async Task SaveAsync(IEnumerable<VpnProfile> profiles)
    {
        Directory.CreateDirectory(DataDir);
        await using var stream = File.Create(ProfilesPath);
        await JsonSerializer.SerializeAsync(stream, profiles.ToList(), JsonOptions);
    }

    public static async Task<IReadOnlyList<VpnProfile>> LoadAsync()
    {
        if (!File.Exists(ProfilesPath))
            return Array.Empty<VpnProfile>();

        try
        {
            await using var stream = File.OpenRead(ProfilesPath);
            var profiles = await JsonSerializer.DeserializeAsync<List<VpnProfile>>(stream, JsonOptions);
            return profiles ?? new List<VpnProfile>();
        }
        catch
        {
            return Array.Empty<VpnProfile>();
        }
    }
}
