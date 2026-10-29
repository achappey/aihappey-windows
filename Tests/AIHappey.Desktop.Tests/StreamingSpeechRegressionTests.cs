using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using AIHappey.Desktop.Core;

internal static class StreamingSpeechRegressionTests
{
    private static StreamEvent Event(string type, string delta = "", string id = "a") => new(type,
        JsonSerializer.SerializeToElement(new { type, id, delta }), null);
    private static async Task Until(Func<bool> ready)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!ready()) await Task.Delay(10, timeout.Token);
    }
    public static async Task RunAsync(Action<bool, string> check)
    {
        var dg = StreamingSpeechProtocol.Create("deepgram/aura-2-asteria-en", new() { ["sampleRate"] = 48000, ["api_key"] = "never-forward" });
        check(dg.SampleRate == 48000 && dg.Endpoint.Host == "api.deepgram.com" && !dg.Endpoint.ToString().Contains("never-forward"), "streaming speech: provider settings are allowlisted and sample rates explicit");
        var cs = StreamingSpeechProtocol.Create("cartesia/tts/sonic-3.6/voice-id", new() { ["language"] = "nl", ["speed"] = 1.1 });
        var first = cs.Append("Hel"); var final = cs.Finish();
        check(first["voice"]!["id"]!.ToString() == "voice-id" && first["continue"]!.GetValue<bool>() && !final["continue"]!.GetValue<bool>()
            && final["transcript"]!.ToString() == "" && final["context_id"]!.ToString() == first["context_id"]!.ToString(), "streaming speech: Cartesia keeps the selected voice and context across partial/final messages");
        check(!cs.Done(new() { ["type"] = "done", ["context_id"] = "stale" }), "streaming speech: stale Cartesia context is ignored");
        check(!StreamingSpeechProtocol.Supports(new("deepgram/aura-2-asteria-en", "speech") { ModelType = "speech" })
            && StreamingSpeechProtocol.Supports(new("deepgram/aura-2-asteria-en", "speech") { ModelType = "speech", Tags = ["real-time", "realtime-token"] }), "streaming speech: current catalog and registered adapter gate automatic activation");
        var spoken = new ConcurrentQueue<string>();
        await using (var observer = new StreamingAgentSpeech((started, ct) => Task.FromResult<IStreamingSpeechSession>(new FakeSpeech(text => { spoken.Enqueue(text); started(); })),
            (_, _) => throw new Exception("Unexpected fallback"), _ => check(false, "Unexpected speech failure"), default))
        {
            observer.Observe(0, Event("text-delta", "Hel")); await Until(() => !spoken.IsEmpty);
            check(spoken.SequenceEqual(["Hel"]), "streaming speech: partial text reaches the engine before text-end");
            observer.Observe(0, Event("reasoning-delta", "private")); observer.Observe(0, Event("tool-input-available", "private"));
            observer.Observe(0, Event("text-delta", "lo.")); observer.Observe(0, Event("text-end"));
            observer.Observe(0, Event("text-delta", "late"));
            observer.Observe(1, Event("text-delta", "Again.")); observer.CompleteResponse(1);
            await observer.DrainAsync();
            check(spoken.SequenceEqual(["Hel", "lo.", "Again."]), "streaming speech: tool continuations remain ordered and late/reasoning/tool payloads are not spoken");
        }
        foreach (var played in new[] { false, true })
        {
            var fallback = new ConcurrentQueue<string>(); var failures = 0;
            await using var observer = new StreamingAgentSpeech((started, ct) => Task.FromResult<IStreamingSpeechSession>(new FakeSpeech(_ => { if (played) started(); throw new IOException("fixture"); })),
                (text, _) => { fallback.Enqueue(text); return Task.CompletedTask; }, _ => Interlocked.Increment(ref failures), default);
            observer.Observe(0, Event("text-delta", "Hel")); await Task.Delay(30);
            check(fallback.IsEmpty, "streaming speech: fallback never speaks an unfinished preview");
            observer.Observe(0, Event("text-delta", "lo.")); observer.CompleteResponse(0); await observer.DrainAsync();
            check(played ? fallback.IsEmpty && failures == 1 : fallback.SequenceEqual(["Hello."]) && failures == 0,
                played ? "streaming speech: failure after playback never replays the answer" : "streaming speech: pre-playback failure falls back once with completed text");
        }
        using (var cancellation = new CancellationTokenSource())
        {
            var calls = 0;
            await using var observer = new StreamingAgentSpeech((_, ct) => Task.FromResult<IStreamingSpeechSession>(new FakeSpeech(_ => Interlocked.Increment(ref calls))),
                (_, _) => throw new Exception("Cancellation replayed"), _ => check(false, "Cancellation reported"), cancellation.Token);
            observer.Observe(0, Event("text-delta", "Hello")); await Until(() => calls == 1);
            cancellation.Cancel(); observer.Observe(0, Event("text-delta", "late")); await observer.DrainAsync();
            check(calls == 1, "streaming speech: cancellation stops speech and rejects stale deltas without fallback");
        }
        using (var socket = new FixtureSocket())
        {
            var audio = new ConcurrentQueue<byte[]>();
            await using var session = new StreamingSpeechSession(socket, dg, audio.Enqueue, default); session.Start();
            await session.AppendAsync("Hello.", default); await session.FlushAsync(default);
            socket.Event(new() { ["type"] = "Flushed", ["sequence_id"] = 0 });
            await session.AppendAsync(" Again.", default);
            var finished = session.FinishAsync(default);
            socket.Event(new() { ["type"] = "Flushed", ["sequence_id"] = 0 }); await Task.Delay(50);
            check(!finished.IsCompleted, "streaming speech: duplicate earlier flush acknowledgement cannot finish later input");
            await Until(() => socket.Sent.Count(value => value["type"]?.ToString() == "Flush") == 2);
            socket.Binary([0, 1]); socket.Event(new() { ["type"] = "Flushed", ["sequence_id"] = 1 }); await finished;
            check(audio.Count == 1, "streaming speech: binary audio is delivered incrementally before final flush completion");
        }
        using (var socket = new FixtureSocket())
        {
            var audio = new ConcurrentQueue<byte[]>();
            await using var session = new StreamingSpeechSession(socket, cs, audio.Enqueue, default); session.Start();
            await session.AppendAsync("Hello", default); var finished = session.FinishAsync(default);
            socket.Event(new() { ["type"] = "chunk", ["context_id"] = "stale", ["data"] = "AQI=" });
            socket.Event(new() { ["type"] = "chunk", ["context_id"] = first["context_id"]!.ToString(), ["data"] = "AQI=" });
            socket.Event(new() { ["type"] = "done", ["context_id"] = first["context_id"]!.ToString() }); await finished;
            check(audio.Count == 1 && audio.Single().SequenceEqual(new byte[] { 1, 2 }), "streaming speech: Cartesia base64 chunks and done are filtered by context");
        }
    }
    private sealed class FakeSpeech(Action<string> append) : IStreamingSpeechSession
    {
        public Task AppendAsync(string text, CancellationToken ct) { append(text); return Task.CompletedTask; }
        public Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
        public Task FinishAsync(CancellationToken ct) => Task.CompletedTask;
        public Task CancelAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class FixtureSocket : WebSocket
    {
        private readonly Channel<(byte[] Bytes, WebSocketMessageType Type)> incoming = Channel.CreateUnbounded<(byte[], WebSocketMessageType)>();
        public readonly ConcurrentQueue<JsonObject> Sent = new();
        private WebSocketState state = WebSocketState.Open;
        public override WebSocketState State => state;
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override string? SubProtocol => null;
        public void Event(JsonObject value) => incoming.Writer.TryWrite((Encoding.UTF8.GetBytes(value.ToJsonString()), WebSocketMessageType.Text));
        public void Binary(byte[] bytes) => incoming.Writer.TryWrite((bytes, WebSocketMessageType.Binary));
        public override void Abort() { state = WebSocketState.Aborted; incoming.Writer.TryComplete(); }
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken ct) { Abort(); return Task.CompletedTask; }
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken ct) { Abort(); return Task.CompletedTask; }
        public override void Dispose() => Abort();
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool endOfMessage, CancellationToken ct)
        { Sent.Enqueue(JsonNode.Parse(buffer.AsSpan())!.AsObject()); return Task.CompletedTask; }
        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken ct)
        { var value = await incoming.Reader.ReadAsync(ct); value.Bytes.CopyTo(buffer.AsSpan()); return new(value.Bytes.Length, value.Type, true); }
    }
}
