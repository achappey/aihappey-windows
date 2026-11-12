using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using AIHappey.Desktop.Core;

internal static class DeepLTranscriptionTests
{
    public static async Task Run(Action<bool, string> check)
    {
        const string model = "deepl/voice-realtime/translate-to/nl";
        check(DeepLRealtimeProtocol.TargetLanguage("deepl/voice-realtime") is null
            && DeepLRealtimeProtocol.TargetLanguage("deepl/voice-realtime/translate-to/en-US") == "en-US", "DeepL default and variant language slugs");
        var preferences = new RealtimeTranscriptionPreferences { ProviderOptions = JsonNode.Parse("""{"deepl":{"target_languages":["fr"],"target_media_languages":["fr"],"message_format":"msgpack","future":{"keep":true}}}""")!.AsObject() };
        var request = RealtimeTranscriptionConfiguration.TokenRequest(model, preferences, DateTimeOffset.UtcNow);
        var options = request["providerOptions"]!["deepl"]!;
        check(options["target_languages"]!.ToJsonString() == "[\"nl\"]" && options["target_media_languages"] is null
            && options["message_format"]!.ToString() == "json" && options["future"]!["keep"]!.GetValue<bool>(), "DeepL model owns output, PCM and JSON without modifying custom options");
        check(preferences.ProviderOptions["deepl"]!["target_languages"]![0]!.ToString() == "fr", "DeepL token options do not mutate preferences");
        var address = DeepLRealtimeProtocol.Connection("wss://voice.deepl.com/current?route=2&token=old", "ephemeral+/token");
        check(address.AbsolutePath == "/current" && address.Query.Contains("route=2") && !address.Query.Contains("old")
            && address.Query.Contains("ephemeral%2B%2Ftoken"), "DeepL uses current provider URL and replaces token without dropping route");
        foreach (var invalid in new[] { "wss://evil.test", "wss://api.deepl.com.evil.test", "ws://api.deepl.com", "wss://user@api.deepl.com", "wss://api.deepl.com:444" })
        {
            try { DeepLRealtimeProtocol.Connection(invalid); check(false, "DeepL untrusted URL rejected"); }
            catch (GatewayException) { check(true, "DeepL rejects untrusted URL " + invalid); }
        }
        var message = JsonNode.Parse("""{"source_transcript_update":{"concluded":[{"text":"Source","start_time":0,"end_time":100,"language":"en"}],"tentative":[]},"target_transcript_update":{"language":"nl","concluded":[{"text":"Hallo","start_time":0,"end_time":100}],"tentative":[{"text":" wrong","start_time":100,"end_time":200}]}}""")!.AsObject();
        check(DeepLRealtimeProtocol.TranscriptEvent(message, null)!["concluded"]![0]!["text"]!.ToString() == "Source"
            && DeepLRealtimeProtocol.TranscriptEvent(message, "de") is null, "DeepL selects source-only default and ignores other target languages");
        var transcript = new RealtimeTranscriptionTranscript("deepl");
        transcript.Apply(DeepLRealtimeProtocol.TranscriptEvent(message, "nl")!, 1);
        check(transcript.Snapshot().Text == "Hallo" && transcript.Snapshot().DisplayText == "Hallo wrong", "DeepL tentative preview never enters confirmed transcript");
        var final = Update(" wereld。\n", 100, 200); transcript.Apply(final, 2); transcript.Apply(final, 2);
        var snapshot = transcript.Snapshot(); var saved = snapshot.Response(model, "completed");
        check(snapshot.Text == "Hallo wereld。\n" && snapshot.Preview == "" && snapshot.Segments.Count == 2
            && snapshot.Segments.Last().EndSecond == .2 && snapshot.Language == "nl", "DeepL appends once, replaces preview, preserves exact spacing and converts milliseconds");
        check(saved["text"]!.ToString() == snapshot.Text && saved["language"]!.ToString() == "nl" && !saved.ToJsonString().Contains("wrong"), "DeepL saves selected translation and language using normal transcription storage");
        var turns = new List<string>();
        using (var handler = new AgentRealtimeTurns("deepl", () => { }, _ => { }, turns.Add))
        {
            handler.Activity(true); handler.Apply(Update("Hallo", 0, 100)); handler.Apply(final);
            await Task.Delay(950); check(turns.Count == 0, "DeepL native chat does not end turns while speech is active");
            handler.Activity(false); await Task.Delay(950);
            check(turns.SequenceEqual(["Hallo wereld。"]), "DeepL native chat joins confirmed fragments into a single turn");
            handler.Apply(final); await Task.Delay(950); check(turns.Count == 1, "DeepL native chat ignores replayed fragments");
            handler.SetMuted(true); handler.Apply(Update("discard", 250, 300)); handler.SetMuted(false); handler.Apply(Update("discard", 250, 300));
            await Task.Delay(950); check(turns.Count == 1, "DeepL native chat discards muted text and late conclusions");
        }

        using var input = new Input(); using var socket = new Socket();
        await using var transport = new TranscriptionWebSocketSession("deepl", new() { ["model"] = model },
            _ => Task.FromResult(address.AbsoluteUri), input, (uri, _, _) => { check(uri == address, "DeepL native connector receives dynamic credential-bearing URL"); return Task.FromResult<WebSocket>(socket); });
        await transport.StartAsync(default); check(input.Subscribed, "DeepL capture starts only after connection");
        for (var i = 0; i < 6; i++) input.Push(Enumerable.Repeat((byte)42, 640).ToArray());
        await socket.Audio.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var finishing = transport.FinishInputAsync(); await socket.Ended.Task.WaitAsync(TimeSpan.FromSeconds(2));
        check(!input.Subscribed && !finishing.IsCompleted, "DeepL native stop releases capture, drains audio, and waits for final outputs");
        var sent = socket.Sent.Where(value => value["source_media_chunk"] is not null).ToArray();
        check(sent.Length == 2 && Convert.FromBase64String(sent[0]["source_media_chunk"]!["data"]!.ToString()).Length == 3200
            && Convert.FromBase64String(sent[1]["source_media_chunk"]!["data"]!.ToString()).Length == 640, "DeepL native audio uses JSON base64 100-ms PCM chunks and retains final partial chunk");
        socket.Push(new() { ["end_of_target_transcript"] = new JsonObject { ["language"] = "nl" } });
        await Task.Delay(30); check(!finishing.IsCompleted, "DeepL end-of-transcript is not final stream completion");
        socket.Push(new() { ["end_of_stream"] = new JsonObject() }); await finishing;
        check(socket.Sent.Last()["end_of_source_media"] is JsonObject, "DeepL sends documented end-of-source-media before final completion");

        using var failedInput = new Input(); using var failedSocket = new Socket();
        await using var failed = new TranscriptionWebSocketSession("deepl", new() { ["model"] = model }, _ => Task.FromResult(address.AbsoluteUri), failedInput, (_, _, _) => Task.FromResult<WebSocket>(failedSocket));
        await failed.StartAsync(default);
        failedSocket.Push(new() { ["error"] = new JsonObject { ["error_message"] = "ephemeral+/token" } });
        try { await foreach (var _ in failed.Events.ReadAllAsync()) { } check(false, "DeepL error must fail event stream"); }
        catch (RealtimeFailureException error) { check(!error.ToString().Contains("ephemeral"), "DeepL provider errors never expose credentials"); }
    }

    private static JsonObject Update(string text, int start, int end) => new()
    {
        ["type"] = "deepl.transcript", ["language"] = "nl", ["concluded"] = new JsonArray(new JsonObject { ["text"] = text, ["start_time"] = start, ["end_time"] = end }), ["tentative"] = new JsonArray()
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
        public List<JsonObject> Sent { get; } = [];
        public TaskCompletionSource Audio = new(TaskCreationOptions.RunContinuationsAsynchronously), Ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
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
            if (type != WebSocketMessageType.Text) throw new Exception("DeepL requires JSON text frames.");
            var message = JsonNode.Parse(bytes.AsSpan())!.AsObject(); Sent.Add(message);
            if (message["source_media_chunk"] is not null) Audio.TrySetResult();
            if (message["end_of_source_media"] is not null) Ended.TrySetResult();
            return Task.CompletedTask;
        }
        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> bytes, CancellationToken ct)
        {
            var message = await incoming.Reader.ReadAsync(ct); var encoded = Encoding.UTF8.GetBytes(message.ToJsonString());
            encoded.CopyTo(bytes.AsSpan()); return new(encoded.Length, WebSocketMessageType.Text, true);
        }
    }
}
