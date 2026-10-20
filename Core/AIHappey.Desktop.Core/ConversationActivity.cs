using AIHappey.Vercel.Models;

namespace AIHappey.Desktop.Core;

public sealed record ConversationActivityItem(
    UIMessagePart Part,
    string MessageId,
    DateTimeOffset Timestamp,
    int MessageIndex,
    int PartIndex);

public sealed record ConversationActivity(
    IReadOnlyList<ConversationActivityItem> Tools,
    IReadOnlyList<ConversationActivityItem> Data);

/// <summary>
/// Projects conversation-wide activity without changing portable messages or part order.
/// Both lists are newest-first, matching the browser activity drawer.
/// </summary>
public static class ConversationActivityProjection
{
    public static ConversationActivity Project(IReadOnlyList<ConversationMessage> messages,
        IReadOnlyList<ConversationActivityItem>? transientData = null)
    {
        var tools = new List<ConversationActivityItem>();
        var data = new List<ConversationActivityItem>();
        for (var messageIndex = messages.Count - 1; messageIndex >= 0; messageIndex--)
        {
            var message = messages[messageIndex];
            for (var partIndex = message.Message.Parts.Count - 1; partIndex >= 0; partIndex--)
            {
                var part = message.Message.Parts[partIndex];
                var item = new ConversationActivityItem(part, message.Message.Id, message.Timestamp, messageIndex, partIndex);
                if (PortableConversations.IsTool(part)) tools.Add(item);
                else if (part.Type.StartsWith("data-", StringComparison.Ordinal)) data.Add(item);
            }
        }
        // Transient events arrive after persisted message parts, as in the browser drawer.
        if (transientData is not null) data.InsertRange(0, transientData.Reverse());
        return new(tools, data);
    }
}

/// <summary>UI-only data, never attached to portable messages, history, exports, or replay requests.</summary>
public sealed class TransientConversationData
{
    private string? partition, conversationId;
    private long scope;
    private readonly List<ConversationActivityItem> items = [];
    public IReadOnlyList<ConversationActivityItem> Items => items;

    public long SetScope(string nextPartition, string nextConversationId)
    {
        if (partition == nextPartition && conversationId == nextConversationId) return scope;
        partition = nextPartition; conversationId = nextConversationId;
        items.Clear(); return ++scope;
    }

    public static bool IsTransient(StreamEvent item) => item.Type.StartsWith("data-", StringComparison.Ordinal)
        && item.Raw.TryGetProperty("transient", out var transient) && transient.ValueKind == System.Text.Json.JsonValueKind.True;

    public bool Add(long expectedScope, StreamEvent item, ConversationMessage message)
    {
        if (scope != expectedScope || !IsTransient(item)) return false;
        items.Add(new(new PortableUIPart(item.Raw.Clone()), message.Message.Id, message.Timestamp, -1, items.Count));
        return true;
    }
}
