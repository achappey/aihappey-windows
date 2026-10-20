using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Desktop.Core;
using AIHappey.Vercel.Models;

internal static class ChatToolSettingsRegressionTests
{
    private static JsonElement Json(string text) => JsonSerializer.Deserialize<JsonElement>(text);
    private static JsonElement Definition(string name) => JsonSerializer.SerializeToElement(new { name, inputSchema = new { type = "object" } });
    private static UIMessage User() => new() { Id = "user", Role = Role.user, Parts = [new TextUIPart { Text = "Hello" }] };
    private static UIMessage Assistant(string state, string name = "search") => new() { Id = "assistant", Role = Role.assistant,
        Parts = [PortableConversations.Part(new JsonObject { ["type"] = "tool-" + name, ["state"] = state, ["toolCallId"] = "call", ["input"] = new JsonObject() })] };

    public static async Task RunAsync(Action<bool, string> check, string root)
    {
        var fresh = new ChatPreferences();
        check(fresh.NormalizedToolChoice is null && fresh.MaxToolCalls is null && fresh.StopTools.Count == 0
            && fresh.ToolAnnotations.OpenWorldHint && fresh.ToolAnnotations.DestructiveHint
            && !fresh.ToolAnnotations.ReadOnlyHint && !fresh.ToolAnnotations.IdempotentHint, "chat tools: browser-compatible defaults");
        var legacy = JsonSerializer.Deserialize<ChatPreferences>("{}", JsonSerializerOptions.Web)!;
        check(legacy.ToolAnnotations.OpenWorldHint && legacy.ToolAnnotations.DestructiveHint, "chat tools: legacy settings retain permissive defaults");
        var malformed = JsonSerializer.Deserialize<ChatPreferences>("""{"toolChoice":"required","maxToolCalls":0,"stopTools":null,"toolAnnotations":null}""", JsonSerializerOptions.Web)!.Clone();
        check(malformed.NormalizedToolChoice is null && malformed.MaxToolCalls is null && malformed.StopTools.Count == 0
            && malformed.ToolAnnotations.OpenWorldHint, "chat tools: invalid required and null persisted collections normalize safely");
        check(new ChatPreferences { ToolChoice = "future-invalid" }.NormalizedToolChoice is null, "chat tools: unknown choices fall back to automatic");
        var preferences = new ChatPreferences { ToolChoice = "required", MaxToolCalls = 3, StopTools = [" search ", "search", ""],
            ToolAnnotations = new() { OpenWorldHint = false, DestructiveHint = false, ReadOnlyHint = true, IdempotentHint = true } };
        var clone = preferences.Clone(); clone.StopTools.Clear(); clone.ToolAnnotations.OpenWorldHint = true;
        check(preferences.StopTools.SequenceEqual(["search"]) && !preferences.ToolAnnotations.OpenWorldHint, "chat tools: drafts deeply isolate selections and policy");
        var directory = Path.Combine(root, "tool-settings");
        await SettingsStore.SaveAsync(directory, new() { Chat = preferences });
        var restored = (await SettingsStore.LoadAsync(directory, new())).Chat;
        check(restored.ToolChoice == "required" && restored.MaxToolCalls == 3 && restored.StopTools.SequenceEqual(["search"])
            && !restored.ToolAnnotations.OpenWorldHint && !restored.ToolAnnotations.DestructiveHint
            && restored.ToolAnnotations.ReadOnlyHint && restored.ToolAnnotations.IdempotentHint, "chat tools: all new settings survive save and restart");
        foreach (var text in new[] { "0", "-1", "1.5", "1e2", "2147483648", "abc" })
            check(!ChatPreferences.TryTokenLimit(text, out _), "chat tools: optional call count rejects " + text);
        check(ChatPreferences.TryTokenLimit("", out var none) && none is null && ChatPreferences.TryTokenLimit(" 2 ", out var two) && two == 2,
            "chat tools: blank is unlimited and positive whole numbers are valid");
        preferences.MaxToolCalls = null; preferences.NormalizeTools();
        check(preferences.ToolChoice == "required", "chat tools: stop selection alone permits required");
        preferences.StopTools.Clear(); preferences.NormalizeTools();
        check(preferences.ToolChoice is null, "chat tools: removing both constraints downgrades required to automatic");

        // All 16 policy combinations against all 16 explicit tool-annotation combinations.
        for (var flags = 0; flags < 16; flags++)
        {
            var policy = new ChatToolPolicy { ReadOnlyHint = (flags & 1) != 0, IdempotentHint = (flags & 2) != 0,
                DestructiveHint = (flags & 4) != 0, OpenWorldHint = (flags & 8) != 0 };
            var matches = true;
            for (var annotations = 0; annotations < 16; annotations++)
            {
                var ro = (annotations & 1) != 0; var id = (annotations & 2) != 0;
                var destructive = (annotations & 4) != 0; var open = (annotations & 8) != 0;
                var tool = JsonSerializer.SerializeToElement(new { annotations = new { readOnlyHint = ro, idempotentHint = id, destructiveHint = destructive, openWorldHint = open } });
                var expected = !(policy.ReadOnlyHint && !ro) && (ro || !(policy.IdempotentHint && !id) && !(destructive && !policy.DestructiveHint))
                    && !(open && !policy.OpenWorldHint);
                matches &= policy.Allows(tool) == expected;
            }
            check(matches, "chat tools: browser annotation truth table policy " + flags);
        }
        check(new ChatToolPolicy().Allows(Definition("unknown")) && !new ChatToolPolicy { ReadOnlyHint = true }.Allows(Definition("unknown")),
            "chat tools: missing annotations use browser defaults");
        var readOnlyContradictory = Json("""{"annotations":{"readOnlyHint":true,"destructiveHint":true,"idempotentHint":false}}""");
        check(new ChatToolPolicy { ReadOnlyHint = true, IdempotentHint = true, DestructiveHint = false }.Allows(readOnlyContradictory),
            "chat tools: read-only tools bypass destructive and idempotency gates");

        var executions = 0;
        var snapshot = McpTurnSnapshot.Create(new[] { "one", "two" }.Select(id => new McpConnectedServer(new(id, id, "", "https://" + id + ".example/"),
            new(Json("{}"), Json("{}"), null, [Json("""{"name":"search","inputSchema":{"type":"object"},"annotations":{"openWorldHint":true}}""")]),
            (_, _, _, _, _) => { executions++; return Task.FromResult(Json("""{"content":[]}""")); })));
        var alias = snapshot.Tools[0].GetProperty("name").GetString()!;
        check(alias != "search" && snapshot.OriginalToolName(alias) == "search", "chat tools: MCP aliases retain original identity");
        var stop = new ChatPreferences { ToolChoice = "required", StopTools = [" SEARCH "] };
        check(stop.EffectiveToolChoice([User(), Assistant("output-available", alias)], snapshot) == "none", "chat tools: stop names match case-insensitively through MCP aliases");
        var previewPreferences = new ChatPreferences { ActivePlugins = [DesktopLocalTools.Conversations] };
        var preview = snapshot.SettingsTools(previewPreferences, hasSkills: true, hasPlugins: true);
        check(executions == 0 && snapshot.Tools.Count == 2 && preview.Count > 2 && snapshot.ConversationTools is null
            && preview.Any(option => option.Name == DesktopSkillTurn.ActivateTool) && preview.Any(option => option.Name == AgentPluginTools.ReadToolName),
            "chat tools: declaration preview includes draft plugins and skills without executing or registering live tools");
        previewPreferences.ToolAnnotations.OpenWorldHint = false;
        check(!snapshot.SettingsTools(previewPreferences).Any(option => option.Name == alias), "chat tools: preview applies draft policy");
        snapshot.ApplyPolicy(previewPreferences.ToolAnnotations);
        check(snapshot.Tools.Count == 0 && !snapshot.Contains(alias) && snapshot.Context.All(block => block.GetProperty("tools").GetArrayLength() == 0),
            "chat tools: policy removes declarations, executable routes, and system-context tool catalogs");
        try { await snapshot.CallAsync(alias, Json("{}"), "blocked", "en", default); throw new Exception("Blocked route executed"); }
        catch (InvalidOperationException) { check(executions == 0, "chat tools: blocked aliases cannot execute"); }

        var limited = new ChatPreferences { ToolChoice = "required", MaxToolCalls = 1 };
        foreach (var state in new[] { "output-available", "output-error", "approval-responded" })
            check(limited.EffectiveToolChoice([User(), Assistant(state)]) == "none", "chat tools: completed state consumes budget: " + state);
        foreach (var state in new[] { "input-streaming", "input-available", "approval-requested" })
            check(limited.EffectiveToolChoice([User(), Assistant(state)]) == "required", "chat tools: pending state does not consume budget: " + state);
        check(limited.EffectiveToolChoice([Assistant("output-available"), User()]) == "required", "chat tools: historical calls do not consume the next user-turn budget");
        check(new ChatPreferences { StopTools = ["other"] }.EffectiveToolChoice([Assistant("output-available")]) is null,
            "chat tools: unrelated completed tools do not trigger stops");
        var body = fresh.RequestBody("openai/model", "fixture", [User()], "openai");
        check(!body.ContainsKey("toolChoice") && !body.ContainsKey("maxToolCalls") && !body.ContainsKey("stopTools") && !body.ContainsKey("toolAnnotations"),
            "chat tools: automatic/unlimited and client-only policy fields omitted from request");
        preferences.ToolAnnotations.ReadOnlyHint = true;
        var rawSnapshot = McpTurnSnapshot.Empty; rawSnapshot.AddLocal(Definition("unknown"), (_, _) => throw new Exception("Never called"));
        body = preferences.RequestBody("openai/model", "fixture", [User()], "openai", rawSnapshot);
        check(body["tools"]!.AsArray().Count == 0 && body["providerMetadata"]!["openai"]!["tools"]!.AsArray().Count > 0,
            "chat tools: defensive request filtering never removes provider-native tools");

        await CheckAgentIsolationAsync(check);
        foreach (var byStop in new[] { false, true })
        foreach (var review in new[] { false, true }) await CheckContinuationAsync(check, byStop, review);
    }

