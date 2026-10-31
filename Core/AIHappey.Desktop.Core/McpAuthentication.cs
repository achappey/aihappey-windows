using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Authentication;
using ModelContextProtocol.Client;

namespace AIHappey.Desktop.Core;

public sealed class McpSignInRequiredException : Exception
{
    public McpSignInRequiredException() : base("MCP sign-in required.") { }
}

/// <summary>Session-owned policy. Service credentials never enter custom-server OAuth or registry reads.</summary>
public sealed class DesktopMcpAuthentication(DesktopSession session) : IDesktopMcpAuthentication
{
    internal Func<HttpMessageHandler>? CreateTransport { get; init; }
    internal Func<McpOAuthBrowser> CreateBrowser { get; init; } = () => new();
    internal string? TokenDirectory { get; init; }
    public Task<HttpMessageHandler> ConfigureAsync(DesktopMcpServer server, HttpClientTransportOptions options, CancellationToken ct) =>
        ConfigureAsync(server, options, false, ct);

    public Task<HttpMessageHandler> ConfigureAsync(DesktopMcpServer server, HttpClientTransportOptions options, bool interactive, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var partition = session.McpPartition;
        var endpoint = options.Endpoint;
        // Explicit per-server credentials win; never combine them with host credentials or OAuth.
        options.AdditionalHeaders = server.PluginId is null ? McpValidation.Headers(server.Headers) : new(server.Headers, StringComparer.OrdinalIgnoreCase);
        var manual = McpHeaderConfiguration.HasManualCredentials(server);
        var service = manual ? null : MatchService(endpoint, session.Settings, session.Runtime);
        var handler = new McpAuthenticationHandler(endpoint, () => session.McpPartition == partition
                && (service is null || MatchService(endpoint, session.Settings, session.Runtime) == service),
            service is { } kind ? (request, allowUi, token) => session.Host.AuthenticateMcpAsync(request, kind, allowUi, token) : null,
            interactive && !manual, CreateTransport?.Invoke(), interactive && !manual && service is null ? CreateBrowser() : null);
        if (!manual && service is null)
        {
            var cache = new McpOAuthTokenCache(TokenDirectory ?? Path.Combine(session.DataDirectory, "mcp-oauth"), partition, endpoint);
            options.OAuth = new ClientOAuthOptions
            {
                RedirectUri = handler.RedirectUri,
                TokenCache = cache,
                DynamicClientRegistration = new()
                {
                    ClientName = "AIHappey Windows", ApplicationType = "native"
                },
                AuthorizationCallbackHandler = async (context, token) =>
                {
                    handler.CheckIdentity();
                    if (!handler.Interactive) throw new McpSignInRequiredException();
                    var uri = handler.LegacyAuthorizationUri(context.AuthorizationUri);
                    return await handler.Browser!.AuthorizeAsync(uri, token);
                }
            };
        }
        return Task.FromResult<HttpMessageHandler>(handler);
    }

    public Task ForgetAsync(DesktopMcpServer server, CancellationToken ct) =>
        new McpOAuthTokenCache(TokenDirectory ?? Path.Combine(session.DataDirectory, "mcp-oauth"), session.McpPartition,
            new Uri(server.Url)).ClearAsync(ct);

    public static ServiceKind? MatchService(Uri endpoint, DesktopSettings settings, IRuntimeResolver runtime)
    {
        var matches = new List<(ServiceKind Kind, int Length)>();
        foreach (var kind in Enum.GetValues<ServiceKind>())
        {
            var config = settings.For(kind);
            var root = config.Location == RuntimeLocation.Remote
                ? DesktopSettings.RemoteUri(config.RemoteUrl) : runtime.GetRunningEndpoint(kind);
            if (root is not null && Within(endpoint, root)) matches.Add((kind, root.AbsolutePath.Length));
        }
        if (matches.Count == 0) return null;
        var longest = matches.Max(m => m.Length);
        var best = matches.Where(m => m.Length == longest).ToArray();
        // Two services with an identical destination cannot safely select a token audience.
        if (best.Length != 1) throw new InvalidOperationException("Ambiguous MCP service destination.");
        return best[0].Kind;
    }

