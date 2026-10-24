using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AIHappey.Desktop.Core;
using ModelContextProtocol.Authentication;
using ModelContextProtocol.Client;

internal static class McpAuthenticationRegressionTests
{
    public static async Task RunAsync(Action<bool, string> check, string root)
    {
        var host = new Host();
        await using var session = new DesktopSession(host, new Runtime(), new()
        {
            Ai = new() { Location = RuntimeLocation.Remote, RemoteUrl = "https://services.example/ai" },
            Agents = new() { Location = RuntimeLocation.Remote, RemoteUrl = "https://services.example/agents" }
        });
        foreach (var (url, expected) in new (string, ServiceKind?)[]
        {
            ("https://services.example/ai/ai-models", ServiceKind.Ai),
            ("https://services.example/agents/tools", ServiceKind.Agents),
            ("https://services.example/ai-evil/tools", null), ("https://services.example.evil/ai/tools", null),
            ("https://services.example:8443/ai/tools", null), ("https://elsewhere.example/tools", null)
        }) check(DesktopMcpAuthentication.MatchService(new(url), session.Settings, session.Runtime) == expected, "MCP service boundary: " + url);
        var overlapping = session.Settings.Clone(); overlapping.Agents.RemoteUrl = "https://services.example/ai/agents";
        check(DesktopMcpAuthentication.MatchService(new("https://services.example/ai/agents/tools"), overlapping, session.Runtime) == ServiceKind.Agents,
            "MCP most-specific service path selects the correct audience");
        overlapping.Agents.RemoteUrl = overlapping.Ai.RemoteUrl;
        try { DesktopMcpAuthentication.MatchService(new("https://services.example/ai/tools"), overlapping, session.Runtime); throw new Exception("Ambiguous audience accepted"); }
        catch (InvalidOperationException) { check(true, "MCP ambiguous service audience rejected"); }

        var serviceRequests = 0;
        var serviceAuth = new DesktopMcpAuthentication(session)
        {
            CreateTransport = () => new CallbackHandler(request =>
            {
                serviceRequests++;
                check(request.Headers.Authorization?.Parameter == "host-token-" + serviceRequests, "MCP host authentication refreshed per request");
                return new(HttpStatusCode.OK);
            })
        };
        var options = new HttpClientTransportOptions { Endpoint = new("https://services.example/ai/ai-models") };
        using (var http = new HttpClient(await serviceAuth.ConfigureAsync(Server(options.Endpoint), options, default)))
        {
            using var first = await http.GetAsync(options.Endpoint);
            using var second = await http.GetAsync(options.Endpoint);
            check(options.OAuth is null && host.Services.All(s => s == ServiceKind.Ai), "MCP trusted service skips custom OAuth");
            try { using var invalid = await http.GetAsync("https://elsewhere.example/tools"); throw new Exception("Credential boundary crossed"); }
            catch (InvalidOperationException) { check(serviceRequests == 2, "MCP host handler refuses changed destinations before network"); }
            session.Settings.Ai.RemoteUrl = "https://changed.example/";
            try { using var invalid = await http.GetAsync(options.Endpoint); throw new Exception("Stale endpoint accepted"); }
            catch (OperationCanceledException) { check(serviceRequests == 2, "MCP host handler rejects changed service configuration"); }
            session.Settings.Ai.RemoteUrl = "https://services.example/ai";
        }
        var manual = Server(options.Endpoint); manual.Headers["Authorization"] = "Bearer explicit-secret";
        var manualOptions = new HttpClientTransportOptions { Endpoint = options.Endpoint };
        using (await serviceAuth.ConfigureAsync(manual, manualOptions, default))
            check(manualOptions.OAuth is null && manualOptions.AdditionalHeaders!["Authorization"] == "Bearer explicit-secret", "MCP explicit headers take precedence");

        foreach (var legacy in new[] { true, false })
        {
            var fixture = new OAuthFixture(check, legacy);
            var tokenRoot = Path.Combine(root, "mcp-oauth-" + legacy);
            var auth = new DesktopMcpAuthentication(session)
            {
                TokenDirectory = tokenRoot,
                CreateTransport = () => fixture.Handler(),
                CreateBrowser = () => new(uri => fixture.Launch(uri))
            };
            var factory = new DesktopMcpClientFactory(auth);
            var server = Server(fixture.Endpoint);
            var store = new DesktopMcpStore(Path.Combine(root, "mcp-manager-" + legacy));
            await using var manager = new DesktopMcpManager(factory, store);
            await manager.LoadAsync("test", default);
            server.Enabled = true;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await manager.InstallAsync(server, timeout.Token);
            check(fixture.Launches == 0 && manager.Servers.Single().State == McpConnectionState.AuthenticationRequired,
                "MCP startup/install silently reports sign-in required: legacy=" + legacy);
            await manager.SignInAsync(server.Id, timeout.Token);
            check(manager.Servers.Single().State == McpConnectionState.Connected && fixture.Launches == 1,
                "MCP OAuth PKCE completes and discovers automatically: legacy=" + legacy);
            await manager.ReconnectAsync(timeout.Token);
            check(manager.Servers.Single().State == McpConnectionState.Connected && fixture.Launches == 1,
                "MCP cached token reconnects silently: legacy=" + legacy);
            check(fixture.TokenExchanges == 1 && fixture.LastCodeVerifier is { Length: >= 43 }, "MCP SDK performs one PKCE token exchange");
            var cache = new McpOAuthTokenCache(tokenRoot, session.McpPartition, fixture.Endpoint);
            var tokens = (await cache.GetTokensAsync())!;
            check(tokens.AuthorizationServer == fixture.Issuer && tokens.ClientId == "native-client" && tokens.RefreshToken is null,
                "MCP cache persists issuer/client binding without inventing a refresh token");
            check(!string.Join("", Directory.GetFiles(tokenRoot).Select(File.ReadAllText)).Contains("fixture-access-token"), "MCP tokens encrypted at rest");
            check(await new McpOAuthTokenCache(tokenRoot, "other-account", fixture.Endpoint).GetTokensAsync() is null
                && await new McpOAuthTokenCache(tokenRoot, session.McpPartition, new("https://mcp.example/other")).GetTokensAsync() is null,
                "MCP OAuth credentials isolated by account and resource");
            tokens.ExpiresIn = 1; tokens.ObtainedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
            await cache.StoreTokensAsync(tokens);
            await manager.ReconnectAsync(timeout.Token);
            check(manager.Servers.Single().State == McpConnectionState.AuthenticationRequired && fixture.Launches == 1 && fixture.TokenExchanges == 1,
                "MCP expired token without refresh never opens browser or sends fake refresh grant");
            await manager.SetEnabledAsync(server.Id, true, timeout.Token, interactive: true);
            check(manager.Servers.Single().State == McpConnectionState.Connected && fixture.Launches == 2, "MCP explicit enable permits sign-in");
            fixture.RejectTokens = true;
            await manager.ReconnectAsync(timeout.Token);
            check(manager.Servers.Single().State == McpConnectionState.AuthenticationRequired && fixture.Launches == 2,
                "MCP rejected cached token requires explicit authorization, not a background browser");
            fixture.Forbidden = true;
            await manager.ReconnectAsync(timeout.Token);
            check(manager.Servers.Single().State == McpConnectionState.Error
                && manager.Servers.Single().Error == DesktopResources.Get("McpAccessDenied") && fixture.Launches == 2,
                "MCP forbidden distinguished from authentication failure");
            await manager.RemoveAsync(server.Id, timeout.Token);
            check(await cache.GetTokensAsync() is null, "MCP uninstall forgets saved OAuth credentials");
        }
        await BrowserChecks(check);
        await LifetimeChecks(check, root);
        check(DesktopMcpManager.SafeError(new Exception("secret", new McpSignInRequiredException())) == DesktopResources.Get("McpAuthRequired"),
            "MCP nested SDK authentication errors safely projected without secrets");
    }

