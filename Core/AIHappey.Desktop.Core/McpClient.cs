using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace AIHappey.Desktop.Core;

/// <summary>Hosts may configure SDK OAuth or provide a custom delegating handler later.
/// The supplied handler must enforce the endpoint's credential boundary. Never use gateway authentication.</summary>
public interface IDesktopMcpAuthentication
{
    Task<HttpMessageHandler> ConfigureAsync(DesktopMcpServer server, HttpClientTransportOptions options, CancellationToken ct);
}

public sealed class DesktopMcpHeaderAuthentication : IDesktopMcpAuthentication
{
    public Task<HttpMessageHandler> ConfigureAsync(DesktopMcpServer server, HttpClientTransportOptions options, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        options.AdditionalHeaders = McpValidation.Headers(server.Headers);
        return Task.FromResult<HttpMessageHandler>(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
    }
}

public sealed record McpDiscovery(JsonElement ServerInfo, JsonElement Capabilities, string? Instructions, IReadOnlyList<JsonElement> Tools)
{
    public IReadOnlyList<JsonElement> Resources { get; init; } = [];
    public IReadOnlyList<JsonElement> ResourceTemplates { get; init; } = [];
    public IReadOnlyList<JsonElement> Prompts { get; init; } = [];
    public IReadOnlyList<McpSkillManifest> Skills { get; init; } = [];
}

public interface IDesktopMcpConnection : IAsyncDisposable
{
    bool ManagesToolTimeout => false;
    event Action? ToolsChanged;
    event Action? ResourcesChanged;
    event Action? PromptsChanged { add { } remove { } }
    Task<JsonElement> GetPromptAsync(string name, IReadOnlyDictionary<string, string> arguments, CancellationToken ct) =>
        throw new NotSupportedException();
    Task<McpPromptCompletion> CompletePromptAsync(string prompt, string name, string value,
        IReadOnlyDictionary<string, string> arguments, CancellationToken ct) => Task.FromResult(McpPromptCompletion.Empty);
    Task<McpDiscovery> DiscoverAsync(CancellationToken ct);
    Task<JsonElement> CallAsync(string name, JsonElement input, string callId, string locale, CancellationToken ct);
    Task<JsonElement> ReadAsync(string uri, string? cursor, int limit, CancellationToken ct);
    Task<IReadOnlyList<string>> CompleteAsync(string template, string name, string value, IReadOnlyDictionary<string, string> arguments, CancellationToken ct);
}

public interface IDesktopMcpClientFactory
{
    Task<IDesktopMcpConnection> ConnectAsync(DesktopMcpServer server, CancellationToken ct);
}

public sealed class DesktopMcpClientFactory(IDesktopMcpAuthentication? authentication = null) : IDesktopMcpClientFactory
{
    private readonly IDesktopMcpAuthentication authentication = authentication ?? new DesktopMcpHeaderAuthentication();
    private Func<ModelContextPreferences> preferences = () => new();
    private DesktopElicitationHandler? elicitation;
    public void Configure(Func<ModelContextPreferences> preferences, DesktopElicitationHandler elicitation)
    {
        this.preferences = preferences; this.elicitation = elicitation;
    }
    public async Task<IDesktopMcpConnection> ConnectAsync(DesktopMcpServer server, CancellationToken ct)
    {
        server = server.Clone(); server.Validate();
        var options = new HttpClientTransportOptions { Endpoint = McpValidation.Endpoint(server.Url), TransportMode = HttpTransportMode.StreamableHttp };
        var handler = await authentication.ConfigureAsync(server, options, ct);
        var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan, MaxResponseContentBufferSize = 8 * 1024 * 1024 };
        var transport = new HttpClientTransport(options, http, ownsHttpClient: true);
        var formEnabled = preferences().EnableFormElicitation && elicitation is not null;
        var requests = new McpRequestLifetime();
        try
        {
            var client = await McpClient.CreateAsync(transport, new McpClientOptions
            {
                ClientInfo = new Implementation { Name = "aihappey-desktop", Version = "1.0.0" },
                Capabilities = new ClientCapabilities { Elicitation = formEnabled ? new ElicitationCapability { Form = new() } : null },
                Handlers = new McpClientHandlers
                {
                    ElicitationHandler = formEnabled ? async (request, token) =>
                    {
                        using var lifetime = requests.Link(token);
                        lifetime.Token.ThrowIfCancellationRequested();
                        if (!preferences().EnableFormElicitation || request is null || request.Mode is not (null or "form"))
                            return new ElicitResult { Action = "decline" };
                        return await elicitation!(server.Name + " · " + server.Url, request, lifetime.Token);
                    } : null
                }
            }, cancellationToken: ct);
            return new SdkConnection(client, preferences, requests);
        }
        catch { requests.Dispose(); await transport.DisposeAsync(); throw; }
    }

