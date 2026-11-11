using System.Text.Json.Nodes;

namespace AIHappey.Desktop.Core;

/// <summary>Correlates commits and authoritative finals; never promotes a delta to saved text.</summary>
internal sealed class OpenAiTranscriptionTurns(TimeSpan? drainDelay = null, TimeSpan? finalizationTimeout = null)
{
    private readonly object gate = new();
    private readonly HashSet<string> pending = new(StringComparer.Ordinal), completed = new(StringComparer.Ordinal);
    private TaskCompletionSource changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool manual, speech, awaitingCommit;
    private string? activeItem, commitId;
    private Exception? failure;

    // False means a correlated empty commit raced a server-side VAD commit.
    public bool Observe(JsonObject message)
    {
        lock (gate)
        {
            var type = message["type"]?.ToString(); var id = message["item_id"]?.ToString();
            var benign = false;
            if (type is "session.created" or "session.updated" or "transcription_session.created" or "transcription_session.updated")
            {
                var session = message["session"] as JsonObject;
                var input = session?["audio"]?["input"] as JsonObject;
                manual = input?.ContainsKey("turn_detection") == true ? input["turn_detection"] is null
                    : session?.ContainsKey("turn_detection") == true && session["turn_detection"] is null;
            }
            if (type == "input_audio_buffer.speech_started") { speech = true; activeItem = id; }
            if (type == "input_audio_buffer.committed") { awaitingCommit = false; if (activeItem is null || activeItem == id) speech = false; }
            if (id is not null && type is "input_audio_buffer.speech_started" or "input_audio_buffer.committed" or "conversation.item.input_audio_transcription.delta")
            { if (!completed.Contains(id)) pending.Add(id); }
            if (id is not null && type is "conversation.item.input_audio_transcription.completed" or "conversation.item.input_audio_transcription.failed")
            { pending.Remove(id); completed.Add(id); }
            if (type == "error")
            {
                var error = message["error"] as JsonObject;
                if (awaitingCommit && error?["event_id"]?.ToString() == commitId && error?["code"]?.ToString() == "input_audio_buffer_commit_empty")
                { awaitingCommit = speech = false; benign = true; }
                else failure = OpenAiTranscriptionErrors.Provider(error);
            }
            if (type == "conversation.item.input_audio_transcription.failed")
                failure = new RealtimeFailureException("transcription-finalize", "OpenAI could not finalize the transcription.");
            var previous = changed; changed = new(TaskCreationOptions.RunContinuationsAsynchronously); previous.TrySetResult();
            return !benign;
        }
    }

    public async Task FinishAsync(Func<JsonObject, CancellationToken, Task> send, CancellationToken ct)
    {
        // RTP and reliable data-channel messages have no shared ordering guarantee.
        await Task.Delay(drainDelay ?? TimeSpan.FromMilliseconds(150), ct).ConfigureAwait(false);
        JsonObject? commit = null;
        lock (gate)
        {
            if (failure is not null) throw failure;
            if ((speech || manual) && !awaitingCommit)
            {
                awaitingCommit = true; commitId = Guid.NewGuid().ToString("N");
                commit = new() { ["type"] = "input_audio_buffer.commit", ["event_id"] = commitId };
            }
        }
        if (commit is not null) await send(commit, ct).ConfigureAwait(false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(finalizationTimeout ?? TimeSpan.FromSeconds(5));
        try
        {
            while (true)
            {
                Task next;
                lock (gate)
                {
                    if (failure is not null) throw failure;
                    if (pending.Count == 0 && !awaitingCommit) return;
                    next = changed.Task;
                }
                await next.WaitAsync(timeout.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new RealtimeFailureException("transcription-finalize-timeout", "Timed out waiting for the final OpenAI transcription. Confirmed text has been retained."); }
    }
}
