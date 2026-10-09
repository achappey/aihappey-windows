using System.Text.Json;

namespace AIHappey.Desktop.Core;

public sealed record ModelProviderIcon(string Src, string? Theme);
public sealed record ModelProvider(string Name, string? Homepage, IReadOnlyList<ModelProviderIcon> Icons);

/// <summary>Display-only metadata generated from the browser catalog. No credentials, endpoints or runtime policy.</summary>
public static class ModelProviders
{
    private static readonly Lazy<IReadOnlyDictionary<string, ModelProvider>> catalog = new(() =>
    {
        using var stream = typeof(ModelProviders).Assembly.GetManifestResourceStream("Desktop.ModelProviders")
            ?? throw new InvalidOperationException("Missing provider display metadata.");
        return JsonSerializer.Deserialize<Dictionary<string, ModelProvider>>(stream, JsonSerializerOptions.Web)!;
    });
    public static ModelProvider Get(string key) => catalog.Value.TryGetValue(key, out var provider) ? provider : new(key, null, []);
    public static Uri? SafeWebUri(string? source) => Uri.TryCreate(source, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(uri.UserInfo) && !uri.IsLoopback ? uri : null;
    public static Uri? IconUri(ModelProvider provider, bool dark) => SafeWebUri(
        provider.Icons.FirstOrDefault(i => i.Theme == (dark ? "dark" : "light"))?.Src ?? provider.Icons.FirstOrDefault()?.Src);
}
