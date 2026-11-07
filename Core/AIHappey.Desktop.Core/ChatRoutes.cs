using System.Net.Http.Headers;

namespace AIHappey.Desktop.Core;

/// <summary>Immutable turn destination. Conversation model IDs and storage partitions stay canonical.</summary>
public sealed record DesktopChatRoute(string Endpoint, string Model, string? ProviderKey, Uri? DirectUrl = null)
{
    public bool IsDirect => DirectUrl is not null;
    public bool IsNative => Endpoint != "/api/chat";
}

public static class DesktopChatRoutes
{
    public static IReadOnlyList<string> GatewayEndpoints { get; } = Array.AsReadOnly(new[]
        { "/api/chat", "/v1/responses", "/v1/messages", "/v1/chat/completions" });
    private static readonly string[] completionPaths = ["/v1/chat/completions", "/chat/completions", "/paas/v4/chat/completions", "/v1/openai/chat/completions"];
    public static bool IsCompletions(string? endpoint) => completionPaths.Contains(endpoint, StringComparer.Ordinal);
    public static bool IsSupported(string? endpoint) => endpoint is "/api/chat" or "/v1/responses" or "/v1/messages" || IsCompletions(endpoint);
    public static IReadOnlyList<string> DirectEndpoints(string? providerKey)
    {
        if (providerKey is null || !ProviderCatalog.All.TryGetValue(providerKey, out var provider)
            || !SafeBase(provider.DirectApiBaseUrl, out _)) return [];
        return provider.DirectChatEndpoints.Where(path => path != "/api/chat" && IsSupported(path)).Distinct(StringComparer.Ordinal).ToArray();
    }
    public static string PreferredDirectEndpoint(string? providerKey, string selected)
    {
        var paths = DirectEndpoints(providerKey);
        return paths.Contains(selected) ? selected : paths.Contains("/v1/responses") ? "/v1/responses"
            : paths.Contains("/v1/messages") ? "/v1/messages" : paths.FirstOrDefault() ?? "/api/chat";
    }
    public static bool DirectAvailable(IDesktopHost host, string? providerKey) => host.SupportsDirectChat && providerKey is not null
        && DirectEndpoints(providerKey).Count > 0 && host.HasDirectChatCredential(providerKey);
    public static DesktopChatRoute Resolve(IDesktopHost host, ChatPreferences preferences, ChatTarget model)
    {
        var provider = ChatPreferences.ResolveProvider(ServiceKind.Ai, model.Id, model.ProviderKey);
        if (preferences.EndpointMode != "direct")
            return new(GatewayEndpoints.Contains(preferences.Endpoint) ? preferences.Endpoint : "/api/chat", model.Id, provider);
        if (!DirectAvailable(host, provider)) throw new InvalidOperationException(DesktopResources.Get("DirectChatUnavailable"));
        var path = PreferredDirectEndpoint(provider, preferences.Endpoint);
        var localId = !string.IsNullOrWhiteSpace(model.ProviderModelId) ? model.ProviderModelId
            : model.Id.StartsWith(provider + "/", StringComparison.OrdinalIgnoreCase) ? model.Id[(provider!.Length + 1)..] : model.Id;
        return new(path, localId, provider, DirectUrl(provider!, path));
    }
    public static Uri DirectUrl(string providerKey, string endpoint)
    {
        var provider = ProviderCatalog.Get(providerKey);
        if (!DirectEndpoints(providerKey).Contains(endpoint) || !SafeBase(provider.DirectApiBaseUrl, out var root))
            throw new InvalidOperationException(DesktopResources.Get("DirectChatUnavailable"));
        // Append, rather than resolve an absolute path: metadata may contain a provider-specific base path.
        return new Uri(root!.AbsoluteUri.TrimEnd('/') + endpoint);
    }
    private static bool SafeBase(string? value, out Uri? uri) => Uri.TryCreate(value, UriKind.Absolute, out uri)
        && uri.Scheme == "https" && !uri.IsLoopback && string.IsNullOrEmpty(uri.UserInfo)
        && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment);
}

/// <summary>Pure credential scoping shared by the header host and tests. Never applies gateway headers.</summary>
public static class DesktopDirectChatAuthentication
{
    public static string? Credential(IReadOnlyDictionary<string, string> headers, string providerKey)
    {
        if (!ProviderCatalog.All.TryGetValue(providerKey, out var provider)) return null;
        var name = ProviderCatalog.ApiKeyHeader(provider);
        return headers.FirstOrDefault(pair => string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)).Value?.Trim() is { Length: > 0 } key ? key : null;
    }
    public static void Apply(HttpRequestMessage request, IReadOnlyDictionary<string, string> headers, string providerKey, string endpoint)
    {
        if (request.RequestUri != DesktopChatRoutes.DirectUrl(providerKey, endpoint)
            || Credential(headers, providerKey) is not { } key || key.Any(char.IsControl))
            throw new InvalidOperationException(DesktopResources.Get("DirectChatUnavailable"));
        // Pin authentication after provider passthrough headers, without accepting credential overrides.
        if (key.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) key = key[7..].Trim();
        if (key.Length == 0) throw new InvalidOperationException(DesktopResources.Get("DirectChatUnavailable"));
        if (endpoint == "/v1/messages")
        {
            request.Headers.Add("x-api-key", key);
            if (!request.Headers.Contains("anthropic-version")) request.Headers.Add("anthropic-version", "2023-06-01");
        }
        else if (providerKey == "google") request.Headers.Add("x-goog-api-key", key);
        else request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }
}
