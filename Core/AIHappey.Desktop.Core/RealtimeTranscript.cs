using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Vercel.Models;

namespace AIHappey.Desktop.Core;

/// <summary>Reduces normalized realtime events into portable history, never raw audio or credentials.</summary>
public sealed class RealtimeTranscript(Conversation conversation, string model)
{
    private readonly Dictionary<string, ConversationMessage> drafts = new(StringComparer.Ordinal);
    private readonly HashSet<string> completed = new(StringComparer.Ordinal);
    private string? liveDraft;
    private Role? liveSpeaker;
    public ConversationMessage Draft(string id, Role role)
    {
        if (drafts.TryGetValue(id, out var found)) return found;
        var message = new ConversationMessage { Status = "streaming", Message = new UIMessage { Id = Guid.NewGuid().ToString("N"), Role = role,
            Metadata = new() { ["model"] = model, ["realtime"] = true, ["timestamp"] = DateTimeOffset.UtcNow.ToString("O"), ["providerKey"] = RealtimeConfiguration.Provider(model) } } };
        conversation.Messages.Add(message); drafts[id] = message; return message;
    }
    private void Text(string id, Role role, string text, bool final, bool append)
    {
        if (completed.Contains(id)) return;
        var draft = Draft(id, role);
        var result = append ? draft.Text + text : text;
        if (result.Length > 2_000_000) throw new InvalidDataException("Realtime transcript exceeded its limit.");
        draft.Message.Parts.RemoveAll(p => p.Type == "text");
        if (result.Length > 0) draft.Message.Parts.Insert(0, PortableConversations.Part(new JsonObject { ["type"] = "text", ["text"] = result, ["state"] = final ? "done" : "streaming" }));
        if (final) { draft.Status = "complete"; completed.Add(id); }
    }
    public bool Apply(JsonObject value)
    {
        var type = value["type"]?.ToString() ?? "";
        if (type is "session.input_transcript.delta" or "session.output_transcript.delta")
        {
            var role = type == "session.input_transcript.delta" ? Role.user : Role.assistant;
            if (liveSpeaker != role) { liveSpeaker = role; liveDraft = "live:" + Guid.NewGuid().ToString("N"); }
            Text(liveDraft!, role, value["delta"]?.ToString() ?? "", false, true);
            var draft = drafts[liveDraft!];
            draft.Message.Metadata!["end_ms"] = value["end_ms"]?.GetValue<double>() ?? 0;
            draft.Message.Metadata.TryAdd("start_ms", value["start_ms"]?.GetValue<double>() ?? 0);
            return true;
        }
        if (type == "session.closed")
        {
            foreach (var entry in drafts.Where(entry => entry.Key.StartsWith("live:", StringComparison.Ordinal)))
            {
                entry.Value.Status = "complete";
                foreach (var part in entry.Value.Message.Parts.Where(part => part.Type == "text").ToArray())
                {
                    var node = PortableConversations.Node(part); node["state"] = "done";
                    var index = entry.Value.Message.Parts.IndexOf(part); entry.Value.Message.Parts[index] = PortableConversations.Part(node);
                }
            }
            conversation.Metadata["liveFinalUsage"] = JsonSerializer.SerializeToElement(value["usage"]);
            conversation.Metadata["liveCloseReason"] = value["reason"]?.ToString() ?? "unknown";
            return true;
        }
        if (type.StartsWith("conversation.item.input_audio_transcription.", StringComparison.Ordinal))
        {
            var id = "user:" + value["item_id"]?.ToString();
            Text(id, Role.user, (value["transcript"] ?? value["delta"])?.ToString() ?? "", type.EndsWith("completed", StringComparison.Ordinal), type.EndsWith("delta", StringComparison.Ordinal)); return true;
        }
        if (type is "response.output_audio_transcript.delta" or "response.audio_transcript.delta" or "response.output_text.delta" or "response.text.delta")
        { Text("assistant:" + value["response_id"], Role.assistant, value["delta"]?.ToString() ?? "", false, true); return true; }
        if (type is "response.cancelled" or "response.canceled")
        {
            if (drafts.TryGetValue("assistant:" + value["response_id"], out var draft)) draft.Status = "interrupted";
            return true;
        }
        if (type == "response.usage" && value["usage"] is JsonObject usage)
        { SetUsage(Draft("assistant:" + value["response_id"], Role.assistant), usage); return true; }
        if (type != "response.done" || value["response"] is not JsonObject response) return false;
        var responseId = "assistant:" + response["id"];
        var parts = (response["output"] as JsonArray ?? new()).Where(o => o?["type"]?.ToString() == "message")
            .SelectMany(o => o?["content"] as JsonArray ?? new()).ToArray();
        var text = string.Join("\n", parts.Select(p => (p?["transcript"] ?? p?["text"])?.ToString()).Where(t => !string.IsNullOrEmpty(t)));
        if (text.Length > 0 || drafts.ContainsKey(responseId))
        {
            if (text.Length == 0 && drafts.TryGetValue(responseId, out var existing)) text = existing.Text;
            Text(responseId, Role.assistant, text, true, false);
            var output = drafts[responseId];
            if (response["status"]?.ToString() is "cancelled" or "incomplete") output.Status = "interrupted";
            if (response["usage"] is JsonObject finalUsage) SetUsage(output, finalUsage);
        }
        return true;
    }
    public static void SetUsage(ConversationMessage output, JsonObject usage)
    {
        var normalized = new JsonObject { ["inputTokens"] = (usage["input_tokens"] ?? usage["promptTokenCount"] ?? usage["inputTokens"])?.DeepClone(),
            ["outputTokens"] = (usage["output_tokens"] ?? usage["responseTokenCount"] ?? usage["candidatesTokenCount"] ?? usage["outputTokens"])?.DeepClone(),
            ["totalTokens"] = (usage["total_tokens"] ?? usage["totalTokenCount"] ?? usage["totalTokens"])?.DeepClone() };
        if (output.Message.Metadata is null) output.Message = new UIMessage { Id = output.Message.Id, Role = output.Message.Role, Parts = output.Message.Parts, Metadata = new() };
        output.Message.Metadata!["usage"] = JsonSerializer.SerializeToElement(normalized);
    }
    public void Stop()
    {
        foreach (var draft in drafts.Values.Where(d => d.Status == "streaming")) draft.Status = "interrupted";
    }
    public static JsonArray InputContent(UIMessage message, bool google)
    {
        var content = new JsonArray();
        foreach (var part in message.Parts)
        {
            var raw = PortableConversations.Element(part);
            if (part.Type == "text") content.Add(google ? new JsonObject { ["text"] = PortableConversations.Text(part) } : new JsonObject { ["type"] = "input_text", ["text"] = PortableConversations.Text(part) });
            else if (part.Type == "file")
            {
                var mediaType = PortableConversations.String(raw, "mediaType") ?? "";
                var url = PortableConversations.String(raw, "url") ?? "";
                if (!mediaType.StartsWith("image/", StringComparison.Ordinal) || !url.StartsWith("data:" + mediaType + ";base64,", StringComparison.Ordinal))
                    throw new NotSupportedException(DesktopResources.Get("RealtimeUnsupportedAttachment"));
                content.Add(google ? new JsonObject { ["inlineData"] = new JsonObject { ["mimeType"] = mediaType, ["data"] = url[(url.IndexOf(',') + 1)..] } }
                    : new JsonObject { ["type"] = "input_image", ["image_url"] = url });
            }
            else throw new NotSupportedException(DesktopResources.Get("RealtimeUnsupportedAttachment"));
        }
        return content;
    }
}