    private sealed class SdkConnection : IDesktopMcpConnection
    {
        private readonly McpClient client;
        private readonly Func<ModelContextPreferences> preferences;
        private readonly McpRequestLifetime requests;
        public bool ManagesToolTimeout => true;
        private readonly IAsyncDisposable subscription;
        private readonly IAsyncDisposable resourceSubscription;
        private readonly IAsyncDisposable skillSubscription;
        private readonly IAsyncDisposable promptSubscription;
        public event Action? ToolsChanged;
        public event Action? ResourcesChanged;
        public event Action? PromptsChanged;
        public SdkConnection(McpClient client, Func<ModelContextPreferences> preferences, McpRequestLifetime requests)
        {
            this.client = client;
            this.preferences = preferences;
            this.requests = requests;
            subscription = client.RegisterNotificationHandler(NotificationMethods.ToolListChangedNotification, (_, _) =>
            { ToolsChanged?.Invoke(); return ValueTask.CompletedTask; });
            resourceSubscription = client.RegisterNotificationHandler(NotificationMethods.ResourceListChangedNotification, (_, _) =>
            { ResourcesChanged?.Invoke(); return ValueTask.CompletedTask; });
            skillSubscription = client.RegisterNotificationHandler("notifications/skills/list_changed", (_, _) =>
            { ResourcesChanged?.Invoke(); return ValueTask.CompletedTask; });
            promptSubscription = client.RegisterNotificationHandler(NotificationMethods.PromptListChangedNotification, (_, _) =>
            { PromptsChanged?.Invoke(); return ValueTask.CompletedTask; });
        }
        public async Task<McpDiscovery> DiscoverAsync(CancellationToken ct)
        {
            IReadOnlyList<JsonElement> tools = [], resources = [], templates = [], prompts = [];
            if (client.ServerCapabilities.Prompts is not null)
                prompts = await McpCatalogPagination.ReadAsync(async (cursor, token) =>
                {
                    var page = await client.ListPromptsAsync(new ListPromptsRequestParams { Cursor = cursor }, token);
                    return (page.Prompts.Select(p => JsonSerializer.SerializeToElement(p)).ToArray(), page.NextCursor);
                }, ct);
            if (client.ServerCapabilities.Tools is not null)
                tools = await McpCatalogPagination.ReadAsync(async (cursor, token) =>
                {
                    var page = await client.ListToolsAsync(new ListToolsRequestParams { Cursor = cursor }, token);
                    return (page.Tools.Select(t => JsonSerializer.SerializeToElement(t)).ToArray(), page.NextCursor);
                }, ct);
            if (client.ServerCapabilities.Resources is not null)
            {
                resources = await McpCatalogPagination.ReadAsync(async (cursor, token) =>
                {
                    var page = await client.ListResourcesAsync(new ListResourcesRequestParams { Cursor = cursor }, token);
                    return (page.Resources.Select(r => JsonSerializer.SerializeToElement(r)).ToArray(), page.NextCursor);
                }, ct);
                templates = await McpCatalogPagination.ReadAsync(async (cursor, token) =>
                {
                    var page = await client.ListResourceTemplatesAsync(new ListResourceTemplatesRequestParams { Cursor = cursor }, token);
                    return (page.ResourceTemplates.Select(r => JsonSerializer.SerializeToElement(r)).ToArray(), page.NextCursor);
                }, ct);
            }
            IReadOnlyList<McpSkillManifest> skills = [];
            if (preferences().EnableSkills && client.ServerCapabilities.Resources is not null
                && client.ServerCapabilities.Extensions?.ContainsKey("io.modelcontextprotocol/skills") == true)
            {
                var entries = await McpCatalogPagination.ReadAsync(async (cursor, token) =>
                {
                    var response = await client.SendRequestAsync(new JsonRpcRequest { Method = "skills/list",
                        Params = cursor is null ? new JsonObject() : new JsonObject { ["cursor"] = cursor } }, token);
                    var page = JsonSerializer.SerializeToElement(response.Result);
                    if (!page.TryGetProperty("skills", out var list) || list.ValueKind != JsonValueKind.Array)
                        throw new InvalidDataException("Invalid MCP skills list.");
                    return (list.EnumerateArray().Select(s => s.Clone()).ToArray(), CatalogProjection.Text(page, "nextCursor"));
                }, ct);
                skills = entries.Select(McpSkillManifest.Parse).ToArray();
            }
            return new(JsonSerializer.SerializeToElement(client.ServerInfo), JsonSerializer.SerializeToElement(client.ServerCapabilities),
                client.ServerInstructions, tools) { Resources = resources, ResourceTemplates = templates, Skills = skills, Prompts = prompts };
        }
        public async Task<JsonElement> GetPromptAsync(string name, IReadOnlyDictionary<string, string> arguments, CancellationToken ct)
        {
            await requests.Gate.WaitAsync(ct); requests.SetActive(ct);
            try
            {
                var result = await client.GetPromptAsync(new GetPromptRequestParams
                    { Name = name, Arguments = arguments.ToDictionary(p => p.Key, p => JsonSerializer.SerializeToElement(p.Value)) }, ct);
                // Only projected content is sent to the model; client-only metadata never becomes history.
                var json = JsonSerializer.SerializeToNode(result)!.AsObject(); json.Remove("_meta");
                return DesktopMcpPrompts.ValidateResult(JsonSerializer.SerializeToElement(json));
            }
            finally { requests.SetActive(default); requests.Gate.Release(); }
        }
        public async Task<McpPromptCompletion> CompletePromptAsync(string prompt, string name, string value,
            IReadOnlyDictionary<string, string> arguments, CancellationToken ct)
        {
            if (client.ServerCapabilities.Completions is null) return McpPromptCompletion.Empty;
            var result = await client.CompleteAsync(new CompleteRequestParams
            {
                Ref = new PromptReference { Name = prompt }, Argument = new() { Name = name, Value = value },
                Context = new() { Arguments = arguments.ToDictionary(p => p.Key, p => p.Value) }
            }, ct);
            return new(result.Completion.Values.Take(100).ToArray(), result.Completion.Total, result.Completion.HasMore == true);
        }
        public async Task<JsonElement> ReadAsync(string uri, string? cursor, int limit, CancellationToken ct)
        {
            DesktopMcpResources.ValidateUri(uri);
            var meta = new JsonObject { ["limit"] = limit };
            if (cursor is not null) meta["cursor"] = cursor;
            await requests.Gate.WaitAsync(ct); requests.SetActive(ct);
            try
            {
                var result = await client.ReadResourceAsync(new ReadResourceRequestParams { Uri = uri, Meta = meta }, ct);
                var json = JsonSerializer.SerializeToNode(result)!.AsObject(); json.Remove("_meta");
                return DesktopMcpResources.ValidateResult(JsonSerializer.SerializeToElement(json));
            }
            finally { requests.SetActive(default); requests.Gate.Release(); }
        }
        public async Task<IReadOnlyList<string>> CompleteAsync(string template, string name, string value,
            IReadOnlyDictionary<string, string> arguments, CancellationToken ct)
        {
            if (client.ServerCapabilities.Completions is null) return [];
            var result = await client.CompleteAsync(new CompleteRequestParams
            {
                Ref = new ResourceTemplateReference { Uri = template }, Argument = new() { Name = name, Value = value },
                Context = new() { Arguments = arguments.ToDictionary(p => p.Key, p => p.Value) }
            }, ct);
            return result.Completion.Values.Take(100).ToArray();
        }
        public async Task<JsonElement> CallAsync(string name, JsonElement input, string callId, string locale, CancellationToken ct)
        {
            if (input.ValueKind != JsonValueKind.Object) throw new InvalidOperationException(DesktopResources.Get("McpInvalidArguments"));
            var settings = preferences().Clone();
            await requests.Gate.WaitAsync(ct);
            try
            {
                using var timeout = new McpToolTimeout(TimeSpan.FromMinutes(settings.ToolTimeoutMinutes), settings.ResetTimeoutOnProgress, ct);
                requests.SetActive(timeout.Token);
                var result = await client.CallToolAsync(name,
                    input.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone()), timeout, new RequestOptions
                {
                    ProgressToken = new ProgressToken(callId), Meta = new JsonObject { ["chat/locale"] = locale }
                }, timeout.Token);
                timeout.Token.ThrowIfCancellationRequested();
                // _meta is client-only and may contain credentials/UI data. Never forward it to the model.
                var json = JsonSerializer.SerializeToNode(result)!.AsObject(); json.Remove("_meta");
                return JsonSerializer.SerializeToElement(json);
            }
            finally { requests.SetActive(default); requests.Gate.Release(); }
        }
        public async ValueTask DisposeAsync()
        {
            requests.Dispose();
            await subscription.DisposeAsync(); await resourceSubscription.DisposeAsync(); await skillSubscription.DisposeAsync();
            await promptSubscription.DisposeAsync(); await client.DisposeAsync();
        }
    }
}

