namespace AIHappey.Desktop.Core;

public sealed record ModelProviderIcon(string Src, string? Theme);
public sealed record ModelProvider(string Name, string? Homepage, IReadOnlyList<ModelProviderIcon> Icons);

/// <summary>Compatibility projection of the shared provider catalog. No independent metadata snapshot.</summary>
public static class ModelProviders
{
    public static ModelProvider Get(string key)
    {
        var provider = ProviderCatalog.Get(key);
        return new(provider.Name, provider.Urls.Homepage, provider.Icons.Count > 0 ? provider.Icons
            : ProviderCatalog.FaviconUri(provider.Urls.Homepage) is { } favicon ? [new(favicon.AbsoluteUri, null)] : []);
    }
    public static Uri? SafeWebUri(string? source) => Uri.TryCreate(source, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(uri.UserInfo) && !uri.IsLoopback ? uri : null;
    public static Uri? IconUri(ModelProvider provider, bool dark) => SafeWebUri(
        provider.Icons.FirstOrDefault(i => i.Theme == (dark ? "dark" : "light"))?.Src ?? provider.Icons.FirstOrDefault()?.Src);
}
