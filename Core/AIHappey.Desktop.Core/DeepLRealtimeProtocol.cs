using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AIHappey.Desktop.Core;

/// <summary>DeepL Voice is transcription/translation only, never conversational voice or TTS.</summary>
public static class DeepLRealtimeProtocol
{
    public static string? TargetLanguage(string model)
    {
        if (model == "deepl/voice-realtime") return null;
        var match = Regex.Match(model, @"^deepl/voice-realtime/translate-to/([a-z]{2,3}(?:-[A-Za-z0-9]{2,8})*)$", RegexOptions.CultureInvariant);
        if (!match.Success) throw new ArgumentException("Select a DeepL Voice realtime transcription model.");
        return match.Groups[1].Value;
    }

    public static JsonObject Options(string model, JsonObject options)
    {
        var target = TargetLanguage(model);
        var result = (JsonObject)options.DeepClone();
        result["message_format"] = "json"; result["source_media_content_type"] = "audio/pcm;encoding=s16le;rate=16000";
        result["target_languages"] = target is null ? new JsonArray() : new JsonArray(target);
        foreach (var field in new[] { "target_media_languages", "target_media_content_type", "target_media_voice" }) result.Remove(field);
        return result;
    }

    public static Uri Connection(string address, string? token = null)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != "wss" || uri.Port != 443
            || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0
            || !(uri.Host == "api.deepl.com" || uri.Host.EndsWith(".deepl.com", StringComparison.OrdinalIgnoreCase)))
            throw new GatewayException("The gateway returned an invalid DeepL streaming address.");
        if (token is null) return uri;
        if (string.IsNullOrWhiteSpace(token)) throw new GatewayException("The gateway returned an invalid DeepL credential.");
        // Preserve the provider's current address and non-token query parameters.
        var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(part => !string.Equals(Uri.UnescapeDataString(part.Split('=')[0]), "token", StringComparison.Ordinal)).ToList();
        query.Add("token=" + Uri.EscapeDataString(token));
        return new UriBuilder(uri) { Query = string.Join('&', query) }.Uri;
    }

    public static JsonObject? TranscriptEvent(JsonObject message, string? target)
    {
        var update = message[target is null ? "source_transcript_update" : "target_transcript_update"] as JsonObject;
        if (update is null || target is not null && !string.Equals(update["language"]?.ToString(), target, StringComparison.OrdinalIgnoreCase)) return null;
        foreach (var field in new[] { "concluded", "tentative" })
        {
            if (update[field] is not JsonArray segments || segments.Count > 100_000) throw new InvalidDataException("Invalid DeepL transcript segments.");
            foreach (var segment in segments)
                if (segment is not JsonObject item || item["text"] is not JsonValue text || !text.TryGetValue<string>(out var value) || value.Length > 2_000_000
                    || !RealtimeTranscriptionConfiguration.TryNumber(item["start_time"], out var start) || start < 0 || start != Math.Truncate(start)
                    || !RealtimeTranscriptionConfiguration.TryNumber(item["end_time"], out var end) || end < start || end != Math.Truncate(end))
                    throw new InvalidDataException("Invalid DeepL transcript segment.");
        }
        return new() { ["type"] = "deepl.transcript", ["language"] = target ?? update["concluded"]?.AsArray().FirstOrDefault()?["language"]?.ToString()
            ?? update["tentative"]?.AsArray().FirstOrDefault()?["language"]?.ToString(),
            ["concluded"] = update["concluded"]!.DeepClone(), ["tentative"] = update["tentative"]!.DeepClone() };
    }
}
