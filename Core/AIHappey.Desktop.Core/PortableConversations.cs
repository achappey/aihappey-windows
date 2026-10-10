using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AIHappey.Vercel.Models;

namespace AIHappey.Desktop.Core;

/// <summary>Desktop boundary codec. The document is the shared id/messages/metadata contract,
/// not the desktop's in-memory view model. Raw parts preserve future SDK/provider fields.</summary>
public static class PortableConversations
{
    public const string DesktopMetadata = "aihappeyDesktop";
    public static JsonSerializerOptions Json { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        options.Converters.Add(new PortableMessageConverter());
        options.Converters.Add(new PortablePartsConverter());
        return options;
    }

    public static Conversation Read(string json) => JsonSerializer.Deserialize<Conversation>(json, Json)
        ?? throw new JsonException("Missing conversation.");
    public static string Write(Conversation conversation) => JsonSerializer.Serialize(conversation, Json);

    public static JsonElement Element(UIMessagePart part) => part is PortableUIPart raw
        ? raw.Value : JsonSerializer.SerializeToElement(part, part.GetType(), Json);
    public static string? String(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(key, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    public static string? MetadataString(Dictionary<string, object>? metadata, string key) => metadata is not null
        && metadata.TryGetValue(key, out var value) ? value as string ?? (value is JsonElement element && element.ValueKind == JsonValueKind.String ? element.GetString() : null) : null;
    public static JsonObject Object(object? value) => value is null ? new() : JsonSerializer.SerializeToNode(value, Json) as JsonObject ?? new();
    public static PortableUIPart Part(JsonObject value) => new(JsonSerializer.SerializeToElement(value, Json));
    public static PortableUIPart Part(JsonElement value) => new(value.Clone());
    public static JsonObject Node(UIMessagePart part) => JsonNode.Parse(Element(part).GetRawText())!.AsObject();
    public static string Text(UIMessagePart part) => String(Element(part), "text") ?? "";
    public static bool IsTool(UIMessagePart part) => part.Type == "dynamic-tool" || part.Type.StartsWith("tool-", StringComparison.Ordinal);
    public static string ToolName(UIMessagePart part) => String(Element(part), "title") ?? String(Element(part), "toolName")
        ?? (part.Type.StartsWith("tool-", StringComparison.Ordinal) ? part.Type[5..] : "Tool");

    public static UIMessage WithMetadata(UIMessage message, Dictionary<string, object> metadata) => new PortableUIMessage
    {
        Id = message.Id, Role = message.Role, Parts = message.Parts, Metadata = metadata,
        Extra = message is PortableUIMessage portable ? portable.Extra : []
    };

    public static bool CanReplay(ConversationMessage message)
    {
        if (message.Message.Role is Role.user or Role.system) return true;
        if (message.Status != "complete") return false;
        return message.Message.Parts.All(part =>
        {
            var state = String(Element(part), "state");
            var raw = Element(part);
            return !IsTool(part) || state is "output-available" or "output-error" or "output-denied"
                || state == "approval-responded" && (DesktopToolApprovals.Approved(raw) == false
                    || DesktopToolApprovals.Approved(raw) == true && DesktopToolApprovals.HasOutput(raw));
        });
    }
}

[JsonConverter(typeof(PortablePartConverter))]
public sealed class PortableUIPart(JsonElement value) : UIMessagePart
{
    public JsonElement Value { get; } = value;
    public override string Type { get; init; } = PortableConversations.String(value, "type") ?? "unknown";
}

public sealed class PortablePartConverter : JsonConverter<PortableUIPart>
{
    public override PortableUIPart Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    { using var doc = JsonDocument.ParseValue(ref reader); return new(doc.RootElement.Clone()); }
    public override void Write(Utf8JsonWriter writer, PortableUIPart value, JsonSerializerOptions options) => value.Value.WriteTo(writer);
}

internal sealed class PortablePartsConverter : JsonConverter<UIMessagePart>
{
    public override UIMessagePart Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        if (PortableConversations.String(doc.RootElement, "type") is null) throw new JsonException("Missing part type.");
        return new PortableUIPart(doc.RootElement.Clone());
    }
    public override void Write(Utf8JsonWriter writer, UIMessagePart value, JsonSerializerOptions options)
    {
        if (value is PortableUIPart raw) raw.Value.WriteTo(writer);
        else JsonSerializer.Serialize(writer, value, value.GetType(), options);
    }
}

public sealed class PortableUIMessage : UIMessage
{
    public Dictionary<string, JsonElement> Extra { get; init; } = [];
}

internal sealed class PortableMessageConverter : JsonConverter<UIMessage>
{
    public override UIMessage Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;
        var id = PortableConversations.String(root, "id");
        var role = PortableConversations.String(root, "role");
        if (string.IsNullOrWhiteSpace(id)) throw new JsonException("Invalid UI message: missing or empty string id.");
        if (!Enum.TryParse<Role>(role, out var parsedRole) || !Enum.IsDefined(parsedRole))
            throw new JsonException("Invalid UI message: role must be user, assistant, or system.");
        if (!root.TryGetProperty("parts", out var parts) || parts.ValueKind != JsonValueKind.Array)
            throw new JsonException("Invalid UI message: parts must be an array.");
        return new PortableUIMessage
        {
            Id = id, Role = parsedRole, Parts = parts.Deserialize<List<UIMessagePart>>(options)!,
            Metadata = root.TryGetProperty("metadata", out var metadata) && metadata.ValueKind != JsonValueKind.Null
                ? metadata.Deserialize<Dictionary<string, object>>(options) : null,
            Extra = root.EnumerateObject().Where(p => p.Name is not "id" and not "role" and not "parts" and not "metadata")
                .ToDictionary(p => p.Name, p => p.Value.Clone())
        };
    }
    public override void Write(Utf8JsonWriter writer, UIMessage value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("id", value.Id); writer.WriteString("role", value.Role.ToString());
        writer.WritePropertyName("parts"); JsonSerializer.Serialize(writer, value.Parts, options);
        if (value.Metadata is not null) { writer.WritePropertyName("metadata"); JsonSerializer.Serialize(writer, value.Metadata, options); }
        if (value is PortableUIMessage portable)
            foreach (var (key, extra) in portable.Extra) { writer.WritePropertyName(key); extra.WriteTo(writer); }
        writer.WriteEndObject();
    }
}

