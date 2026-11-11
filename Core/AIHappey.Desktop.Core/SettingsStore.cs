using System.Text.Json;

namespace AIHappey.Desktop.Core;

public static class SettingsStore
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> writes = new(StringComparer.OrdinalIgnoreCase);

    internal static Task<DesktopSettings> LoadForSessionAsync(string directory, DesktopSession session)
    {
        // Seed only first-run defaults. Deserialized user preferences always take precedence.
        var defaults = session.Settings.Clone();
        defaults.ConversationStorage.CloudEnabled = session.Host is IDesktopConversationHost { ConversationsEndpoint: not null };
        return LoadAsync(directory, defaults, session.Host.ApplySettingsPolicy);
    }

    public static async Task<DesktopSettings> LoadAsync(string directory, DesktopSettings defaults,
        Action<DesktopSettings>? applyHostPolicy = null)
    {
        var path = Path.Combine(directory, "settings.json");
        var settings = defaults;
        if (File.Exists(path))
        {
            try
            {
                await using var stream = File.OpenRead(path);
                settings = await JsonSerializer.DeserializeAsync<DesktopSettings>(stream, JsonSerializerOptions.Web) ?? defaults;
            }
            catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException(DesktopResources.Get("SettingsReadFailed"));
            }
        }
        else
        {
            // Only a new installation opts in. Older files without this header retain
            // the provider's compatibility fallback, even when chat settings are absent.
            var headers = settings.Chat.ProviderHeaders.GetValueOrDefault("anthropic") ?? new(StringComparer.OrdinalIgnoreCase);
            if (!headers.Keys.Any(name => name.Equals(AnthropicChatConfig.VersionHeader, StringComparison.OrdinalIgnoreCase)))
                headers[AnthropicChatConfig.VersionHeader] = AnthropicChatConfig.LatestApiVersion;
            settings.Chat.ProviderHeaders["anthropic"] = headers;
        }
        applyHostPolicy?.Invoke(settings);
        return settings;
    }

    public static async Task SaveAsync(string directory, DesktopSettings settings)
    {
        var gate = writes.GetOrAdd(Path.GetFullPath(directory), _ => new(1, 1));
        await gate.WaitAsync();
        try
        {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "settings.json");
        var temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(settings, JsonSerializerOptions.Web));
        File.Move(temp, path, true);
        }
        finally { gate.Release(); }
    }
}
