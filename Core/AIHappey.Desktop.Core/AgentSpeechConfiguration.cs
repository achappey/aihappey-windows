using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Vercel.Models;

namespace AIHappey.Desktop.Core;

/// <summary>Pure agent speech resolution. Stored provider options and standalone defaults are never modified.</summary>
public static class AgentSpeechConfiguration
{
    private static string? Clean(JsonNode? value) => string.IsNullOrWhiteSpace(DesktopAgent.Text(value)) ? null : DesktopAgent.Text(value).Trim();
    public static double? Number(JsonNode? value) => value is JsonValue scalar && scalar.TryGetValue<double>(out var number) ? number : null;
    public static string? FormatName(string? type) => type?.Trim().ToLowerInvariant() switch
    {
        null or "" => null,
        "audio/mpeg" or "audio/mp3" => "mp3", "audio/wav" or "audio/wave" or "audio/x-wav" => "wav",
        "audio/opus" => "opus", "audio/aac" => "aac", "audio/flac" => "flac",
        "audio/pcm" or "audio/pcm_s16le" => "pcm", "audio/ogg" => "ogg", "audio/webm" => "webm", "audio/mp4" => "m4a",
        var value => value
    };
    public static void Validate(JsonObject? speech)
    {
        if (speech is null) return;
        if (speech["speed"] is { } speed && (Number(speed) is not { } value || !double.IsFinite(value) || value <= 0))
            throw new InvalidOperationException(DesktopResources.Get("SpeechSettingsInvalid"));
        if (speech["outputFormat"] is JsonObject format && format["rate"] is { } rate
            && (Number(rate) is not { } hz || !double.IsFinite(hz) || hz <= 0 || hz > int.MaxValue || hz != Math.Truncate(hz) || Clean(format["type"]) is null))
            throw new InvalidOperationException(DesktopResources.Get("SpeechSettingsInvalid"));
    }
    public static bool IsValid(JsonObject? speech) { try { Validate(speech); return true; } catch (InvalidOperationException) { return false; } }
    public static JsonObject ProviderOptions(JsonObject speech)
    {
        Validate(speech);
        var provider = DesktopAgent.Provider(DesktopAgent.Text(speech["model"]?["id"]));
        var options = (JsonObject)(speech["model"]?["providerMetadata"]?[provider] as JsonObject ?? new()).DeepClone();
        Replace(options, ["voice", "voiceId", "voice_id", "speaker", "speaker_id"], Clean(speech["voice"]));
        Replace(options, ["speed", "speakingRate", "speaking_rate"], Number(speech["speed"]));
        Replace(options, ["instructions"], Clean(speech["instructions"]));
        Replace(options, ["language", "languageCode", "language_code"], Clean(speech["language"]));
        Replace(options, ["response_format", "outputFormat"], FormatName(Clean(speech["outputFormat"]?["type"])));
        Replace(options, ["sampleRate", "sample_rate", "sample_rate_hertz"], Number(speech["outputFormat"]?["rate"]));
        foreach (var key in new[] { "voice_setting", "voiceSetting", "generation_config" })
            if (options[key] is JsonObject nested)
            {
                Replace(nested, ["voice_id", "voiceId", "id"], Clean(speech["voice"]));
                Replace(nested, ["speed"], Number(speech["speed"]));
            }
        return options;
    }
    private static void Replace(JsonObject target, string[] keys, object? value)
    {
        if (value is null) return;
        foreach (var key in keys) if (target.ContainsKey(key)) target[key] = JsonSerializer.SerializeToNode(value);
    }
    public static JsonObject SocketOptions(JsonObject speech)
    {
        var options = ProviderOptions(speech);
        foreach (var key in new[] { "voice", "language" }) if (Clean(speech[key]) is { } value) options[key] = value;
        if (Number(speech["speed"]) is { } speed) options["speed"] = speed;
        if (Number(speech["outputFormat"]?["rate"]) is { } rate) options["sampleRate"] = (int)rate;
        return options;
    }
    public static SpeechRequest Request(JsonObject speech, string text)
    {
        var options = ProviderOptions(speech);
        var id = DesktopAgent.Text(speech["model"]?["id"]);
        if (id.Length == 0) throw new InvalidOperationException(DesktopResources.Get("SpeechPromptRequired"));
        return new()
        {
            Model = id, Text = text, Voice = Clean(speech["voice"]), Instructions = Clean(speech["instructions"]),
            Language = Clean(speech["language"]), Speed = (float?)Number(speech["speed"]),
            OutputFormat = FormatName(Clean(speech["outputFormat"]?["type"])),
            ProviderOptions = options.Count == 0 ? null : new() { [DesktopAgent.Provider(id)] = JsonSerializer.SerializeToElement(options) }
        };
    }
}
