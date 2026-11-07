using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Desktop.Core;
using AIHappey.Vercel.Models;

internal static class NativeChatToolRegressionTests
{
    internal static readonly string[] Endpoints = ["/v1/responses", "/v1/messages", "/v1/chat/completions"];
    private static JsonElement Element(string json) => JsonSerializer.Deserialize<JsonElement>(json);
    private static UIMessage User() => new() { Id = "user", Role = Role.user, Parts = [PortableConversations.Part(JsonNode.Parse("""{"type":"text","text":"Use echo"}""")!.AsObject())] };
    private static ConversationMessage Assistant() => new() { Status = "streaming", Message = new() { Id = "assistant", Role = Role.assistant, Parts = [] } };

    internal static async Task RunAsync(Action<bool, string> check, string root)
    {
        foreach (var endpoint in Endpoints) { await ContinuationAsync(endpoint, check, root); Declarations(endpoint, check); }
        ProviderMessages(check); ProviderResponses(check); InvalidStreams(check);
        await NativeChatToolTransportTests.RunAsync(check);
        await NativeChatToolSafetyTests.RunAsync(check);
    }

    private static async Task ContinuationAsync(string endpoint, Action<bool, string> check, string root)
    {
        var provider = endpoint == "/v1/messages" ? "anthropic" : "openai";
        var route = new DesktopChatRoute(endpoint, provider + "/test", provider);
        var preferences = new ChatPreferences { MaxToolCalls = 2 };
        var snapshot = McpTurnSnapshot.Empty;
        var executions = 0; var approvals = 0; var requests = 0;
        snapshot.AddLocal(Element("""{"name":"echo","description":"Echo input","inputSchema":{"type":"object","properties":{"value":{"type":"integer"}}}}"""),
            (input, ct) => { executions++; return Task.FromResult(Element("""{"content":[{"type":"text","text":"echo-result"}],"structuredContent":{"value":1}}""")); });
        var conversation = new Conversation { Id = Guid.NewGuid().ToString(), Target = route.Model };
        conversation.Messages.Add(new() { Message = User() });
        var output = Assistant(); conversation.Messages.Add(output);
        var wires = new List<JsonObject>();
        async IAsyncEnumerable<StreamEvent> Stream(List<UIMessage> messages, [EnumeratorCancellation] CancellationToken ct)
        {
            wires.Add(NativeChatRequests.Build(route, preferences, messages, snapshot));
            var round = requests++;
            var mapper = new NativeChatStream(route);
            foreach (var packet in round < 2 ? ToolPackets(endpoint, "call-" + round) : TextPackets(endpoint))
            {
                ct.ThrowIfCancellationRequested();
                foreach (var item in mapper.Apply(packet))
                {
                    check(item.Type != "tool-approval-request", endpoint + " does not fabricate approval requests from native tool calls");
                    yield return item;
                }
            }
            mapper.CheckComplete(); await Task.CompletedTask;
        }
        await DesktopChatTurn.RunAsync(conversation, output, Stream,
            (_, _) => { approvals++; throw new Exception(endpoint + " must not prompt without a backend approval request"); },
            (_, _) => Task.CompletedTask, snapshot, "en", default);
        check(requests == 3 && executions == 2 && approvals == 0 && output.Status == "complete", endpoint + " executes and continues exactly once per call without approval");
        check(DesktopToolApprovals.Pending(output.Message).Count == 0 && output.Message.Parts.Where(PortableConversations.IsTool)
            .All(part => PortableConversations.String(PortableConversations.Element(part), "state") == "output-available"
                && !PortableConversations.Element(part).TryGetProperty("approval", out _)), endpoint + " completed client tools have outputs without invented approval metadata");
        check(output.Message.Parts.Count(p => p.Type == "step-start") == 3 && output.Text.Contains("Final answer"), endpoint + " retains step boundaries and final text");
        check(wires[1].ToJsonString().Contains("echo-result") && wires[2]["tool_choice"]!.ToJsonString().Contains("none"), endpoint + " replays results and enforces tool budget");
        check(!wires.Any(w => w.ContainsKey("previous_response_id") || w.ContainsKey("conversation") || w.ContainsKey("providerMetadata")), endpoint + " requests are stateless and protocol-local");
        var portable = JsonSerializer.Serialize(conversation, PortableConversations.Json);
        var restored = PortableConversations.Read(portable);
        var store = new HistoryStore(Path.Combine(root, "native-tools"));
        var partition = HistoryStore.Partition("native-tools");
        await store.SaveAsync(partition, restored);
        var reloaded = (await store.GetAsync(partition, restored.Id))!;
        foreach (var replayEndpoint in Endpoints)
        {
            var replay = NativeChatRequests.Build(new(replayEndpoint, route.Model, provider), preferences, reloaded.Messages.Select(m => m.Message).ToArray());
            var wire = replay.ToJsonString();
            check(wire.Contains("call-0") && wire.Contains("call-1") && wire.Contains("echo-result") && wire.Contains("Final answer"), endpoint + " saved rounds project to " + replayEndpoint);
            if (replayEndpoint == "/v1/chat/completions")
                check(replay["messages"]!.AsArray().Count(m => m?["role"]?.ToString() == "tool") == 2, "Completions retains tool roles across saved rounds");
        }
        check(portable.Contains("callProviderMetadata") && !portable.Contains("approval") && !portable.Contains("response.output_item"), endpoint + " saves portable tool results without fabricated approvals or SSE frames");
        if (endpoint == "/v1/messages") check(NativeChatRequests.Build(route, preferences, reloaded.Messages.Select(m => m.Message).ToArray()).ToJsonString().Contains("signature-first-second"), "fragmented Messages signatures survive storage/replay");
        var toolPart = output.Message.Parts.First(PortableConversations.IsTool);
        var index = output.Message.Parts.IndexOf(toolPart);
        // Explicit historical denial remains replayable; native tool calls must not create one themselves.
        var raw = PortableConversations.Node(toolPart);
        raw["state"] = "approval-responded";
        raw["approval"] = new JsonObject { ["id"] = "historical-approval", ["approved"] = false };
        output.Message.Parts[index] = PortableConversations.Part(raw);
        var denied = NativeChatRequests.Build(route, preferences, conversation.Messages.Select(m => m.Message).ToArray());
        check(denied.ToJsonString().Contains("denied release") && PortableConversations.Node(output.Message.Parts[index])["output"] is not null, endpoint + " denial replay preserves the saved result");
        raw["state"] = "input-streaming"; output.Message.Parts[index] = PortableConversations.Part(raw);
        check(!NativeChatRequests.Build(route, preferences, conversation.Messages.Select(m => m.Message).ToArray()).ToJsonString().Contains("call-0"), endpoint + " omits dangling cancelled calls");
    }

