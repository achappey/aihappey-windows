using System.Text.Json.Nodes;

namespace AIHappey.Desktop.Core;

internal static class SmallestStreamingSpeech
{
    public static bool Supports(ChatTarget model, SpeechPreferences preferences)
    {
        preferences.ProviderOptions.TryGetValue("smallestai", out var options);
        var format = (preferences.OutputFormat ?? (options?["outputFormat"] ?? options?["output_format"])?.ToString())?.Trim().ToLowerInvariant();
        return model.Id.StartsWith("smallestai/", StringComparison.Ordinal) && StreamingSpeechProtocol.Supports(model)
            && format is null or "" or "wav" or "wave" or "pcm"
            && (options?["pronunciationDicts"] as JsonArray)?.Count is not > 0
            && (options?["pronunciation_dicts"] as JsonArray)?.Count is not > 0;
    }
    public static async Task<SpeechResult> GenerateAsync(ChatTarget model, string text, SpeechPreferences preferences,
        DesktopRealtimeTokenClient tokens, ChatPreferences headers, CancellationToken ct)
    {
        var request = preferences.Clone().Request(model, text);
        preferences.ProviderOptions.TryGetValue("smallestai", out var configured);
        var options = (JsonObject)(configured ?? new()).DeepClone();
        if (!string.IsNullOrWhiteSpace(request.Voice)) options["voice"] = request.Voice;
        if (request.Speed is { } speed) options["speed"] = speed;
        if (!string.IsNullOrWhiteSpace(request.Language)) options["language"] = request.Language;
        var protocol = StreamingSpeechProtocol.Create(model.Id, options);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var token = await tokens.GetAsync(new() { ["model"] = model.Id, ["providerOptions"] = new JsonObject { ["smallestai"] = new JsonObject() } }, headers, "smallestai", timeout.Token);
        using var output = new StreamingSpeechOutput(protocol.SampleRate, () => { });
        using var registration = ct.Register(output.Dispose);
        using var collected = new MemoryStream();
        await using var session = await StreamingSpeechSession.OpenAsync(protocol, token, bytes =>
        {
            if (collected.Length + bytes.Length > 100 * 1024 * 1024) throw new InvalidOperationException("Speech audio exceeded its limit.");
            collected.Write(bytes); output.Push(bytes);
        }, ct);
        try
        {
            await session.AppendAsync(request.Text, ct); await session.FinishAsync(ct); await output.DrainAsync(ct); ct.ThrowIfCancellationRequested();
            var pcm = collected.ToArray();
            var format = (request.OutputFormat ?? (options["outputFormat"] ?? options["output_format"])?.ToString()) == "pcm" ? "pcm" : "wav";
            var bytes = format == "wav" ? SpeechAudio.PcmWave(pcm, protocol.SampleRate) : pcm;
            var mime = format == "wav" ? "audio/wav" : "audio/pcm;rate=" + protocol.SampleRate;
            var warnings = new JsonArray();
            if (!string.IsNullOrWhiteSpace(request.Instructions)) warnings.Add(new JsonObject { ["type"] = "unsupported", ["feature"] = "instructions" });
            var response = new JsonObject { ["audio"] = new JsonObject { ["base64"] = Convert.ToBase64String(bytes), ["mimeType"] = mime, ["format"] = format },
                ["warnings"] = warnings, ["providerMetadata"] = new JsonObject { ["smallestai"] = new JsonObject { ["streamed"] = true, ["sampleRate"] = protocol.SampleRate } },
                ["response"] = new JsonObject { ["modelId"] = model.Id, ["timestamp"] = DateTimeOffset.UtcNow.ToString("O") } };
            return new(request, response, bytes, mime, format);
        }
        catch { await session.CancelAsync(); throw; }
    }
}
