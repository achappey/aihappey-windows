using System.Text.Json.Nodes;

namespace AIHappey.Desktop.Core;

public sealed record RealtimeTranscriptionSegment(string Id, string Text, double StartSecond, double EndSecond, bool EstimatedTiming);
public sealed record RealtimeTranscriptionSnapshot(string Text, string Preview, IReadOnlyList<RealtimeTranscriptionSegment> Segments)
{
    public string? Language { get; init; }
    public bool ExactSpacing { get; init; }
    public bool HasConfirmedText => !string.IsNullOrWhiteSpace(Text);
    public string DisplayText => ExactSpacing ? Text + Preview : string.Join(" ", new[] { Text, Preview }.Where(s => !string.IsNullOrWhiteSpace(s)));
    public JsonObject Response(string model, string status) => new()
    {
        ["text"] = Text,
        ["language"] = Language,
        ["segments"] = new JsonArray(Segments.Select(s => (JsonNode)new JsonObject { ["text"] = s.Text, ["startSecond"] = s.StartSecond,
            ["endSecond"] = s.EndSecond, ["estimatedTiming"] = s.EstimatedTiming }).ToArray()),
        ["durationInSeconds"] = Segments.Count == 0 ? null : JsonValue.Create(Segments.Max(s => s.EndSecond)),
        ["warnings"] = new JsonArray(),
        ["response"] = new JsonObject { ["modelId"] = model, ["timestamp"] = DateTimeOffset.UtcNow.ToString("O"),
            ["body"] = new JsonObject { ["kind"] = "realtime", ["status"] = status } }
    };
}

