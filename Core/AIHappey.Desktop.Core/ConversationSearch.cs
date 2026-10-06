namespace AIHappey.Desktop.Core;

public sealed record ConversationSearchHit(Conversation Conversation, string? Snippet);

/// <summary>Same result semantics as browser search: recent six, first match per chat, bounded text snippets.
/// Operates only on the current history partition, never on credentials, diagnostic events, or tool payloads.</summary>
public static class ConversationSearch
{
    public static IReadOnlyList<ConversationSearchHit> Find(IEnumerable<Conversation> conversations, string query, CancellationToken ct = default)
    {
        var text = query.Trim();
        var results = new List<ConversationSearchHit>();
        var ordered = conversations.OrderByDescending(conversation => conversation.Updated).DistinctBy(conversation => conversation.Id);
        foreach (var conversation in ordered)
        {
            ct.ThrowIfCancellationRequested();
            if (text.Length == 0) results.Add(new(conversation, null));
            else
            {
                var message = conversation.Messages.Select(item => item.Text).FirstOrDefault(value => value.Contains(text, StringComparison.OrdinalIgnoreCase));
                if (message is not null) results.Add(new(conversation, Snippet(message, text)));
                else if (conversation.Title.Contains(text, StringComparison.OrdinalIgnoreCase)) results.Add(new(conversation, null));
            }
            if (results.Count == (text.Length == 0 ? 6 : 50)) break;
        }
        return results;
    }

    private static string Snippet(string value, string query)
    {
        var match = value.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        var start = Math.Max(0, match - 60);
        var end = Math.Min(value.Length, start + 200);
        // Keep Unicode surrogate pairs intact at the clipped edges.
        if (start > 0 && char.IsLowSurrogate(value[start])) start--;
        if (end < value.Length && char.IsHighSurrogate(value[end - 1])) end++;
        return (start > 0 ? "…" : "") + value[start..end].Replace('\r', ' ').Replace('\n', ' ') + (end < value.Length ? "…" : "");
    }
}