public sealed class ConversationConverter : JsonConverter<Conversation>
{
    public override Conversation Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;
        var id = PortableConversations.String(root, "id");
        if (string.IsNullOrWhiteSpace(id) || !root.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array)
            throw new JsonException("Invalid conversation document.");
        var metadata = root.TryGetProperty("metadata", out var meta) && meta.ValueKind != JsonValueKind.Null
            ? meta.Deserialize<Dictionary<string, object>>(PortableConversations.Json) ?? [] : [];
        var desktop = metadata.TryGetValue(PortableConversations.DesktopMetadata, out var hints) ? PortableConversations.Object(hints) : new();
        var result = new Conversation
        {
            Id = id, Metadata = metadata,
            Title = PortableConversations.MetadataString(metadata, "name") ?? DesktopResources.Get("NewChat"),
            Service = desktop["service"]?.ToString() == "agents" ? ServiceKind.Agents : ServiceKind.Ai,
            Target = desktop["target"]?.ToString() ?? "",
            Extra = root.EnumerateObject().Where(p => p.Name is not "id" and not "messages" and not "metadata")
                .ToDictionary(p => p.Name, p => p.Value.Clone())
        };
        foreach (var value in messages.EnumerateArray())
        {
            var message = value.Deserialize<UIMessage>(PortableConversations.Json)!;
            var messageHints = message.Metadata?.TryGetValue(PortableConversations.DesktopMetadata, out var hint) == true
                ? PortableConversations.Object(hint) : new();
            var state = messageHints["status"]?.ToString();
            var partStates = message.Parts.Select(p => PortableConversations.String(PortableConversations.Element(p), "state")).ToArray();
            result.Messages.Add(new()
            {
                Message = message,
                Timestamp = DateTimeOffset.TryParse(PortableConversations.MetadataString(message.Metadata, "timestamp"), out var timestamp) ? timestamp : DateTimeOffset.UnixEpoch,
                Status = state ?? (partStates.Any(s => s == "approval-requested") ? "approval required"
                    : partStates.Any(s => s is "streaming" or "input-streaming" or "input-available") ? "interrupted" : "complete"),
                ErrorMessage = GatewayErrors.Display(messageHints["errorMessage"]?.ToString())
            });
        }
        if (string.IsNullOrEmpty(result.Target)) result.Target = result.Messages.AsEnumerable().Reverse()
            .Where(m => m.Message.Role == Role.assistant).Select(m => PortableConversations.MetadataString(m.Message.Metadata, "model")).FirstOrDefault(m => !string.IsNullOrWhiteSpace(m)) ?? "";
        result.Updated = DateTimeOffset.TryParse(desktop["updatedAt"]?.ToString(), out var updated) ? updated
            : result.Messages.Select(m => m.Timestamp).DefaultIfEmpty(DateTimeOffset.UnixEpoch).Max();
        return result;
    }

    public override void Write(Utf8JsonWriter writer, Conversation value, JsonSerializerOptions options)
    {
        var metadata = value.Metadata.ToDictionary(p => p.Key, p => p.Value);
        metadata["name"] = value.Title;
        var desktop = metadata.TryGetValue(PortableConversations.DesktopMetadata, out var hints) ? PortableConversations.Object(hints) : new();
        desktop["service"] = value.Service == ServiceKind.Agents ? "agents" : "ai";
        desktop["target"] = value.Target; desktop["updatedAt"] = value.Updated.ToString("O");
        metadata[PortableConversations.DesktopMetadata] = desktop;
        writer.WriteStartObject(); writer.WriteString("id", value.Id); writer.WritePropertyName("messages"); writer.WriteStartArray();
        foreach (var item in value.Messages)
        {
            var messageMetadata = item.Message.Metadata?.ToDictionary(p => p.Key, p => p.Value) ?? [];
            if (!messageMetadata.ContainsKey("timestamp") && item.Timestamp != DateTimeOffset.UnixEpoch) messageMetadata["timestamp"] = item.Timestamp.ToString("O");
            var messageHints = messageMetadata.TryGetValue(PortableConversations.DesktopMetadata, out var hint) ? PortableConversations.Object(hint) : new();
            messageHints["status"] = item.Status; messageMetadata[PortableConversations.DesktopMetadata] = messageHints;
            if (item.ErrorMessage is { } errorMessage) messageHints["errorMessage"] = errorMessage;
            else messageHints.Remove("errorMessage");
            JsonSerializer.Serialize(writer, PortableConversations.WithMetadata(item.Message, messageMetadata), PortableConversations.Json);
        }
        writer.WriteEndArray(); writer.WritePropertyName("metadata"); JsonSerializer.Serialize(writer, metadata, PortableConversations.Json);
        foreach (var (key, extra) in value.Extra) { writer.WritePropertyName(key); extra.WriteTo(writer); }
        writer.WriteEndObject();
    }
}