/// <summary>Pure protocol reducer. Final text survives missing timestamps, reordered events and preview replacement.</summary>
public sealed class RealtimeTranscriptionTranscript(string provider)
{
    private sealed class Item(string id, int order)
    {
        public string Id = id, Text = "", Partial = "";
        public int Order = order, Connection;
        public double? Start, End;
        public bool Final;
    }
    private readonly Dictionary<string, Item> items = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string?> previous = new(StringComparer.Ordinal);
    private int sequence;
    private double lastEnd;
    private string preview = "";
    private string? language;
    private bool mistralDone;
    private int connection;
    private double connectionOffset;
    private Item Get(string id)
    {
        if (!items.TryGetValue(id, out var item))
        {
            if (items.Count >= 100_000) throw new InvalidDataException("Transcription segment limit exceeded.");
            items[id] = item = new(id, sequence++) { Connection = connection };
        }
        return item;
    }
    public RealtimeTranscriptionSnapshot Snapshot()
    {
        if (provider == "deepl")
        {
            var orderedSegments = items.Values.OrderBy(item => item.Order).ToArray();
            var exact = string.Concat(orderedSegments.Select(item => item.Text));
            if (exact.Length + preview.Length > 2_000_000) throw new InvalidDataException("Transcription text limit exceeded.");
            return new(exact, preview, orderedSegments.Select(item => new RealtimeTranscriptionSegment(item.Id, item.Text, item.Start ?? 0, item.End ?? 0, false)).ToArray())
                { Language = language, ExactSpacing = true };
        }
        if (provider == "mistral")
        {
            var exactText = items.GetValueOrDefault("mistral-session")?.Text ?? "";
            if (exactText.Length > 2_000_000) throw new InvalidDataException("Transcription text limit exceeded.");
            return new(exactText, "", []); // Deltas are already final; no provider timestamps are supplied.
        }
        var ordered = items.Values.OrderBy(i => i.Connection).ThenBy(i => i.Start ?? double.MaxValue).ThenBy(i => i.Order).ToList();
        // OpenAI commit events explicitly identify predecessor order, even when completion events arrive out of order.
        if (provider == "openai" && previous.Count > 0)
        {
            var result = new List<Item>(); var visited = new HashSet<string>();
            foreach (var item in ordered)
            {
                var chain = new List<Item>(); var current = item;
                while (visited.Add(current.Id))
                {
                    chain.Add(current);
                    if (previous.GetValueOrDefault(current.Id) is not { } id || !items.TryGetValue(id, out var before)) break;
                    current = before;
                }
                chain.Reverse(); result.AddRange(chain);
            }
            ordered = result;
        }
        var segments = ordered.Where(i => i.Final && !string.IsNullOrWhiteSpace(i.Text)).Select(i => new RealtimeTranscriptionSegment(i.Id, i.Text.Trim(),
            Math.Max(0, i.Start ?? 0), Math.Max(i.Start ?? 0, i.End ?? i.Start ?? 0), i.Start is null || i.End is null || provider == "google")).ToArray();
        var text = string.Join(" ", segments.Select(s => s.Text));
        var partial = provider == "openai" ? string.Join(" ", ordered.Where(i => !i.Final).Select(i => i.Partial).Where(s => s.Length > 0)) : preview;
        if (text.Length + partial.Length > 2_000_000) throw new InvalidDataException("Transcription text limit exceeded.");
        return new(text, partial, segments);
    }
    public bool Apply(JsonObject message, double elapsed)
    {
        if (provider == "deepl")
        {
            if (message["type"]?.ToString() != "deepl.transcript") return false;
            foreach (var segment in message["concluded"]!.AsArray())
            {
                var segmentId = new JsonArray(segment!["start_time"]!.DeepClone(), segment["end_time"]!.DeepClone(), segment["text"]!.DeepClone()).ToJsonString();
                var item = Get(segmentId); item.Text = segment["text"]!.GetValue<string>(); item.Final = true;
                item.Start = Number(segment["start_time"]) / 1000; item.End = Number(segment["end_time"]) / 1000;
            }
            language = message["language"]?.ToString() ?? language;
            preview = string.Concat(message["tentative"]!.AsArray().Select(segment => segment!["text"]!.GetValue<string>()));
            return true;
        }
        if (provider == "openai" && message["type"]?.ToString() == "aihappey.transcription.connection")
        {
            connection = message["connection"]!.GetValue<int>();
            connectionOffset = (Number(message["offset_ms"]) ?? 0) / 1000;
            foreach (var item in items.Values.Where(item => !item.Final)) item.Partial = "";
            return true;
        }
        if (provider == "mistral")
        {
            var eventType = message["type"]?.ToString();
            if (eventType is not ("transcription.text.delta" or "transcription.done") || mistralDone) return false;
            if (message["text"] is not JsonValue value || !value.TryGetValue<string>(out var text)) throw new InvalidDataException("Invalid Mistral transcript.");
            var item = Get("mistral-session");
            if (eventType == "transcription.done") { item.Text = text; mistralDone = true; }
            else item.Text += text;
            item.Final = true; return true;
        }
        if (provider == "smallestai")
        {
            if (message["type"]?.ToString() != "transcription") return false;
            var text = (message["transcript"] ?? message["transcription"])?.ToString() ?? "";
            if (message["is_final"]?.GetValue<bool>() != true) { preview = text; return true; }
            preview = "";
            if (string.IsNullOrWhiteSpace(text)) return true;
            var item = Get("smallestai-" + sequence); item.Text = text; item.Final = true;
            var words = (message["words"] as JsonArray ?? new()).Where(word => Number(word?["start"]) is not null && Number(word?["end"]) is not null).ToArray();
            item.Start = words.Length > 0 ? words.Min(word => Number(word?["start"])!.Value) : null;
            item.End = words.Length > 0 ? words.Max(word => Number(word?["end"])!.Value) : null;
            // Keep elapsed fallback separate from verified provider timing.
            if (item.Start is not null) lastEnd = item.End ?? item.Start.Value;
            return true;
        }
        if (provider == "google")
        {
            var content = message["serverContent"];
            if (content?["interimInputTranscription"]?["text"] is { } interim) { preview = interim.ToString(); return true; }
            if (content?["inputTranscription"]?["text"] is { } final)
            {
                var item = Get("google-" + sequence); item.Text = final.ToString(); item.Final = true; item.Start = lastEnd; item.End = Math.Max(lastEnd, elapsed);
                lastEnd = item.End.Value; preview = ""; return true;
            }
            return false;
        }
        if (provider == "gladia")
        {
            if (message["type"]?.ToString() != "transcript") return false;
            var data = message["data"]; var utterance = data?["utterance"];
            var text = utterance?["text"]?.ToString() ?? "";
            if (data?["is_final"]?.GetValue<bool>() == true)
            {
                var item = Get(data["id"]?.ToString() ?? "gladia-" + sequence); item.Final = true; item.Text = text;
                item.Start = Number(utterance?["start"]); item.End = Number(utterance?["end"]); preview = "";
            }
            else preview = text;
            return true;
        }
        var type = message["type"]?.ToString(); var id = message["item_id"]?.ToString();
        if (id is null) return false;
        if (provider == "openai") id = connection + ":" + id;
        var next = Get(id);
        switch (type)
        {
            case "input_audio_buffer.committed": previous[id] = message["previous_item_id"] is { } before ? connection + ":" + before.ToString() : null; break;
            case "input_audio_buffer.speech_started": next.Start = Number(message["audio_start_ms"]) / 1000 + connectionOffset; break;
            case "input_audio_buffer.speech_stopped": next.End = Number(message["audio_end_ms"]) / 1000 + connectionOffset; break;
            case "conversation.item.input_audio_transcription.delta": if (!next.Final) next.Partial += message["delta"]?.ToString(); break;
            case "conversation.item.input_audio_transcription.completed": next.Text = message["transcript"]?.ToString() ?? ""; next.Partial = ""; next.Final = true; break;
            default: return false;
        }
        return true;
    }
    private static double? Number(JsonNode? value) => double.TryParse(value?.ToString(), System.Globalization.NumberStyles.Float,
        System.Globalization.CultureInfo.InvariantCulture, out var result) && double.IsFinite(result) && result >= 0 ? result : null;
}