    private static bool Within(Uri endpoint, Uri root) => endpoint.Scheme == root.Scheme
        && endpoint.IdnHost == root.IdnHost && endpoint.Port == root.Port
        && (endpoint.AbsolutePath == root.AbsolutePath.TrimEnd('/')
            || endpoint.AbsolutePath.StartsWith(root.AbsolutePath.TrimEnd('/') + "/", StringComparison.Ordinal));
}

/// <summary>No redirects/cookies. The SDK owns PKCE, state/issuer/resource validation, refresh and request retries.</summary>
internal sealed class McpAuthenticationHandler : DelegatingHandler
{
    private readonly Uri endpoint;
    private readonly Func<bool> identityCurrent;
    private readonly Func<HttpRequestMessage, bool, CancellationToken, Task>? authenticate;
    private bool legacyMetadata, legacyAuthorization;
    public bool Interactive { get; private set; }
    public McpOAuthBrowser? Browser { get; }
    public Uri RedirectUri => Browser?.RedirectUri ?? new Uri("http://127.0.0.1/oauth-callback");

    internal McpAuthenticationHandler(Uri endpoint, Func<bool> identityCurrent,
        Func<HttpRequestMessage, bool, CancellationToken, Task>? authenticate, bool interactive,
        HttpMessageHandler? inner = null, McpOAuthBrowser? browser = null)
        : base(inner ?? new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
    {
        this.endpoint = endpoint; this.identityCurrent = identityCurrent; this.authenticate = authenticate;
        Interactive = interactive;
        if (interactive && authenticate is null) Browser = browser ?? new McpOAuthBrowser();
    }

    internal void CheckIdentity()
    {
        if (!identityCurrent()) throw new OperationCanceledException("MCP identity changed.");
    }
    internal void CompleteHandshake() { Interactive = false; Browser?.Dispose(); }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        CheckIdentity();
        var uri = request.RequestUri ?? throw new InvalidOperationException();
        _ = McpValidation.Endpoint(uri.GetLeftPart(UriPartial.Path));
        var isResource = uri.GetLeftPart(UriPartial.Path) == endpoint.GetLeftPart(UriPartial.Path);
        if (authenticate is not null)
        {
            if (!isResource) throw new InvalidOperationException("MCP credential destination changed.");
            // Interactive host sign-in, like the browser callback, is outside the HTTP network budget.
            await authenticate(request, Interactive, ct);
            CheckIdentity();
        }
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // Browser waiting occurs in the callback, not in an HTTP request's network budget.
        budget.CancelAfter(TimeSpan.FromSeconds(30));
        var response = await base.SendAsync(request, budget.Token);
        CheckIdentity();
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            response.Dispose();
            throw new HttpRequestException("MCP access denied.", null, HttpStatusCode.Forbidden);
        }
        // MCPhappey advertises a metadata document where RFC 9728 expects an issuer.
        // Normalize only the same-origin, exact legacy metadata URL, never arbitrary documents/issuers.
        if (!isResource && authenticate is null && request.Method == HttpMethod.Get && response.IsSuccessStatusCode
            && response.Content.Headers.ContentType?.MediaType == "application/json")
        {
            try
            {
                var bytes = await DesktopCatalogClient.ReadBoundedAsync(response.Content, 1024 * 1024, budget.Token);
                var json = JsonNode.Parse(bytes);
                if (json is JsonObject obj)
                {
                    if (obj["resource"]?.GetValue<string>()?.TrimEnd('/') == endpoint.AbsoluteUri.TrimEnd('/')
                        && obj["authorization_servers"] is JsonArray servers)
                    {
                        for (var i = 0; i < servers.Count; i++)
                            if (Uri.TryCreate(servers[i]?.GetValue<string>(), UriKind.Absolute, out var server)
                                && server.AbsoluteUri == endpoint.GetLeftPart(UriPartial.Authority) + "/.well-known/oauth-authorization-server")
                            { servers[i] = endpoint.GetLeftPart(UriPartial.Authority); legacyMetadata = true; }
                    }
                    if (legacyMetadata && uri.AbsoluteUri == endpoint.GetLeftPart(UriPartial.Authority) + "/.well-known/oauth-authorization-server"
                        && obj["issuer"]?.GetValue<string>() == endpoint.GetLeftPart(UriPartial.Authority)
                        && obj["authorization_endpoint"]?.GetValue<string>() == endpoint.GetLeftPart(UriPartial.Authority) + "/authorize"
                        && obj["token_endpoint"]?.GetValue<string>() == endpoint.GetLeftPart(UriPartial.Authority) + "/token"
                        && obj["registration_endpoint"]?.GetValue<string>() == endpoint.GetLeftPart(UriPartial.Authority) + "/register")
                        legacyAuthorization = true;
                    response.Content.Dispose();
                    response.Content = new StringContent(obj.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
                }
                else { response.Content.Dispose(); response.Content = new ByteArrayContent(bytes); }
            }
            catch { response.Dispose(); throw; }
        }
        return response;
    }

