using System.Diagnostics;
using System.Buffers.Binary;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace AIHappey.Desktop.Core;

public interface IRealtimeTranscriptionSession : IAsyncDisposable
{
    ChannelReader<RealtimeTranscriptionSnapshot> Updates { get; }
    RealtimeTranscriptionSnapshot Snapshot { get; }
    Task StartAsync(CancellationToken ct);
    Task StopAsync();
    Task SetMutedAsync(bool muted) => Task.CompletedTask;
}

/// <summary>Reusable transcription service. It knows neither WinUI nor a library/history store.</summary>
public sealed class RealtimeTranscriptionSession : IRealtimeTranscriptionSession
{
    private readonly IRealtimeSession transport;
    private readonly Func<Task> finish;
    private readonly ITranscriptionAudioInput input;
    private readonly bool ownsInput;
    private readonly RealtimeTranscriptionTranscript transcript;
    private readonly Channel<RealtimeTranscriptionSnapshot> updates = Channel.CreateBounded<RealtimeTranscriptionSnapshot>(new BoundedChannelOptions(64) { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest });
    private readonly CancellationTokenSource startup = new();
    private readonly object gate = new();
    private readonly Stopwatch clock = new();
    private Task? pump, start, stop;
    private bool connected;
    private volatile bool muted;
    private readonly Action<JsonObject>? onEvent;
    private readonly string provider;
    private readonly TranscriptionAudioActivity? activity;
    public ChannelReader<RealtimeTranscriptionSnapshot> Updates => updates.Reader;
    public RealtimeTranscriptionSnapshot Snapshot { get; private set; } = new("", "", []);