    internal static IEnumerable<string> ToolPackets(string endpoint, string id)
    {
        if (endpoint == "/v1/responses")
        {
            yield return """{"type":"response.output_item.added","output_index":0,"item":{"type":"function_call","id":"item-{{id}}","call_id":"{{id}}","name":"echo","arguments":""}}""".Replace("{{id}}", id);
            yield return """{"type":"response.function_call_arguments.delta","item_id":"item-{{id}}","delta":"{\"value\":"}""".Replace("{{id}}", id);
            yield return """{"type":"response.function_call_arguments.delta","item_id":"item-{{id}}","delta":"1}"}""".Replace("{{id}}", id);
            yield return """{"type":"response.output_item.done","output_index":0,"item":{"type":"function_call","id":"item-{{id}}","call_id":"{{id}}","name":"echo","arguments":"{\"value\":1}","status":"completed"}}""".Replace("{{id}}", id);
            yield return """{"type":"response.completed","response":{"id":"response-{{id}}","object":"response","status":"completed","output":[{"type":"function_call","id":"item-{{id}}","call_id":"{{id}}","name":"echo","arguments":"{\"value\":1}","status":"completed"}]}}""".Replace("{{id}}", id);
        }
        else if (endpoint == "/v1/messages")
        {
            yield return """{"type":"message_start","message":{"id":"message-{{id}}","type":"message","role":"assistant","content":[],"model":"anthropic/test","usage":{"input_tokens":1,"output_tokens":0}}}""".Replace("{{id}}", id);
            yield return """{"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":"Thinking"}}""";
            yield return """{"type":"content_block_delta","index":0,"delta":{"type":"signature_delta","signature":"signature-first-"}}""";
            yield return """{"type":"content_block_delta","index":0,"delta":{"type":"signature_delta","signature":"second"}}""";
            yield return """{"type":"content_block_stop","index":0}""";
            yield return """{"type":"content_block_start","index":1,"content_block":{"type":"tool_use","id":"{{id}}","name":"echo","input":{}}}""".Replace("{{id}}", id);
            yield return """{"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"{\"value\":"}}""";
            yield return """{"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"1}"}}""";
            yield return """{"type":"content_block_stop","index":1}""";
            yield return """{"type":"message_delta","delta":{"stop_reason":"tool_use"},"usage":{"output_tokens":1}}""";
            yield return """{"type":"message_stop"}""";
        }
        else
        {
            yield return """{"id":"chat-{{id}}","object":"chat.completion.chunk","choices":[{"index":0,"delta":{"reasoning_content":"Thinking","tool_calls":[{"index":0,"id":"{{id}}","type":"function","function":{"name":"ec","arguments":"{\"value\":"}}]},"finish_reason":null}]}""".Replace("{{id}}", id);
            yield return """{"id":"chat","object":"chat.completion.chunk","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"name":"ho","arguments":"1}"}}]},"finish_reason":null}]}""";
            yield return """{"id":"chat","object":"chat.completion.chunk","choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}]}""";
        }
    }

