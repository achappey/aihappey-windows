using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
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

public sealed record McpDiscovery(JsonElement ServerInfo, JsonElement Capabilities, string? Instructions, IReadOnlyList<JsonElement> Tools);

public interface IDesktopMcpConnection : IAsyncDisposable
{
    event Action? ToolsChanged;
    Task<McpDiscovery> DiscoverAsync(CancellationToken ct);
    Task<JsonElement> CallAsync(string name, JsonElement input, string callId, string locale, CancellationToken ct);
}

public interface IDesktopMcpClientFactory
{
    Task<IDesktopMcpConnection> ConnectAsync(DesktopMcpServer server, CancellationToken ct);
}

public sealed class DesktopMcpClientFactory(IDesktopMcpAuthentication? authentication = null) : IDesktopMcpClientFactory
{
    private readonly IDesktopMcpAuthentication authentication = authentication ?? new DesktopMcpHeaderAuthentication();
    public async Task<IDesktopMcpConnection> ConnectAsync(DesktopMcpServer server, CancellationToken ct)
    {
        server = server.Clone(); server.Validate();
        var options = new HttpClientTransportOptions { Endpoint = McpValidation.Endpoint(server.Url), TransportMode = HttpTransportMode.StreamableHttp };
        var handler = await authentication.ConfigureAsync(server, options, ct);
        var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan, MaxResponseContentBufferSize = 8 * 1024 * 1024 };
        var transport = new HttpClientTransport(options, http, ownsHttpClient: true);
        try
        {
            var client = await McpClient.CreateAsync(transport, new McpClientOptions
            {
                ClientInfo = new Implementation { Name = "aihappey-desktop", Version = "1.0.0" },
                Capabilities = new ClientCapabilities()
            }, cancellationToken: ct);
            return new SdkConnection(client);
        }
        catch { await transport.DisposeAsync(); throw; }
    }

    private sealed class SdkConnection : IDesktopMcpConnection
    {
        private readonly McpClient client;
        private readonly IAsyncDisposable subscription;
        public event Action? ToolsChanged;
        public SdkConnection(McpClient client)
        {
            this.client = client;
            subscription = client.RegisterNotificationHandler(NotificationMethods.ToolListChangedNotification, (_, _) =>
            { ToolsChanged?.Invoke(); return ValueTask.CompletedTask; });
        }
        public async Task<McpDiscovery> DiscoverAsync(CancellationToken ct)
        {
            // Explicit pagination protects against repeated cursors and unbounded server catalogs.
            var tools = new List<JsonElement>(); var seen = new HashSet<string>(); string? cursor = null;
            if (client.ServerCapabilities.Tools is not null)
                for (var page = 0; ; page++)
                {
                    if (page >= 1000 || tools.Count >= 10000) throw new InvalidOperationException("MCP tool limit.");
                    var result = await client.ListToolsAsync(new ListToolsRequestParams { Cursor = cursor }, ct);
                    tools.AddRange(result.Tools.Select(t => JsonSerializer.SerializeToElement(t)));
                    cursor = result.NextCursor;
                    if (string.IsNullOrEmpty(cursor)) break;
                    if (!seen.Add(cursor)) throw new InvalidOperationException("Repeated tools cursor.");
                }
            return new(JsonSerializer.SerializeToElement(client.ServerInfo), JsonSerializer.SerializeToElement(client.ServerCapabilities),
                client.ServerInstructions, tools);
        }
        public async Task<JsonElement> CallAsync(string name, JsonElement input, string callId, string locale, CancellationToken ct)
        {
            if (input.ValueKind != JsonValueKind.Object) throw new InvalidOperationException(DesktopResources.Get("McpInvalidArguments"));
            var result = await client.CallToolAsync(new CallToolRequestParams
            {
                Name = name, Arguments = input.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone()),
                Meta = new JsonObject { ["progressToken"] = callId, ["chat/locale"] = locale }
            }, ct);
            // _meta is client-only and may contain credentials/UI data. Never forward it to the model.
            var json = JsonSerializer.SerializeToNode(result)!.AsObject(); json.Remove("_meta");
            return JsonSerializer.SerializeToElement(json);
        }
        public async ValueTask DisposeAsync() { await subscription.DisposeAsync(); await client.DisposeAsync(); }
    }
}

public enum McpConnectionState { Disabled, Connecting, Connected, Error }
public sealed record McpConnectionView(DesktopMcpServer Server, McpConnectionState State, McpDiscovery? Discovery, string? Error);

/// <summary>One manager per desktop session. Mutations are serialized; epochs reject stale discovery/turn snapshots.</summary>
public sealed class DesktopMcpManager(IDesktopMcpClientFactory factory, DesktopMcpStore store) : IAsyncDisposable
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
            .Select(e => (e.Server.CatalogItem, e.Discovery!, (Func<string, JsonElement, string, string, CancellationToken, Task<JsonElement>>)
                ((name, input, id, locale, ct) => CallAsync(e, name, input, id, locale, ct)))));
    }
    private async Task<JsonElement> CallAsync(Entry entry, string name, JsonElement input, string callId, string locale, CancellationToken ct)
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
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        try { return await connection.CallAsync(name, input, callId, locale, timeout.Token); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e)
        {
            if (e is HttpRequestException or IOException)
            { lock (sync) { entry.State = McpConnectionState.Error; entry.Discovery = null; entry.Error = SafeError(e); } Changed?.Invoke(); }
            throw new InvalidOperationException(SafeError(e));
        }
    }
    public static string SafeError(Exception error) => DesktopResources.Get(error switch
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
