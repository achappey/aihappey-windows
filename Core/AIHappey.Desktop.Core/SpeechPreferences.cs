using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Vercel.Models;

namespace AIHappey.Desktop.Core;

/// <summary>Browser-compatible text-to-speech settings. Provider objects are deliberately
/// opaque so newly added gateway fields survive native editing and settings round trips.</summary>
public sealed class SpeechPreferences
{
    public string? Voice { get; set; }
    public string? OutputFormat { get; set; }
    public string? Instructions { get; set; }
    public double? Speed { get; set; }
    public string? Language { get; set; }
    public Dictionary<string, JsonObject> ProviderOptions { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public SpeechPreferences Clone() => new()
    {
        Voice = Voice, OutputFormat = OutputFormat, Instructions = Instructions, Speed = Speed, Language = Language,
        ProviderOptions = (ProviderOptions ?? []).ToDictionary(p => p.Key, p => (JsonObject)p.Value.DeepClone(), StringComparer.OrdinalIgnoreCase)
    };

    public void Validate()
    {
        if (Speed is { } speed && (!double.IsFinite(speed) || speed is < .5 or > 2)
            || OutputFormat is { Length: > 0 } format && !SpeechAudio.OutputFormats.Contains(format, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException(DesktopResources.Get("SpeechSettingsInvalid"));
    }

    public SpeechRequest Request(ChatTarget model, string text)
    {
        Validate();
        if (model.ModelType != "speech" || string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException(DesktopResources.Get("SpeechPromptRequired"));
        var provider = ChatPreferences.ResolveProvider(ServiceKind.Ai, model.Id, model.ProviderKey);
        ProviderOptions.TryGetValue(provider ?? "", out var options);
        // Keep provider-specific speed authoritative when it is explicitly configured. Otherwise
        // serialize the same effective default (1) that the native controls display.
        float? speed = Speed is { } generalSpeed ? (float)generalSpeed
            : ProviderSpeed(options) is not null ? null : 1f;
        return new()
        {
            Model = model.Id, Text = text.Trim(), Voice = Trim(Voice), OutputFormat = Trim(OutputFormat),
            Instructions = Trim(Instructions), Speed = speed, Language = Trim(Language),
            ProviderOptions = provider is not null && options is not null
                ? new() { [provider] = JsonSerializer.SerializeToElement(options, JsonSerializerOptions.Web) } : null
        };
    }

    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static double? ProviderSpeed(JsonObject? options)
        => options?["speed"] is JsonValue value && value.TryGetValue<double>(out var speed) && double.IsFinite(speed) ? speed : null;
}

/// <summary>Pure OpenAI speech-option editing helpers shared by the native form and regression tests.</summary>
public static class OpenAISpeechOptions
{
    public const string DefaultVoice = "__default__";
    public const string CustomVoice = "__custom__";
    public static readonly string[] Voices = ["alloy", "ash", "ballad", "coral", "echo", "fable", "onyx", "nova", "sage", "shimmer", "verse", "marin", "cedar"];
    public static readonly string[] Formats = ["mp3", "opus", "aac", "flac", "wav", "pcm"];

    public static string VoiceSelection(JsonObject config)
    {
        var text = Text(config["voice"]);
        if (string.IsNullOrWhiteSpace(text) && config["voice"] is not JsonObject) return DefaultVoice;
        return text is not null && Voices.Contains(text, StringComparer.Ordinal) ? text : CustomVoice;
    }

    public static string CustomVoiceId(JsonObject config) => config["voice"] switch
    {
        JsonObject value => Text(value["id"]) ?? "",
        JsonValue value when value.TryGetValue<string>(out var text) && !Voices.Contains(text, StringComparer.Ordinal) => text,
        _ => ""
    };

    public static void SetVoice(JsonObject config, string selection, string? customId = null)
    {
        if (selection == DefaultVoice) { config.Remove("voice"); return; }
        if (selection != CustomVoice) { config["voice"] = selection; return; }
        var voice = config["voice"] is JsonObject current ? (JsonObject)current.DeepClone() : new JsonObject();
        var id = customId?.Trim();
        if (string.IsNullOrWhiteSpace(id)) voice.Remove("id"); else voice["id"] = id;
        config["voice"] = voice;
    }

    public static void SetFormat(JsonObject config, string? format)
    {
        if (string.IsNullOrWhiteSpace(format)) config.Remove("response_format");
        else config["response_format"] = format;
    }

    public static void SetSpeed(JsonObject config, double value) => config["speed"] = value;
    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}

public static class SpeechAudio
{
    public static byte[] PcmWave(byte[] pcm, int sampleRate)
    {
        if (pcm.Length == 0 || pcm.Length % 2 != 0 || sampleRate is not (8000 or 16000 or 24000 or 44100)) throw new InvalidDataException("Invalid speech PCM audio.");
        using var stream = new MemoryStream(44 + pcm.Length);
        using var writer = new BinaryWriter(stream, System.Text.Encoding.ASCII, true);
        writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + pcm.Length); writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt "));
        writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(sampleRate); writer.Write(sampleRate * 2);
        writer.Write((short)2); writer.Write((short)16); writer.Write(System.Text.Encoding.ASCII.GetBytes("data")); writer.Write(pcm.Length); writer.Write(pcm);
        return stream.ToArray();
    }
    public static readonly string[] OutputFormats = ["mp3", "wav", "opus", "aac", "flac", "pcm"];
    private const int MaximumBytes = 100 * 1024 * 1024;

    public static (byte[] Bytes, string MediaType, string Format) Decode(JsonNode? audio)
    {
        string? encoded = null, mediaType = null, format = null;
        if (audio is JsonValue value && value.TryGetValue<string>(out var source))
        {
            if (source.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                var comma = source.IndexOf(',');
                if (comma > 5 && source[..comma].EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
                { mediaType = source[5..(comma - 7)]; encoded = source[(comma + 1)..]; }
            }
            else encoded = source;
        }
        else if (audio is JsonObject item)
        {
            encoded = Text(item["base64"]) ?? Text(item["data"]);
            mediaType = Text(item["mimeType"]) ?? Text(item["mime_type"]);
            format = Text(item["format"]);
        }
        if (string.IsNullOrWhiteSpace(encoded) || encoded.Length > (MaximumBytes + 2L) / 3 * 4 + 4)
            throw new InvalidOperationException(DesktopResources.Get("SpeechResponseInvalid"));
        byte[] bytes;
        try { bytes = Convert.FromBase64String(encoded); }
        catch (FormatException) { throw new InvalidOperationException(DesktopResources.Get("SpeechResponseInvalid")); }
        if (bytes.Length == 0 || bytes.Length > MaximumBytes) throw new InvalidOperationException(DesktopResources.Get("SpeechResponseInvalid"));
        format = NormalizeFormat(format, mediaType);
        mediaType = NormalizeMediaType(mediaType, format);
        return (bytes, mediaType, format);
    }

    public static bool IsAudio(string? mediaType) => mediaType?.Split(';')[0].Trim().StartsWith("audio/", StringComparison.OrdinalIgnoreCase) == true;
    public static string Extension(string mediaType, string? format = null) => NormalizeFormat(format, mediaType) switch
    { "mp3" => ".mp3", "wav" => ".wav", "opus" => ".opus", "aac" => ".aac", "flac" => ".flac", "pcm" => ".pcm", "m4a" => ".m4a", "ogg" => ".ogg", "webm" => ".webm", _ => ".audio" };

    private static string NormalizeFormat(string? format, string? mediaType)
    {
        var value = format?.Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(value)) return value;
        return mediaType?.Split(';')[0].Trim().ToLowerInvariant() switch
        {
            "audio/mpeg" or "audio/mp3" => "mp3", "audio/wav" or "audio/wave" or "audio/x-wav" or "audio/l16" => "wav",
            "audio/ogg" => "ogg", "audio/opus" => "opus", "audio/aac" => "aac", "audio/flac" => "flac",
            "audio/mp4" or "audio/x-m4a" => "m4a", "audio/webm" => "webm", "audio/pcm" => "pcm", _ => "audio"
        };
    }
    private static string NormalizeMediaType(string? mediaType, string format)
    {
        if (IsAudio(mediaType)) return mediaType!.Split(';')[0].Trim().ToLowerInvariant();
        return format switch { "mp3" => "audio/mpeg", "wav" => "audio/wav", "opus" => "audio/opus", "ogg" => "audio/ogg", "aac" => "audio/aac",
            "flac" => "audio/flac", "m4a" => "audio/mp4", "webm" => "audio/webm", "pcm" => "audio/pcm", _ => "application/octet-stream" };
    }
    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