    public RealtimeTranscriptionSession(string model, RealtimeTranscriptionPreferences preferences,
        Func<JsonObject, CancellationToken, Task<string>> getToken, ITranscriptionAudioInput? audioInput = null,
        Action<JsonObject>? onEvent = null, Action<bool>? onAudioActivity = null)
    {
        preferences = preferences.Clone(); preferences.Validate();
        provider = RealtimeConfiguration.Provider(model); transcript = new(provider);
        input = audioInput ?? new TranscriptionAudioInput(provider == "openai" ? 48000 : 16000); ownsInput = audioInput is null;
        this.onEvent = onEvent;
        activity = onAudioActivity is null ? null : new TranscriptionAudioActivity(onAudioActivity);
        var gatedInput = new MutedInput(input, () => muted, activity);
        Task<string> Token(CancellationToken ct) => getToken(RealtimeTranscriptionConfiguration.TokenRequest(model, preferences, DateTimeOffset.UtcNow), ct);
        if (provider == "openai")
        {
            var openai = new OpenAiRealtimeSession(Token, transcriptionInput: gatedInput); transport = openai; finish = openai.FinishTranscriptionAsync;
        }
        else if (provider is "google" or "gladia" or "smallestai" or "mistral")
        {
            var setup = provider == "google" ? RealtimeTranscriptionConfiguration.GoogleSetup(model, preferences)
                : provider == "mistral" ? new JsonObject { ["model"] = model, ["session"] = RealtimeTranscriptionConfiguration.MistralSession(preferences.ProviderOptions[provider] as JsonObject ?? new()) }
                : provider == "smallestai" ? RealtimeConfiguration.Merge(preferences.ProviderOptions[provider] as JsonObject ?? new(), new() { ["model"] = model }) : null;
            if (provider == "smallestai") _ = RealtimeTranscriptionConfiguration.SmallestConnection(model, setup!);
            var ws = new TranscriptionWebSocketSession(provider, setup, Token, gatedInput);
            transport = ws; finish = ws.FinishInputAsync;
        }
        else throw new NotSupportedException("Unsupported transcription provider.");
    }
    public Task StartAsync(CancellationToken ct)
    {
        lock (gate) { if (stop is not null) throw new OperationCanceledException(); return start ??= StartCoreAsync(ct); }
    }
    private async Task StartCoreAsync(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, startup.Token);
        pump = PumpAsync(); clock.Start();
        try { await transport.StartAsync(linked.Token).ConfigureAwait(false); linked.Token.ThrowIfCancellationRequested(); connected = true; }
        catch { await transport.DisposeAsync().ConfigureAwait(false); throw; }
    }
    private async Task PumpAsync()
    {
        try
        {
            await foreach (var message in transport.Events.ReadAllAsync().ConfigureAwait(false))
            {
                if (message["type"]?.ToString() is "error" or "conversation.item.input_audio_transcription.failed")
                    throw new RealtimeFailureException("transcription-provider", "The provider reported a transcription error.");
                // Mistral's handler must advance its cumulative cursor through
                // muted/discarded deltas, without presenting them as new turns.
                if (!muted || provider == "mistral") onEvent?.Invoke((JsonObject)message.DeepClone());
                if (!transcript.Apply(message, clock.Elapsed.TotalSeconds)) continue;
                Snapshot = transcript.Snapshot(); updates.Writer.TryWrite(Snapshot);
            }
            updates.Writer.TryComplete();
        }
        catch (Exception error) { updates.Writer.TryComplete(RealtimeFailureException.Safe(error, "transcription-events", "The real-time transcription session failed.")); }
    }
    public Task StopAsync() { lock (gate) return stop ??= StopCoreAsync(); }
    private async Task StopCoreAsync()
    {
        if (!connected) startup.Cancel();
        if (start is not null) try { await start.ConfigureAwait(false); } catch { }
        try { if (connected) await finish().ConfigureAwait(false); }
        catch (Exception error) { updates.Writer.TryComplete(RealtimeFailureException.Safe(error, "transcription-finalize", "The provider could not confirm the final transcription.")); }
        finally
        {
            await transport.DisposeAsync().ConfigureAwait(false);
            if (pump is not null) await pump.ConfigureAwait(false); else updates.Writer.TryComplete();
            if (ownsInput) input.Dispose(); clock.Stop();
        }
    }
    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
    public Task SetMutedAsync(bool value) { muted = value; activity?.Reset(); return Task.CompletedTask; }
    private sealed class MutedInput(ITranscriptionAudioInput source, Func<bool> muted, TranscriptionAudioActivity? activity) : ITranscriptionAudioInput
    {
        public int SampleRate => source.SampleRate;
        public IDisposable Subscribe(Action<byte[]> audio, Action<Exception> failed) => source.Subscribe(bytes =>
        {
            var silent = muted();
            activity?.Push(bytes, source.SampleRate, silent);
            audio(silent ? new byte[bytes.Length] : bytes);
        }, failed);
        public void Dispose() { } // Capture ownership remains with the session/caller.
    }
}

/// <summary>PCM16 mono activity hints only; never manufactures transcription or an LLM turn.</summary>
public sealed class TranscriptionAudioActivity(Action<bool> changed)
{
    private readonly object gate = new();
    private bool speaking;
    private double loudMs, quietMs;
    public void Push(byte[] bytes, int sampleRate, bool muted = false)
    {
        lock (gate)
        {
            if (muted) { Reset(); return; }
            var count = bytes.Length / 2;
            if (count == 0 || sampleRate <= 0) return;
            double energy = 0;
            for (var i = 0; i < count; i++)
            {
                var sample = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(i * 2, 2)) / 32768d;
                energy += sample * sample;
            }
            var duration = count * 1000d / sampleRate;
            if (Math.Sqrt(energy / count) > 0.025)
            {
                loudMs += duration; quietMs = 0;
                if (!speaking && loudMs >= 120) { speaking = true; changed(true); }
            }
            else
            {
                loudMs = 0; quietMs += duration;
                if (speaking && quietMs >= 600) { speaking = false; changed(false); }
            }
        }
    }
    public void Reset()
    {
        lock (gate)
        {
            loudMs = quietMs = 0;
            if (speaking) { speaking = false; changed(false); }
        }
    }
}
