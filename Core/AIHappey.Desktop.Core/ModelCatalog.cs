using System.Text.Json;

namespace AIHappey.Desktop.Core;

/// <summary>One cache-first catalog per active context. Refreshes never own the UI's busy state.</summary>
public sealed class DesktopModelCatalog(Func<string> scope, Func<CancellationToken, Task<JsonElement>> discover,
    DesktopModelCatalogStore store, CancellationToken lifetime) : IDisposable
{
    private readonly object gate = new();
    private Entry? current;
    private bool disposed;
    private sealed class Entry
    {
        public Entry(string scope, CancellationToken lifetime)
        {
            Scope = scope; Lifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime); Token = Lifetime.Token;
        }
        public string Scope { get; }
        public CancellationTokenSource Lifetime { get; }
        public CancellationToken Token { get; }
        public Task<JsonElement?>? Hydration;
        public Task<JsonElement>? Pending;
        public JsonElement? Response;
        public bool BackgroundStarted;
    }

    public event Action<string, IReadOnlyList<ChatTarget>>? Changed;
    public event Action<string>? RefreshFailed;

    public static IReadOnlyList<ChatTarget> Project(JsonElement response) => AiModelCatalog.NewestFirst(
        response.GetProperty("data").EnumerateArray().Select(model => AiModelCatalog.Project(model, ServiceKind.Ai)));

    private Entry Context()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var key = scope();
            if (current?.Scope == key) return current;
            current?.Lifetime.Cancel(); current?.Lifetime.Dispose();
            return current = new(key, lifetime);
        }
    }

    private void RequireCurrent(Entry entry)
    {
        entry.Token.ThrowIfCancellationRequested();
        if (disposed || !ReferenceEquals(current, entry) || entry.Scope != scope()) throw new OperationCanceledException();
    }

    public async Task<IReadOnlyList<ChatTarget>> ListAsync(CancellationToken ct, bool fresh = false)
    {
        ct.ThrowIfCancellationRequested();
        var entry = Context();
        Task<JsonElement?> hydration;
        lock (gate) hydration = entry.Hydration ??= store.LoadAsync(entry.Scope, entry.Token);
        var saved = await hydration.WaitAsync(ct).ConfigureAwait(false);
        JsonElement? available;
        lock (gate)
        {
            RequireCurrent(entry);
            entry.Response ??= saved;
            available = entry.Response;
            if (!fresh && available.HasValue && !entry.BackgroundStarted)
            {
                entry.BackgroundStarted = true;
                _ = ObserveRefreshAsync(entry);
            }
        }
        if (fresh || !available.HasValue) available = await Refresh(entry).WaitAsync(ct).ConfigureAwait(false);
        lock (gate) { RequireCurrent(entry); available = entry.Response ?? available; }
        ct.ThrowIfCancellationRequested();
        return Project(available.Value);
    }

    private Task<JsonElement> Refresh(Entry entry)
    {
        lock (gate)
        {
            RequireCurrent(entry);
            if (entry.Pending is { IsCompleted: false }) return entry.Pending;
            entry.BackgroundStarted = true;
            return entry.Pending = Task.Run(async () =>
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(entry.Token);
                timeout.CancelAfter(TimeSpan.FromMinutes(2));
                var response = await discover(timeout.Token).ConfigureAwait(false);
                if (!DesktopModelCatalogStore.ValidResponse(response)) throw new GatewayException(DesktopResources.Get("InvalidModelCatalog"));
                lock (gate) { RequireCurrent(entry); entry.Response = response.Clone(); }
                await store.SaveAsync(entry.Scope, response, timeout.Token).ConfigureAwait(false);
                lock (gate) RequireCurrent(entry);
                Changed?.Invoke(entry.Scope, Project(response));
                return response;
            });
        }
    }

    private async Task ObserveRefreshAsync(Entry entry)
    {
        try { await Refresh(entry).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch { if (!disposed && entry.Scope == scope()) RefreshFailed?.Invoke(entry.Scope); }
    }

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true; current?.Lifetime.Cancel();
            current?.Lifetime.Dispose();
            current = null;
        }
    }
}
