using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using AIHappey.Desktop.Core;

internal static class SpeechmaticsTranscriptionTests
{
    public static async Task Run(Action<bool, string> check)
    {
        foreach (var model in new[] { "standard", "enhanced", "melia-1", "oak-1", "linden-1" })
        {
            var id = "speechmatics/" + model;
            foreach (var region in new[] { "global", "eu", "us", "au" })
            {
                var options = JsonNode.Parse("""{"transcription_config":{"language":"nl","additional_vocab":[{"content":"AIHappey","sounds_like":["AI happy"]}]},"realtime_config":{"max_delay":0.7},"agent_config":{"emit_sentences":true}}""")!.AsObject();
                options["region"] = region;
                var start = SpeechmaticsRealtimeProtocol.Start(id, options); var config = start["transcription_config"]!;
                var uri = SpeechmaticsRealtimeProtocol.Connection(id, options, "temporary.jwt.fixture");
                check(uri.Host == region + ".rt.speechmatics.com" && uri.AbsolutePath == (model == "linden-1" ? "/v2/agent" : "/v2"), "Speechmatics region/protocol " + region + "/" + model);
                check(config["language"]!.ToString() == (model is "melia-1" or "oak-1" ? "multi" : "nl") && config["model"]!.ToString() == model,
                    "Speechmatics model/language " + model);
                check(start["audio_format"]?["sample_rate"]?.GetValue<int>() == 16000 && config["enable_partials"]!.GetValue<bool>()
                    && (model == "linden-1" ? config["max_delay"] is null && start["turn_config"]?["turn_detection_mode"]?.ToString() == "vad" : config["emit_sentences"] is null), "Speechmatics isolated native settings " + model);
                var preferences = new RealtimeTranscriptionPreferences { ProviderOptions = new() { ["speechmatics"] = options } };
                var token = RealtimeTranscriptionConfiguration.TokenRequest(id, preferences, DateTimeOffset.UtcNow);
                check(token["providerOptions"]?["speechmatics"]?.AsObject().Count == 1 && token["providerOptions"]?["speechmatics"]?["ttl"]?.GetValue<int>() == 60, "Speechmatics token carries only short lifetime");
            }
        }
        foreach (var ttl in new[] { 59d, 86401, 60.5, double.NaN })
        {
            try { _ = SpeechmaticsRealtimeProtocol.Ttl(new() { ["ttl"] = ttl }); check(false, "Invalid Speechmatics TTL rejected"); }
            catch (ArgumentException) { check(true, "Invalid Speechmatics TTL rejected"); }
        }
        var multilingualOptions = new RealtimeTranscriptionPreferences { ProviderOptions = JsonNode.Parse("""{"speechmatics":{"realtime_config":{"language_hints":["nl","en"]}}}""")!.AsObject() };
        multilingualOptions.Validate(); check(true, "Speechmatics stored language hints validate independently of the selected model");
        var transcript = new RealtimeTranscriptionTranscript("speechmatics");
        transcript.Apply(Event("AddPartialTranscript", "wrong"), 1); transcript.Apply(Event("AddPartialTranscript", "replacement"), 1);
        check(transcript.Snapshot().Text == "" && transcript.Snapshot().Preview == "replacement", "Speechmatics partial snapshots replace without committing");
        var first = Event("AddTranscript", "你好，"); transcript.Apply(first, 1); transcript.Apply(first, 1);
        transcript.Apply(Event("AddTranscript", "世界。 ", 1, 2), 2); transcript.Apply(Event("AddPartialTranscript", "draft", 2, 3), 3);
        check(transcript.Snapshot().Text == "你好，世界。 " && transcript.Snapshot().Segments.Count == 2, "Speechmatics preserves exact formatting and deduplicates finals");
        transcript.Apply(new() { ["message"] = "EndOfTranscript" }, 3);
        check(transcript.Snapshot().DisplayText == "你好，世界。 ", "Speechmatics final completion never promotes interim text");
        var agent = new RealtimeTranscriptionTranscript("speechmatics");
        agent.Apply(Event("AddSegment", "Hello"), 1); agent.Apply(Event("AddPartialSegment", "draft", 1, 2), 2);
        check(agent.Snapshot().DisplayText == "Hello draft" && !agent.Apply(new() { ["message"] = "EndOfTurn" }, 2), "Speechmatics Agent turns do not commit drafts or submit chat");
        agent.Apply(Event("AddSegment", "world.", 1, 2), 2);
        check(agent.Snapshot().Text == "Hello world." && agent.Snapshot().Segments.Last().EndSecond == 2, "Speechmatics Agent stable segments retain timing and boundaries");

        foreach (var model in new[] { "standard", "linden-1" })
        foreach (var withAudio in new[] { false, true })
        {
            using var input = new Input(); using var socket = new Socket();
            var setup = new JsonObject { ["model"] = "speechmatics/" + model, ["options"] = new JsonObject { ["region"] = "eu" },
                ["recognition"] = SpeechmaticsRealtimeProtocol.Start(model, new()) };
            await using var session = new TranscriptionWebSocketSession("speechmatics", setup, _ => Task.FromResult("temporary.jwt.fixture"), input, (uri, _, _) =>
            {
                check(uri.Host == "eu.rt.speechmatics.com" && uri.Query == "?jwt=temporary.jwt.fixture", "Native Speechmatics uses only the short-lived JWT at the chosen region");
                return Task.FromResult<WebSocket>(socket);
            });
            var starting = session.StartAsync(default); await socket.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            check(!input.Subscribed, "Native Speechmatics waits for RecognitionStarted before microphone capture");
            socket.Push(new() { ["message"] = "Info" }); socket.Push(new() { ["message"] = "RecognitionStarted" }); await starting;
            if (withAudio) { input.Push([0, 128, 0, 0, 255, 127]); await socket.Audio.Task.WaitAsync(TimeSpan.FromSeconds(2)); socket.Push(new() { ["message"] = "AudioAdded", ["seq_no"] = 1 }); }
            var finish = session.FinishInputAsync(); check(ReferenceEquals(finish, session.FinishInputAsync()), "Native Speechmatics finalization is idempotent");
            await socket.Ended.Task.WaitAsync(TimeSpan.FromSeconds(2));
            check(socket.Messages.Last()["last_seq_no"]!.GetValue<int>() == (withAudio ? 1 : 0) && !input.Subscribed && !finish.IsCompleted,
                "Native Speechmatics drains binary audio and sends the exact EOS sequence including empty input");
            socket.Push(Event(model == "linden-1" ? "AddSegment" : "AddTranscript", "Final"));
            socket.Push(new() { ["message"] = "EndOfTranscript" }); await finish.WaitAsync(TimeSpan.FromSeconds(2));
            check(socket.Binary.Count == (withAudio ? 1 : 0) && (!withAudio || socket.Binary[0].SequenceEqual(new byte[] { 0, 128, 0, 0, 255, 127 })), "Native Speechmatics sends raw little-endian PCM16 frames");
            check(session.Events.TryRead(out var ready) && ready["message"]?.ToString() == "RecognitionStarted", "Native Speechmatics preserves readiness metadata for delimiter handling");
        }

        using var failedInput = new Input(); using var failedSocket = new Socket();
        var failedSetup = new JsonObject { ["model"] = "standard", ["recognition"] = SpeechmaticsRealtimeProtocol.Start("standard", new()) };
        await using var failed = new TranscriptionWebSocketSession("speechmatics", failedSetup, _ => Task.FromResult("temporary.jwt.fixture"), failedInput, (_, _, _) => Task.FromResult<WebSocket>(failedSocket));
        var failedStart = failed.StartAsync(default); await failedSocket.Started.Task;
        failedSocket.Push(new() { ["message"] = "RecognitionStarted" }); await failedStart;
        failedSocket.Push(new() { ["message"] = "Error", ["reason"] = "temporary.jwt.fixture" });
        try { await foreach (var _ in failed.Events.ReadAllAsync()) { } check(false, "Speechmatics errors terminate events"); }
        catch (RealtimeFailureException error) { check(!error.ToString().Contains("temporary.jwt"), "Native Speechmatics errors redact credentials"); }

        using var cancelledInput = new Input(); using var cancellation = new CancellationTokenSource();
        await using var cancelled = new TranscriptionWebSocketSession("speechmatics", failedSetup, async ct => { await Task.Delay(Timeout.Infinite, ct); return "unused"; }, cancelledInput);
        var cancelledStart = cancelled.StartAsync(cancellation.Token); cancellation.Cancel();
        try { await cancelledStart; check(false, "Speechmatics cancelled startup rejects"); }
        catch (OperationCanceledException) { check(!cancelledInput.Subscribed, "Native Speechmatics cancelled startup never opens microphone capture"); }
    }

