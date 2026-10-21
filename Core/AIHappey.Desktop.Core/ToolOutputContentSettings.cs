using System.Text.Json.Nodes;

namespace AIHappey.Desktop.Core;

/// <summary>Browser-compatible preferences for the request-only view of client tool results.</summary>
public sealed class ToolOutputContentSettings
{
    public bool Text { get; set; } = true;
    public bool Images { get; set; }
    public bool Audio { get; set; }
    public bool Video { get; set; }
    public bool Documents { get; set; }

    public ToolOutputContentSettings Clone() => new()
    {
        Text = Text, Images = Images, Audio = Audio, Video = Video, Documents = Documents
    };

    // Only call on a serialized request, never on live or persisted message parts.
    // Keep this aligned with chat/engine/mapToolOutputContentForRequest.ts in the chat app.
    internal void ApplyToRequest(JsonObject request)
    {
        if (request["messages"] is not JsonArray messages) return;
        foreach (var message in messages.OfType<JsonObject>())
        {
            if (message["parts"] is not JsonArray parts) continue;
            foreach (var part in parts.OfType<JsonObject>())
            {
                if (part["providerExecuted"] is not JsonValue executed || !executed.TryGetValue<bool>(out var providerExecuted)
                    || providerExecuted || part["output"] is not JsonObject output || output["content"] is not JsonArray content) continue;
                for (var i = 0; i < content.Count; i++)
                {
                    var omitted = OmittedCategory(content[i]);
                    if (omitted is not null)
                        content[i] = new JsonObject { ["type"] = "text", ["text"] = $"{omitted} content omitted by user settings." };
                }
            }
        }
    }

    private string? OmittedCategory(JsonNode? content)
    {
        if (content is not JsonObject block) return null;
        if (String(block["type"]) == "text") return Text ? null : "Text";
        var mime = String(String(block["type"]) == "resource" ? (block["resource"] as JsonObject)?["mimeType"] : block["mimeType"]);
        if (mime is null) return null;
        var normalized = mime.Trim().ToLowerInvariant();
        if (normalized.StartsWith("text/", StringComparison.Ordinal)
            || normalized is "application/json" or "application/xml" or "application/javascript" or "application/x-javascript" or "application/graphql"
            || normalized.EndsWith("+json", StringComparison.Ordinal) || normalized.EndsWith("+xml", StringComparison.Ordinal))
            return Text ? null : "Text";
        if (normalized.StartsWith("image/", StringComparison.Ordinal)) return Images ? null : "Image";
        if (normalized.StartsWith("audio/", StringComparison.Ordinal)) return Audio ? null : "Audio";
        if (normalized.StartsWith("video/", StringComparison.Ordinal)) return Video ? null : "Video";
        if (normalized.StartsWith("application/", StringComparison.Ordinal)) return Documents ? null : "Document";
        return null;
    }

    private static string? String(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