    private static async Task CheckAgentIsolationAsync(Action<bool, string> check)
    {
        using var handler = new TestHandler(); using var http = new HttpClient(handler);
        await using var session = new DesktopSession(new TestHost(), new TestRuntime(), new());
        var client = new DesktopChatClient(session, http);
        var agent = DesktopAgent.Parse("""{"name":"Fixture","description":"Isolation fixture","instructions":"Remain unchanged","model":{"id":"openai/model"},"mcpClient":{"policy":{"openWorldHint":true,"future":7}},"futureField":42}""");
        var definition = agent.Definition.ToJsonString();
        var disruptive = new ChatPreferences { ToolChoice = "none", MaxToolCalls = 1, StopTools = ["search"],
            ToolAnnotations = new() { ReadOnlyHint = true, IdempotentHint = true, OpenWorldHint = false, DestructiveHint = false } };
        foreach (var local in new[] { false, true })
        {
            var messages = new List<UIMessage> { User(), Assistant("output-available") };
            await foreach (var _ in client.StreamAsync(ServiceKind.Agents, "Fixture", "agent", messages, default, new(), localAgent: local ? agent : null)) { }
            var before = handler.LastBody;
            await foreach (var _ in client.StreamAsync(ServiceKind.Agents, "Fixture", "agent", messages, default, disruptive, localAgent: local ? agent : null)) { }
            check(before == handler.LastBody && agent.Definition.ToJsonString() == definition,
                "chat tools: restrictive global settings cannot change " + (local ? "local" : "remote") + " agent requests or definitions");
            var wire = JsonNode.Parse(handler.LastBody!)!.AsObject();
            check(!wire.ContainsKey("toolChoice") && !wire.ContainsKey("maxToolCalls") && !wire.ContainsKey("tools"), "chat tools: no model-chat tool settings injected into agents");
        }
    }