    private static DesktopMcpServer Server(Uri uri) => new() { Id = "auth-server", Name = "Authentication fixture", Url = uri.AbsoluteUri };

    private static async Task BrowserChecks(Action<bool, string> check)
    {
        Task? delivery = null;
        McpOAuthBrowser? browser = null;
        browser = new(uri => delivery = Task.Run(async () =>
        {
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
            using var wrong = await http.GetAsync(browser!.RedirectUri + "?code=bad&state=wrong");
            check(wrong.StatusCode == HttpStatusCode.BadRequest, "MCP loopback listener rejects wrong state and keeps waiting");
            using var duplicate = await http.GetAsync(browser.RedirectUri + "?code=bad&state=expected&state=expected");
            check(duplicate.StatusCode == HttpStatusCode.BadRequest, "MCP loopback listener rejects duplicate OAuth parameters");
            using var valid = await http.GetAsync(browser.RedirectUri + "?code=valid&state=expected&iss=https%3A%2F%2Fissuer.example");
            check(valid.StatusCode == HttpStatusCode.OK && valid.Headers.CacheControl?.NoStore == true, "MCP callback is non-cacheable");
        }));
        using (browser)
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
        {
            var result = await browser.AuthorizeAsync(new("https://issuer.example/authorize?state=expected"), timeout.Token);
            await delivery!;
            check(result.Code == "valid" && result.State == "expected" && result.Iss == "https://issuer.example", "MCP native callback preserves state and issuer for SDK validation");
        }
        using var canceled = new CancellationTokenSource();
        using var waiting = new McpOAuthBrowser(_ => canceled.Cancel());
        try { await waiting.AuthorizeAsync(new("https://issuer.example/authorize?state=cancel"), canceled.Token); throw new Exception("Cancellation ignored"); }
        catch (OperationCanceledException) { check(true, "MCP native browser wait honors cancellation"); }
    }

