using System.Text.Json;
using AIHappey.Desktop.Core;

namespace AIHappey_Desktop_HeaderAuth;

public sealed class HeaderConfiguration
{
    public Dictionary<string, string[]>? DefaultProvidersByType { get; set; }
    public static HeaderConfiguration Load()
    {
        var overridePath = Path.Combine(DesktopPaths.ProfileDirectory("HeaderAuth"), "desktop.json");
        return LoadFile(File.Exists(overridePath) ? overridePath : Path.Combine(AppContext.BaseDirectory, "desktop.json"));
    }
    public static HeaderConfiguration LoadFile(string path) => File.Exists(path)
        ? JsonSerializer.Deserialize<HeaderConfiguration>(File.ReadAllText(path), JsonSerializerOptions.Web) ?? new() : new();
}
