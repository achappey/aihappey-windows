using System.Text.Json.Nodes;

namespace AIHappey.Desktop.Core;

public static class SmallestRealtimeProtocol
{
    public const int InputRate = 16000;
    public static Uri Connection(string token)
    {
        if (!token.StartsWith("wct_", StringComparison.Ordinal)) throw new RealtimeFailureException("smallest-token", "Smallest AI agents require a short-lived, single-use call token.");
        return new("wss://api.smallest.ai/atoms/v1/agent/connect?token=" + Uri.EscapeDataString(token) + "&input_audio_format=pcm_16000");
    }
    public static int OutputRate(JsonObject created)
    {
        var rate = created["sample_rate"]?.GetValue<int>() ?? 0;
        if (created["type"]?.ToString() != "session.created" || rate is not (8000 or 16000 or 24000 or 44100)
            || created["channels"]?.GetValue<int>() != 1
            || created["input_audio_format"] is { } input && input.ToString() != "pcm_16000"
            || created["output_audio_format"] is { } output && output.ToString() != "pcm_" + rate)
            throw new RealtimeFailureException("smallest-format", "Smallest AI negotiated incompatible PCM audio formats.");
        return rate;
    }
    public static JsonObject TextInput(JsonObject message)
    {
        if (message["type"]?.ToString() == "input_text.send" && !string.IsNullOrWhiteSpace(message["text"]?.ToString()))
            return new() { ["type"] = "input_text.send", ["text"] = message["text"]!.DeepClone() };
        if (message["type"]?.ToString() != "conversation.item.create" || message["item"]?["role"]?.ToString() != "user"
            || message["item"]?["content"] is not JsonArray content || content.Any(part => part?["type"]?.ToString() is not ("input_text" or "text")))
            throw new NotSupportedException("Smallest AI agents accept text input, not attachments or client tool results.");
        var text = string.Concat(content.Select(part => part?["text"]?.ToString()));
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("Smallest AI text input cannot be empty.");
        return new() { ["type"] = "input_text.send", ["text"] = text };
    }
}