    private static async Task LifetimeChecks(Action<bool, string> check, string root)
    {
        var factory = new WaitingFactory();
        await using var manager = new DesktopMcpManager(factory, new DesktopMcpStore(Path.Combine(root, "mcp-lifetime")));
        await manager.LoadAsync("account", default);
        var server = Server(new("https://waiting.example/tools")); server.Enabled = true;
        await manager.InstallAsync(server, default);
        var signIn = manager.SignInAsync(server.Id, default);
        await factory.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await manager.SignInAsync(server.Id, default);
        check(factory.InteractiveCalls == 1 && manager.Servers.Single().State == McpConnectionState.SigningIn, "MCP duplicate sign-in suppressed");
        await manager.SetEnabledAsync(server.Id, false, default).WaitAsync(TimeSpan.FromSeconds(5));
        factory.Complete.TrySetResult(new Connection()); // Deliberately ignore cancellation to test stale completion.
        await signIn.WaitAsync(TimeSpan.FromSeconds(5));
        check(manager.Servers.Single().State == McpConnectionState.Disabled && manager.Capture().Tools.Count == 0,
            "MCP disable remains responsive during browser interaction and rejects stale connection");
    }

    private sealed class OAuthFixture(Action<bool, string> check, bool legacy)
    {
        public readonly Uri Endpoint = new("https://mcp.example/tools");
        public string Issuer => legacy ? "https://mcp.example" : "https://issuer.example";
        public int Launches, TokenExchanges;
        public bool RejectTokens, Forbidden;
        public string? LastCodeVerifier;
        private string? challenge;
        public HttpMessageHandler Handler() => new AsyncHandler(async request =>
        {
            var uri = request.RequestUri!;
            check(!request.Headers.Contains("X-App-Credential"), "MCP custom OAuth never receives host provider keys");
            if (uri == Endpoint)
            {
                if (Forbidden) return new(HttpStatusCode.Forbidden);
                if (request.Headers.Authorization?.Parameter != "fixture-access-token" || RejectTokens)
                {
                    var response = new HttpResponseMessage(HttpStatusCode.Unauthorized);
                    response.Headers.WwwAuthenticate.Add(new("Bearer", "resource_metadata=\"https://mcp.example/.well-known/oauth-protected-resource/tools\""));
                    return response;
                }
                if (request.Method != HttpMethod.Post) return new(HttpStatusCode.MethodNotAllowed);
                var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync()).RootElement;
                if (!json.TryGetProperty("id", out var id)) return new(HttpStatusCode.Accepted);
                var method = json.GetProperty("method").GetString();
                return Json(new { jsonrpc = "2.0", id = id.Clone(), result = method == "initialize"
                    ? (object)new { protocolVersion = json.GetProperty("params").GetProperty("protocolVersion").GetString(), capabilities = new { }, serverInfo = new { name = "fixture", version = "1.0" } }
                    : new { } });
            }
            check(request.Headers.Authorization is null, "MCP resource token not sent to OAuth metadata/registration/token endpoints");
            if (uri.AbsolutePath == "/.well-known/oauth-protected-resource/tools")
                return Json(new { resource = Endpoint.AbsoluteUri, authorization_servers = new[] { legacy ? Issuer + "/.well-known/oauth-authorization-server" : Issuer }, scopes_supported = new[] { "mcp.read" } });
            if (uri.AbsolutePath == "/.well-known/oauth-authorization-server")
                return Json(new { issuer = Issuer, authorization_endpoint = Issuer + "/authorize", token_endpoint = Issuer + "/token", registration_endpoint = Issuer + "/register",
                    response_types_supported = new[] { "code" }, grant_types_supported = new[] { "authorization_code", "refresh_token" },
                    token_endpoint_auth_methods_supported = new[] { "none" }, code_challenge_methods_supported = new[] { "S256" } });
            if (uri.AbsolutePath == "/register")
            {
                var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync()).RootElement;
                check(body.GetProperty("redirect_uris")[0].GetString()!.StartsWith("http://127.0.0.1:"), "MCP native registration uses loopback redirect");
                return Json(new { client_id = "native-client", token_endpoint_auth_method = "none" });
            }
            if (uri.AbsolutePath == "/token")
            {
                var body = Query(await request.Content!.ReadAsStringAsync());
                check(body["grant_type"] == "authorization_code" && body["resource"] == Endpoint.AbsoluteUri && body["code"] == "fixture-code", "MCP token exchange keeps resource binding");
                LastCodeVerifier = body["code_verifier"];
                check(Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(LastCodeVerifier))) == challenge, "MCP SDK PKCE S256 verifier matches browser challenge");
                TokenExchanges++;
                return Json(new { access_token = "fixture-access-token", token_type = "Bearer", expires_in = 3600 });
            }
            throw new Exception("Unexpected OAuth fixture URL: " + uri);
        });
        public void Launch(Uri uri)
        {
            Launches++;
            var query = Query(uri.Query.TrimStart('?'));
            challenge = query["code_challenge"];
            check(query["code_challenge_method"] == "S256" && query["client_id"] == "native-client", "MCP browser authorization uses PKCE and registered client");
            check(legacy ? !query.ContainsKey("resource") : query["resource"] == Endpoint.AbsoluteUri, "MCP resource authorization compatibility is narrowly scoped");
            _ = Task.Run(async () =>
            {
                using var http = new HttpClient();
                using var response = await http.GetAsync(query["redirect_uri"] + "?code=fixture-code&state=" + Uri.EscapeDataString(query["state"]));
            });
        }
    }
    private static Dictionary<string, string> Query(string value) => value.Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(p => p.Split('=', 2)).ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1].Replace('+', ' ')));
    private static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    private sealed class CallbackHandler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(send(request)); }
    private sealed class AsyncHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request); }
    private sealed class Host : IDesktopHost
    {
        public string ProfileId => "McpAuthTests"; public bool AllowLocal => true; public string AccountLabel => "fixture"; public string HistoryIdentity => "account";
        public readonly List<ServiceKind> Services = [];
        public Task AuthenticateAsync(HttpRequestMessage request, ServiceKind service, CancellationToken ct)
        { Services.Add(service); request.Headers.Authorization = new("Bearer", "host-token-" + Services.Count); request.Headers.Add("X-App-Credential", "host-secret"); return Task.CompletedTask; }
        public Task ManageAccountAsync(object root, CancellationToken ct) => Task.CompletedTask;
    }
    private sealed class Runtime : IRuntimeResolver
    {
        public Task<Uri> ResolveAsync(ServiceKind kind, DesktopSettings settings, CancellationToken ct) => Task.FromResult(DesktopSettings.RemoteUri(settings.For(kind).RemoteUrl));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class WaitingFactory : IDesktopMcpClientFactory
    {
        public int InteractiveCalls;
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<IDesktopMcpConnection> Complete = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<IDesktopMcpConnection> ConnectAsync(DesktopMcpServer server, CancellationToken ct) => throw new McpSignInRequiredException();
        public Task<IDesktopMcpConnection> ConnectAsync(DesktopMcpServer server, bool interactive, CancellationToken ct)
        { if (!interactive) return ConnectAsync(server, ct); InteractiveCalls++; Started.TrySetResult(); return Complete.Task; }
    }
    private sealed class Connection : IDesktopMcpConnection
    {
        public event Action? ToolsChanged { add { } remove { } } public event Action? ResourcesChanged { add { } remove { } }
        public Task<McpDiscovery> DiscoverAsync(CancellationToken ct) => Task.FromResult(new McpDiscovery(JsonSerializer.SerializeToElement(new { }), JsonSerializer.SerializeToElement(new { }), null, []));
        public Task<JsonElement> CallAsync(string name, JsonElement input, string callId, string locale, CancellationToken ct) => throw new NotSupportedException();
        public Task<JsonElement> ReadAsync(string uri, string? cursor, int limit, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> CompleteAsync(string template, string name, string value, IReadOnlyDictionary<string, string> arguments, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
