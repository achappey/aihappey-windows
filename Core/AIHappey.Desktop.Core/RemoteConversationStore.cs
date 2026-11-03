using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AIHappey.Desktop.Core;

/// <summary>Browser-compatible HTTP storage. Only acknowledged snapshots are retained, in memory only.</summary>
public sealed class RemoteConversationStore(HttpClient http, Uri endpoint,
    Func<HttpRequestMessage, CancellationToken, Task> authenticate, Func<bool> isCurrent)
{
    private readonly SemaphoreSlim writes = new(1, 1);
    private readonly Dictionary<string, JsonObject> snapshots = new(StringComparer.Ordinal);
    private readonly HashSet<string> known = new(StringComparer.Ordinal);
    private readonly HashSet<string> deleted = new(StringComparer.Ordinal);
    private bool? granular;
    private readonly string collection = endpoint.AbsoluteUri.TrimEnd('/');

    private void Check(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!isCurrent()) throw new OperationCanceledException("Conversation account changed.", ct);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string suffix, JsonNode? body, CancellationToken ct)
    {
        Check(ct);
        using var request = new HttpRequestMessage(method, collection + suffix);
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        await authenticate(request, ct);
        Check(ct);
        var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        try { Check(ct); return response; }
        catch { response.Dispose(); throw; }
    }

    private static string Route(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 2048 || id.Contains('/') || id.Contains('\\') || id.Any(char.IsControl)
            || id is "." or "..") throw new ArgumentException("Invalid conversation ID.");
        return "/" + Uri.EscapeDataString(id);
    }

    public async Task<IReadOnlyList<Conversation>> ListAsync(CancellationToken ct = default)
    {
        using var response = await SendAsync(HttpMethod.Get, "/summaries", null, ct);
        response.EnsureSuccessStatusCode();
        var items = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))?.AsArray() ?? throw new JsonException("Missing conversation summaries.");
        Check(ct);
        var output = new List<Conversation>();
        foreach (var node in items)
        {
            var item = node?.AsObject() ?? throw new JsonException("Invalid conversation summary.");
            var id = item["id"]?.GetValue<string>() ?? throw new JsonException("Missing conversation ID.");
            _ = Route(id);
            output.Add(new() { Id = id, Title = item["name"]?.GetValue<string>() ?? DesktopResources.Get("NewChat"),
                Updated = item["activityAt"]?.GetValue<DateTimeOffset>() ?? DateTimeOffset.UnixEpoch,
                IsSummary = true, MessageCount = item["messageCount"]?.GetValue<int>() ?? 0 });
        }
        await writes.WaitAsync(ct);
        try { foreach (var item in output) { known.Add(item.Id); deleted.Remove(item.Id); } }
        finally { writes.Release(); }
        return output.OrderByDescending(item => item.Updated).ToArray();
    }

    private async Task<Conversation?> ReadAsync(string id, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, Route(id), null, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) { snapshots.Remove(id); deleted.Add(id); return null; }
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);
        var conversation = PortableConversations.Read(json);
        if (conversation.Id != id) throw new JsonException("Conversation ID mismatch.");
        Check(ct);
        snapshots[id] = JsonNode.Parse(json)!.AsObject(); known.Add(id); deleted.Remove(id);
        return conversation;
    }

    public async Task<Conversation?> GetAsync(string id, CancellationToken ct = default)
    {
        await writes.WaitAsync(ct);
        try { return await ReadAsync(id, ct); }
        finally { writes.Release(); }
    }

    public async Task SaveAsync(Conversation conversation, CancellationToken ct = default)
    {
        // Freeze the portable view before awaiting network IO: streaming may keep enriching the live message.
        var next = JsonNode.Parse(PortableConversations.Write(conversation))!.AsObject();
        await writes.WaitAsync(ct);
        try
        {
            Check(ct);
            var id = conversation.Id; var route = Route(id);
            if (deleted.Contains(id)) throw new InvalidOperationException(DesktopResources.Get("ConversationNotFound"));
            if (!known.Contains(id))
            {
                using var created = await SendAsync(HttpMethod.Post, "", next, ct);
                created.EnsureSuccessStatusCode();
                known.Add(id); snapshots[id] = next;
                return;
            }
            if (!snapshots.ContainsKey(id) && await ReadAsync(id, ct) is null)
                throw new InvalidOperationException(DesktopResources.Get("ConversationNotFound"));
            var previous = snapshots[id];
            if (granular is null)
            {
                using var capabilities = await SendAsync(HttpMethod.Get, "/capabilities", null, ct);
                if (capabilities.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed) granular = false;
                else
                {
                    capabilities.EnsureSuccessStatusCode();
                    granular = JsonNode.Parse(await capabilities.Content.ReadAsStringAsync(ct))?["granularMessageMutations"]?.GetValue<bool>() == true;
                }
            }
            var oldMessages = previous["messages"]!.AsArray(); var newMessages = next["messages"]!.AsArray();
            var oldIds = oldMessages.Select(MessageId).ToArray(); var newIds = newMessages.Select(MessageId).ToArray();
            if (newIds.Distinct(StringComparer.Ordinal).Count() != newIds.Length) throw new JsonException("Duplicate message ID.");
            var common = newIds.Where(oldIds.Contains).ToArray();
            var appendOnly = newIds.Take(common.Length).SequenceEqual(common)
                && oldIds.Where(newIds.Contains).SequenceEqual(common);
            if (granular != true || !JsonNode.DeepEquals(ComparableMetadata(previous), ComparableMetadata(next)) || !appendOnly)
            {
                using var updated = await SendAsync(HttpMethod.Put, route, next, ct);
                updated.EnsureSuccessStatusCode(); snapshots[id] = next; return;
            }
            foreach (var messageId in oldIds.Where(mid => !newIds.Contains(mid)))
            {
                using var removed = await SendAsync(HttpMethod.Delete, route + "/messages" + Route(messageId), null, ct);
                removed.EnsureSuccessStatusCode();
                oldMessages.Remove(oldMessages.First(message => MessageId(message) == messageId));
            }
            foreach (var message in newMessages)
            {
                var messageId = MessageId(message);
                var old = oldMessages.FirstOrDefault(item => MessageId(item) == messageId);
                if (JsonNode.DeepEquals(old, message)) continue;
                using var changed = await SendAsync(old is null ? HttpMethod.Post : HttpMethod.Patch,
                    route + "/messages" + (old is null ? "" : Route(messageId)), message, ct);
                changed.EnsureSuccessStatusCode();
                if (old is null) oldMessages.Add(message!.DeepClone());
                else oldMessages[oldMessages.IndexOf(old)] = message!.DeepClone();
            }
            snapshots[id] = next;
        }
        finally { writes.Release(); }
    }

    private static string MessageId(JsonNode? message) => message?["id"]?.GetValue<string>() ?? throw new JsonException("Missing message ID.");
    private static JsonNode? ComparableMetadata(JsonObject document)
    {
        var copy = document.DeepClone();
        (copy["metadata"]?[PortableConversations.DesktopMetadata] as JsonObject)?.Remove("updatedAt");
        copy.AsObject().Remove("messages");
        return copy;
    }

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        await writes.WaitAsync(ct);
        try
        {
            using var response = await SendAsync(HttpMethod.Delete, Route(id), null, ct);
            if (response.StatusCode != HttpStatusCode.NotFound) response.EnsureSuccessStatusCode();
            deleted.Add(id); snapshots.Remove(id);
        }
        finally { writes.Release(); }
    }

    public async Task<IReadOnlyList<ConversationSearchHit>> SearchAsync(string query, IReadOnlyList<Conversation> summaries, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query)) return ConversationSearch.Find(summaries, "", ct);
        using var response = await SendAsync(HttpMethod.Get, "/search?query=" + Uri.EscapeDataString(query.Trim()) + "&limit=50", null, ct);
        response.EnsureSuccessStatusCode();
        var results = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))?["results"]?.AsArray() ?? throw new JsonException("Missing search results.");
        Check(ct);
        var byId = summaries.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var hits = new Dictionary<string, ConversationSearchHit>(StringComparer.Ordinal);
        foreach (var item in results)
        {
            var id = item?["conversationId"]?.GetValue<string>();
            if (id is not null && byId.TryGetValue(id, out var conversation)) hits.TryAdd(id, new(conversation, item?["snippet"]?.GetValue<string>()));
        }
        // The server searches message text; retain the existing title-search behavior too.
        foreach (var item in summaries.Where(item => item.Title.Contains(query, StringComparison.OrdinalIgnoreCase))) hits.TryAdd(item.Id, new(item, null));
        return hits.Values.Take(50).ToArray();
    }
}
