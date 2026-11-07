using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Desktop.Core;
using AIHappey.Vercel.Models;

internal static class NativeChatToolTransportTests
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        foreach (var endpoint in NativeChatToolRegressionTests.Endpoints)
        foreach (var direct in new[] { false, true })
        {
            var provider = endpoint == "/v1/messages" ? "anthropic" : "openai";
            var settings = new DesktopSettings();
            var session = new DesktopSession(new Host(), new Runtime(), settings);
            using var gateway = new Handler(endpoint); using var native = new Handler(endpoint);
            using var gatewayHttp = new HttpClient(gateway); using var directHttp = new HttpClient(native);
            var client = new DesktopChatClient(session, gatewayHttp, directHttp);
            var route = new DesktopChatRoute(endpoint, direct ? "test" : provider + "/test", provider,
                direct ? DesktopChatRoutes.DirectUrl(provider, endpoint) : null);
            var snapshot = McpTurnSnapshot.Empty; var executed = 0;
            snapshot.AddLocal(JsonSerializer.Deserialize<JsonElement>("""{"name":"echo","inputSchema":{"type":"object","properties":{}}}"""),
                (_, _) => { executed++; return Task.FromResult(JsonSerializer.Deserialize<JsonElement>("""{"content":[{"type":"text","text":"transport-result"}]}""")); });
            var conversation = new Conversation();
            conversation.Messages.Add(new() { Message = new() { Id = "u", Role = Role.user, Parts = [PortableConversations.Part(new JsonObject { ["type"] = "text", ["text"] = "Use echo" })] } });
            var output = new ConversationMessage { Status = "streaming", Message = new() { Id = "a", Role = Role.assistant, Parts = [] } };
            conversation.Messages.Add(output);
            await DesktopChatTurn.RunAsync(conversation, output,
                (messages, ct) => client.StreamAsync(ServiceKind.Ai, provider + "/test", conversation.Id, messages, ct, new(), provider, mcp: snapshot, route: route),
                (_, _) => throw new Exception(endpoint + " must not prompt without a backend approval request"), (_, _) => Task.CompletedTask, snapshot, "en", default);
            var selected = direct ? native : gateway; var unused = direct ? gateway : native;
            check(selected.Bodies.Count == 2 && unused.Bodies.Count == 0 && executed == 1 && output.Text == "Final answer", endpoint + " " + (direct ? "direct" : "gateway") + " streams a tool turn and stateless continuation over real client transport");
            check(selected.Bodies[0]["tools"]!.ToJsonString().Contains("echo") && selected.Bodies[1].ToJsonString().Contains("transport-result"), endpoint + " transport carries captured tools and executed results");
            check(DesktopToolApprovals.Pending(output.Message).Count == 0 && output.Message.Parts.Where(PortableConversations.IsTool)
                .All(part => !PortableConversations.Element(part).TryGetProperty("approval", out _)), endpoint + " " + (direct ? "direct" : "gateway") + " has no fabricated approval metadata");
            check(selected.Headers.All(h => h.ContainsKey("X-Gateway-Secret") == !direct) && selected.Bodies.All(b => b["model"]!.ToString() == route.Model), endpoint + " continuation keeps credentials and canonical/local model routing isolated");
        }
        await HostedMatrixAsync(check);
    }

    private static ConversationMessage Reduce(string endpoint, string provider, IEnumerable<string> packets)
    {
        var output = new ConversationMessage { Message = new() { Id = Guid.NewGuid().ToString(), Role = Role.assistant, Parts = [] } };
        var assembler = new MessageAssembler(output); var mapper = new NativeChatStream(new(endpoint, provider + "/test", provider));
        foreach (var packet in packets) foreach (var item in mapper.Apply(packet)) assembler.Apply(item);
        mapper.CheckComplete(); return output;
    }
    private static string Item(string type, JsonObject item, int index) => new JsonObject { ["type"] = type, ["item"] = item.DeepClone(), ["output_index"] = index }.ToJsonString();
    private const string Complete = """{"type":"response.completed","response":{"id":"matrix","object":"response","status":"completed","output":[]}}""";

    private static async Task HostedMatrixAsync(Action<bool, string> check)
    {
        var native = new[] {
            """{"type":"web_search_call","id":"web","status":"completed","action":{"type":"search","query":"test","sources":[{"url":"https://example.com","title":"Source"}]}}""",
            """{"type":"file_search_call","id":"files","status":"completed","queries":["test"],"results":[]}""",
            """{"type":"code_interpreter_call","id":"code","status":"completed","container_id":"container","code":"print(1)","outputs":[]}""",
            """{"type":"image_generation_call","id":"image","status":"completed","result":"AA==","output_format":"png"}""",
            """{"type":"mcp_call","id":"mcp","status":"completed","name":"echo","server_label":"aihappey","arguments":"{}","output":"{\"content\":[{\"type\":\"text\",\"text\":\"hosted-result\"}]}"}""",
            """{"type":"mcp_list_tools","id":"mcp-list","server_label":"hosted","tools":[]}"""
        };
        foreach (var json in native)
        {
            var item = JsonNode.Parse(json)!.AsObject();
            var output = Reduce("/v1/responses", "openai", [Item("response.output_item.added", item, 0), Item("response.output_item.done", item, 0), Item("response.output_item.done", item, 0), Complete]);
            var tool = PortableConversations.Node(output.Message.Parts.Single(PortableConversations.IsTool));
            var calls = 0; var snapshot = McpTurnSnapshot.Empty;
            snapshot.AddNative("echo", (_, _) => { calls++; return Task.FromResult(JsonSerializer.SerializeToElement(new { })); });
            await DesktopMcpToolExecution.ExecutePendingAsync(output, snapshot, [], "en", default);
            check(calls == 0 && tool["providerExecuted"]!.GetValue<bool>() && DesktopToolApprovals.Pending(output.Message).Count == 0, item["type"] + " never executes or requests local review");
            var replay = NativeChatRequests.Build(new("/v1/responses", "openai/test", "openai"), new(), [output.Message]);
            check(replay["input"]!.AsArray().Count(i => i?["type"]?.ToString() == item["type"]!.ToString()) == 1, item["type"] + " is deduplicated and replays as a native item");
            if (item["type"]!.ToString() == "image_generation_call") check(output.Message.Parts.Count(p => p.Type == "file") == 1, "final generated images use the existing portable file presentation once");
        }
        foreach (var pair in new[] {
            ("""{"type":"tool_search_call","id":"search","execution":"server","status":"completed","arguments":{"description":"find tools"}}""", """{"type":"tool_search_output","id":"search-output","execution":"server","status":"completed","tools":[]}"""),
            ("""{"type":"program","id":"program","call_id":"program-call","status":"completed","code":"print(1)","fingerprint":"opaque"}""", """{"type":"program_output","id":"program-output","call_id":"program-call","status":"completed","result":"1"}""") })
        {
            var call = JsonNode.Parse(pair.Item1)!.AsObject(); var result = JsonNode.Parse(pair.Item2)!.AsObject();
            // Output may arrive before its call; pairing is transport-local and must not invent another card.
            var output = Reduce("/v1/responses", "openai", [Item("response.output_item.done", result, 1), Item("response.output_item.done", call, 0),
                Item("response.output_item.done", result, 1), Item("response.output_item.done", call, 0), Complete]);
            var replay = NativeChatRequests.Build(new("/v1/responses", "openai/test", "openai"), new(), [output.Message]);
            check(output.Message.Parts.Count(PortableConversations.IsTool) == 1 && replay["input"]!.AsArray().Count == 2, call["type"] + " correlates native call/output identities, including absent search call IDs");
        }
        foreach (var type in new[] { "web_search_tool_result", "web_fetch_tool_result", "code_execution_tool_result", "bash_code_execution_tool_result", "text_editor_code_execution_tool_result", "tool_search_tool_result", "mcp_tool_result" })
        {
            var output = Reduce("/v1/messages", "anthropic", [
                """{"type":"content_block_start","index":0,"content_block":{"type":"server_tool_use","id":"server","name":"echo","input":{}}}""", """{"type":"content_block_stop","index":0}""",
                new JsonObject { ["type"] = "content_block_start", ["index"] = 1, ["content_block"] = new JsonObject { ["type"] = type, ["tool_use_id"] = "server", ["content"] = new JsonArray() } }.ToJsonString(),
                """{"type":"content_block_stop","index":1}""", """{"type":"message_stop"}""" ]);
            var body = NativeChatRequests.Build(new("/v1/messages", "anthropic/test", "anthropic"), new(), [output.Message]);
            check(body["messages"]![0]!["content"]!.AsArray().Count == 2 && body.ToJsonString().Contains(type), type + " remains a typed provider result during native replay");
        }
    }

    private sealed class Host : IDesktopHost
    {
        public string ProfileId => "native-tool-http";
        public bool AllowLocal => true;
        public string AccountLabel => "fixture";
        public string HistoryIdentity => "fixture";
        public bool SupportsDirectChat => true;
        public bool HasDirectChatCredential(string provider) => true;
        public Task AuthenticateDirectChatAsync(HttpRequestMessage request, string provider, string endpoint, CancellationToken ct)
        { DesktopDirectChatAuthentication.Apply(request, new Dictionary<string, string> { ["X-OpenAI-Key"] = "openai-test", ["X-Anthropic-Key"] = "anthropic-test" }, provider, endpoint); return Task.CompletedTask; }
        public Task AuthenticateAsync(HttpRequestMessage request, ServiceKind service, CancellationToken ct)
        { request.Headers.Add("X-Gateway-Secret", "gateway-test"); return Task.CompletedTask; }
        public Task ManageAccountAsync(object root, CancellationToken ct) => Task.CompletedTask;
    }
    private sealed class Runtime : IRuntimeResolver
    {
        public Task<Uri> ResolveAsync(ServiceKind service, DesktopSettings settings, CancellationToken ct) => Task.FromResult(new Uri("https://gateway.example/prefix/"));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Handler(string endpoint) : HttpMessageHandler
    {
        public List<JsonObject> Bodies = [];
        public List<Dictionary<string, string>> Headers = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Bodies.Add(JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject());
            Headers.Add(request.Headers.ToDictionary(p => p.Key, p => string.Join(",", p.Value), StringComparer.OrdinalIgnoreCase));
            var packets = Bodies.Count == 1 ? NativeChatToolRegressionTests.ToolPackets(endpoint, "http-call") : NativeChatToolRegressionTests.TextPackets(endpoint);
            return new(HttpStatusCode.OK) { Content = new StringContent(string.Concat(packets.Select(p => "data: " + p + "\n\n")) + "data: [DONE]\n\n", Encoding.UTF8, "text/event-stream") };
        }
    }
}