    internal static IEnumerable<string> TextPackets(string endpoint) => endpoint == "/v1/responses"
        ? ["""{"type":"response.output_text.delta","item_id":"final","content_index":0,"delta":"Final answer"}""", """{"type":"response.completed","response":{"id":"response-final","object":"response","status":"completed","output":[]}}"""]
        : endpoint == "/v1/messages" ? ["""{"type":"content_block_start","index":0,"content_block":{"type":"text","text":"Final answer"}}""", """{"type":"content_block_stop","index":0}""", """{"type":"message_delta","delta":{"stop_reason":"end_turn"}}""", """{"type":"message_stop"}"""]
        : ["""{"id":"final","object":"chat.completion.chunk","choices":[{"index":0,"delta":{"content":"Final answer"},"finish_reason":"stop"}]}"""];

    private static void Declarations(string endpoint, Action<bool, string> check)
    {
        var provider = endpoint == "/v1/messages" ? "anthropic" : "openai";
        var preferences = new ChatPreferences { ToolChoice = "required", MaxToolCalls = 1 };
        preferences.ProviderMetadata[provider] = JsonNode.Parse(endpoint == "/v1/messages"
            ? """{"tools":[{"type":"web_search_20260318","name":"web_search"}],"tool_choice":{"type":"any"}}"""
            : """{"tools":[{"type":"web_search"},{"type":"function","function":{"name":"native","parameters":{"type":"object"}}}],"tool_choice":"required"}""")!.AsObject();
        var snapshot = McpTurnSnapshot.Empty;
        snapshot.AddLocal(Element("""{"name":"echo","inputSchema":{"type":"object","properties":{}}}"""), (_, _) => Task.FromResult(Element("{}")));
        var body = NativeChatRequests.Build(new(endpoint, provider + "/test", provider), preferences, [User()], snapshot);
        check(body["tools"]!.AsArray().Any(t => (t?["name"] ?? t?["function"]?["name"])?.ToString() == "echo"), endpoint + " declares client functions with the native schema");
        check(endpoint != "/v1/responses" || body["include"]!.AsArray().Any(v => v?.ToString() == "reasoning.encrypted_content"), "Responses requests stateless encrypted reasoning");
        preferences.ToolChoice = "none";
        var disabled = NativeChatRequests.Build(new(endpoint, provider + "/test", provider), preferences, [User()], snapshot);
        check(disabled["tool_choice"]!.ToJsonString().Contains("none") && !disabled["tools"]!.ToJsonString().Contains("echo"), endpoint + " none overrides configured forced choice");
    }

