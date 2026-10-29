using System.Text.Json.Nodes;

namespace AIHappey.Desktop.Core;

/// <summary>Provider protocol only; credentials and audio devices are owned by the caller.</summary>
public abstract class StreamingSpeechProtocol
{
    public abstract string Provider { get; }
    public abstract Uri Endpoint { get; }
    public int SampleRate { get; protected init; }
    public virtual bool CanFlush => false;
    public virtual int QuietPeriodMilliseconds => 0;
    public abstract JsonObject Append(string text);
    public abstract JsonObject Finish();
    public abstract JsonObject Cancel();
    public virtual string? Audio(JsonObject message) => null;
    public abstract bool Done(JsonObject message);
    public static readonly IReadOnlyDictionary<string, Func<string, JsonObject, StreamingSpeechProtocol>> Factories =
        new Dictionary<string, Func<string, JsonObject, StreamingSpeechProtocol>>(StringComparer.Ordinal)
        {
            ["deepgram"] = (id, options) => new DeepgramSpeech(id, options),
            ["cartesia"] = (id, options) => new CartesiaSpeech(id, options),
            ["smallestai"] = (id, options) => new SmallestSpeech(id, options)
        };
    public static bool Supports(ChatTarget? target) => target?.ModelType == "speech"
        && target.Tags.Contains("real-time") && target.Tags.Contains("realtime-token")
        && Factories.ContainsKey(target.Id.Split('/')[0]);
    public static StreamingSpeechProtocol Create(string model, JsonObject options)
    {
        var slash = model.IndexOf('/');
        if (slash < 1 || !Factories.TryGetValue(model[..slash], out var factory)) throw new InvalidOperationException("Unsupported streaming speech provider.");
        return factory(model[(slash + 1)..], options);
    }
    private static int Rate(JsonObject options, int[] allowed)
    {
        var value = options["sampleRate"] ?? options["sample_rate"];
        var rate = value is null ? 24000 : value.GetValue<int>();
        if (!allowed.Contains(rate)) throw new InvalidOperationException("Unsupported streaming speech sample rate.");
        return rate;
    }
    private static double? Bounded(JsonNode? value, double min, double max)
    {
        if (value is null) return null;
        var result = value.GetValue<double>();
        if (!double.IsFinite(result) || result < min || result > max) throw new InvalidOperationException("Invalid streaming speech setting.");
        return result;
    }
    private sealed class SmallestSpeech : StreamingSpeechProtocol
    {
        public override string Provider => "smallestai";
        public override Uri Endpoint => new("wss://api.smallest.ai/waves/v1/tts/live");
        public override int QuietPeriodMilliseconds => 5000;
        private readonly string context = Guid.NewGuid().ToString();
        private readonly string voice;
        private readonly JsonObject payload;
        public SmallestSpeech(string id, JsonObject options)
        {
            var parts = id.Split('/');
            var model = parts[0].Replace('-', '_');
            voice = (parts.Length == 2 ? parts[1] : (options["voice"] ?? options["voiceId"] ?? options["voice_id"])?.ToString())?.Trim() ?? "";
            if (parts.Length > 2 || model is not ("lightning_v3.1" or "lightning_v3.1_pro") || voice.Length == 0)
                throw new InvalidOperationException("Smallest AI streaming speech requires a supported model and voice.");
            SampleRate = Rate(options, [8000, 16000, 24000, 44100]);
            payload = new() { ["model"] = model, ["voice_id"] = voice, ["sample_rate"] = SampleRate, ["output_format"] = "pcm",
                ["speed"] = Bounded(options["speed"], .5, 2) ?? 1, ["context_id"] = context, ["max_buffer_delay_ms"] = 200, ["complete_backoff_ms"] = 0 };
            string[] languages = ["auto", "en", "hi", "mr", "kn", "ta", "bn", "gu", "te", "ml", "pa", "or", "es", "de", "fr", "it", "nl", "sv", "pt", "ru", "el", "fi", "no", "pl", "ar", "zh", "id", "ja", "ko", "ms", "tr", "vi"];
            foreach (var (source, target) in new[] { ("language", "language"), ("numberPronunciationLanguage", "number_pronunciation_language"), ("dateFormat", "date_format") })
            {
                var value = options[source] ?? options[target];
                if (value is null || value.ToString().Length == 0) continue;
                if (!(target == "date_format" ? new[] { "EU", "US" } : languages).Contains(value.ToString())) throw new InvalidOperationException("Unsupported Smallest AI speech language or date format.");
                payload[target] = value.DeepClone();
            }
            foreach (var (source, target) in new[] { ("wordTimestamps", "word_timestamps"), ("mathNotation", "math_notation") })
                if ((options[source] ?? options[target]) is { } value) payload[target] = value.GetValue<bool>();
        }
        public override JsonObject Append(string text) { var result = (JsonObject)payload.DeepClone(); result["text"] = text; result["continue"] = true; return result; }
        public override JsonObject Finish() => new() { ["context_id"] = context, ["voice_id"] = voice, ["continue"] = false };
        public override JsonObject Cancel() => new() { ["context_id"] = context, ["cancel_request"] = true };
        public override string? Audio(JsonObject message) => message["status"]?.ToString() == "chunk" ? message["data"]?["audio"]?.GetValue<string>() : null;
        public override bool Done(JsonObject message) => message["status"]?.ToString() == "complete";
    }
    private sealed class DeepgramSpeech : StreamingSpeechProtocol
    {
        public override string Provider => "deepgram";
        public override Uri Endpoint { get; }
        public override bool CanFlush => true;
        public DeepgramSpeech(string model, JsonObject options)
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(model, "^aura-(?:2-)?[a-z]+-[a-z]{2}$")) throw new InvalidOperationException("Unsupported Deepgram speech model.");
            SampleRate = Rate(options, [8000, 16000, 24000, 32000, 48000]);
            var query = $"model={Uri.EscapeDataString(model)}&encoding=linear16&sample_rate={SampleRate}";
            if (Bounded(options["speed"], 0.7, 1.5) is { } speed) query += "&speed=" + speed.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if ((options["mipOptOut"] ?? options["mip_opt_out"]) is JsonValue flag && flag.TryGetValue<bool>(out var optOut)) query += "&mip_opt_out=" + optOut.ToString().ToLowerInvariant();
            Endpoint = new("wss://api.deepgram.com/v1/speak?" + query);
        }
        public override JsonObject Append(string text) => new() { ["type"] = "Speak", ["text"] = text };
        public override JsonObject Finish() => new() { ["type"] = "Flush" };
        public override JsonObject Cancel() => new() { ["type"] = "Clear" };
        public override bool Done(JsonObject message) => message["type"]?.ToString() == "Flushed";
    }
    private sealed class CartesiaSpeech : StreamingSpeechProtocol
    {
        public override string Provider => "cartesia";
        public override Uri Endpoint => new("wss://api.cartesia.ai/tts/websocket?cartesia_version=2026-08-14");
        private readonly string context = Guid.NewGuid().ToString();
        private readonly JsonObject payload;
        public CartesiaSpeech(string model, JsonObject options)
        {
            var parts = model.Split('/');
            if (parts.Length != 3 || parts[0] != "tts" || !new[] { "sonic-3.6", "sonic-3.5", "sonic-3", "sonic-latest" }.Contains(parts[1]) || string.IsNullOrWhiteSpace(parts[2]))
                throw new InvalidOperationException("Unsupported Cartesia speech model.");
            if (options["apiVersion"] is { } version && version.ToString() != "2026-08-14") throw new InvalidOperationException("Unsupported Cartesia streaming API version.");
            SampleRate = Rate(options, [8000, 16000, 22050, 24000, 44100, 48000]);
            var config = new JsonObject();
            if (Bounded(options["speed"], 0.6, 1.5) is { } speed) config["speed"] = speed;
            if (Bounded(options["volume"], 0.5, 2) is { } volume) config["volume"] = volume;
            if (options["emotion"] is { } emotion)
            {
                if (!new[] { "neutral", "calm", "angry", "content", "sad" }.Contains(emotion.ToString())) throw new InvalidOperationException("Unsupported Cartesia streaming emotion.");
                config["emotion"] = emotion.DeepClone();
            }
            payload = new() { ["model_id"] = parts[1], ["voice"] = new JsonObject { ["id"] = parts[2] }, ["context_id"] = context,
                ["output_format"] = new JsonObject { ["container"] = "raw", ["encoding"] = "pcm_s16le", ["sample_rate"] = SampleRate },
                ["generation_config"] = config, ["max_buffer_delay_ms"] = 200 };
            if (options["language"] is { } language) payload["language"] = language.DeepClone();
        }
        private JsonObject Text(string text, bool more) { var result = (JsonObject)payload.DeepClone(); result["transcript"] = text; result["continue"] = more; return result; }
        public override JsonObject Append(string text) => Text(text, true);
        public override JsonObject Finish() => Text("", false);
        public override JsonObject Cancel() => new() { ["context_id"] = context, ["cancel"] = true };
        public override string? Audio(JsonObject message) => message["context_id"]?.ToString() == context && message["type"]?.ToString() == "chunk" ? message["data"]?.GetValue<string>() : null;
        public override bool Done(JsonObject message) => message["context_id"]?.ToString() == context
            && (message["type"]?.ToString() == "done" || message["type"]?.ToString() == "chunk" && message["done"]?.GetValue<bool>() == true);
    }
}
