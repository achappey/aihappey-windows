using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Desktop.Core;
using AIHappey.Vercel.Models;

internal static class ChatEndpointRegressionTests
{
    internal static readonly string[] Responses = [
        """{"type":"response.output_item.added","output_index":0,"item":{"id":"msg_1","type":"message","role":"assistant","content":[],"phase":"final_answer"}}""",
        """{"type":"response.output_text.delta","item_id":"msg_1","output_index":0,"content_index":0,"delta":"Hello"}""",
        """{"type":"response.output_text.delta","item_id":"msg_1","output_index":0,"content_index":0,"delta":" world"}""",
        """{"type":"response.output_text.done","item_id":"msg_1","output_index":0,"content_index":0,"text":"Hello world"}""",
        """{"type":"response.completed","response":{"id":"resp_1","object":"response","status":"completed","model":"openai/test","output":[{"id":"msg_1","type":"message","role":"assistant","phase":"final_answer","content":[{"type":"output_text","text":"Hello world","annotations":[]}]}],"usage":{"input_tokens":4,"output_tokens":2,"total_tokens":6}}}"""
    ];
    internal static readonly string[] Messages = [
        """{"type":"message_start","message":{"id":"msg_1","type":"message","role":"assistant","content":[],"model":"anthropic/test","stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":4,"output_tokens":0}}}""",
        """{"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":""}}""",
        """{"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"Think"}}""",
        """{"type":"content_block_delta","index":0,"delta":{"type":"signature_delta","signature":"opaque-signature"}}""",
        """{"type":"content_block_stop","index":0}""",
        """{"type":"content_block_start","index":1,"content_block":{"type":"text","text":""}}""",
        """{"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"Hello world"}}""",
        """{"type":"content_block_stop","index":1}""",
        """{"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":2}}""",
        """{"type":"message_stop"}"""
    ];
    internal static readonly string[] Completions = [
        """{"id":"chat_1","object":"chat.completion.chunk","model":"openai/test","choices":[{"index":0,"delta":{"role":"assistant","content":"Hello"},"finish_reason":null}]}""",
        """{"id":"chat_1","object":"chat.completion.chunk","model":"openai/test","choices":[{"index":0,"delta":{"content":" world"},"finish_reason":null}]}""",
        """{"id":"chat_1","object":"chat.completion.chunk","model":"openai/test","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}""",
        """{"id":"chat_1","object":"chat.completion.chunk","model":"openai/test","choices":[],"usage":{"prompt_tokens":4,"completion_tokens":2,"total_tokens":6}}"""
    ];