    private static void ProviderMessages(Action<bool, string> check)
    {
        var route = new DesktopChatRoute("/v1/messages", "anthropic/test", "anthropic");
        var mapper = new NativeChatStream(route); var output = Assistant(); var assembler = new MessageAssembler(output);
        foreach (var packet in new[] {
            """{"type":"message_start","message":{"id":"provider-msg","type":"message","role":"assistant","content":[],"model":"anthropic/test","usage":{}}}""",
            """{"type":"content_block_start","index":0,"content_block":{"type":"server_tool_use","id":"search","name":"web_search","input":{"query":"test"}}}""",
            """{"type":"content_block_stop","index":0}""",
            """{"type":"content_block_start","index":1,"content_block":{"type":"text","text":"Between"}}""",
            """{"type":"content_block_stop","index":1}""",
            """{"type":"content_block_start","index":2,"content_block":{"type":"web_search_tool_result","tool_use_id":"search","content":{"type":"web_search_tool_result_error","error_code":"rate_limited"}}}""",
            """{"type":"content_block_stop","index":2}""",
            """{"type":"message_delta","delta":{"stop_reason":"end_turn"}}""", """{"type":"message_stop"}""" })
            foreach (var item in mapper.Apply(packet)) assembler.Apply(item);
        mapper.CheckComplete();
        var part = PortableConversations.Node(output.Message.Parts.Single(PortableConversations.IsTool));
        check(part["providerExecuted"]!.GetValue<bool>() && part["state"]!.ToString() == "output-error" && !assembler.ApprovalRequired, "Messages provider results are nonlocal and preserve typed failures");
        var blocks = NativeChatRequests.Build(route, new(), [output.Message])["messages"]![0]!["content"]!.AsArray();
        check(blocks.Select(b => b!["type"]!.ToString()).SequenceEqual(new[] { "server_tool_use", "text", "web_search_tool_result" }) && blocks[2]!["content"]!["type"]!.ToString() == "web_search_tool_result_error", "Messages restores exact provider block order and typed error content");
    }

    private static void ProviderResponses(Action<bool, string> check)
    {
        var route = new DesktopChatRoute("/v1/responses", "openai/test", "openai");
        var mapper = new NativeChatStream(route); var output = Assistant(); var assembler = new MessageAssembler(output);
        foreach (var packet in new[] {
            """{"type":"response.output_item.done","output_index":0,"item":{"type":"shell_call","id":"shell-item","call_id":"shell-call","status":"completed","action":{"commands":["echo test"]},"environment":{"type":"container_reference","container_id":"hosted-container"}}}""",
            """{"type":"response.output_text.delta","item_id":"text","content_index":0,"delta":"Between"}""",
            """{"type":"response.output_item.done","output_index":1,"item":{"type":"message","id":"text","role":"assistant","content":[{"type":"output_text","text":"Between","annotations":[]}]}}""",
            """{"type":"response.output_item.done","output_index":2,"item":{"type":"shell_call_output","id":"shell-output","call_id":"shell-call","status":"completed","output":[{"stdout":"test","stderr":"","outcome":{"type":"exit","exit_code":0}}]}}""",
            """{"type":"response.completed","response":{"id":"hosted","object":"response","status":"completed","output":[]}}""" })
            foreach (var item in mapper.Apply(packet)) assembler.Apply(item);
        mapper.CheckComplete();
        var part = PortableConversations.Node(output.Message.Parts.Single(PortableConversations.IsTool));
        check(part["providerExecuted"]!.GetValue<bool>() && part["callProviderMetadata"]!["openai"]!["type"]!.ToString() == "shell_call"
            && part["resultProviderMetadata"]!["openai"]!["type"]!.ToString() == "shell_call_output", "Responses keeps hosted call/result metadata separate");
        var input = NativeChatRequests.Build(route, new(), [output.Message])["input"]!.AsArray();
        check(input.Select(i => i!["type"]!.ToString()).SequenceEqual(new[] { "shell_call", "message", "shell_call_output" }) && !input.ToJsonString().Contains("function_call_output"), "Responses restores hosted item order without inventing a function result");
    }

    private static void InvalidStreams(Action<bool, string> check)
    {
        var mapper = new NativeChatStream(new("/v1/messages", "anthropic/test", "anthropic"));
        mapper.Apply("""{"type":"content_block_start","index":0,"content_block":{"type":"tool_use","id":"bad","name":"echo","input":{}}}""");
        mapper.Apply("""{"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"{"}}""");
        try { mapper.Apply("""{"type":"content_block_stop","index":0}"""); throw new Exception("Expected invalid tool JSON rejection"); }
        catch (GatewayException) { check(true, "malformed streamed arguments cannot become executable calls"); }
    }
}
