using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AIHappey.Desktop.Core;

public interface IOpenAiTranscriptionConnection : IRealtimeSession
{
    Task FinishTranscriptionAsync();
}

internal static class OpenAiTranscriptionErrors
{
    public static bool IsDurationLimit(JsonObject? error) => error?["code"]?.ToString() is "session_duration_exceeded" or "session_duration_limit_exceeded"
        || Regex.IsMatch(error?["message"]?.ToString() ?? "", @"session[^\r\n]*(?:maximum|max)[^\r\n]*duration", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    public static RealtimeFailureException Provider(JsonObject? error) => IsDurationLimit(error)
        ? new("transcription-duration", "The OpenAI transcription connection reached its duration limit.")
        : new("transcription-provider", "OpenAI reported a transcription error.");
}

/// <summary>Transcription-only rollover. Conversation/voice sessions never use this owner.</summary>
public sealed class RenewingOpenAiTranscriptionSession : RealtimeSession
{
    private readonly Func<IOpenAiTranscriptionConnection> createConnection;
    private readonly TimeProvider time;
    private readonly TimeSpan renewAfter;
    private readonly CancellationTokenSource stopping = new();
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object gate = new();
    private Task? run;
    private Exception? failure;
    private int disposed;

    public RenewingOpenAiTranscriptionSession(Func<CancellationToken, Task<string>> tokenFactory, ITranscriptionAudioInput input,
        Func<IOpenAiTranscriptionConnection>? connectionFactory = null, TimeProvider? timeProvider = null, TimeSpan? renewalInterval = null)
    {
        createConnection = connectionFactory ?? (() => new OpenAiRealtimeSession(tokenFactory, transcriptionInput: input));
        time = timeProvider ?? TimeProvider.System;
        renewAfter = renewalInterval ?? TimeSpan.FromMinutes(55);
        if (renewAfter <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(renewalInterval));
    }

    public override Task StartAsync(CancellationToken ct)
    {
        lock (gate)
        {
            if (stopping.IsCancellationRequested) throw new OperationCanceledException();
            run ??= RunAsync(ct);
            return ready.Task;
        }
    }

    private sealed class ConnectionState { public volatile bool Closing; }
    private async Task<bool> PumpAsync(IOpenAiTranscriptionConnection connection, ConnectionState state)
    {
        await foreach (var message in connection.Events.ReadAllAsync().ConfigureAwait(false))
        {
            var type = message["type"]?.ToString();
            if (type == "error")
            {
                if (OpenAiTranscriptionErrors.IsDurationLimit(message["error"] as JsonObject)) return true;
                throw OpenAiTranscriptionErrors.Provider(message["error"] as JsonObject);
            }
            if (type == "conversation.item.input_audio_transcription.failed")
                throw new RealtimeFailureException("transcription-provider", "OpenAI could not finalize the transcription.");
            Emit(message);
        }
        if (!state.Closing) throw new RealtimeFailureException("transcription-disconnected", "The OpenAI transcription connection closed unexpectedly.");
        return false;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, stopping.Token);
        var recordingStart = time.GetTimestamp();
        var generation = 0;
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                var connection = createConnection();
                var state = new ConnectionState();
                // Use an explicit connection boundary, not a session ID from an
                // upstream event: those events can arrive after the first turn.
                Emit(new() { ["type"] = "aihappey.transcription.connection", ["connection"] = ++generation,
                    ["offset_ms"] = time.GetElapsedTime(recordingStart).TotalMilliseconds });
                var pump = PumpAsync(connection, state);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                // Start the deadline before negotiation, conservatively counting
                // setup time. Token expiration is NOT the session duration limit.
                var renew = Task.Delay(renewAfter, time, deadline.Token);
                var started = false;
                try
                {
                    await connection.StartAsync(lifetime.Token).ConfigureAwait(false);
                    started = true;
                    ready.TrySetResult();
                    await Task.WhenAny(pump, renew).ConfigureAwait(false);
                    var expired = pump.IsCompleted && await pump.ConfigureAwait(false);
                    if (!expired)
                    {
                        try { await connection.FinishTranscriptionAsync().ConfigureAwait(false); }
                        catch (RealtimeFailureException error) when (error.Code == "transcription-duration" && !stopping.IsCancellationRequested)
                        { /* A delayed timer can race the actual provider limit. */ }
                    }
                }
                finally
                {
                    deadline.Cancel();
                    state.Closing = true;
                    await connection.DisposeAsync().ConfigureAwait(false);
                    // Drain final events before the next connection boundary or
                    // completion of the recording's public update stream.
                    try { await pump.ConfigureAwait(false); }
                    catch when (!started && lifetime.IsCancellationRequested) { }
                }
            }
            ready.TrySetCanceled(lifetime.Token);
            Complete();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            ready.TrySetCanceled(lifetime.Token);
            Complete();
        }
        catch (Exception error)
        {
            failure = RealtimeFailureException.Safe(error, "transcription-renewal", "The OpenAI transcription connection could not be started or renewed. Confirmed text has been retained.");
            ready.TrySetException(failure);
            Fail(failure);
        }
    }

    public async Task FinishTranscriptionAsync()
    {
        Task? running;
        lock (gate) { stopping.Cancel(); running = run; }
        if (running is not null) await running.ConfigureAwait(false); else Complete();
        if (failure is not null) throw failure;
    }
    public override Task SendAsync(JsonObject message, CancellationToken ct) => throw new NotSupportedException("This is a transcription-only connection owner.");
    public override Task SetMutedAsync(bool muted) => Task.CompletedTask; // The recording's gated input owns mute state.
    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        try { await FinishTranscriptionAsync().ConfigureAwait(false); }
        catch when (failure is not null) { /* Finish/public events already report this failure. Disposal must still release the caller's input. */ }
        finally { Lifetime.Cancel(); Complete(); }
    }
}
