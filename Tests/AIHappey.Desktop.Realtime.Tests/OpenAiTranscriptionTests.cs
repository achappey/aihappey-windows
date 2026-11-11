using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using AIHappey.Desktop.Core;

internal static class OpenAiTranscriptionTests
{
    public static async Task Run(Action<bool, string> check)
    {
        await RepeatedRenewal(check);
        await RecoveryAndFailure(check);
        await StopDuringHandover(check);
        await Finalization(check);
    }

    private static JsonObject Final(string id, string text) => new() { ["type"] = "conversation.item.input_audio_transcription.completed", ["item_id"] = id, ["transcript"] = text };
    private static JsonObject Limit() => new() { ["type"] = "error", ["error"] = new JsonObject { ["code"] = "session_expired", ["message"] = "Your session hit the maximum duration of 60 minutes" } };
    private static async Task Until(Func<bool> done)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!done()) await Task.Delay(1, timeout.Token);
    }

    private static async Task RepeatedRenewal(Action<bool, string> check)
    {
        var clock = new Clock(); var input = new Input(); var connections = new ConcurrentQueue<Connection>();
        var requests = new ConcurrentQueue<JsonObject>(); var credentials = 0;
        var preferences = new RealtimeTranscriptionPreferences { ProviderOptions = JsonNode.Parse("""{"openai":{"session":{"audio":{"input":{"transcription":{"language":"nl"}}}}}}""")!.AsObject() };
        await using var recording = new RealtimeTranscriptionSession((token, source) => new(token, source, () =>
        {
            var connection = new Connection(token, source); connections.Enqueue(connection); return connection;
        }, clock), "openai/gpt-transcribe", preferences, (payload, _) =>
        {
            requests.Enqueue(payload); return Task.FromResult("fresh-" + Interlocked.Increment(ref credentials));
        }, input);
        var root = Path.Combine(Path.GetTempPath(), "aihappey-openai-renewal-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new TranscriptionLibraryStore(root, "renewal-test");
            var item = new StoredTranscription { Id = Guid.NewGuid().ToString("N"), Kind = "realtime", Model = "openai/gpt-transcribe", Filename = "long-recording.txt" };
            var saving = Task.Run(async () =>
            {
                await foreach (var snapshot in recording.Updates.ReadAllAsync()) await store.SaveRealtimeAsync(item, snapshot, "active");
            });
            await recording.StartAsync(default);
            preferences.ProviderOptions["openai"]!["session"]!["audio"]!["input"]!["transcription"]!["language"] = "en";
            var first = connections.ToArray()[0];
            first.Push(Final("same", "Untimed first hour."));
            first.OnFinish = () => first.Push(Final("tail", "Last old phrase."));
            first.Push(new() { ["type"] = "conversation.item.input_audio_transcription.delta", ["item_id"] = "draft", ["delta"] = "unconfirmed preview" });
            clock.Advance(TimeSpan.FromMinutes(54));
            check(connections.Count == 1, "OpenAI does not renew before the safety-margin deadline");
            await recording.SetMutedAsync(true);
            clock.Advance(TimeSpan.FromMinutes(1));
            await Until(() => connections.Count == 2 && connections.Last().Started);
            var second = connections.Last(); input.Push([1, 2, 3, 4]);
            check(second.Pcm.Last().All(b => b == 0), "Windows recording mute survives connection replacement");
            await recording.SetMutedAsync(false); input.Push([1, 2, 3, 4]);
            check(second.Pcm.Last().SequenceEqual(new byte[] { 1, 2, 3, 4 }), "unmuting after rollover resumes the same input");
            second.Push(new() { ["type"] = "input_audio_buffer.speech_started", ["item_id"] = "same", ["audio_start_ms"] = 1000 });
            second.Push(new() { ["type"] = "input_audio_buffer.speech_stopped", ["item_id"] = "same", ["audio_end_ms"] = 2000 });
            second.Push(Final("same", "Second hour."));
            await Until(() => recording.Snapshot.Text.Contains("Second hour."));
            check(recording.Snapshot.Text == "Untimed first hour. Last old phrase. Second hour."
                && recording.Snapshot.Preview.Length == 0, "renewal drains old finals and orders connections independently of missing timestamps and reused IDs");
            var timed = recording.Snapshot.Segments.Single(segment => segment.Text == "Second hour.");
            check(timed.StartSecond == 3301 && timed.EndSecond == 3302, "OpenAI reset timestamps are offset onto the whole recording timeline");
            clock.Advance(TimeSpan.FromMinutes(55));
            await Until(() => connections.Count == 3 && connections.Last().Started);
            check(connections.Select(c => c.Credential).Distinct().Count() == 3 && requests.Count == 3
                && requests.All(r => r["model"]?.ToString() == "openai/gpt-transcribe"
                    && r["providerOptions"]?["openai"]?["session"]?["audio"]?["input"]?["transcription"]?["language"]?.ToString() == "nl"),
                "every renewal mints a fresh token with the recording's original model and cloned settings");
            await Task.WhenAll(recording.StopAsync(), recording.StopAsync()); await saving;
            await store.SaveRealtimeAsync(item, recording.Snapshot, "completed");
            var reloaded = await new TranscriptionLibraryStore(root, "renewal-test").ListAsync();
            check(reloaded.Count == 1 && reloaded[0].Item.Id == item.Id
                && reloaded[0].Item.Response["text"]?.ToString() == "Untimed first hour. Last old phrase. Second hour."
                && reloaded[0].Item.Response["response"]?["body"]?["status"]?.ToString() == "completed",
                "a fresh Windows library reload sees one combined completed transcription across multiple renewals");
            clock.Advance(TimeSpan.FromHours(2));
            check(connections.Count == 3 && connections.All(c => c.Disposals == 1) && input.Active == 0 && input.Disposals == 0,
                "Stop cancels future renewals, disposes each connection once, and respects caller-owned capture");
        }
        finally { await recording.StopAsync(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static async Task RecoveryAndFailure(Action<bool, string> check)
    {
        foreach (var failReplacement in new[] { false, true })
        {
            var clock = new Clock(); var connections = new ConcurrentQueue<Connection>();
            await using var owner = new RenewingOpenAiTranscriptionSession(_ => Task.FromResult("unused"), new Input(), () =>
            {
                var connection = new Connection(_ => Task.FromResult("fixture"), new Input());
                if (failReplacement && connections.Count > 0) connection.StartError = new InvalidOperationException("upstream secret must not be exposed");
                connections.Enqueue(connection); return connection;
            }, clock);
            var transcript = new RealtimeTranscriptionTranscript("openai"); Exception? failure = null;
            var pump = Task.Run(async () =>
            {
                try { await foreach (var message in owner.Events.ReadAllAsync()) transcript.Apply(message, 0); }
                catch (Exception error) { failure = error; }
            });
            await owner.StartAsync(default);
            connections.First().Push(Final("a", "Keep confirmed text."));
            connections.First().Push(Limit()); connections.First().Push(Limit());
            clock.Advance(TimeSpan.FromMinutes(55));
            await Until(() => connections.Count == 2);
            if (failReplacement)
            {
                await pump;
                check(failure is RealtimeFailureException && !failure.Message.Contains("secret")
                    && transcript.Snapshot().Text == "Keep confirmed text.", "failed OpenAI renewal retains confirmed text and exposes only a safe error");
                try { await owner.FinishTranscriptionAsync(); check(false, "failed renewal must not report success"); }
                catch (RealtimeFailureException) { check(true, "failed renewal cannot be mistaken for a completed recording"); }
            }
            else
            {
                await Until(() => connections.Last().Started);
                check(connections.Count == 2 && failure is null, "duration-limit events and a simultaneous timer produce exactly one replacement");
                connections.First().Push(Final("late", "obsolete"));
                connections.Last().Push(new() { ["type"] = "error", ["error"] = new JsonObject { ["code"] = "invalid_api_key", ["message"] = "secret" } });
                await pump;
                check(failure is RealtimeFailureException && transcript.Snapshot().Text == "Keep confirmed text.", "unrelated provider errors remain fatal and stale old events cannot mutate the combined transcript");
            }
            clock.Advance(TimeSpan.FromHours(2));
            check(connections.Count == 2 && connections.All(c => c.Disposals == 1), "failed connections are released without an infinite renewal loop");
        }
    }

    private static async Task StopDuringHandover(Action<bool, string> check)
    {
        foreach (var duringStartup in new[] { false, true })
        {
            var clock = new Clock(); var connections = new ConcurrentQueue<Connection>();
            var pause = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var owner = new RenewingOpenAiTranscriptionSession(_ => Task.FromResult("fixture"), new Input(), () =>
            {
                var connection = new Connection(_ => Task.FromResult("fixture"), new Input());
                if (duringStartup && connections.Count > 0) connection.StartGate = pause.Task;
                else if (!duringStartup) connection.FinishGate = pause.Task;
                connections.Enqueue(connection); return connection;
            }, clock);
            var pump = Task.Run(async () => { await foreach (var _ in owner.Events.ReadAllAsync()) { } });
            await owner.StartAsync(default); clock.Advance(TimeSpan.FromMinutes(55));
            await Until(() => duringStartup ? connections.Count == 2 : connections.First().Finishes > 0);
            var stopping = owner.FinishTranscriptionAsync();
            if (!duringStartup) pause.TrySetResult();
            await stopping.WaitAsync(TimeSpan.FromSeconds(5)); await pump;
            clock.Advance(TimeSpan.FromHours(1));
            check(connections.Count == (duringStartup ? 2 : 1) && connections.All(c => c.Disposals == 1),
                duringStartup ? "Stop cancels pending replacement startup without resurrecting capture" : "Stop during old-connection finalization prevents any replacement");
        }
    }

    private static async Task Finalization(Action<bool, string> check)
    {
        var turns = new OpenAiTranscriptionTurns(TimeSpan.Zero, TimeSpan.FromSeconds(1));
        turns.Observe(new() { ["type"] = "session.created", ["session"] = new JsonObject { ["audio"] = new JsonObject { ["input"] = new JsonObject { ["turn_detection"] = null } } } });
        var committed = 0;
        await turns.FinishAsync((message, _) =>
        {
            committed++;
            turns.Observe(new() { ["type"] = "input_audio_buffer.committed", ["item_id"] = "a" });
            turns.Observe(Final("a", "Manual final.")); return Task.CompletedTask;
        }, default);
        check(committed == 1, "Windows manual-turn transcription explicitly commits on finalization without VAD events");

        turns = new(TimeSpan.Zero, TimeSpan.FromSeconds(1));
        turns.Observe(new() { ["type"] = "input_audio_buffer.committed", ["item_id"] = "a" });
        turns.Observe(new() { ["type"] = "input_audio_buffer.committed", ["item_id"] = "b" });
        var draining = turns.FinishAsync((_, _) => throw new Exception("must not double commit"), default);
        turns.Observe(Final("b", "Second.")); check(!draining.IsCompleted, "finalization waits for all pending turns, not just the first completion");
        turns.Observe(Final("a", "First.")); await draining;

        turns = new(TimeSpan.Zero, TimeSpan.FromSeconds(1));
        turns.Observe(new() { ["type"] = "input_audio_buffer.speech_started", ["item_id"] = "a" });
        await turns.FinishAsync((message, _) =>
        {
            var benign = turns.Observe(new() { ["type"] = "error", ["error"] = new JsonObject { ["code"] = "input_audio_buffer_commit_empty", ["event_id"] = message["event_id"]!.ToString() } });
            check(!benign, "a VAD-raced empty commit is filtered only when its event ID matches");
            turns.Observe(Final("a", "Retained.")); return Task.CompletedTask;
        }, default);

        turns = new(TimeSpan.Zero, TimeSpan.FromMilliseconds(10));
        turns.Observe(new() { ["type"] = "input_audio_buffer.committed", ["item_id"] = "pending" });
        try { await turns.FinishAsync((_, _) => Task.CompletedTask, default); check(false, "missing final must time out"); }
        catch (RealtimeFailureException error) { check(error.Code == "transcription-finalize-timeout", "missing authoritative finals fail with a bounded timeout, never fabricated success"); }
        check(OpenAiTranscriptionErrors.IsDurationLimit(Limit()["error"]!.AsObject())
            && !OpenAiTranscriptionErrors.IsDurationLimit(new() { ["code"] = "token_expired", ["message"] = "Token expired" }), "session duration limits are distinguished from token expiry");
    }

    private sealed class Connection(Func<CancellationToken, Task<string>> token, ITranscriptionAudioInput input) : RealtimeSession, IOpenAiTranscriptionConnection
    {
        public volatile bool Started;
        public string Credential = "";
        public int Finishes, Disposals;
        public Task? StartGate, FinishGate;
        public Exception? StartError;
        public Action? OnFinish;
        public readonly ConcurrentQueue<byte[]> Pcm = new();
        private IDisposable? subscription;
        public void Push(JsonObject message) => Emit(message);
        public override async Task StartAsync(CancellationToken ct)
        {
            Credential = await token(ct);
            if (StartGate is not null) await StartGate.WaitAsync(ct);
            if (StartError is not null) throw StartError;
            subscription = input.Subscribe(Pcm.Enqueue, Fail); Started = true;
        }
        public async Task FinishTranscriptionAsync()
        {
            Interlocked.Increment(ref Finishes); subscription?.Dispose(); subscription = null;
            if (FinishGate is not null) await FinishGate;
            OnFinish?.Invoke();
        }
        public override Task SendAsync(JsonObject message, CancellationToken ct) => Task.CompletedTask;
        public override Task SetMutedAsync(bool muted) => Task.CompletedTask;
        public override ValueTask DisposeAsync()
        {
            if (Interlocked.Increment(ref Disposals) == 1) { subscription?.Dispose(); subscription = null; Lifetime.Cancel(); Complete(); }
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Input : ITranscriptionAudioInput
    {
        private readonly ConcurrentDictionary<int, Action<byte[]>> subscribers = new(); private int sequence;
        public int SampleRate => 48000;
        public int Active => subscribers.Count;
        public int Disposals;
        public void Push(byte[] pcm) { foreach (var callback in subscribers.Values) callback(pcm); }
        public IDisposable Subscribe(Action<byte[]> audio, Action<Exception> failed)
        {
            var id = Interlocked.Increment(ref sequence); subscribers[id] = audio; return new Subscription(() => subscribers.TryRemove(id, out _));
        }
        public void Dispose() { Disposals++; subscribers.Clear(); }
        private sealed class Subscription(Action dispose) : IDisposable { public void Dispose() => dispose(); }
    }

    private sealed class Clock : TimeProvider
    {
        private readonly object gate = new(); private readonly List<ManualTimer> timers = [];
        private long now;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Interlocked.Read(ref now);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (gate) { var timer = new ManualTimer(this, callback, state); timers.Add(timer); timer.Change(dueTime, period); return timer; }
        }
        public void Advance(TimeSpan amount)
        {
            List<ManualTimer> due;
            lock (gate) { now += (long)amount.TotalMilliseconds; due = timers.Where(t => t.Due <= now).ToList(); foreach (var timer in due) timer.Due = long.MaxValue; }
            foreach (var timer in due) timer.Fire();
        }
        private sealed class ManualTimer(Clock owner, TimerCallback callback, object? state) : ITimer
        {
            public long Due = long.MaxValue;
            public bool Change(TimeSpan dueTime, TimeSpan period) { lock (owner.gate) Due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : owner.now + (long)dueTime.TotalMilliseconds; return true; }
            public void Fire() => callback(state);
            public void Dispose() { lock (owner.gate) Due = long.MaxValue; }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
