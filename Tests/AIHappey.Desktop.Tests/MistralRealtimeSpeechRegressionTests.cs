using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using AIHappey.Desktop.Core;

internal static class MistralRealtimeSpeechRegressionTests
{
    private const string Model = "mistral/voxtral-mini-tts-3-0";
    private static byte[] Wave(byte[]? samples = null, int rate = 24000)
    {
        samples ??= [0, 128, 255, 127];
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + samples.Length); writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(rate); writer.Write(rate * 2);
        writer.Write((short)2); writer.Write((short)16); writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(samples.Length); writer.Write(samples);
        return stream.ToArray();
    }
    private static JsonObject Delta(byte[] bytes) => new() { ["type"] = "speech.audio.delta", ["audio_data"] = Convert.ToBase64String(bytes) };
    private static JsonObject Done() => new() { ["type"] = "speech.audio.done" };
    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }
    private static async Task<bool> Rejects(Func<Task> action, string? safe = null)
    {
        try { await action(); return false; }
        catch (TimeoutException) { throw; } // A stuck session is not a successful rejection.
        catch (Exception error) { return safe is null || !error.ToString().Contains(safe); }
    }
    private static async Task Configure(StreamingSpeechRegressionTests.FixtureSocket socket, MistralRealtimeSpeechSession session, JsonObject config)
    {
        socket.Event(new() { ["type"] = "session.created" });
        await Until(() => !socket.Sent.IsEmpty);
        socket.Event(new() { ["type"] = "session.updated", ["session"] = config.DeepClone() });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)); await session.WaitReadyAsync(timeout.Token);
    }
    public static async Task RunAsync(Action<bool, string> check)
    {
        var config = MistralRealtimeSpeech.Session(Model, new());
        check(config["voice"]!.ToString() == "voice_447" && config["response_format"]!.ToString() == "wav"
            && config["streaming_mode"]!.ToString() == "responsive", "Mistral speech: preset default, responsive streaming and WAV metadata are explicit");
        check(StreamingSpeechProtocol.Supports(new(Model, "Mistral") { ModelType = "speech", Tags = ["real-time", "realtime-token"] })
            && !StreamingSpeechProtocol.Supports(new(Model + "/sarah", "Shortcut") { ModelType = "speech", Tags = ["real-time", "realtime-token"] })
            && !StreamingSpeechProtocol.Supports(new(Model, "Untagged") { ModelType = "speech" }), "Mistral speech: exact tested model and catalog capabilities gate activation");
        foreach (var invalid in new JsonObject[] { new() { ["voice"] = "sarah" }, new() { ["voice"] = "voice_1", ["ref_audio"] = "AQI=" },
            new() { ["streaming_mode"] = "fast" }, new() { ["ref_audio"] = "invalid" }, new() { ["sampleRate"] = 44100 } })
            check(await Rejects(() => { MistralRealtimeSpeech.Session(Model, invalid); return Task.CompletedTask; }), "Mistral speech: unsupported or conflicting settings fail before connection");
        var reference = MistralRealtimeSpeech.Session(Model, new() { ["ref_audio"] = "AQI=", ["streaming_mode"] = "long_form", ["api_key"] = "secret" });
        check(reference["voice"] is null && reference["ref_audio"]!.ToString() == "AQI=" && reference["api_key"] is null, "Mistral speech: reference audio excludes default voice; credential settings are not forwarded");
        var decoded = new List<byte>();
        var reader = new StreamingWaveDecoder((pcm, rate) => { decoded.AddRange(pcm); check(rate == 44100, "Mistral speech: WAV establishes actual playback sample rate"); });
        foreach (var value in Wave([1, 2, 3, 4], 44100)) reader.Push([value]);
        reader.Finish(); check(decoded.SequenceEqual(new byte[] { 1, 2, 3, 4 }), "Mistral speech: headers and samples can span every WebSocket delta boundary");
        var truncated = new StreamingWaveDecoder((_, _) => { }); truncated.Push(Wave()[..45]);
        check(await Rejects(() => { truncated.Finish(); return Task.CompletedTask; }), "Mistral speech: incomplete WAV samples are rejected");
        using (var socket = new StreamingSpeechRegressionTests.FixtureSocket())
        {
            var audio = new ConcurrentQueue<byte[]>();
            await using var session = new MistralRealtimeSpeechSession(socket, config, (pcm, _) => audio.Enqueue(pcm), default); session.Start();
            var pending = session.AppendAsync(" Hello ", default);
            await Task.Delay(30); check(socket.Sent.IsEmpty && !pending.IsCompleted, "Mistral speech: no input before acknowledged setup");
            await Configure(socket, session, config); await pending; await session.AppendAsync("world.\n", default);
            check(socket.Sent.Where(value => value["type"]?.ToString() == "input_text.append").Select(value => value["text"]!.ToString()).SequenceEqual([" Hello ", "world.\n"]), "Mistral speech: whitespace and deltas are preserved verbatim");
            socket.Event(Delta(Wave()[..45])); await Until(() => audio.Count == 1);
            await session.FlushAsync(default); socket.Event(Delta(Wave()[45..])); socket.Event(Done());
            await session.AppendAsync(" Again.", default); var finish = session.FinishAsync(default);
            socket.Event(Delta(Wave([1, 2]))); socket.Event(Done()); await Until(() => audio.Count == 3);
            await finish.WaitAsync(TimeSpan.FromSeconds(5));
            check(finish.IsCompletedSuccessfully, "Mistral speech: validated final audio completes without waiting for server closure");
            socket.ServerClose(); await finish.WaitAsync(TimeSpan.FromSeconds(5));
            check(audio.SelectMany(bytes => bytes).SequenceEqual(new byte[] { 0, 128, 255, 127, 1, 2 }), "Mistral speech: multiple WAV generations preserve ordered audio without replaying headers");
            check(socket.Sent.Last()["type"]!.ToString() == "input_text.end" && socket.Sent.Last()["text"] is null, "Mistral speech: final end carries no text");
        }
        foreach (var status in new WebSocketCloseStatus?[] { null, WebSocketCloseStatus.NormalClosure, WebSocketCloseStatus.Empty, WebSocketCloseStatus.InternalServerError })
        {
            using var socket = new StreamingSpeechRegressionTests.FixtureSocket();
            var audio = new ConcurrentQueue<byte[]>(); var failures = 0;
            await using var session = new MistralRealtimeSpeechSession(socket, config, (pcm, _) => audio.Enqueue(pcm), default, () => Interlocked.Increment(ref failures)); session.Start();
            await Configure(socket, session, config); await session.AppendAsync("Complete reply.", default); var finish = session.FinishAsync(default);
            check(ReferenceEquals(finish, session.FinishAsync(default)), "Mistral speech: repeated finish shares one completion task");
            socket.Event(Delta(Wave())); socket.Event(Done()); if (status is { } code) socket.ServerClose(code);
            await finish.WaitAsync(TimeSpan.FromSeconds(5));
            socket.Abort(); await Task.Delay(30); await session.DisposeAsync(); await session.DisposeAsync();
            check(failures == 0 && audio.SelectMany(bytes => bytes).SequenceEqual(new byte[] { 0, 128, 255, 127 }),
                "Mistral speech: final audio survives missing/abnormal close or receive failure and disposal does not stop playback as a failure");
        }
        foreach (var empty in new[] { false, true })
        {
            using var socket = new StreamingSpeechRegressionTests.FixtureSocket();
            var audio = new ConcurrentQueue<byte[]>();
            await using var session = new MistralRealtimeSpeechSession(socket, config, (pcm, _) => audio.Enqueue(pcm), default); session.Start();
            await Configure(socket, session, config);
            if (!empty)
            {
                await session.AppendAsync("Already flushed.", default); await session.FlushAsync(default);
                socket.Event(Delta(Wave())); socket.Event(Done());
                await Until(() => !audio.IsEmpty); await Task.Delay(30);
            }
            await session.FinishAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
            check(socket.Sent.Count(value => value["type"]?.ToString() == "input_text.end") == 1,
                "Mistral speech: empty or fully flushed input ends without a phantom generation or a required server close");
        }
        using (var socket = new StreamingSpeechRegressionTests.FixtureSocket())
        {
            var audio = new ConcurrentQueue<byte[]>(); var failures = 0;
            await using var session = new MistralRealtimeSpeechSession(socket, config, (pcm, _) => audio.Enqueue(pcm), default, () => Interlocked.Increment(ref failures)); session.Start();
            await Configure(socket, session, config); await session.AppendAsync("First.", default); await session.FlushAsync(default);
            socket.Event(Delta(Wave())); socket.Event(Done()); await Until(() => !audio.IsEmpty);
            await session.AppendAsync("Second.", default); var finish = session.FinishAsync(default);
            socket.ServerClose(WebSocketCloseStatus.InternalServerError);
            check(await Rejects(() => finish.WaitAsync(TimeSpan.FromSeconds(5))), "Mistral speech: earlier flush completion cannot acknowledge missing later input on abnormal close");
            await Until(() => Volatile.Read(ref failures) == 1);
        }
        foreach (var bytes in new byte[]?[] { null, Wave()[..45] })
        {
            using var socket = new StreamingSpeechRegressionTests.FixtureSocket();
            await using var session = new MistralRealtimeSpeechSession(socket, config, (_, _) => { }, default); session.Start();
            await Configure(socket, session, config); await session.AppendAsync("Pending.", default); var finish = session.FinishAsync(default);
            if (bytes is not null) socket.Event(Delta(bytes)); socket.Event(Done());
            check(await Rejects(() => finish.WaitAsync(TimeSpan.FromSeconds(5))), "Mistral speech: final done cannot acknowledge missing or truncated WAV audio");
        }
        using (var socket = new StreamingSpeechRegressionTests.FixtureSocket())
        {
            await using var session = new MistralRealtimeSpeechSession(socket, config, (_, _) => { }, default); session.Start();
            await Configure(socket, session, config); await session.AppendAsync("Pending.", default); var finish = session.FinishAsync(default);
            socket.ServerClose(); check(await Rejects(() => finish.WaitAsync(TimeSpan.FromSeconds(5))), "Mistral speech: premature normal close cannot acknowledge outstanding generations");
        }
        using (var socket = new StreamingSpeechRegressionTests.FixtureSocket())
        {
            await using var session = new MistralRealtimeSpeechSession(socket, config, (_, _) => { }, default); session.Start();
            socket.Event(new() { ["type"] = "session.created" }); await Until(() => !socket.Sent.IsEmpty);
            socket.Event(new() { ["type"] = "session.updated", ["session"] = new JsonObject { ["response_format"] = "mp3" } });
            check(await Rejects(() => session.WaitReadyAsync(default).WaitAsync(TimeSpan.FromSeconds(5))), "Mistral speech: incompatible applied settings reject setup");
        }
        using (var socket = new StreamingSpeechRegressionTests.FixtureSocket())
        using (var cancellation = new CancellationTokenSource())
        {
            var count = 0;
            await using var session = new MistralRealtimeSpeechSession(socket, config, (_, _) => count++, cancellation.Token); session.Start();
            await Configure(socket, session, config); await session.AppendAsync("Hello.", default); var finish = session.FinishAsync(default);
            cancellation.Cancel();
            check(await Rejects(() => finish.WaitAsync(TimeSpan.FromSeconds(5))) && count == 0
                && !socket.Sent.Any(value => value["type"]?.ToString() == "cancel"), "Mistral speech: cancellation rejects pending completion, stops audio, and sends no undocumented cancel message");
        }
        using (var socket = new StreamingSpeechRegressionTests.FixtureSocket())
        {
            await using var session = new MistralRealtimeSpeechSession(socket, config, (_, _) => { }, default); session.Start();
            socket.Event(new() { ["type"] = "error", ["error"] = new JsonObject { ["message"] = "rt_super_secret" } });
            check(await Rejects(() => session.WaitReadyAsync(default).WaitAsync(TimeSpan.FromSeconds(5)), "rt_super_secret"), "Mistral speech: provider errors never disclose credentials or raw event bodies");
        }
    }
}