public enum McpConnectionState { Disabled, Connecting, Connected, Error }
public sealed record McpConnectionView(DesktopMcpServer Server, McpConnectionState State, McpDiscovery? Discovery, string? Error);

/// <summary>One manager per desktop session. Mutations are serialized; epochs reject stale discovery/turn snapshots.</summary>
public sealed class DesktopMcpManager(IDesktopMcpClientFactory factory, DesktopMcpStore store,
    Func<ModelContextPreferences>? preferences = null) : IAsyncDisposable
{
    private sealed class Entry(DesktopMcpServer server)
    {
        public DesktopMcpServer Server = server;
        public McpConnectionState State;
        public McpDiscovery? Discovery;
        public string? Error;
        public IDesktopMcpConnection? Connection;
        public readonly CancellationTokenSource Lifetime = new();
        public Action? Changed;
    }
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly object sync = new();
    private readonly Dictionary<string, Entry> entries = [];
    private string? partition;
    private bool disposed;
    public event Action? Changed;
    public IReadOnlyList<McpConnectionView> Servers
    {
        get { lock (sync) return entries.Values.Select(e => new McpConnectionView(e.Server.Clone(), e.State, e.Discovery, e.Error))
            .OrderBy(s => s.Server.Name, StringComparer.OrdinalIgnoreCase).ToArray(); }
    }
    public async Task<bool> LoadAsync(string nextPartition, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (partition == nextPartition) return false;
            await ClearAsync();
            var result = await store.LoadAsync(nextPartition, ct);
            lock (sync) { partition = nextPartition; foreach (var server in result.Servers) entries.Add(server.Id, new(server)); }
            Changed?.Invoke();
            foreach (var entry in Entries().Where(e => e.Server.Enabled)) await ConnectAsync(entry, ct);
            return result.HasInvalidEntries;
        }
        finally { gate.Release(); }
    }
    public async Task InstallAsync(DesktopMcpServer server, CancellationToken ct)
    {
        server = server.Clone(); server.Validate();
        await gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var next = Entries().Select(e => e.Server.Clone()).Where(s => s.Id != server.Id).Append(server).ToArray();
            await store.SaveAsync(partition ?? throw new InvalidOperationException("MCP state is not initialized."), next, ct);
            Entry? previous; lock (sync) entries.TryGetValue(server.Id, out previous);
            if (previous is not null) await DisconnectAsync(previous);
            var entry = new Entry(server);
            lock (sync) entries[server.Id] = entry;
            Changed?.Invoke();
            if (server.Enabled) await ConnectAsync(entry, ct);
        }
        finally { gate.Release(); }
    }
    public async Task SetEnabledAsync(string id, bool enabled, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            Entry entry; lock (sync) entry = entries[id];
            var server = entry.Server.Clone(); server.Enabled = enabled;
            await store.SaveAsync(partition!, Entries().Select(e => e.Server.Id == id ? server : e.Server), ct);
            await DisconnectAsync(entry);
            // A fresh lifetime also invalidates old request routing even when reconnecting the same URL.
            entry = new(server); lock (sync) entries[id] = entry;
            Changed?.Invoke();
            if (enabled) await ConnectAsync(entry, ct);
        }
        finally { gate.Release(); }
    }
    public async Task RemoveAsync(string id, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            Entry? entry; lock (sync) entries.TryGetValue(id, out entry);
            if (entry is null) return;
            await store.SaveAsync(partition!, Entries().Where(e => e != entry).Select(e => e.Server), ct);
            await DisconnectAsync(entry); lock (sync) entries.Remove(id);
            Changed?.Invoke();
        }
        finally { gate.Release(); }
    }
    private Entry[] Entries() { lock (sync) return entries.Values.ToArray(); }
    public async Task ReconnectAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            foreach (var previous in Entries())
            {
                await DisconnectAsync(previous);
                var next = new Entry(previous.Server.Clone());
                lock (sync) entries[next.Server.Id] = next;
                if (next.Server.Enabled) await ConnectAsync(next, ct);
            }
        }
        finally { gate.Release(); Changed?.Invoke(); }
    }
    public async Task ResetAsync(CancellationToken ct)
    {
        foreach (var entry in Entries()) entry.Lifetime.Cancel();
        await gate.WaitAsync(ct);
        try { await ClearAsync(); } finally { gate.Release(); }
    }
    private async Task ConnectAsync(Entry entry, CancellationToken ct)
    {
        lock (sync) { entry.State = McpConnectionState.Connecting; entry.Error = null; }
        Changed?.Invoke();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, entry.Lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        IDesktopMcpConnection? connection = null;
        try
        {
            connection = await factory.ConnectAsync(entry.Server, timeout.Token);
            var discovery = await connection.DiscoverAsync(timeout.Token);
            timeout.Token.ThrowIfCancellationRequested();
            lock (sync) { entry.Connection = connection; entry.Discovery = discovery; entry.State = McpConnectionState.Connected; }
            entry.Changed = () => _ = RefreshAsync(entry);
            connection.ToolsChanged += entry.Changed;
            connection.ResourcesChanged += entry.Changed;
            connection.PromptsChanged += entry.Changed;
            connection = null;
        }
        catch (Exception e)
        {
            lock (sync) { entry.State = McpConnectionState.Error; entry.Discovery = null; entry.Error = SafeError(e); }
            if (connection is not null) await connection.DisposeAsync();
            if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
        }
        finally { Changed?.Invoke(); }
    }
    private async Task RefreshAsync(Entry entry)
    {
        try
        {
            await gate.WaitAsync(entry.Lifetime.Token);
            try
            {
                if (disposed || entry.Connection is null || entry.State != McpConnectionState.Connected) return;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(entry.Lifetime.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                var discovery = await entry.Connection.DiscoverAsync(timeout.Token);
                timeout.Token.ThrowIfCancellationRequested();
                lock (sync) entry.Discovery = discovery;
                Changed?.Invoke();
            }
            catch (Exception e) when (!entry.Lifetime.IsCancellationRequested)
            { lock (sync) { entry.State = McpConnectionState.Error; entry.Discovery = null; entry.Error = SafeError(e); } Changed?.Invoke(); }
            finally { gate.Release(); }
        }
        catch (OperationCanceledException) { }
    }
    public McpTurnSnapshot Capture()
    {
        lock (sync) return McpTurnSnapshot.Create(entries.Values.Where(e => e.State == McpConnectionState.Connected && e.Server.Enabled && e.Discovery is not null)
            .Select(e => new McpConnectedServer(e.Server.CatalogItem, e.Discovery!,
                (name, input, id, locale, ct) => CallAsync(e, name, input, id, locale, ct),
                (uri, cursor, limit, ct) => RequestAsync(e, (connection, token) => connection.ReadAsync(uri, cursor, limit, token), ct),
                (template, name, value, arguments, ct) => RequestAsync(e,
                    (connection, token) => connection.CompleteAsync(template, name, value, arguments, token), ct))));
    }
    public IReadOnlyList<DesktopMcpSkill> CaptureSkills()
    {
        lock (sync)
        {
            if (preferences?.Invoke().EnableSkills == false) return [];
            return entries.Values.Where(e => e.State == McpConnectionState.Connected && e.Server.Enabled && e.Discovery is not null)
                .SelectMany(e => e.Discovery!.Skills.Select(manifest => new DesktopMcpSkill(e.Server.Url, manifest,
                    (uri, ct) => RequestAsync(e, (connection, token) => connection.ReadAsync(uri, null, 100, token), ct),
                    () => { lock (sync) return preferences?.Invoke().EnableSkills != false && !disposed
                        && entries.TryGetValue(e.Server.Id, out var current) && current == e && e.State == McpConnectionState.Connected && e.Server.Enabled; })))
                .DistinctBy(s => s.Descriptor.Id).ToArray();
        }
    }
    public bool HasPromptCapability => Servers.Any(s => s.State == McpConnectionState.Connected && s.Server.Enabled
        && s.Discovery is { } discovery && DesktopMcpPrompts.Capability(discovery.Capabilities, "prompts"));

    public IReadOnlyList<McpPromptEntry> CapturePrompts()
    {
        lock (sync) return entries.Values.Where(e => e.State == McpConnectionState.Connected && e.Server.Enabled
            && e.Discovery is { } discovery && DesktopMcpPrompts.Capability(discovery.Capabilities, "prompts"))
            .SelectMany(e => e.Discovery!.Prompts.Where(p => CatalogProjection.Text(p, "name") is { Length: > 0 })
                .Select(p => new McpPromptEntry(e.Server.Id, CatalogProjection.Text(e.Discovery.ServerInfo, "title") ?? e.Server.Name,
                    p.Clone(),
                    (name, arguments, ct) => RequestAsync(e, (connection, token) => connection.GetPromptAsync(name, arguments, token), ct),
                    DesktopMcpPrompts.Capability(e.Discovery.Capabilities, "completions")
                        ? (prompt, name, value, arguments, ct) => RequestAsync(e,
                            (connection, token) => connection.CompletePromptAsync(prompt, name, value, arguments, token), ct) : null,
                    () => { lock (sync) return !disposed && entries.TryGetValue(e.Server.Id, out var current) && current == e
                        && e.State == McpConnectionState.Connected && e.Server.Enabled && e.Discovery!.Prompts.Any(item =>
                            CatalogProjection.Text(item, "name") == CatalogProjection.Text(p, "name") && item.GetRawText() == p.GetRawText()); })))
            .ToArray();
    }
    private Task<JsonElement> CallAsync(Entry entry, string name, JsonElement input, string callId, string locale, CancellationToken ct) =>
        RequestAsync(entry, (connection, token) => connection.CallAsync(name, input, callId, locale, token), ct, toolCall: true);

    private async Task<T> RequestAsync<T>(Entry entry, Func<IDesktopMcpConnection, CancellationToken, Task<T>> request, CancellationToken ct, bool toolCall = false)
    {
        IDesktopMcpConnection connection;
        lock (sync)
        {
            if (disposed || !entries.TryGetValue(entry.Server.Id, out var current) || current != entry
                || entry.State != McpConnectionState.Connected || entry.Connection is null)
                throw new InvalidOperationException(DesktopResources.Get("McpDisconnected"));
            connection = entry.Connection;
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, entry.Lifetime.Token);
        // SDK tool calls own the resettable timer. Resources retain their independent request budget.
        if (!toolCall || !connection.ManagesToolTimeout)
            timeout.CancelAfter(TimeSpan.FromMinutes(toolCall ? preferences?.Invoke().ToolTimeoutMinutes ?? 5 : 5));
        try
        {
            var result = await request(connection, timeout.Token);
            timeout.Token.ThrowIfCancellationRequested();
            lock (sync)
                if (disposed || !entries.TryGetValue(entry.Server.Id, out var current) || current != entry || entry.Connection != connection)
                    throw new InvalidOperationException(DesktopResources.Get("McpDisconnected"));
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e)
        {
            if (e is HttpRequestException or IOException)
            { lock (sync) { entry.State = McpConnectionState.Error; entry.Discovery = null; entry.Error = SafeError(e); } Changed?.Invoke(); }
            throw new InvalidOperationException(SafeError(e), e);
        }
    }
    public static string SafeError(Exception error) => error is InvalidOperationException { InnerException: { } inner }
        ? SafeError(inner) : DesktopResources.Get(error switch
    {
        HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden } => "McpAuthRequired",
        OperationCanceledException => "McpTimedOut", _ => "McpConnectionFailed"
    });
    private async Task DisconnectAsync(Entry entry)
    {
        entry.Lifetime.Cancel();
        var connection = entry.Connection;
        lock (sync) { entry.Connection = null; entry.Discovery = null; entry.State = McpConnectionState.Disabled; }
        if (connection is not null)
        {
            if (entry.Changed is not null) connection.ToolsChanged -= entry.Changed;
            if (entry.Changed is not null) connection.ResourcesChanged -= entry.Changed;
            if (entry.Changed is not null) connection.PromptsChanged -= entry.Changed;
            try { await connection.DisposeAsync(); } catch { /* Shutdown/disable must still remove the connection. */ }
        }
        // Retain the canceled source: in-flight snapshots may still use its token.
    }
    private async Task ClearAsync()
    {
        foreach (var entry in Entries()) await DisconnectAsync(entry);
        lock (sync) { entries.Clear(); partition = null; }
        Changed?.Invoke();
    }
    public async ValueTask DisposeAsync()
    {
        foreach (var entry in Entries()) entry.Lifetime.Cancel();
        await gate.WaitAsync();
        try { disposed = true; await ClearAsync(); }
        finally { gate.Release(); }
    }
}
