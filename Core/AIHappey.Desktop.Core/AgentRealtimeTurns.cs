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
    private bool muted, speaking, mistralDone;
    private string mistralText = "";
    private string deepLPartial = "";
    private double deepLDiscardThrough = -1, deepLDraftThrough = -1;
    private int mistralThrough, mistralTimerVersion;
    public void Activity(bool value)
    {
        lock (gate)
        {
            if (disposed || muted || provider is not ("mistral" or "deepl")) return;
            speaking = value;
            if (provider == "mistral") ScheduleMistral(); else ScheduleDeepL();
        }
    }
    public void SetMuted(bool value)
    {
        lock (gate) { muted = value; if (value) Reset(); }
    }
    public void Apply(JsonObject message)
    {
        lock (gate)
        {
            if (disposed) return;
            if (muted && provider is not ("mistral" or "deepl")) return;
            if (message["event_id"] is { } eventId && !events.Add(eventId.ToString())) return;
            var type = message["type"]?.ToString();
            if (provider == "mistral") ApplyMistral(message, type);
            else if (provider == "deepl" && type == "deepl.transcript")
            {
                double End(JsonNode? segment) => double.Parse(segment!["end_time"]!.ToString(), System.Globalization.CultureInfo.InvariantCulture);
                var all = message["concluded"]!.AsArray().Concat(message["tentative"]!.AsArray()).ToArray();
                if (all.Length > 0) deepLDraftThrough = Math.Max(deepLDraftThrough, all.Max(End));
                if (muted) { deepLDiscardThrough = deepLDraftThrough; return; }
                foreach (var segment in message["concluded"]!.AsArray())
                {
                    if (End(segment) <= deepLDiscardThrough) continue;
                    var key = new JsonArray(segment!["start_time"]!.DeepClone(), segment["end_time"]!.DeepClone(), segment["text"]!.DeepClone()).ToJsonString();
                    if (!completed.Add(key)) continue;
                    var text = segment["text"]!.GetValue<string>(); fragments.Add(text);
                    if (!string.IsNullOrWhiteSpace(text)) Start();
                }
                deepLPartial = string.Concat(message["tentative"]!.AsArray().Where(segment => End(segment) > deepLDiscardThrough).Select(segment => segment!["text"]!.GetValue<string>()));
                if (!string.IsNullOrWhiteSpace(deepLPartial)) Start();
                preview(string.Concat(fragments) + deepLPartial); ScheduleDeepL();
            }
            else if (provider == "openai")
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
    private void ScheduleDeepL()
    {
        timer?.Dispose(); timer = null;
        var version = ++mistralTimerVersion;
        if (disposed || muted || speaking || deepLPartial.Length > 0 || fragments.Count == 0) return;
        timer = new Timer(_ =>
        {
            lock (gate)
                if (version == mistralTimerVersion && !speaking && !muted && !disposed) Finish();
        }, null, 900, Timeout.Infinite);
    }
    private void ApplyMistral(JsonObject message, string? type)
    {
        if (mistralDone || message["text"] is not JsonValue value || !value.TryGetValue<string>(out var text)) return;
        if (type == "transcription.text.delta")
        {
            mistralText += text;
            if (muted) { mistralThrough = mistralText.Length; return; }
            if (!string.IsNullOrWhiteSpace(text))
            {
                Start(); preview(mistralText[mistralThrough..].Trim()); ScheduleMistral();
            }
        }
        else if (type == "transcription.done")
        {
            mistralDone = true;
            // The final is cumulative. Never replay corrections to a prefix that
            // has already been submitted, reset, or discarded while muted.
            if (text.StartsWith(mistralText[..mistralThrough], StringComparison.Ordinal)) mistralText = text;
            if (muted) mistralThrough = mistralText.Length;
            var tail = mistralText[mistralThrough..].Trim();
            if (tail.Length > 0 && !muted) { Start(); preview(tail); }
            FinishMistral();
        }
    }
    private void ScheduleMistral()
    {
        timer?.Dispose(); timer = null;
        var version = ++mistralTimerVersion;
        if (disposed || muted || speaking || string.IsNullOrWhiteSpace(mistralText[mistralThrough..])) return;
        timer = new Timer(_ =>
        {
            lock (gate)
                if (version == mistralTimerVersion && !speaking && !muted && !disposed) FinishMistral();
        }, null, 900, Timeout.Infinite);
    }
    private void FinishMistral()
    {
        ++mistralTimerVersion; timer?.Dispose(); timer = null;
        var text = mistralText[mistralThrough..].Trim();
        mistralThrough = mistralText.Length; active = false;
        if (text.Length > 0 && !muted && !disposed) turn(text);
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
        var text = (provider == "deepl" ? string.Concat(fragments) : string.Join(" ", fragments)).Trim(); fragments.Clear(); active = false;
        if (text.Length > 0) turn(text);
    }
    public void Reset()
    {
        lock (gate)
        {
            timer?.Dispose(); timer = null; foreach (var id in partials.Keys) completed.Add(id);
            partials.Clear(); fragments.Clear(); active = false;
            deepLPartial = "";
            deepLDiscardThrough = Math.Max(deepLDiscardThrough, deepLDraftThrough);
            ++mistralTimerVersion; speaking = false; mistralThrough = mistralText.Length;
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
