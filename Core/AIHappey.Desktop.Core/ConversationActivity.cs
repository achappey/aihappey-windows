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
    public static ConversationActivity Project(IReadOnlyList<ConversationMessage> messages)
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
        return new(tools, data);
    }
}
