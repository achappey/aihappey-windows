using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AIHappey.Desktop.Core;

public static class MistralRealtimeSpeech
{
    public static bool IsModel(string model) => model == "mistral/voxtral-mini-tts-3-0";
    public static JsonObject Session(string model, JsonObject options)
    {
        if (!IsModel(model)) throw new InvalidOperationException("Select a supported Mistral realtime speech model.");
        if (options["sampleRate"] is not null || options["sample_rate"] is not null)
            throw new InvalidOperationException("Mistral realtime speech does not support selecting a sample rate.");
        var voice = (options["voice"] ?? options["voice_id"] ?? options["voiceId"])?.GetValue<string>();
        var reference = options["ref_audio"]?.GetValue<string>();
        if (voice is not null && !Regex.IsMatch(voice, "^voice_[0-9]+$", RegexOptions.CultureInvariant))
            throw new InvalidOperationException("Mistral requires a preset voice ID.");
        if (reference is not null)
        {
            if (reference.Length == 0 || reference.Length > 8 * 1024 * 1024
                || !Regex.IsMatch(reference, @"^(?:[A-Za-z0-9+/]{4})*(?:[A-Za-z0-9+/]{2}==|[A-Za-z0-9+/]{3}=)?$", RegexOptions.CultureInvariant))
                throw new InvalidOperationException("Mistral reference audio must be bounded base64 audio.");
            if (voice is not null) throw new InvalidOperationException("Select a Mistral voice or reference audio, not both.");
        }
        var mode = (options["streaming_mode"] ?? options["streamingMode"])?.GetValue<string>() ?? "responsive";
        if (mode is not ("responsive" or "long_form")) throw new InvalidOperationException("Unsupported Mistral streaming mode.");
        var session = new JsonObject { ["streaming_mode"] = mode, ["response_format"] = "wav" };
        if (reference is not null) session["ref_audio"] = reference; else session["voice"] = voice ?? "voice_447";
        return session;
    }
}
