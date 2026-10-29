using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Desktop.Core;

internal static class OpenAiRealtimeSpeechRegressionTests
{
    private const string Model = "openai/gpt-realtime-2.1-mini";
    private static async Task Until(Func<bool> ready)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!ready()) await Task.Delay(5, timeout.Token);
    }
    private static JsonObject Created(JsonObject request, string id) => new() { ["type"] = "response.created", ["response"] = new JsonObject { ["id"] = id, ["metadata"] = request["response"]!["metadata"]!.DeepClone() } };
    private static JsonObject Audio(string id, string data = "AQI=") => new() { ["type"] = "response.output_audio.delta", ["response_id"] = id, ["delta"] = data };
    private static JsonObject Done(string id, string status = "completed") => new() { ["type"] = "response.done", ["response"] = new JsonObject { ["id"] = id, ["status"] = status } };
    private static JsonObject[] Requests(StreamingSpeechRegressionTests.FixtureSocket socket) => socket.Sent.Where(value => value["type"]?.ToString() == "response.create").ToArray();
    public static async Task RunAsync(Action<bool, string> check)
    {
        var target = new ChatTarget(Model, "Realtime speech") { ModelType = "audio", Tags = ["real-time", "realtime-token", "realtime-session", "realtime-speech"] };
        check(OpenAiRealtimeSpeech.Supports(target) && !StreamingSpeechProtocol.Supports(target), "OpenAI speech: explicit agent capability leaves the shared model classified as audio");
        check(!OpenAiRealtimeSpeech.Supports(new(Model, "missing tags") { ModelType = "audio" })
            && !OpenAiRealtimeSpeech.Supports(new("openai/gpt-realtime-2.1", "other model") { ModelType = "audio", Tags = target.Tags }), "OpenAI speech: missing capability or unsupported model cannot activate speech");
        var config = OpenAiRealtimeSpeech.Session(Model, new() { ["voice"] = "cedar", ["speed"] = 1.2, ["api_key"] = "private", ["response_format"] = "mp3", ["instructions"] = "override" });
        check(config["audio"]!["output"]!["format"]!["rate"]!.GetValue<int>() == 24000
            && config["audio"]!["output"]!["voice"]!.ToString() == "cedar" && config["audio"]!["input"]!["turn_detection"] is null
            && config["tools"]!.AsArray().Count == 0 && config["tool_choice"]!.ToString() == "none"
            && !config.ToJsonString().Contains("private") && !config.ToJsonString().Contains("mp3") && !config.ToJsonString().Contains("override"), "OpenAI speech: GA output settings are allowlisted with no VAD/tools or copied HTTP credentials/options");
        foreach (var options in new[] { new JsonObject { ["voice"] = "nova" }, new JsonObject { ["speed"] = 2 }, new JsonObject { ["speed"] = "1" } })
        {
            try { OpenAiRealtimeSpeech.Session(Model, options); check(false, "OpenAI speech: invalid options must fail"); }
            catch (InvalidOperationException) { check(true, "OpenAI speech: incompatible voice/speed is rejected before credential acquisition"); }
        }
        check(OpenAiRealtimeSpeech.BatchLength("Hel", false) == 0 && OpenAiRealtimeSpeech.BatchLength("Hello. Again", false) == 7
            && OpenAiRealtimeSpeech.BatchLength(new string('a', 999) + "😀x", true) == 999, "OpenAI speech: batching waits for sentences and never splits Unicode surrogate pairs");
        using (var socket = new StreamingSpeechRegressionTests.FixtureSocket())
        {
            var audio = new ConcurrentQueue<byte[]>(); var failed = 0;
            await using var session = new OpenAiRealtimeSpeechSession(socket, config, audio.Enqueue, default, () => Interlocked.Increment(ref failed));
            var starting = session.StartAsync(default); await Until(() => socket.Sent.Count > 0);
            check(socket.Sent.First()["type"]!.ToString() == "session.update" && socket.Sent.First()["session"]!["model"] is null, "OpenAI speech: initialization sends GA session update without changing the model");
            socket.Event(new() { ["type"] = "session.created", ["session"] = config.DeepClone() }); await Task.Delay(20);
            check(!starting.IsCompleted && Requests(socket).Length == 0, "OpenAI speech: session.created alone cannot bypass configuration readiness");
            socket.Event(new() { ["type"] = "session.updated", ["session"] = config.DeepClone() }); await starting;
            await session.AppendAsync("Hel", default); check(Requests(socket).Length == 0, "OpenAI speech: individual LLM tokens are buffered rather than spoken separately");
            await session.AppendAsync("lo. ", default); await session.AppendAsync("Again.", default); var finish = session.FinishAsync(default);
            var first = Requests(socket).Single(); socket.Event(Created(first, "first")); socket.Event(Audio("stale")); socket.Event(Audio("first"));
            socket.Event(new() { ["type"] = "response.output_audio.done", ["response_id"] = "first" }); await Until(() => audio.Count == 1);
            check(!finish.IsCompleted && Requests(socket).Length == 1, "OpenAI speech: output_audio.done is not response completion and stale audio is ignored");
            socket.Event(Done("first")); await Until(() => Requests(socket).Length == 2); socket.Event(Done("first"));
            var requests = Requests(socket);
            check(requests.Select(value => value["response"]!["input"]![0]!["content"]![0]!["text"]!.ToString()).SequenceEqual(new[] { "Hello. ", "Again." })
                && requests.All(value => value["response"]!["conversation"]!.ToString() == "none" && value["response"]!["input"]!.AsArray().Count == 1), "OpenAI speech: ordered out-of-band batches preserve text without accumulating conversation history");
            socket.Event(Created(requests[1], "second")); socket.Event(Audio("first")); socket.Event(Audio("second")); socket.Event(Done("second")); await finish;
            check(audio.Count == 2 && failed == 0, "OpenAI speech: successful completion leaves queued PCM available for caller playback draining");
        }
        foreach (var status in new[] { "failed", "cancelled", "incomplete", "missing-audio", "odd-pcm" })
        {
            using var socket = new StreamingSpeechRegressionTests.FixtureSocket(); var failures = 0;
            await using var session = new OpenAiRealtimeSpeechSession(socket, config, _ => { }, default, () => Interlocked.Increment(ref failures));
            var start = session.StartAsync(default); socket.Event(new() { ["type"] = "session.updated", ["session"] = config.DeepClone() }); await start;
            await session.AppendAsync("Hello", default); var finish = session.FinishAsync(default); socket.Event(Created(Requests(socket).Single(), "one"));
            if (status != "missing-audio") socket.Event(Audio("one", status == "odd-pcm" ? "AQ==" : "AQI="));
            socket.Event(Done("one", status is "odd-pcm" or "missing-audio" ? "completed" : status));
            try { await finish; check(false, "OpenAI speech: invalid completion must fail"); }
            catch (InvalidOperationException error) { check(failures == 1 && !error.Message.Contains("private"), "OpenAI speech: " + status + " cannot masquerade as success and clears playback"); }
        }
        using (var socket = new StreamingSpeechRegressionTests.FixtureSocket())
        using (var ct = new CancellationTokenSource())
        {
            var audio = 0;
            await using var session = new OpenAiRealtimeSpeechSession(socket, config, _ => Interlocked.Increment(ref audio), ct.Token);
            var start = session.StartAsync(default); socket.Event(new() { ["type"] = "session.updated", ["session"] = config.DeepClone() }); await start;
            await session.AppendAsync("Hello. Next.", ct.Token); socket.Event(Created(Requests(socket).Single(), "one"));
            var finish = session.FinishAsync(ct.Token); ct.Cancel();
            try { await finish; check(false, "OpenAI speech: cancelled completion must not succeed"); }
            catch (OperationCanceledException) { check(socket.State == WebSocketState.Aborted && Requests(socket).Length == 1 && audio == 0, "OpenAI speech: barge-in aborts the block-owned socket and discards pending audio/text"); }
        }
        using (var socket = new StreamingSpeechRegressionTests.FixtureSocket())
        {
            await using var session = new OpenAiRealtimeSpeechSession(socket, config, _ => { }, default);
            var start = session.StartAsync(default); var bad = (JsonObject)config.DeepClone(); bad["audio"]!["output"]!["format"]!["rate"] = 48000;
            socket.Event(new() { ["type"] = "session.updated", ["session"] = bad });
            try { await start; check(false, "OpenAI speech: invalid rate must fail"); }
            catch (InvalidOperationException) { check(Requests(socket).Length == 0, "OpenAI speech: incompatible negotiated PCM fails during startup"); }
        }
        try { await OpenAiRealtimeSpeechSession.OpenAsync(config, "sk_permanent", _ => { }, default); check(false, "OpenAI speech: permanent credentials must fail"); }
        catch (InvalidOperationException) { check(true, "OpenAI speech: permanent provider keys are rejected before opening a socket"); }
        var errors = 0;
        await using (var observer = new StreamingAgentSpeech((_, _) => throw new IOException("fixture"), (_, _) => throw new Exception("HTTP fallback"), _ => errors++, default, allowLegacyFallback: false))
        {
            foreach (var index in new[] { 0, 1 })
            {
                observer.Observe(index, new("text-delta", JsonSerializer.SerializeToElement(new { id = "one", delta = "Hello" }), null)); observer.CompleteResponse(index);
            }
            await observer.DrainAsync(); check(errors == 1, "OpenAI speech: Realtime failures report once without HTTP fallback or poisoning agent output");
        }
    }
}
