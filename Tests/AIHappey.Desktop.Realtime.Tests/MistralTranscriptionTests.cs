using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using AIHappey.Desktop.Core;

internal static class MistralTranscriptionTests
{
    public static async Task Run(Action<bool, string> check)
    {
        const string model = "mistral/voxtral-mini-transcribe-realtime-3";
        var preferences = new RealtimeTranscriptionPreferences { ProviderOptions = JsonNode.Parse("""{"mistral":{"language":"nl","context_bias":["Rotterdam"],"target_streaming_delay_ms":480,"diarize":true}}""")!.AsObject() };
        var session = RealtimeTranscriptionConfiguration.MistralSession(preferences.ProviderOptions["mistral"]!.AsObject());
        check(session["audio_format"]?["sample_rate"]?.GetValue<int>() == 16000 && session["diarize"] is null, "Mistral settings use native PCM16 and exclude advanced controls");
        check(RealtimeTranscriptionConfiguration.MistralConnection(model).Query == "?model=voxtral-mini-transcribe-realtime-3", "Mistral connection preserves the scoped model exactly");
        var request = RealtimeTranscriptionConfiguration.TokenRequest(model, preferences, DateTimeOffset.UtcNow);
        check(request["model"]?.ToString() == model && request["providerOptions"]?["mistral"]?.AsObject().Count == 0, "Mistral session settings are not forwarded to token minting");
        foreach (var delay in new[] { 80, 480, 1200, 2400 })
            check(RealtimeTranscriptionConfiguration.MistralSession(new() { ["target_streaming_delay_ms"] = delay })["target_streaming_delay_ms"]?.GetValue<int>() == delay, "Mistral accepts documented delay " + delay);
        foreach (var delay in new[] { 79, 1201, 2399, 2401 })
        {
            try { _ = RealtimeTranscriptionConfiguration.MistralSession(new() { ["target_streaming_delay_ms"] = delay }); check(false, "Mistral invalid delay rejected"); }
            catch (ArgumentException) { check(true, "Mistral rejects undocumented delay " + delay); }
        }
        check(!RealtimeTranscriptionConfiguration.IsMistralModel("mistral/voxtral-mini-transcribe-3") && !RealtimeTranscriptionConfiguration.IsMistralModel("mistral/voxtral-mini-tts-3-0"), "Mistral batch and TTS models cannot open transcription WebSockets");
        var transcript = new RealtimeTranscriptionTranscript("mistral");
        foreach (var text in new[] { "  Hello", " world", ".\n" }) transcript.Apply(new() { ["type"] = "transcription.text.delta", ["text"] = text }, 1);
        check(transcript.Snapshot().Text == "  Hello world.\n" && transcript.Snapshot().HasConfirmedText && transcript.Snapshot().Segments.Count == 0, "Mistral deltas are confirmed verbatim without invented timestamps");
        transcript.Apply(new() { ["type"] = "transcription.done", ["text"] = "  Final.\n" }, 2);
        transcript.Apply(new() { ["type"] = "transcription.text.delta", ["text"] = "late" }, 3);
        check(transcript.Snapshot().Text == "  Final.\n", "Mistral final transcript replaces deltas and ignores late events");

        using var input = new Input(); using var socket = new Socket();
        await using var transport = new TranscriptionWebSocketSession("mistral", new() { ["model"] = model, ["session"] = session },
            _ => Task.FromResult("rt_fixture.jwt"), input, (uri, _, _) => { check(uri == RealtimeTranscriptionConfiguration.MistralConnection(model), "native Mistral connector receives the exact model URL"); return Task.FromResult<WebSocket>(socket); });
        var starting = transport.StartAsync(CancellationToken.None);
        await socket.Configured.Task.WaitAsync(TimeSpan.FromSeconds(2));
        check(!input.Subscribed, "native Mistral microphone waits for session.updated");
        socket.Push(new() { ["type"] = "session.updated", ["session"] = session.DeepClone() });
        await starting;
        check(input.Subscribed, "native Mistral microphone starts after acknowledged configuration");
        input.Push([0, 128, 0, 0, 255, 127]);
        await socket.Audio.Task.WaitAsync(TimeSpan.FromSeconds(2));
        check(Convert.FromBase64String(socket.Sent.Single(message => message["type"]?.ToString() == "input_audio.append")["audio"]!.ToString()).SequenceEqual(new byte[] { 0, 128, 0, 0, 255, 127 }), "native Mistral sends exact PCM16 bytes in base64 JSON");
        var finish = transport.FinishInputAsync();
        await socket.Ended.Task.WaitAsync(TimeSpan.FromSeconds(2));
        check(!finish.IsCompleted && !input.Subscribed, "native Mistral stop drains audio and waits for the authoritative final event");
        socket.Push(new() { ["type"] = "transcription.done", ["text"] = "done" });
        await finish;
        check(socket.Sent.Last()["type"]?.ToString() == "input_audio.end", "native Mistral sends the documented end-of-audio message");

        using var failedInput = new Input(); using var failedSocket = new Socket();
        await using var failed = new TranscriptionWebSocketSession("mistral", new() { ["model"] = model, ["session"] = session.DeepClone() },
            _ => Task.FromResult("rt_fixture"), failedInput, (_, _, _) => Task.FromResult<WebSocket>(failedSocket));
        var failedStart = failed.StartAsync(CancellationToken.None);
        await failedSocket.Configured.Task.WaitAsync(TimeSpan.FromSeconds(2));
        failedSocket.Push(new() { ["type"] = "error", ["error"] = new JsonObject { ["message"] = "rt_secret" } });
        try { await failedStart; check(false, "native Mistral rejected setup"); }
        catch (RealtimeFailureException error) { check(!failedInput.Subscribed && !error.ToString().Contains("rt_secret"), "native Mistral setup errors are credential-safe and never start capture"); }
    }

