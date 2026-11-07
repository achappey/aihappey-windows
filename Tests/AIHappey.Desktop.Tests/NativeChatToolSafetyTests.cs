using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Desktop.Core;
using AIHappey.Vercel.Models;

internal static class NativeChatToolSafetyTests
{
    private static UIMessage Message(string json) => PortableConversations.Read("{\"id\":\"fixture\",\"messages\":[" + json + "]}").Messages[0].Message;
    private static ConversationMessage Reduce(string endpoint, params string[] packets)
    {
        var output = new ConversationMessage { Message = new() { Id = "a", Role = Role.assistant, Parts = [] } };
        var mapper = new NativeChatStream(new(endpoint, "provider/test", "provider")); var assembler = new MessageAssembler(output);
        foreach (var packet in packets) foreach (var item in mapper.Apply(packet)) assembler.Apply(item);
        mapper.CheckComplete(); return output;
    }
    internal static async Task RunAsync(Action<bool, string> check)
    {
        foreach (var endpoint in NativeChatToolRegressionTests.Endpoints)
        {
            var snapshot = McpTurnSnapshot.Empty;
            snapshot.AddLocal(JsonSerializer.Deserialize<JsonElement>("""{"name":"echo","inputSchema":{"type":"object","properties":{}},"outputSchema":{"type":"object"}}"""), (_, _) => Task.FromResult(JsonSerializer.SerializeToElement(new { })));
            var preferences = new ChatPreferences { UseToolNamespaces = true };
            preferences.ToolRequestConfig["echo"] = new() { AllowedCallers = ["programmatic"], DeferLoading = true };
            var user = Message("""{"id":"u","role":"user","parts":[{"type":"text","text":"test"}]}""");
            var body = NativeChatRequests.Build(new(endpoint, "provider/test", "provider"), preferences, [user], snapshot);
            check(body["tools"]![0]!["type"]?.ToString() == "namespace" == (endpoint == "/v1/responses"), endpoint + " scopes tool namespaces to the compatible protocol");
            if (endpoint == "/v1/messages") check(body["tools"]![0]!["defer_loading"]!.GetValue<bool>(), "Messages retains deferred tool settings without sending Responses namespace/caller fields");
            if (endpoint == "/v1/responses") check(body["tools"]![0]!["tools"]![0]!["allowed_callers"]![0]!.ToString() == "programmatic", "Responses namespaces retain per-tool caller options");
            var assistant = Message("""{"id":"a","role":"assistant","parts":[{"type":"step-start"},{"type":"tool-echo","toolCallId":"filtered","providerExecuted":false,"state":"output-available","input":{},"output":{"content":[{"type":"image","mimeType":"image/png","data":"private-image"}]}}]}""");
            var before = JsonSerializer.Serialize(assistant, PortableConversations.Json);
            var filtered = NativeChatRequests.Build(new(endpoint, "provider/test", "provider"), preferences, [user, assistant]);
            check(filtered.ToJsonString().Contains("Image content omitted") && !filtered.ToJsonString().Contains("private-image")
                && JsonSerializer.Serialize(assistant, PortableConversations.Json) == before, endpoint + " filters output only on request copies");
            preferences.StopTools.Add("echo");
            check(NativeChatRequests.Build(new(endpoint, "provider/test", "provider"), preferences, [user, assistant], snapshot)["tool_choice"]!.ToJsonString().Contains("none"), endpoint + " respects stop-tool policy");
            await InterruptedAsync(endpoint, check);
        }
        var parallel = Reduce("/v1/chat/completions",
            """{"id":"p","object":"chat.completion.chunk","choices":[{"index":0,"delta":{"tool_calls":[{"index":1,"id":"second","type":"function","function":{"name":"echo","arguments":"{\"b\":"}},{"index":0,"id":"first","type":"function","function":{"name":"echo","arguments":"{\"a\":"}}]},"finish_reason":null}]}""",
            """{"id":"p","object":"chat.completion.chunk","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"1}"}},{"index":1,"function":{"arguments":"2}"}}]},"finish_reason":null}]}""",
            """{"id":"p","object":"chat.completion.chunk","choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}]}""");
        check(parallel.Message.Parts.Count(PortableConversations.IsTool) == 2 && parallel.Message.Parts.Where(PortableConversations.IsTool)
            .Select(PortableConversations.Node).Any(p => p["input"]?["a"]?.GetValue<int>() == 1), "Completions correlates interleaved parallel arguments by native index");
        var legacy = Reduce("/v1/chat/completions",
            """{"id":"l","object":"chat.completion.chunk","choices":[{"index":0,"delta":{"function_call":{"name":"echo","arguments":"{"}},"finish_reason":null}]}""",
            """{"id":"l","object":"chat.completion.chunk","choices":[{"index":0,"delta":{"function_call":{"arguments":"}"}},"finish_reason":"function_call"}]}""");
        check(legacy.Message.Parts.Count(PortableConversations.IsTool) == 1 && DesktopToolApprovals.Pending(legacy.Message).Count == 0
            && PortableConversations.Node(legacy.Message.Parts.Single(PortableConversations.IsTool))["state"]!.ToString() == "input-available",
            "legacy function deltas remain one executable tool call without a fabricated approval");
        var custom = Reduce("/v1/chat/completions",
            """{"id":"c","object":"chat.completion.chunk","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"custom","type":"custom","custom":{"name":"custom-tool","input":"plain "}}]},"finish_reason":null}]}""",
            """{"id":"c","object":"chat.completion.chunk","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"custom":{"input":"text"}}]},"finish_reason":"tool_calls"}]}""");
        check(PortableConversations.Node(custom.Message.Parts.Single(PortableConversations.IsTool))["input"]!.GetValue<string>() == "plain text", "custom string input survives fragmented deltas without becoming JSON or bypassing handler validation");
        var patch = Reduce("/v1/responses",
            """{"type":"response.output_item.done","output_index":0,"item":{"type":"apply_patch_call","id":"patch-item","call_id":"patch-call","status":"completed","operation":{"type":"create_file","path":"test.txt","diff":"+test"}}}""",
            """{"type":"response.completed","response":{"id":"patch","object":"response","status":"completed","output":[]}}""");
        var patchPart = PortableConversations.Node(patch.Message.Parts.Single(PortableConversations.IsTool));
        patchPart["state"] = "output-available"; patchPart["output"] = JsonNode.Parse("""{"structuredContent":{"status":"completed","output":"applied"},"content":[]}""");
        patch.Message.Parts[patch.Message.Parts.IndexOf(patch.Message.Parts.Single(PortableConversations.IsTool))] = PortableConversations.Part(patchPart);
        var patchReplay = NativeChatRequests.Build(new("/v1/responses", "provider/test", "provider"), new(), [patch.Message])["input"]!.AsArray();
        check(patchReplay[0]!["type"]!.ToString() == "apply_patch_call" && patchReplay[1]!["type"]!.ToString() == "apply_patch_call_output"
            && patchReplay[1]!["output"]!.ToString() == "applied", "Responses apply-patch uses the existing client harness input and native result contract");
        var interleaved = Message("""{"id":"interleaved","role":"assistant","parts":[{"type":"step-start"},{"type":"tool-echo","toolCallId":"one","providerExecuted":false,"state":"output-available","input":{},"output":"one"},{"type":"text","text":"between calls"},{"type":"tool-echo","toolCallId":"two","providerExecuted":false,"state":"output-available","input":{},"output":"two"}]}""");
        var messages = NativeChatRequests.Build(new("/v1/messages", "provider/test", "provider"), new(), [interleaved])["messages"]!.AsArray();
        check(messages.Count == 2 && messages[0]!["content"]!.AsArray().Count == 3 && messages[1]!["content"]!.AsArray().Count == 2,
            "Messages keeps interleaved text and parallel calls in their original assistant step, before all user results");
        await PauseAsync(check);
        foreach (var nested in new[] { false, true })
        {
            var input = JsonNode.Parse("""{"index":0,"id":"hosted-completion","type":"function","function":{"name":"echo","arguments":"{}"}}""")!.AsObject();
            (nested ? input["function"]!.AsObject() : input)["provider_executed"] = true;
            var result = JsonNode.Parse("""{"index":0,"id":"hosted-completion","type":"function","function":{"name":"echo","output":{"content":[{"type":"text","text":"Already executed"}]}}}""")!.AsObject();
            (nested ? result["function"]!.AsObject() : result)["provider_executed"] = true;
            string Packet(JsonObject call) => new JsonObject { ["id"] = "hosted", ["object"] = "chat.completion.chunk", ["choices"] = new JsonArray(new JsonObject {
                ["index"] = 0, ["delta"] = new JsonObject { ["tool_calls"] = new JsonArray(call.DeepClone()) } }) }.ToJsonString();
            var hosted = Reduce("/v1/chat/completions", Packet(input), Packet(result),
                """{"id":"hosted","object":"chat.completion.chunk","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}""");
            var tool = PortableConversations.Node(hosted.Message.Parts.Single(PortableConversations.IsTool));
            var snapshot = McpTurnSnapshot.Empty; var executions = 0;
            snapshot.AddNative("echo", (_, _) => { executions++; return Task.FromResult(JsonSerializer.SerializeToElement(new { })); });
            await DesktopMcpToolExecution.ExecutePendingAsync(hosted, snapshot, [], "en", default);
            check(executions == 0 && tool["providerExecuted"]!.GetValue<bool>() && tool["type"]!.ToString() == "tool-echo"
                && tool["output"]!.ToJsonString().Contains("Already executed") && DesktopToolApprovals.Pending(hosted.Message).Count == 0,
                "Completions respects " + (nested ? "nested" : "top-level") + " provider-executed extensions and never repeats remote tool execution locally");
        }
    }
    private static async Task PauseAsync(Action<bool, string> check)
    {
        foreach (var empty in new[] { false, true })
        {
            var conversation = new Conversation();
            var output = new ConversationMessage { Message = new() { Id = "pause", Role = Role.assistant, Parts = [] } };
            conversation.Messages.Add(output); var requests = 0;
            async IAsyncEnumerable<StreamEvent> Stream(List<UIMessage> messages, [EnumeratorCancellation] CancellationToken ct)
            {
                var round = requests++; var mapper = new NativeChatStream(new("/v1/messages", "anthropic/test", "anthropic"));
                var packets = round == 0 ? (empty ? Array.Empty<string>() : new[] {
                    """{"type":"content_block_start","index":0,"content_block":{"type":"text","text":"Working"}}""", """{"type":"content_block_stop","index":0}""" })
                    .Concat(new[] { """{"type":"message_delta","delta":{"stop_reason":"pause_turn"}}""", """{"type":"message_stop"}""" })
                    : NativeChatToolRegressionTests.TextPackets("/v1/messages");
                foreach (var packet in packets) foreach (var item in mapper.Apply(packet)) yield return item;
                mapper.CheckComplete(); await Task.CompletedTask;
            }
            try
            {
                await DesktopChatTurn.RunAsync(conversation, output, Stream, (_, _) => throw new Exception("Provider pauses do not require local review"),
                    (_, _) => Task.CompletedTask, McpTurnSnapshot.Empty, "en", default);
                check(!empty && requests == 2 && output.Text.Contains("Working") && output.Text.Contains("Final answer"), "Messages pause_turn explicitly continues with portable history, not an inference retry");
            }
            catch (GatewayException) when (empty) { check(requests == 1, "an empty provider pause cannot start a continuation busy loop"); }
        }
    }
    private static async Task InterruptedAsync(string endpoint, Action<bool, string> check)
    {
        var conversation = new Conversation();
        var output = new ConversationMessage { Message = new() { Id = "a", Role = Role.assistant, Parts = [] } };
        conversation.Messages.Add(output); var executed = 0; var requested = 0;
        var snapshot = McpTurnSnapshot.Empty;
        snapshot.AddLocal(JsonSerializer.Deserialize<JsonElement>("""{"name":"echo","inputSchema":{"type":"object"}}"""), (_, _) => { executed++; return Task.FromResult(JsonSerializer.SerializeToElement(new { })); });
        async IAsyncEnumerable<StreamEvent> Broken(List<UIMessage> _, [EnumeratorCancellation] CancellationToken ct)
        {
            requested++; var mapper = new NativeChatStream(new(endpoint, "provider/test", "provider"));
            foreach (var packet in NativeChatToolRegressionTests.ToolPackets(endpoint, "interrupted"))
                foreach (var item in mapper.Apply(packet)) if (item.Type != "finish") yield return item;
            await Task.CompletedTask;
            throw new GatewayException("Truncated fixture transport");
        }
        try
        {
            await DesktopChatTurn.RunAsync(conversation, output, Broken, (_, _) => throw new Exception("Interrupted calls must not be reviewed"),
                (_, _) => Task.CompletedTask, snapshot, "en", default);
            throw new Exception("Expected interrupted transport failure");
        }
        catch (GatewayException) { check(executed == 0 && requested == 1, endpoint + " interrupted transport never executes, approves or retries an incomplete turn"); }
    }
}
