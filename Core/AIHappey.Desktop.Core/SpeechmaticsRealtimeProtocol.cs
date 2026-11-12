using System.Text.Json.Nodes;

namespace AIHappey.Desktop.Core;

/// <summary>Speechmatics token settings never become recognition fields or arbitrary connection URLs.</summary>
public static class SpeechmaticsRealtimeProtocol
{
    public static bool IsModel(string model) => RealtimeConfiguration.ModelName(model) is "standard" or "enhanced" or "melia-1" or "oak-1" or "linden-1";
    public static int Ttl(JsonObject options)
    {
        if (options["ttl"] is null) return 60;
        if (!RealtimeTranscriptionConfiguration.TryNumber(options["ttl"], out var ttl) || ttl != Math.Truncate(ttl) || ttl < 60 || ttl > 86400)
            throw new ArgumentException("Speechmatics token TTL must be an integer from 60 to 86400 seconds.");
        return (int)ttl;
    }
    public static Uri Connection(string model, JsonObject options, string credential)
    {
        if (!IsModel(model)) throw new ArgumentException("Select a supported Speechmatics real-time model.");
        var region = options["region"]?.ToString() ?? "global";
        if (region is not ("global" or "eu" or "us" or "au")) throw new ArgumentException("Select a documented Speechmatics region.");
        if (string.IsNullOrWhiteSpace(credential) || credential.Length > 64000 || credential.Any(char.IsWhiteSpace))
            throw new ArgumentException("Speechmatics requires a temporary key.");
        return new($"wss://{region}.rt.speechmatics.com/v2" + (RealtimeConfiguration.ModelName(model) == "linden-1" ? "/agent" : "") + "?jwt=" + Uri.EscapeDataString(credential));
    }
    public static JsonObject Start(string model, JsonObject options)
    {
        _ = Connection(model, options, "validation"); _ = Ttl(options);
        var local = RealtimeConfiguration.ModelName(model); var agent = local == "linden-1";
        foreach (var field in new[] { "transcription_config", "realtime_config", "agent_config" })
            if (options[field] is not null and not JsonObject) throw new ArgumentException("Invalid Speechmatics recognition options.");
        var config = RealtimeConfiguration.Merge(options["transcription_config"] as JsonObject ?? new(), options[agent ? "agent_config" : "realtime_config"] as JsonObject ?? new());
        config["model"] = local; config["enable_partials"] = true;
        var multilingual = local is "melia-1" or "oak-1";
        var language = multilingual ? "multi" : config["language"]?.ToString() ?? "en";
        if (string.IsNullOrWhiteSpace(language) || !multilingual && language is "multi" or "auto") throw new ArgumentException("Select a language for this Speechmatics model.");
        config["language"] = language;
        if (config["diarization"] is { } diarization && diarization.ToString() is not ("none" or "speaker"))
            throw new ArgumentException("Mono Speechmatics transcription supports none or speaker diarization.");
        if (agent && new[] { "max_delay", "max_delay_mode", "language_hints", "conversation_config", "operating_point" }.Any(config.ContainsKey))
            throw new ArgumentException("Put regular-only Speechmatics options in realtime_config, not shared transcription_config.");
        if (!agent && config["max_delay"] is { } delay && (!RealtimeTranscriptionConfiguration.TryNumber(delay, out var number) || number < .7 || number > 4))
            throw new ArgumentException("Speechmatics maximum delay must be from 0.7 to 4 seconds.");
        if (multilingual && config["conversation_config"]?["end_of_utterance_silence_trigger"] is not null)
            throw new ArgumentException("Melia 1 and Oak 1 do not support automatic end-of-utterance configuration.");
        if (config["language_hints"] is JsonArray { Count: 0 }) config.Remove("language_hints");
        if (!multilingual && config.ContainsKey("language_hints")) throw new ArgumentException("Speechmatics language hints require Melia 1 or Oak 1.");
        var start = new JsonObject { ["message"] = "StartRecognition", ["audio_format"] = new JsonObject { ["type"] = "raw", ["encoding"] = "pcm_s16le", ["sample_rate"] = 16000 }, ["transcription_config"] = config };
        if (agent) start["turn_config"] = new JsonObject { ["turn_detection_mode"] = "vad" };
        return start;
    }
}
