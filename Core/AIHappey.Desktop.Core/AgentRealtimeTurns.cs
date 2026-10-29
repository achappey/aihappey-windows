using System.Text.Json.Nodes;
using AIHappey.Vercel.Models;

namespace AIHappey.Desktop.Core;

/// <summary>Chat utterance boundaries, not the transcription library's cumulative document.
/// Only nonempty transcription interrupts a run; acoustic/VAD hints alone never do.</summary>
public sealed class AgentRealtimeTurns(string provider, Action started, Action<string> preview, Action<string> turn) : IDisposable
{
    private readonly object gate = new();
    private readonly HashSet<string> events = new(StringComparer.Ordinal), completed = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> partials = new(StringComparer.Ordinal);
    private readonly List<string> fragments = [];
    private Timer? timer;
    private bool active, disposed;
    private string? unkeyedFinal;
    public void Apply(JsonObject message)
    {
        lock (gate)
        {
            if (disposed) return;
            if (message["event_id"] is { } eventId && !events.Add(eventId.ToString())) return;
            var type = message["type"]?.ToString();
            if (provider == "openai")
            {
                var id = message["item_id"]?.ToString();
                if (id is null || completed.Contains(id)) return;
                if (type == "conversation.item.input_audio_transcription.delta")
                {
                    var delta = message["delta"]?.ToString() ?? "";
                    partials[id] = partials.GetValueOrDefault(id, "") + delta;
                    if (!string.IsNullOrWhiteSpace(delta)) Preview(partials[id]);
                }
                if (type == "conversation.item.input_audio_transcription.completed") Complete(message["transcript"]?.ToString() ?? "", id);
            }
            else if (provider == "gladia" && type == "transcript")
            {
                var data = message["data"]; var utterance = data?["utterance"];
                var id = (utterance?["start"] ?? data?["id"])?.ToString();
                if (id is not null && completed.Contains(id)) return;
                var text = utterance?["text"]?.ToString() ?? "";
                if (data?["is_final"]?.GetValue<bool>() == true) Complete(text, id); else Preview(text);
            }
            else if (provider == "smallestai" && type == "transcription")
            {
                var text = (message["transcript"] ?? message["transcription"])?.ToString() ?? "";
                // Repeated words are valid successive turns; only keyed duplicates are deduplicated.
                if (message["is_final"]?.GetValue<bool>() == true) { unkeyedFinal = null; Complete(text, null); } else Preview(text);
            }
            else if (provider == "google" && message["serverContent"] is JsonObject content)
            {
                if (content["interimInputTranscription"]?["text"] is { } interim) Preview(interim.ToString());
                if (content["inputTranscription"]?["text"] is { } final && !string.IsNullOrWhiteSpace(final.ToString()))
                {
                    Start(); fragments.Add(final.ToString()); preview(string.Join(" ", fragments));
                    timer?.Dispose(); timer = new Timer(_ => { lock (gate) Finish(); }, null, 900, Timeout.Infinite);
                }
                if (content["turnComplete"]?.GetValue<bool>() == true) Finish();
            }
        }
    }
    private void Start() { if (!active) { active = true; started(); } }
    private void Preview(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        timer?.Dispose(); timer = null; unkeyedFinal = null; Start();
        preview(string.Join(" ", fragments.Append(text)));
    }
    private void Complete(string text, string? id)
    {
        if (id is not null) { if (!completed.Add(id)) return; partials.Remove(id); }
        if (string.IsNullOrWhiteSpace(text) || id is null && text == unkeyedFinal) return;
        unkeyedFinal = text; Start(); active = false; preview(text); turn(text.Trim());
    }
    private void Finish()
    {
        timer?.Dispose(); timer = null;
        if (disposed || fragments.Count == 0) return;
        var text = string.Join(" ", fragments).Trim(); fragments.Clear(); active = false;
        if (text.Length > 0) turn(text);
    }
    public void Reset()
    {
        lock (gate)
        {
            timer?.Dispose(); timer = null; foreach (var id in partials.Keys) completed.Add(id);
            partials.Clear(); fragments.Clear(); active = false;
        }
    }
    public void Dispose() { lock (gate) { disposed = true; Reset(); } }
}

public static class AgentRealtimeResponse
{
    /// <summary>Speak the final response, never earlier tool-step narration, reasoning or tool payloads.</summary>
    public static string FinalText(IEnumerable<UIMessagePart> parts)
    {
        var text = new List<string>();
        foreach (var part in parts)
        {
            if (part.Type == "step-start" || PortableConversations.IsTool(part)) text.Clear();
            else if (part.Type == "text") text.Add(PortableConversations.Text(part));
        }
        return string.Join("\n\n", text).Trim();
    }
}