    private sealed class Input : ITranscriptionAudioInput
    {
        public int SampleRate => 16000;
        public bool Subscribed;
        private Action<byte[]>? send;
        public IDisposable Subscribe(Action<byte[]> audio, Action<Exception> failed) { Subscribed = true; send = audio; return new Subscription(this); }
        public void Push(byte[] bytes) => send!(bytes);
        public void Dispose() { Subscribed = false; send = null; }
        private sealed class Subscription(Input input) : IDisposable { public void Dispose() => input.Dispose(); }
    }
    private sealed class Socket : WebSocket
    {
        private readonly Channel<JsonObject> incoming = Channel.CreateUnbounded<JsonObject>();
        private WebSocketState state = WebSocketState.Open;
        public List<JsonObject> Sent { get; } = [];
        public TaskCompletionSource Configured = new(TaskCreationOptions.RunContinuationsAsynchronously), Audio = new(TaskCreationOptions.RunContinuationsAsynchronously), Ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Socket() => Push(new() { ["type"] = "session.created", ["session"] = new JsonObject() });
        public void Push(JsonObject value) => incoming.Writer.TryWrite(value);
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => state;
        public override string? SubProtocol => "realtime";
        public override void Abort() => state = WebSocketState.Aborted;
        public override void Dispose() => state = WebSocketState.Closed;
        public override Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken ct) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus status, string? description, CancellationToken ct) => Task.CompletedTask;
        public override Task SendAsync(ArraySegment<byte> bytes, WebSocketMessageType type, bool end, CancellationToken ct)
        {
            if (type != WebSocketMessageType.Text) throw new Exception("Mistral requires JSON audio frames.");
            var message = JsonNode.Parse(bytes.AsSpan())!.AsObject(); Sent.Add(message);
            switch (message["type"]?.ToString()) { case "session.update": Configured.TrySetResult(); break; case "input_audio.append": Audio.TrySetResult(); break; case "input_audio.end": Ended.TrySetResult(); break; }
            return Task.CompletedTask;
        }
        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> bytes, CancellationToken ct)
        {
            var message = await incoming.Reader.ReadAsync(ct); var encoded = Encoding.UTF8.GetBytes(message.ToJsonString());
            encoded.CopyTo(bytes.AsSpan()); return new(encoded.Length, WebSocketMessageType.Text, true);
        }
    }
}
