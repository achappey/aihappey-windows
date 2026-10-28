using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace AIHappey.Desktop.Core;

public interface IRealtimeTranscriptionSession : IAsyncDisposable
{
    ChannelReader<RealtimeTranscriptionSnapshot> Updates { get; }
    RealtimeTranscriptionSnapshot Snapshot { get; }
    Task StartAsync(CancellationToken ct);
    Task StopAsync();
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
    public ChannelReader<RealtimeTranscriptionSnapshot> Updates => updates.Reader;
    public RealtimeTranscriptionSnapshot Snapshot { get; private set; } = new("", "", []);

    public RealtimeTranscriptionSession(string model, RealtimeTranscriptionPreferences preferences,
        Func<JsonObject, CancellationToken, Task<string>> getToken, ITranscriptionAudioInput? audioInput = null)
    {
        preferences = preferences.Clone(); preferences.Validate();
        var provider = RealtimeConfiguration.Provider(model); transcript = new(provider);
        input = audioInput ?? new TranscriptionAudioInput(provider == "openai" ? 48000 : 16000); ownsInput = audioInput is null;
        Task<string> Token(CancellationToken ct) => getToken(RealtimeTranscriptionConfiguration.TokenRequest(model, preferences, DateTimeOffset.UtcNow), ct);
        if (provider == "openai")
        {
            var openai = new OpenAiRealtimeSession(Token, transcriptionInput: input); transport = openai; finish = openai.FinishTranscriptionAsync;
        }
        else if (provider is "google" or "gladia")
        {
            var ws = new TranscriptionWebSocketSession(provider, provider == "google" ? RealtimeTranscriptionConfiguration.GoogleSetup(model, preferences) : null, Token, input);
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
}
