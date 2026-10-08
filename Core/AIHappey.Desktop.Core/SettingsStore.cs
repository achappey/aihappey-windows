using System.Text.Json;

namespace AIHappey.Desktop.Core;

public static class SettingsStore
{
    public static async Task<DesktopSettings> LoadAsync(string directory, DesktopSettings defaults)
    {
        var path = Path.Combine(directory, "settings.json");
        if (!File.Exists(path)) return defaults;
        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<DesktopSettings>(stream, JsonSerializerOptions.Web) ?? defaults;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(DesktopResources.Get("SettingsReadFailed"));
        }
    }

    public static async Task SaveAsync(string directory, DesktopSettings settings)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "settings.json");
        var temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(settings, JsonSerializerOptions.Web));
        File.Move(temp, path, true);
    }
}