    public static async Task RunAsync(Action<bool, string> check, string root)
    {
        void Reject(Action action, string label)
        {
            try { action(); } catch (InvalidOperationException) { check(true, label); return; }
            catch (GatewayException) { check(true, label); return; }
            throw new Exception("Expected rejection: " + label);
        }
        var preferences = JsonSerializer.Deserialize<ChatPreferences>("{}")!;
        check(preferences.EndpointMode == "gateway" && preferences.Endpoint == "/api/chat", "old settings retain chat gateway default");
        preferences.EndpointMode = "direct"; preferences.Endpoint = "/v1/messages";
        check(preferences.Clone().EndpointMode == "direct" && preferences.InferenceDefaults().Endpoint == "/v1/messages", "cloning and inference reset retain route preferences");
        preferences.Endpoint = "https://evil.example";
        check(preferences.Endpoint == "/api/chat", "unknown endpoint settings cannot become arbitrary destinations");
        var settings = new DesktopSettings();
        var host = new Host();
        var session = new DesktopSession(host, new Runtime(), settings);
        var partition = session.HistoryPartition;
        settings.Chat = preferences;
        check(session.HistoryPartition == partition, "endpoint modes never partition conversation history");
        Directory.CreateDirectory(root);
        await SettingsStore.SaveAsync(root, settings);
        var loaded = await SettingsStore.LoadAsync(root, new());
        check(loaded.Chat.EndpointMode == "direct", "route selection persists in ordinary settings storage");
        var selected = new ChatTarget("openai/org/model", "Model") { ProviderKey = "openai", ProviderModelId = "org/model" };
        preferences.Endpoint = "/v1/responses";
        var direct = DesktopChatRoutes.Resolve(host, preferences, selected);
        check(direct.IsDirect && direct.Model == "org/model" && direct.DirectUrl == new Uri("https://api.openai.com/v1/responses"), "direct route uses catalog URL and provider-local model ID");
        Reject(() => DesktopChatRoutes.Resolve(new TestHost(), preferences, selected), "non-header hosts reject direct routes");
        host.HasKey = false;
        Reject(() => DesktopChatRoutes.Resolve(host, preferences, selected), "missing direct keys fail closed before inference");
        host.HasKey = true;
        Reject(() => DesktopChatRoutes.DirectUrl("openai", "/v1/messages"), "unsupported provider protocol cannot be selected");

        var credentials = new Dictionary<string, string> { ["X-OpenAI-Key"] = "selected-secret", ["X-OpenAIHK-Key"] = "other-secret", ["X-Anthropic-Key"] = "anthropic-secret", ["X-Gateway-Secret"] = "gateway-secret" };
        using (var request = new HttpRequestMessage(HttpMethod.Post, direct.DirectUrl))
        {
            DesktopDirectChatAuthentication.Apply(request, credentials, "openai", direct.Endpoint);
            check(request.Headers.Authorization?.Parameter == "selected-secret" && request.Headers.Count() == 1, "direct auth selects exactly one provider credential");
        }
        check(DesktopDirectChatAuthentication.Credential(new Dictionary<string, string> { ["X-OpenAIHK-Key"] = "other" }, "openai") is null, "similar provider names do not share credentials");
        using (var request = new HttpRequestMessage(HttpMethod.Post, DesktopChatRoutes.DirectUrl("anthropic", "/v1/messages")))
        {
            DesktopDirectChatAuthentication.Apply(request, credentials, "anthropic", "/v1/messages");
            check(request.Headers.GetValues("x-api-key").Single() == "anthropic-secret" && request.Headers.Contains("anthropic-version") && request.Headers.Authorization is null, "Messages direct auth uses native protocol headers");
        }
        using (var request = new HttpRequestMessage(HttpMethod.Post, "https://evil.example/v1/responses"))
            Reject(() => DesktopDirectChatAuthentication.Apply(request, credentials, "openai", direct.Endpoint), "direct auth refuses a destination not owned by the selected route");

        var user = Message(Role.user, "Hello?");
        var system = Message(Role.system, "System context");
        var conversation = new Conversation { Service = ServiceKind.Ai, Target = "openai/test" };
        conversation.Messages.Add(new() { Message = user });
        foreach (var (endpoint, provider, packets) in new[] { ("/v1/responses", "openai", Responses), ("/v1/messages", "anthropic", Messages), ("/v1/chat/completions", "openai", Completions) })
        {
            preferences = new() { Endpoint = endpoint, MaxOutputTokens = 123 };
            var route = new DesktopChatRoute(endpoint, provider + "/test", provider);
            var output = new ConversationMessage { Status = "streaming", Message = new() { Id = Guid.NewGuid().ToString(), Role = Role.assistant, Parts = [] } };
            var assembler = new MessageAssembler(output);
            var mapper = new NativeChatStream(route);
            foreach (var packet in packets) foreach (var item in mapper.Apply(packet)) assembler.Apply(item);
            mapper.CheckComplete();
            check(output.Status == "complete" && output.Text == "Hello world", endpoint + " maps streaming text without duplicating final snapshots");
            check(output.Message.Parts.First().Type == "step-start" && output.Message.Parts.All(p => !p.Type.StartsWith("response.") && !p.Type.StartsWith("content_block")), endpoint + " stores only ordered portable UI parts");
            check(MessageUsage.Read(output.Message.Metadata!, route.Model, output.Timestamp).Tokens == "6", endpoint + " maps usage, including usage arriving after finish");
            conversation.Messages.Add(output);
            var body = NativeChatRequests.Build(route, preferences, new[] { system }.Concat(conversation.Messages.Select(m => m.Message)).ToList());
            var wire = body.ToJsonString();
            check(body["stream"]!.GetValue<bool>() && body["model"]!.ToString() == route.Model && !wire.Contains("providerMetadata"), endpoint + " builds a native request rather than a gateway chat envelope");
            check(wire.Contains("System context") && wire.Contains("Hello world"), endpoint + " replays the same conversation across protocols");
            var tokenField = endpoint == "/v1/messages" ? "max_tokens" : endpoint == "/v1/responses" ? "max_output_tokens" : "max_tokens";
            check(body[tokenField]!.GetValue<int>() == 123, endpoint + " uses native token limit field");
            if (endpoint == "/v1/messages")
                check(wire.Contains("opaque-signature") && output.Message.Parts.Any(p => p.Type == "reasoning"), "thinking signatures survive portable storage and Messages replay");
            var incomplete = new NativeChatStream(route);
            Reject(incomplete.CheckComplete, endpoint + " never treats bare EOF as successful completion");
            Reject(() => mapper.Apply("{\"type\":\"error\",\"error\":{\"message\":\"No access\"}}"), endpoint + " late errors are not silently ignored");
        }
        var portable = JsonSerializer.Serialize(conversation, PortableConversations.Json);
        var restored = JsonSerializer.Deserialize<Conversation>(portable, PortableConversations.Json)!;
        check(restored.Messages.Count == 4 && restored.Messages.Skip(1).All(m => m.Text == "Hello world"), "mixed-endpoint conversation survives portable export/import");
        var store = new HistoryStore(Path.Combine(root, "history"));
        await store.SaveAsync(partition, restored);
        check((await store.GetAsync(partition, restored.Id))!.Messages.Count == 4, "mixed-endpoint conversation survives history reload");

        var reasoningPackets = new[] {
            """{"type":"response.reasoning_summary_text.delta","item_id":"rs_1","summary_index":0,"delta":"Thinking"}""",
            """{"type":"response.output_item.done","output_index":0,"item":{"id":"rs_1","type":"reasoning","summary":[{"type":"summary_text","text":"Thinking"}],"encrypted_content":"opaque-encrypted"}}""",
            """{"type":"response.completed","response":{"id":"r","object":"response","status":"completed","output":[]}}""" };
        var reasoningOutput = new ConversationMessage { Message = new() { Id = "reasoning", Role = Role.assistant, Parts = [] } };
        var reasoningMapper = new NativeChatStream(new("/v1/responses", "openai/test", "openai"));
        var reasoningAssembler = new MessageAssembler(reasoningOutput);
        foreach (var packet in reasoningPackets) foreach (var item in reasoningMapper.Apply(packet)) reasoningAssembler.Apply(item);
        var reasoningBody = NativeChatRequests.Build(new("/v1/responses", "openai/test", "openai"), new(), [reasoningOutput.Message]);
        check(reasoningBody.ToJsonString().Contains("opaque-encrypted") && reasoningOutput.Reasoning == "Thinking", "Responses encrypted reasoning remains replayable under provider metadata");
        Reject(() => new NativeChatStream(new("/v1/messages", "anthropic/test", "anthropic")).Apply("""{"type":"content_block_start","index":0,"content_block":{"type":"unknown_executable","id":"t","name":"shell","input":{}}}"""), "unknown native blocks fail closed rather than becoming executable calls");
        var unsupported = new UIMessage { Id = "file", Role = Role.user, Parts = [PortableConversations.Part(new JsonObject { ["type"] = "file", ["mediaType"] = "audio/wav", ["url"] = "data:audio/wav;base64,AA==" })] };
        Reject(() => NativeChatRequests.Build(new("/v1/messages", "anthropic/test", "anthropic"), new(), [unsupported]), "unsupported attachments fail projection clearly");

        var image = new UIMessage { Id = "image", Role = Role.user, Parts = [PortableConversations.Part(new JsonObject { ["type"] = "file", ["mediaType"] = "image/png", ["url"] = "data:image/png;base64,AA==" })] };
        foreach (var endpoint in DesktopChatRoutes.GatewayEndpoints.Skip(1))
            check(NativeChatRequests.Build(new(endpoint, "provider/test", "provider"), new(), [image]).ToJsonString().Contains("AA=="), endpoint + " projects portable inline images using the native contract");
        var redacted = new NativeChatStream(new("/v1/messages", "anthropic/test", "anthropic"));
        var redactedOutput = new ConversationMessage { Message = new() { Id = "redacted", Role = Role.assistant, Parts = [] } };
        var redactedAssembler = new MessageAssembler(redactedOutput);
        foreach (var packet in new[] { """{"type":"content_block_start","index":0,"content_block":{"type":"redacted_thinking","data":"redacted-opaque"}}""", """{"type":"content_block_stop","index":0}""", """{"type":"message_stop"}""" })
            foreach (var item in redacted.Apply(packet)) redactedAssembler.Apply(item);
        check(NativeChatRequests.Build(new("/v1/messages", "anthropic/test", "anthropic"), new(), [redactedOutput.Message]).ToJsonString().Contains("redacted-opaque"), "redacted thinking survives empty visible reasoning and native replay");
        var sources = new NativeChatStream(new("/v1/responses", "openai/test", "openai"));
        var sourceOutput = new ConversationMessage { Message = new() { Id = "sources", Role = Role.assistant, Parts = [] } };
        var sourceAssembler = new MessageAssembler(sourceOutput);
        foreach (var packet in new[] {
            """{"type":"response.output_text.delta","item_id":"msg_1","content_index":0,"delta":"Answer"}""",
            """{"type":"response.output_text.annotation.added","item_id":"msg_1","content_index":0,"annotation_index":0,"annotation":{"type":"url_citation","url":"https://example.com/source","title":"Source"}}""",
            """{"type":"response.completed","response":{"id":"r","object":"response","status":"completed","output":[],"metadata":{"gateway":{"cost":0.25,"ttft":1.5,"tps":5}},"usage":{"input_tokens":4,"output_tokens":2,"total_tokens":6}}}""" })
            foreach (var item in sources.Apply(packet)) sourceAssembler.Apply(item);
        check(sourceOutput.Message.Parts.Any(part => part.Type == "source-url") && MessageUsage.Read(sourceOutput.Message.Metadata!, "openai/test", sourceOutput.Timestamp).Price is not null,
            "native sources and gateway costs use the same portable parts and footer metadata as browser chat");
        Reject(() => new NativeChatStream(new("/v1/responses", "openai/test", "openai")).Apply("""{"type":"response.incomplete","response":{"id":"r","object":"response","status":"incomplete","output":[]}}"""), "Responses incomplete event is not marked successful");
        Reject(() => new NativeChatStream(new("/v1/messages", "anthropic/test", "anthropic")).Apply("""{"type":"error","error":"native string error"}"""), "string-shaped native errors use bounded gateway error handling");

        await WireChecksAsync(check, session, direct, user);
        using var frames = new MemoryStream(Encoding.UTF8.GetBytes("event: ping\r\n\r\nevent: content_block_delta\r\ndata: {\"index\":0}\r\n\r\n"));
        var framed = new List<SseReader.Frame>();
        await foreach (var frame in SseReader.ReadEventsAsync(frames)) framed.Add(frame);
        check(framed.Count == 1 && framed[0].Event == "content_block_delta", "SSE event names are preserved without leaking from empty ping frames");
        await NativeChatToolRegressionTests.RunAsync(check, root);
    }

