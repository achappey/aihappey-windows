using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AIHappey.Desktop.Core;

/// <summary>One partition-bound runtime. DeletedIds also prevents in-flight saves from resurrecting a deleted chat.</summary>
public sealed class LocalConversationTools(HistoryStore store, string partition, DocumentTextExtraction extraction,
    Func<bool>? isCurrent = null, Func<string, CancellationToken, Task>? deleted = null)
{
    private readonly HashSet<string> deletedIds = new(StringComparer.Ordinal);
    public bool IsDeleted(string id) => deletedIds.Contains(id);
    public Task SaveAsync(Conversation conversation, CancellationToken ct = default)
    {
        // Final shutdown checkpoints still belong to this captured partition, even after
        // tool execution has been disabled by the closing/current-account guard.
        ct.ThrowIfCancellationRequested();
        return IsDeleted(conversation.Id) ? Task.CompletedTask : store.SaveAsync(partition, conversation, ct);
    }
    private void Check(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (isCurrent?.Invoke() == false) throw new OperationCanceledException(ct);
    }
    public Task<JsonElement> CallAsync(string name, JsonElement input, CancellationToken ct) => DesktopLocalTools.SafeAsync(async () =>
    {
        Check(ct);
        if (name == "local_conversations_list_all")
        {
            var items = await store.ListAsync(partition, ct); Check(ct);
            return DesktopLocalTools.Result(new { conversations = items.Where(c => !IsDeleted(c.Id)).Select(c => new
                { id = c.Id, metadata = new { name = c.Title }, messageCount = c.Messages.Count, activityAt = c.Updated.ToString("O") }) });
        }
        if (name == "local_conversations_search_text")
        {
            var query = DesktopLocalTools.Required(input, "query").Trim();
            if (query.Length == 0) throw new LocalToolException("Missing query.");
            var items = await store.ListAsync(partition, ct); Check(ct);
            return DesktopLocalTools.Result(Search(items.Where(c => !IsDeleted(c.Id)), query, SearchLimit(input), ct));
        }
        var id = DesktopLocalTools.Required(input, "conversationId");
        if (name == "local_conversations_delete_conversation")
        {
            Check(ct); store.Delete(partition, id); deletedIds.Add(id);
            if (deleted is not null) await deleted(id, ct); Check(ct);
            return DesktopLocalTools.Result(new { deletedId = id, status = "deleted" });
        }
        var conversation = IsDeleted(id) ? null : await store.GetAsync(partition, id, ct); Check(ct);
        if (name == "local_conversations_get_conversation") return DesktopLocalTools.Result(Sanitize(conversation));
        if (name != "local_conversations_read_attachment") throw new LocalToolException("Unsupported conversation tool.");
        if (conversation is null) throw new LocalToolException("Conversation was not found.");
        var messageId = DesktopLocalTools.Required(input, "messageId"); var filename = DesktopLocalTools.Required(input, "filename");
        var message = conversation.Messages.FirstOrDefault(m => m.Message.Id == messageId);
        if (message is null) throw new LocalToolException("Message was not found in the conversation.");
        var matches = message.Message.Parts.Select(PortableConversations.Element).Where(p => CatalogProjection.Text(p, "type") == "file"
            && Filename(p) == filename).ToArray();
        if (matches.Length == 0) throw new LocalToolException("Attachment was not found in the message.");
        if (matches.Length > 1) throw new LocalToolException("Attachment filename is ambiguous in the message.");
        var part = matches[0]; var url = CatalogProjection.Text(part, "url");
        if (string.IsNullOrWhiteSpace(url)) throw new LocalToolException("Attachment has no inline data.");
        var mediaType = CatalogProjection.Text(part, "mediaType") ?? CatalogProjection.Text(part, "mimeType") ?? "application/octet-stream";
        var base64 = url;
        if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var match = Regex.Match(url, @"^data:([^;,]+);base64,([\s\S]+)$", RegexOptions.IgnoreCase);
            if (!match.Success) throw new LocalToolException("Attachment has an invalid or non-base64 data URI.");
            base64 = match.Groups[2].Value;
            mediaType = CatalogProjection.Text(part, "mediaType") ?? CatalogProjection.Text(part, "mimeType") ?? match.Groups[1].Value;
        }
        else if (Uri.TryCreate(url, UriKind.Absolute, out _) || url.Contains(':'))
            throw new LocalToolException("Only inline attachments can be read; HTTP URLs and other external resources are not supported.");
        if (base64.Length > (ComposerAttachments.MaximumFileBytes + 2L) / 3 * 4 + 4096)
            throw new LocalToolException("Attachment exceeds the local extraction size limit.");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(base64); }
        catch (FormatException) { throw new LocalToolException("Attachment contains invalid base64 data."); }
        var text = await extraction.ExtractAsync(filename, mediaType, bytes, ct); Check(ct);
        if (string.IsNullOrWhiteSpace(text)) throw new LocalToolException("Attachment is unsupported or contains no extractable text.");
        return DesktopLocalTools.Result(new { conversationId = id, messageId, filename, mediaType, text });
    }, ct);

    private static string? Filename(JsonElement part)
    {
        if (CatalogProjection.Text(part, "filename") is { } filename) return filename;
        return part.TryGetProperty("providerMetadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object
            && metadata.TryGetProperty("openai", out var openai) ? CatalogProjection.Text(openai, "filename") : null;
    }
    public static JsonObject? Sanitize(Conversation? conversation)
    {
        if (conversation is null) return null;
        var copy = JsonNode.Parse(PortableConversations.Write(conversation))!.AsObject();
        foreach (var message in copy["messages"]!.AsArray().OfType<JsonObject>())
            foreach (var part in (message["parts"] as JsonArray ?? []).OfType<JsonObject>())
                if (OpenAIChatConfig.Text(part["type"]) == "file") part.Remove("url");
        return copy;
    }
    private static int SearchLimit(JsonElement input) => input.TryGetProperty("limit", out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)
        && double.IsFinite(number) ? (int)Math.Clamp(Math.Floor(number), 1, 50) : 20;
    public static object Search(IEnumerable<Conversation> conversations, string query, int limit = 20, CancellationToken ct = default)
    {
        var terms = Regex.Split(query.Trim().ToLowerInvariant(), @"\s+").Where(s => s.Length > 0).Distinct().ToArray();
        if (terms.Length == 0) throw new LocalToolException("Missing query.");
        limit = Math.Clamp(limit, 1, 50); var results = new List<object>();
        foreach (var conversation in conversations)
            for (var messageIndex = 0; messageIndex < conversation.Messages.Count; messageIndex++)
            {
                ct.ThrowIfCancellationRequested(); var message = conversation.Messages[messageIndex].Message;
                var parts = message.Parts.Where(p => p.Type == "text").ToArray();
                for (var partIndex = 0; partIndex < parts.Length; partIndex++)
                {
                    var text = PortableConversations.Text(parts[partIndex]);
                    var indexes = terms.Select(t => text.IndexOf(t, StringComparison.OrdinalIgnoreCase)).ToArray();
                    if (indexes.Any(i => i < 0)) continue;
                    var first = indexes.Min(); var compact = Regex.Replace(text, @"\s+", " ").Trim();
                    var start = compact.Length > 320 ? Math.Min(compact.Length, Math.Max(0, first - 90)) : 0;
                    var excerpt = compact.Length > 320 ? compact.Substring(start, Math.Min(260, compact.Length - start)) : compact;
                    results.Add(new { conversationId = conversation.Id, messageId = message.Id, messageIndex,
                        role = message.Role.ToString(), partIndex, matchIndex = first,
                        snippet = (start > 0 ? "…" : "") + excerpt + (start + excerpt.Length < compact.Length ? "…" : "") });
                    if (results.Count >= limit) return new { query = query.Trim(), total = results.Count, limit, results };
                }
            }
        return new { query = query.Trim(), total = results.Count, limit, results };
    }
}
