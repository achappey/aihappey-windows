using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Desktop.Core;

internal static class RealtimeRegressionTests
{
    public static async Task RunAsync(Action<bool, string> check, string root)
    {
        var conversation = new Conversation(); var transcript = new RealtimeTranscript(conversation, "google/gemini-live");
        void Event(string json) => transcript.Apply(JsonNode.Parse(json)!.AsObject());
        Event("""{"type":"conversation.item.input_audio_transcription.delta","item_id":"a","delta":"Hello"}""");
        Event("""{"type":"conversation.item.input_audio_transcription.completed","item_id":"a","transcript":"Hello there"}""");
        Event("""{"type":"conversation.item.input_audio_transcription.completed","item_id":"a","transcript":"Hello there"}""");
        check(conversation.Messages.Count == 1 && conversation.Messages[0].Text == "Hello there", "realtime transcript: completed user transcription replaces draft without duplication");
        Event("""{"type":"response.output_audio_transcript.delta","response_id":"r","delta":"Hi"}""");
        Event("""{"type":"response.output_audio_transcript.delta","response_id":"r","delta":"!"}""");
        Event("""{"type":"response.usage","response_id":"r","usage":{"promptTokenCount":10,"responseTokenCount":3,"totalTokenCount":13}}""");
        const string done = """{"type":"response.done","response":{"id":"r","status":"completed","output":[{"type":"message","content":[{"type":"audio","transcript":"Hi!"}]}]}}""";
        Event(done); Event(done);
        check(conversation.Messages.Count == 2 && conversation.Messages[1].Text == "Hi!" && conversation.Messages[1].Status == "complete", "realtime transcript: final assistant response is persisted exactly once");
        var usage = JsonSerializer.SerializeToElement(conversation.Messages[1].Message.Metadata!["usage"]);
        check(usage.GetProperty("totalTokens").GetInt32() == 13, "realtime transcript: Google usage normalizes to desktop token metadata");
        Event("""{"type":"response.output_text.delta","response_id":"partial","delta":"Unfinished"}"""); transcript.Stop();
        check(conversation.Messages.Last().Status == "interrupted", "realtime transcript: stop retains partial speech as interrupted");
        var partition = HistoryStore.Partition("realtime-tests", "account", "endpoint");
        var history = new HistoryStore(Path.Combine(root, "realtime")); await history.SaveAsync(partition, conversation);
        check((await history.ListAsync(partition)).Single().Messages.Count == 3, "realtime transcript: portable history roundtrip retains user and assistant turns");
        using var handler = new TokenHandler(); using var http = new HttpClient(handler);
        var host = new TokenHost(); var session = new DesktopSession(host, new TestRuntime(), new());
        var client = new DesktopRealtimeTokenClient(new(session, http), http);
        var preferences = new ChatPreferences { ApiDebugEnabled = true };
        var token = await client.GetAsync(new() { ["model"] = "openai/realtime" }, preferences, "openai", default);
        check(token == "fixture-ephemeral" && handler.Path == "/v1/realtime/client_secrets" && host.Authenticated, "realtime token: existing runtime resolver and host authentication are used");
        check(!handler.Debug && handler.Authorization == "Bearer fixture-host", "realtime token: debug capture is disabled while backend authentication stays scoped to backend");
        handler.Invalid = true;
        try { await client.GetAsync(new(), preferences, "openai", default); check(false, "expired token"); }
        catch (GatewayException) { check(true, "realtime token: expired credentials are rejected"); }
        check(!PortableConversations.Write(conversation).Contains("fixture-ephemeral"), "realtime credentials never enter transcript history");
        var liveHistory = new Conversation(); var live = new RealtimeTranscript(liveHistory, "openai/gpt-live-1");
        live.Apply(JsonNode.Parse("""{"type":"session.input_transcript.delta","delta":"Hello","start_ms":0,"end_ms":10}""")!.AsObject());
        live.Apply(JsonNode.Parse("""{"type":"session.output_transcript.delta","delta":"Answer","start_ms":10,"end_ms":20}""")!.AsObject());
        live.Apply(JsonNode.Parse("""{"type":"session.output_transcript.delta","delta":" final.","start_ms":20,"end_ms":30}""")!.AsObject());
        live.Apply(JsonNode.Parse("""{"type":"session.closed","reason":"close_requested","usage":{"seconds":12.5}}""")!.AsObject());
        live.Stop();
        check(liveHistory.Messages.Count == 2 && liveHistory.Messages.Last().Text == "Answer final." && liveHistory.Messages.All(message => message.Status == "complete"),
            "Live Stop: final fragments survive and confirmed transcripts are not marked interrupted");
        var livePartition = HistoryStore.Partition("live-tests", "account", "endpoint");
        await history.SaveAsync(livePartition, liveHistory);
        var savedLive = (await history.ListAsync(livePartition)).Single();
        check(savedLive.Messages.Last().Text == "Answer final." && JsonSerializer.SerializeToElement(savedLive.Metadata["liveFinalUsage"]).GetProperty("seconds").GetDouble() == 12.5,
            "Live Stop: portable UI-part history preserves final transcript and cumulative usage");
        var livePreferences = new RealtimePreferences { LiveClientTarget = "private-client-model", ProviderOptions = JsonNode.Parse("""{"openai":{"session":{"store":true,"type":"realtime","audio":{"input":{"turn_detection":{"type":"semantic_vad"}}},"delegation":{"type":"client"}}}}""")!.AsObject() };
        var startup = RealtimeConfiguration.LiveSession(livePreferences, "hello").ToJsonString();
        check(startup.Contains("\"store\":false") && !startup.Contains("private-client-model") && !startup.Contains("semantic_vad"),
            "Live provisioning: storage remains disabled and client selection/legacy VAD stay off the wire");
    }
    private sealed class TokenHost : IDesktopHost
    {
        public bool Authenticated;
        public string ProfileId => "realtime-test";
        public bool AllowLocal => true;
        public string AccountLabel => "test";
        public string HistoryIdentity => "test";
        public Task ManageAccountAsync(object root, CancellationToken ct) => Task.CompletedTask;
        public Task AuthenticateAsync(HttpRequestMessage request, ServiceKind service, CancellationToken ct)
        {
            Authenticated = true; request.Headers.Authorization = new("Bearer", "fixture-host"); request.Headers.Add("X-AIHappey-Debug", "true"); return Task.CompletedTask;
        }
    }
    private sealed class TokenHandler : HttpMessageHandler
    {
        public bool Invalid, Debug; public string? Path, Authorization;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Path = request.RequestUri!.AbsolutePath; Debug = request.Headers.Contains("X-AIHappey-Debug"); Authorization = request.Headers.Authorization?.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
            { value = "fixture-ephemeral", expires_at = Invalid ? 1 : DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeSeconds() }), Encoding.UTF8, "application/json") });
        }
    }
}