    private static async Task WireChecksAsync(Action<bool, string> check, DesktopSession session, DesktopChatRoute direct, UIMessage user)
    {
        using var gatewayHandler = new Handler(); using var directHandler = new Handler();
        using var gatewayHttp = new HttpClient(gatewayHandler); using var directHttp = new HttpClient(directHandler);
        var client = new DesktopChatClient(session, gatewayHttp, directHttp);
        foreach (var endpoint in DesktopChatRoutes.GatewayEndpoints)
        {
            gatewayHandler.Endpoint = endpoint;
            await foreach (var _ in client.StreamAsync(ServiceKind.Ai, "openai/test", "same-conversation", [user], default, new() { Endpoint = endpoint })) { }
            check(gatewayHandler.Url!.AbsolutePath == "/prefix" + endpoint && gatewayHandler.Headers!.ContainsKey("X-Gateway-Secret"), endpoint + " gateway dispatch preserves configured URL base path and host auth");
        }
        await foreach (var _ in client.StreamAsync(ServiceKind.Ai, "openai/test", "same-conversation", [user], default, new() { EndpointMode = "direct", Endpoint = direct.Endpoint }, route: direct)) { }
        check(directHandler.Url == direct.DirectUrl && !directHandler.Headers!.ContainsKey("X-Gateway-Secret")
            && directHandler.Body!["model"]!.ToString() == "org/model", "direct transport bypasses gateway auth and preserves pinned provider model");
        foreach (var enabled in new[] { true, false })
        {
            var preferences = new ChatPreferences();
            preferences.ProviderMetadata["openai"] = new JsonObject { ["multi_agent"] = new JsonObject { ["enabled"] = enabled }, ["inputImageDetail"] = "high" };
            // Deliberately stale/missing saved beta: the outgoing request must repair it.
            preferences.ProviderHeaders["openai"] = new() { ["openai-beta"] = enabled ? "custom=v1" : "custom=v1," + OpenAIChatConfig.MultiAgentBeta };
            foreach (var endpoint in new[] { "/api/chat", "/v1/responses" })
            {
                preferences.Endpoint = gatewayHandler.Endpoint = endpoint;
                await foreach (var _ in client.StreamAsync(ServiceKind.Ai, "openai/test", "same", [user], default, preferences)) { }
                var expected = enabled ? "custom=v1," + OpenAIChatConfig.MultiAgentBeta : "custom=v1";
                check(gatewayHandler.Headers!["OpenAI-Beta"] == expected && !gatewayHandler.Body!.ToJsonString().Contains("inputImageDetail"),
                    endpoint + " transport reconciles standalone multi-agent beta and omits obsolete image detail / " + enabled);
            }
            preferences.EndpointMode = "direct"; preferences.Endpoint = direct.Endpoint;
            await foreach (var _ in client.StreamAsync(ServiceKind.Ai, "openai/test", "same", [user], default, preferences, route: direct)) { }
            check(directHandler.Headers!["OpenAI-Beta"] == (enabled ? "custom=v1," + OpenAIChatConfig.MultiAgentBeta : "custom=v1")
                && !directHandler.Headers.ContainsKey("X-Gateway-Secret"), "direct OpenAI transport reconciles beta without leaking gateway auth / " + enabled);
        }
        gatewayHandler.Endpoint = "/v1/responses";
        await foreach (var _ in client.StreamAsync(ServiceKind.Agents, "agent", "same", [user], default, new() { Endpoint = "/v1/messages", EndpointMode = "direct" })) { }
        check(gatewayHandler.Url!.AbsolutePath == "/prefix/api/chat", "Agents ignore model-chat endpoint preferences");
        directHandler.Status = HttpStatusCode.TemporaryRedirect;
        var before = directHandler.Calls;
        try { await foreach (var _ in client.StreamAsync(ServiceKind.Ai, "openai/test", "same", [user], default, route: direct)) { } throw new Exception("Expected redirect refusal"); }
        catch (GatewayException) { check(directHandler.Calls == before + 1, "direct redirect errors do not cause inference retry"); }
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        try { await foreach (var _ in client.StreamAsync(ServiceKind.Ai, "openai/test", "same", [user], canceled.Token, route: direct)) { } throw new Exception("Expected cancellation"); }
        catch (OperationCanceledException) { check(true, "native stream respects cancellation"); }
    }