    private static JsonObject Event(string message, string text, double start = 0, double end = 1) => new()
    {
        ["message"] = message, ["metadata"] = new JsonObject { ["transcript"] = text, ["start_time"] = start, ["end_time"] = end },
        ["segment"] = new JsonObject { ["transcript"] = text }
    };
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
        public List<JsonObject> Messages { get; } = [];
        public List<byte[]> Binary { get; } = [];
        public TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously), Audio = new(TaskCreationOptions.RunContinuationsAsynchronously), Ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Push(JsonObject value) => incoming.Writer.TryWrite(value);
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => state;
        public override string? SubProtocol => null;
        public override void Abort() => state = WebSocketState.Aborted;
        public override void Dispose() => state = WebSocketState.Closed;
        public override Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken ct) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus status, string? description, CancellationToken ct) => Task.CompletedTask;
        public override Task SendAsync(ArraySegment<byte> bytes, WebSocketMessageType type, bool end, CancellationToken ct)
        {
            if (type == WebSocketMessageType.Binary) { Binary.Add(bytes.ToArray()); Audio.TrySetResult(); }
            else
            {
                var message = JsonNode.Parse(bytes.AsSpan())!.AsObject(); Messages.Add(message);
                if (message["message"]?.ToString() == "StartRecognition") Started.TrySetResult();
                if (message["message"]?.ToString() == "EndOfStream") Ended.TrySetResult();
            }
            return Task.CompletedTask;
        }
        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> bytes, CancellationToken ct)
        {
            var message = await incoming.Reader.ReadAsync(ct); var encoded = Encoding.UTF8.GetBytes(message.ToJsonString());
            encoded.CopyTo(bytes.AsSpan()); return new(encoded.Length, WebSocketMessageType.Text, true);
        }
    }
}
