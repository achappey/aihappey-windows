using AIHappey.Vercel.Models;
using System.Globalization;
using System.Text;
using System.Text.Json;

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
    public static string ToolStateResourceKey(JsonElement part)
    {
        if (part.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Object
            && output.TryGetProperty("isError", out var error) && error.ValueKind == JsonValueKind.True) return "ToolStateError";
        return PortableConversations.String(part, "state") switch
        {
            "input-streaming" => "ToolStateInputStreaming",
            "input-available" => "ToolStateInputAvailable",
            "approval-requested" => "ToolStateApprovalRequested",
            "approval-responded" => DesktopToolApprovals.Approved(part) switch
            {
                true => "ToolStateOutputReviewed", false => "ToolStateOutputDenied", _ => "ToolStateApprovalResponded"
            },
            "output-available" => "ToolStateOutputAvailable",
            "output-error" => "ToolStateError",
            "output-denied" => "ToolStateOutputDenied",
            _ => "ToolStateUnknown"
        };
    }

    public static string? ToolOutputSize(JsonElement part)
    {
        if (PortableConversations.String(part, "state") is not ("approval-responded" or "output-available")
            || !part.TryGetProperty("output", out var output)
            || output.ValueKind == JsonValueKind.Object && output.TryGetProperty("isError", out var error) && error.ValueKind == JsonValueKind.True)
            return null;
        return FormatOutputSize(CompactJsonBytes(output));
    }

    public static string FormatOutputSize(long bytes)
    {
        if (bytes < 1024) return bytes.ToString(CultureInfo.InvariantCulture) + " B";
        var value = (double)bytes / 1024;
        var unit = "KB";
        if (value >= 1024) { value /= 1024; unit = "MB"; }
        if (value >= 1024) { value /= 1024; unit = "GB"; }
        return value.ToString("F1", CultureInfo.InvariantCulture) + " " + unit;
    }

    // Match TextEncoder(JSON.stringify(output)): no indentation or HTML/Unicode escaping.
    // Counting avoids allocating a second serialized copy of potentially large tool results.
    private static long CompactJsonBytes(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => 2 + value.EnumerateObject().Sum(p => JsonStringBytes(p.Name) + 1 + CompactJsonBytes(p.Value))
            + Math.Max(0, value.GetPropertyCount() - 1),
        JsonValueKind.Array => 2 + value.EnumerateArray().Sum(CompactJsonBytes) + Math.Max(0, value.GetArrayLength() - 1),
        JsonValueKind.String => JsonStringBytes(value.GetString()!),
        _ => value.GetRawText().Length
    };

    private static long JsonStringBytes(string value)
    {
        long bytes = 2; // Quotes, plus JSON's required escapes only.
        foreach (var rune in value.EnumerateRunes())
            bytes += rune.Value switch
            {
                '"' or '\\' or '\b' or '\f' or '\n' or '\r' or '\t' => 2,
                < 0x20 => 6,
                _ => rune.Utf8SequenceLength
            };
        return bytes;
    }

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