    internal Uri LegacyAuthorizationUri(Uri uri)
    {
        if (!legacyAuthorization || uri.GetLeftPart(UriPartial.Path) != endpoint.GetLeftPart(UriPartial.Authority) + "/authorize") return uri;
        // The legacy proxy forwards authorization parameters to Entra v2, where this MCP resource is invalid.
        // Keep resource on the token exchange (SDK-owned), matching the working web client.
        return new UriBuilder(uri) { Query = string.Join("&", uri.Query.TrimStart('?').Split('&')
            .Where(p => Uri.UnescapeDataString(p.Split('=')[0]) != "resource")) }.Uri;
    }

    protected override void Dispose(bool disposing) { if (disposing) Browser?.Dispose(); base.Dispose(disposing); }
}

/// <summary>One encrypted file per account and resource; issuer binding is persisted in TokenContainer.</summary>
public sealed class McpOAuthTokenCache(string root, string partition, Uri endpoint, IMcpSecretProtector? protector = null) : ITokenCache
{
    private readonly IMcpSecretProtector secrets = protector ?? new WindowsMcpSecretProtector();
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string purpose = "AIHappey.Desktop.McpOAuth.v1|" + partition + "|" + endpoint.AbsoluteUri;
    private string FilePath => Path.Combine(root, McpValidation.Hash(purpose) + ".bin");

    public async Task ClearAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try { File.Delete(FilePath); } finally { gate.Release(); }
    }

    public async ValueTask<TokenContainer?> GetTokensAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(FilePath)) return null;
            if (new FileInfo(FilePath).Length > 1024 * 1024) return null;
            var json = secrets.Unprotect(await File.ReadAllTextAsync(FilePath, cancellationToken), purpose);
            var tokens = JsonSerializer.Deserialize<TokenContainer>(json);
            return string.IsNullOrEmpty(tokens?.AuthorizationServer) ? null : tokens;
        }
        catch (Exception e) when (e is System.Security.Cryptography.CryptographicException or FormatException or JsonException or IOException)
        { return null; } // Corrupt/unreadable credentials require sign-in; never downgrade to plaintext.
        finally { gate.Release(); }
    }

    public async ValueTask StoreTokensAsync(TokenContainer tokens, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        var temp = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(root);
            // An empty container is the SDK's cache invalidation operation.
            if (string.IsNullOrEmpty(tokens.AccessToken)) { File.Delete(FilePath); return; }
            await File.WriteAllTextAsync(temp, secrets.Protect(JsonSerializer.Serialize(tokens), purpose), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temp, FilePath, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); gate.Release(); }
    }
}
