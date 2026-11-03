using System.Text.Json;

namespace AIHappey.Desktop.Core;

/// <summary>Routes captured partitions, never a late-bound selected store. Local files remain unchanged.</summary>
public sealed class DesktopConversationStorage(DesktopSession session, HttpClient http, HistoryStore local)
{
    private readonly Dictionary<string, RemoteConversationStore> sources = new(StringComparer.Ordinal);
    public HistoryStore Local { get; } = local;
    public void PrepareRemoteSource()
    {
        lock (sources)
        {
            var host = session.Host as IDesktopConversationHost;
            var endpoint = host?.ConversationsEndpoint ?? throw new InvalidOperationException(DesktopResources.Get("ConversationCloudUnavailable"));
            var identity = session.Host.HistoryIdentity;
            var partition = session.RemoteHistoryPartition;
            if (!sources.ContainsKey(partition)) sources[partition] = new(http, endpoint,
                (request, ct) => host.AuthenticateConversationsAsync(request, false, ct),
                () => session.Host.HistoryIdentity == identity && !session.Host.RequiresSignIn && host.ConversationsEndpoint == endpoint);
        }
    }
    private RemoteConversationStore? Remote(string partition)
    {
        lock (sources)
        {
            if (sources.TryGetValue(partition, out var existing)) return existing;
            if (partition == session.LocalHistoryPartition) return null;
            if (partition != session.HistoryPartition) throw new OperationCanceledException("Conversation source changed.");
            if (!session.UsesRemoteConversations) return null;
            var host = (IDesktopConversationHost)session.Host;
            var identity = session.Host.HistoryIdentity;
            var endpoint = host.ConversationsEndpoint!;
            bool Current() => session.Host.HistoryIdentity == identity && !session.Host.RequiresSignIn
                && host.ConversationsEndpoint == endpoint;
            return sources[partition] = new(http, endpoint, (request, ct) => host.AuthenticateConversationsAsync(request, false, ct), Current);
        }
    }
    public void ClearRemoteCaches() { lock (sources) sources.Clear(); }
    public Task<IReadOnlyList<Conversation>> ListAsync(string partition, CancellationToken ct = default) =>
        Remote(partition) is { } remote ? remote.ListAsync(ct) : Local.ListAsync(partition, ct);
    public Task<Conversation?> GetAsync(string partition, string id, CancellationToken ct = default) =>
        Remote(partition) is { } remote ? remote.GetAsync(id, ct) : Local.GetAsync(partition, id, ct);
    public Task SaveAsync(string partition, Conversation conversation, CancellationToken ct = default)
    {
        conversation.Updated = DateTimeOffset.UtcNow;
        return Remote(partition) is { } remote ? remote.SaveAsync(conversation, ct) : Local.SaveAsync(partition, conversation, ct);
    }
    public Task DeleteAsync(string partition, string id, CancellationToken ct = default)
    {
        if (Remote(partition) is { } remote) return remote.DeleteAsync(id, ct);
        ct.ThrowIfCancellationRequested(); Local.Delete(partition, id); return Task.CompletedTask;
    }
    public string DocumentPath(string partition, string id) => Local.DocumentPath(partition, id);
    public async Task<Conversation?> ImportAsync(string partition, string json, CancellationToken ct = default)
    {
        if (Remote(partition) is not { } remote) return await Local.ImportAsync(partition, json, ct);
        var conversation = PortableConversations.Read(json);
        if ((await remote.ListAsync(ct)).Any(item => item.Id == conversation.Id)) return null;
        if (conversation.Messages.Select(item => item.Message.Id).Distinct(StringComparer.Ordinal).Count() != conversation.Messages.Count)
            throw new JsonException("Duplicate message ID.");
        await remote.SaveAsync(conversation, ct);
        return conversation;
    }
    public async Task<IReadOnlyList<Conversation>> LoadAllAsync(string partition, CancellationToken ct)
    {
        var items = await ListAsync(partition, ct);
        if (Remote(partition) is null) return items;
        var output = new List<Conversation>();
        foreach (var item in items)
            if (await GetAsync(partition, item.Id, ct) is { } conversation) output.Add(conversation);
        return output;
    }
    public Task<IReadOnlyList<ConversationSearchHit>> SearchAsync(string partition, string query, IReadOnlyList<Conversation> summaries, CancellationToken ct) =>
        Remote(partition) is { } remote ? remote.SearchAsync(query, summaries, ct)
            : Task.FromResult(ConversationSearch.Find(summaries, query, ct));
}
