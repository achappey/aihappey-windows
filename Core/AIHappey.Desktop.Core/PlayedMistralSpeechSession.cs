using System.Text.Json.Nodes;

namespace AIHappey.Desktop.Core;

/// <summary>Creates the existing PCM player only once the WAV header establishes the sample rate.</summary>
internal sealed class PlayedMistralSpeechSession : IStreamingSpeechSession
{
    private readonly object gate = new();
    private readonly Action started;
    private readonly CancellationTokenRegistration cancellation;
    private StreamingSpeechOutput? output;
    private MistralRealtimeSpeechSession? session;
    private bool stopped;
    private PlayedMistralSpeechSession(Action started, CancellationToken ct)
    {
        this.started = started; cancellation = ct.Register(Stop);
    }
    public static async Task<IStreamingSpeechSession> OpenAsync(JsonObject config, string token, Action started, CancellationToken ct)
    {
        var result = new PlayedMistralSpeechSession(started, ct);
        try
        {
            result.session = await MistralRealtimeSpeechSession.OpenAsync(config, token, result.Push, ct, result.Stop);
            ct.ThrowIfCancellationRequested(); return result;
        }
        catch { await result.DisposeAsync(); throw; }
    }
    private void Push(byte[] bytes, int rate)
    {
        lock (gate) { if (stopped) return; output ??= new(rate, started); output.Push(bytes); }
    }
    private void Stop()
    {
        StreamingSpeechOutput? captured;
        lock (gate) { stopped = true; captured = output; }
        captured?.Dispose();
    }
    public Task AppendAsync(string text, CancellationToken ct) => session!.AppendAsync(text, ct);
    public Task FlushAsync(CancellationToken ct) => session!.FlushAsync(ct);
    public async Task FinishAsync(CancellationToken ct)
    {
        await session!.FinishAsync(ct);
        StreamingSpeechOutput? captured; lock (gate) captured = output;
        if (captured is not null) await captured.DrainAsync(ct);
    }
    public async Task CancelAsync() { Stop(); if (session is not null) await session.CancelAsync(); }
    public async ValueTask DisposeAsync() { cancellation.Dispose(); Stop(); if (session is not null) await session.DisposeAsync(); }
}