    private static async Task CheckContinuationAsync(Action<bool, string> check, bool byStop, bool review)
    {
        var preferences = new ChatPreferences { ToolChoice = "required", MaxToolCalls = byStop ? null : 1, StopTools = byStop ? ["search"] : [] };
        var snapshot = McpTurnSnapshot.Empty; var executions = 0; var reviews = 0;
        foreach (var name in new[] { "search", "second" }) snapshot.AddLocal(Definition(name), (_, _) => { executions++; return Task.FromResult(Json("""{"content":[]}""")); });
        snapshot.ApplyPolicy(preferences.ToolAnnotations);
        using var handler = new TurnHandler(review); using var http = new HttpClient(handler);
        await using var session = new DesktopSession(new TestHost(), new TestRuntime(), new());
        var client = new DesktopChatClient(session, http);
        var output = new ConversationMessage { Status = "streaming", Message = new UIMessage { Id = "answer", Role = Role.assistant } };
        var conversation = new Conversation { Service = ServiceKind.Ai, Messages = [new() { Message = User() }, output] };
        var turnPreferences = preferences.Clone();
        await DesktopChatTurn.RunAsync(conversation, output,
            (messages, ct) => client.StreamAsync(ServiceKind.Ai, "openai/model", conversation.Id, messages, ct, turnPreferences, "openai", mcp: snapshot),
            (_, _) => { reviews++; return Task.FromResult(new ToolApprovalDecision(true)); }, (_, _) => Task.CompletedTask, snapshot, "en", default);
        var first = JsonNode.Parse(handler.Bodies[0])!; var next = JsonNode.Parse(handler.Bodies[1])!;
        check(handler.Bodies.Count == 2 && first["toolChoice"]!.GetValue<string>() == "required" && next["toolChoice"]!.GetValue<string>() == "none"
            && executions == 2 && reviews == (review ? 1 : 0) && output.Text == "Final answer" && output.Status == "complete",
            $"chat tools: {(byStop ? "stop tool" : "call limit")} completes parallel batch then final response; review={review}");
        var completed = output.Message.Parts.Where(PortableConversations.IsTool).ToArray();
        check(completed.Length == 2 && completed.All(part => DesktopToolApprovals.HasOutput(PortableConversations.Element(part))),
            "chat tools: final turn retains both tool results without reexecution");
        check(turnPreferences.EffectiveToolChoice([output.Message, User()], snapshot) == "required", "chat tools: next user turn resets continuation stop/limit");
    }

    private sealed class TurnHandler(bool review) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(ct));
            string[] events = Bodies.Count == 1
                ? ["""{"type":"tool-input-available","toolName":"search","toolCallId":"one","input":{}}""",
                    """{"type":"tool-input-available","toolName":"second","toolCallId":"two","input":{}}""",
                    review ? """{"type":"tool-approval-request","toolCallId":"one","approvalId":"review"}""" : """{"type":"start-step"}""",
                    """{"type":"finish"}"""]
                : ["""{"type":"text-delta","id":"final","delta":"Final answer"}""", """{"type":"finish"}"""];
            return new(HttpStatusCode.OK) { Content = new StringContent(string.Concat(events.Select(item => "data: " + item + "\n\n")) + "data: [DONE]\n\n", Encoding.UTF8, "text/event-stream") };
        }
    }
}