    private static UIMessage Message(Role role, string text) => new() { Id = Guid.NewGuid().ToString(), Role = role,
        Parts = [PortableConversations.Part(new JsonObject { ["type"] = "text", ["text"] = text })] };
    private sealed class Host : IDesktopHost
    {
        public bool HasKey = true;
        public string ProfileId => "endpoint-fixture";
        public bool AllowLocal => true;
        public string AccountLabel => "fixture";
        public string HistoryIdentity => "fixture";
        public bool SupportsDirectChat => true;
        public bool HasDirectChatCredential(string provider) => HasKey;
        public Task AuthenticateDirectChatAsync(HttpRequestMessage request, string provider, string endpoint, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); DesktopDirectChatAuthentication.Apply(request, new Dictionary<string, string> { ["X-OpenAI-Key"] = "selected" }, provider, endpoint); return Task.CompletedTask; }
        public Task AuthenticateAsync(HttpRequestMessage request, ServiceKind service, CancellationToken ct)
        { request.Headers.Add("X-Gateway-Secret", "gateway-secret"); return Task.CompletedTask; }
        public Task ManageAccountAsync(object root, CancellationToken ct) => Task.CompletedTask;
    }
    private sealed class Runtime : IRuntimeResolver
    {
        public Task<Uri> ResolveAsync(ServiceKind service, DesktopSettings settings, CancellationToken ct) => Task.FromResult(new Uri("https://gateway.example/prefix/"));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Handler : HttpMessageHandler
    {
        public string Endpoint = "/v1/responses";
        public Uri? Url;
        public Dictionary<string, string>? Headers;
        public JsonObject? Body;
        public int Calls;
        public HttpStatusCode Status = HttpStatusCode.OK;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Calls++; Url = request.RequestUri;
            Headers = request.Headers.ToDictionary(p => p.Key, p => string.Join(",", p.Value), StringComparer.OrdinalIgnoreCase);
            Body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject();
            var packets = request.RequestUri!.AbsolutePath.EndsWith("/api/chat") ? new[] { """{"type":"finish","finishReason":"stop"}""" }
                : Endpoint == "/v1/messages" ? Messages : Endpoint == "/v1/chat/completions" ? Completions : Responses;
            return new HttpResponseMessage(Status) { Content = new StringContent(string.Join("", packets.Select(p => "data: " + p + "\n\n")) + "data: [DONE]\n\n", Encoding.UTF8, "text/event-stream") };
        }
    }
}
