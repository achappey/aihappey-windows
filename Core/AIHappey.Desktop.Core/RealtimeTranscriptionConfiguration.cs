using System.Text.Json.Nodes;

namespace AIHappey.Desktop.Core;

/// <summary>Transcription-only settings. Never shared with conversational voice settings.</summary>
public sealed class RealtimeTranscriptionPreferences
{
    public JsonObject ProviderOptions { get; set; } = new();
    public RealtimeTranscriptionPreferences Clone() => new() { ProviderOptions = (JsonObject)ProviderOptions.DeepClone() };
    public void Validate()
    {
        if (ProviderOptions.ToJsonString().Length > 64_000) throw new ArgumentException("Transcription settings are too large.");
        _ = RealtimeTranscriptionConfiguration.TokenRequest("openai/validation", this, DateTimeOffset.UtcNow);
        _ = RealtimeTranscriptionConfiguration.TokenRequest("google/validation", this, DateTimeOffset.UtcNow);
    }
}

public static class RealtimeTranscriptionConfiguration
{
    public static bool Supports(ChatTarget? model) => model is not null && model.Tags.Contains("real-time")
        && (RealtimeConfiguration.Provider(model.Id) is "openai" or "google" or "gladia"
            || model.Id is "smallestai/pulse" or "smallestai/pulse-2");

    public static Uri SmallestConnection(string model, JsonObject options)
    {
        var name = RealtimeConfiguration.ModelName(model);
        if (name is not ("pulse" or "pulse-2")) throw new ArgumentException("Smallest AI streaming supports Pulse and Pulse 2, not Pulse Pro.");
        var language = options["language"]?.ToString() ?? "en";
        if (name == "pulse-2" && language != "en") throw new ArgumentException("Smallest AI Pulse 2 supports English only.");
        var query = new Dictionary<string, string> { ["model"] = name, ["language"] = language, ["sample_rate"] = "16000", ["encoding"] = "linear16", ["word_timestamps"] = "true" };
        string[] fields = ["word_timestamps", "diarize", "vad_events", "vad", "endpointing", "format", "punctuate", "capitalize", "itn_normalize", "no_format_delay", "redact_pii", "redact_pci", "sentence_timestamps", "keywords", "emotion_detection", "gender_detection"];
        foreach (var key in fields.Concat(name == "pulse" ? new[] { "eou_timeout_ms", "finalize_on_words", "max_words" } : []))
        {
            if (options[key] is not { } value || value.ToString().Length == 0) continue;
            if (value is not JsonValue) throw new ArgumentException("Invalid Smallest AI transcription setting.");
            query[key] = value is JsonValue flag && flag.TryGetValue<bool>(out var enabled) ? enabled.ToString().ToLowerInvariant() : value.ToString();
        }
        return new("wss://api.smallest.ai/waves/v1/stt/live?" + string.Join('&', query.Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value))));
    }

    public static JsonObject GoogleConfig(RealtimeTranscriptionPreferences preferences)
    {
        var transcription = preferences.ProviderOptions["google"]?["liveConnectConstraints"]?["config"]?["inputAudioTranscription"] as JsonObject ?? new();
        var input = new JsonObject { ["languageCodes"] = transcription["languageCodes"]?.DeepClone() ?? new JsonArray() };
        foreach (var field in new[] { "customVocabulary", "mode" })
            if (transcription[field] is { } value) input[field] = value.DeepClone();
        return new() { ["responseModalities"] = new JsonArray("TEXT"), ["inputAudioTranscription"] = input };
    }
    public static JsonObject GoogleSetup(string model, RealtimeTranscriptionPreferences preferences) => RealtimeConfiguration.GoogleSetup(model, GoogleConfig(preferences));
    public static JsonObject TokenRequest(string model, RealtimeTranscriptionPreferences preferences, DateTimeOffset now)
    {
        var provider = RealtimeConfiguration.Provider(model);
        var options = preferences.ProviderOptions[provider] as JsonObject ?? new();
        JsonObject metadata;
        if (provider == "openai")
        {
            metadata = RealtimeConfiguration.Merge(new() { ["expires_after"] = new JsonObject { ["anchor"] = "created_at", ["seconds"] = 600 } }, options);
            var input = options["session"]?["audio"]?["input"] as JsonObject ?? new();
            input = (JsonObject)input.DeepClone();
            // A transcription session must never generate responses or carry voice-chat tools.
            input["transcription"] = RealtimeConfiguration.Merge(input["transcription"] as JsonObject ?? new(), new() { ["model"] = RealtimeConfiguration.ModelName(model) });
            if (!input.ContainsKey("turn_detection")) input["turn_detection"] = new JsonObject { ["type"] = "server_vad", ["silence_duration_ms"] = 500, ["prefix_padding_ms"] = 300 };
            if (input["turn_detection"] is JsonObject vad) { vad.Remove("create_response"); vad.Remove("interrupt_response"); }
            metadata["session"] = new JsonObject { ["type"] = "transcription", ["audio"] = new JsonObject { ["input"] = input } };
        }
        else if (provider == "google")
        {
            metadata = new() { ["uses"] = options["uses"]?.DeepClone() ?? JsonValue.Create(1),
                ["expireTime"] = RealtimeConfiguration.Expiry(options["expireTime"]?.ToString(), now, TimeSpan.FromMinutes(30)),
                ["newSessionExpireTime"] = RealtimeConfiguration.Expiry(options["newSessionExpireTime"]?.ToString(), now, TimeSpan.FromMinutes(1)),
                ["bidiGenerateContentSetup"] = GoogleSetup(model, preferences) };
            if (options["lockAdditionalFields"] is { } mask) metadata["fieldMask"] = mask.DeepClone();
        }
        else if (provider == "gladia")
        {
            // The provisioned session and the bytes uploaded to it must agree. Native capture is mono PCM16.
            metadata = RealtimeConfiguration.Merge(options, new() { ["encoding"] = "wav/pcm", ["sample_rate"] = 16000, ["bit_depth"] = 16, ["channels"] = 1 });
        }
        else if (provider == "smallestai")
        {
            _ = SmallestConnection(model, options);
            metadata = new();
            if (options["ttl_seconds"] is { } ttl) metadata["ttl_seconds"] = ttl.DeepClone();
        }
        else throw new NotSupportedException("Unsupported real-time transcription provider.");
        return new() { ["model"] = model, ["providerOptions"] = new JsonObject { [provider] = metadata } };
    }
}
