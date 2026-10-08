using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Desktop.Core;
using AIHappey.Vercel.Models;

internal static class ToolApprovalRegressionTests
{
    private static JsonElement Json(string value) => JsonSerializer.Deserialize<JsonElement>(value);
    private static ConversationMessage Reply() => new() { Status = "streaming", Message = new UIMessage { Id = "reply", Role = Role.assistant } };
    private static Conversation Conversation(ServiceKind service, ConversationMessage reply) => new() { Service = service, Target = "target",
        Messages = [new() { Message = new UIMessage { Id = "user", Role = Role.user, Parts = [new TextUIPart { Text = "test" }] } }, reply] };
    private static string[] ApprovalStream(bool reverse = false) => reverse
        ? [Input, Approval, Output, Finish] : [Input, Output, Approval, Finish];
    private const string Input = """{"type":"tool-input-available","toolCallId":"call","toolName":"search","title":"Friendly title","input":{"q":"test"},"providerExecuted":false,"providerMetadata":{"test":{"keep":true}}}""";
    private const string Output = """{"type":"tool-output-available","toolCallId":"call","output":{"content":[{"type":"text","text":"result"}],"structuredContent":{"value":42}},"providerExecuted":false}""";
    private const string Approval = """{"type":"tool-approval-request","toolCallId":"call","approvalId":"approval"}""";
    private const string Finish = """{"type":"finish","messageMetadata":{"usage":{"totalTokens":17}}}""";

    public static async Task RunAsync(Action<bool, string> check, string root)
    {
        foreach (var reverse in new[] { false, true })
        {
            var reply = Reply(); var assembler = new MessageAssembler(reply);
            foreach (var payload in ApprovalStream(reverse)) assembler.Apply(StreamEvent.Parse(payload));
            var pending = DesktopToolApprovals.Pending(reply.Message).Single();
            check(assembler.Finished && assembler.ApprovalRequired && reply.Status == "approval required" && DesktopToolApprovals.HasOutput(pending.Part), "approval/output ordering preserves pending state: " + reverse);
            check(pending.ToolName == "search" && pending.Title == "Friendly title" && pending.Part.TryGetProperty("callProviderMetadata", out _), "approval uses canonical name and retains title/provider fields");
            check(DesktopToolApprovals.Respond(reply.Message, pending, new(true, " search ")) && !DesktopToolApprovals.Respond(reply.Message, pending, new(false)), "approval response is idempotent");
            reply.Status = "complete";
            var raw = PortableConversations.Element(reply.Message.Parts.Single());
            check(raw.GetProperty("approval").GetProperty("reason").GetString() == "search" && raw.GetProperty("output").GetProperty("structuredContent").GetProperty("value").GetInt32() == 42, "response preserves original output and trims reason");
            check(PortableConversations.CanReplay(reply), "completed approved output replays");
            var restored = PortableConversations.Read(PortableConversations.Write(Conversation(ServiceKind.Ai, reply)));
            check(PortableConversations.CanReplay(restored.Messages.Last()), "approval history round trips");
            assembler.Continue(); assembler.Apply(StreamEvent.Parse(Approval));
            check(!assembler.ApprovalRequired, "duplicate approval chunk does not reset an answered decision");
        }

        var settings = new DesktopSettings { AllowedToolList = ["search", "search"] };
        var policy = new DesktopToolApprovalPolicy();
        var named = new PendingToolApproval("p", "c", "search", "Friendly title", Json("{}"));
        check(policy.Automatic(named, settings)?.Reason == "search" && settings.AllowedToolList.Count == 1, "per-tool allowlist deduplicates exact names");
        check(policy.Automatic(named with { ToolName = "Search" }, settings) is null && policy.Automatic(named with { ToolName = "Friendly title" }, settings) is null, "allowlist does not match case variants or titles");
        policy.ApproveAll = true;
        check(policy.Automatic(named, settings)?.Reason == "BRRR", "allow-all matches browser reason");
        await SettingsStore.SaveAsync(Path.Combine(root, "approval-settings"), settings);
        var loaded = await SettingsStore.LoadAsync(Path.Combine(root, "approval-settings"), new());
        check(loaded.AllowedToolList.SequenceEqual(new[] { "search" }) && !new DesktopToolApprovalPolicy().ApproveAll, "per-tool rules persist but allow-all does not");
        loaded.AllowedToolList.Clear(); policy.ApproveAll = false;
        await SettingsStore.SaveAsync(Path.Combine(root, "approval-settings"), loaded);
        check(policy.Automatic(named, loaded) is null && (await SettingsStore.LoadAsync(Path.Combine(root, "approval-settings"), new())).AllowedToolList.Count == 0, "revocation persists and applies to the next approval");
        check(JsonSerializer.Deserialize<DesktopSettings>("{\"allowedToolList\":null}", JsonSerializerOptions.Web)!.AllowedToolList.Count == 0, "null allowlist is safe");

        foreach (var service in new[] { ServiceKind.Ai, ServiceKind.Agents })
        foreach (var decision in new[] { new ToolApprovalDecision(true), new(false, "not trusted"), new(true, "search"), new(true, "BRRR") })
        {
            using var handler = new ApprovalHandler(); using var http = new HttpClient(handler);
            var session = new DesktopSession(new Host(), new RemoteRuntimeResolver(), RemoteSettings());
            var client = new DesktopChatClient(session, http); var reply = Reply(); var conversation = Conversation(service, reply);
            var approvals = 0; var saved = false;
            await DesktopChatTurn.RunAsync(conversation, reply,
                (messages, token) =>
                {
                    if (handler.Bodies.Count > 0) check(saved, "approval decision saved before continuation");
                    return client.StreamAsync(service, "target", conversation.Id, messages, token);
                }, (pending, token) => { approvals++; return Task.FromResult(decision); },
                (force, token) => { saved |= force && DesktopToolApprovals.Pending(reply.Message).Count == 0; return Task.CompletedTask; },
                McpTurnSnapshot.Empty, "en", CancellationToken.None);
            using var body = JsonDocument.Parse(handler.Bodies.Last());
            var parts = body.RootElement.GetProperty("messages").EnumerateArray().Single(m => m.GetProperty("role").GetString() == "assistant").GetProperty("parts");
            var tool = parts.EnumerateArray().Single(p => p.GetProperty("type").GetString() == "tool-search");
            check(handler.Bodies.Count == 2 && approvals == 1 && reply.Text == "continued" && reply.Status == "complete", $"{service} approval round trip continues once: {decision}");
            check(tool.GetProperty("state").GetString() == "approval-responded" && tool.GetProperty("approval").GetProperty("approved").GetBoolean() == decision.Approved
                && tool.GetProperty("approval").GetProperty("id").GetString() == "approval" && tool.TryGetProperty("output", out _), "continuation wire decision and existing output preserved");
            check(decision.Reason is null ? !tool.GetProperty("approval").TryGetProperty("reason", out _) : tool.GetProperty("approval").GetProperty("reason").GetString() == decision.Reason, "continuation wire reason matches browser");
            check(PortableConversations.CanReplay(reply), "final approved/denied assistant history replays");
            await session.DisposeAsync();
        }

        var multi = Reply(); var multiChat = Conversation(ServiceKind.Agents, multi); var rounds = 0; var reviews = new List<string>();
        await DesktopChatTurn.RunAsync(multiChat, multi, (_, ct) => Events(++rounds == 1
            ? [Input, Output, Approval,
                """{"type":"dynamic-tool","toolName":"other","toolCallId":"other","state":"approval-requested","input":{},"output":{},"approval":{"id":"p2"},"futureField":17}""", Finish]
            : ["""{"type":"text-delta","id":"text","delta":"done"}""", Finish], ct),
            (pending, _) => { reviews.Add(pending.ToolName); return Task.FromResult(new ToolApprovalDecision(pending.ToolName == "search", "reviewed")); },
            (_, _) => Task.CompletedTask, McpTurnSnapshot.Empty, "en", CancellationToken.None);
        check(reviews.SequenceEqual(new[] { "search", "other" }) && rounds == 2 && PortableConversations.Element(multi.Message.Parts[1]).GetProperty("futureField").GetInt32() == 17, "multiple named/dynamic approvals reviewed serially before one continuation");

        using var canceled = new CancellationTokenSource();
        var stopped = Reply(); var stoppedChat = Conversation(ServiceKind.Agents, stopped); var cancelRounds = 0;
        try
        {
            await DesktopChatTurn.RunAsync(stoppedChat, stopped, (_, ct) => { cancelRounds++; return Events(ApprovalStream(), ct); },
                (_, ct) => { canceled.Cancel(); return Task.FromCanceled<ToolApprovalDecision>(ct); }, (_, _) => Task.CompletedTask,
                McpTurnSnapshot.Empty, "en", canceled.Token);
            throw new Exception("Expected cancellation");
        }
        catch (OperationCanceledException) { check(stopped.Status == "approval required" && cancelRounds == 1 && DesktopToolApprovals.Pending(stopped.Message).Count == 1 && !PortableConversations.CanReplay(stopped), "cancellation leaves approval unresolved without continuing or fabricating denial"); }

        var interrupted = Reply(); var interruptRounds = 0;
        await DesktopChatTurn.RunAsync(Conversation(ServiceKind.Agents, interrupted), interrupted,
            (_, ct) => { interruptRounds++; return Events([Input, Output, Approval], ct); },
            (_, _) => throw new Exception("Must not approve incomplete stream"), (_, _) => Task.CompletedTask, McpTurnSnapshot.Empty, "en", CancellationToken.None);
        check(interrupted.Status == "approval required" && interruptRounds == 1, "incomplete stream is not automatically approved or retried");

        var bounded = Reply(); var loopRounds = 0;
        try
        {
            await DesktopChatTurn.RunAsync(Conversation(ServiceKind.Agents, bounded), bounded,
                (_, ct) => { loopRounds++; return Events([$$$"""{"type":"tool-search","toolCallId":"loop{{{loopRounds}}}","state":"approval-requested","output":{},"approval":{"id":"loop{{{loopRounds}}}"}}""", Finish], ct); },
                (_, _) => Task.FromResult(new ToolApprovalDecision(true)), (_, _) => Task.CompletedTask, McpTurnSnapshot.Empty, "en", CancellationToken.None);
            throw new Exception("Expected loop limit");
        }
        catch (GatewayException) { check(loopRounds == DesktopChatTurn.MaxRounds, "automatic approval loop is bounded"); }

        await CheckMcpAsync(check);
    }

    private static async Task CheckMcpAsync(Action<bool, string> check)
    {
        var executions = 0;
        var snapshot = McpTurnSnapshot.Create(new[] { new McpConnectedServer(new("server", "server", "", "https://mcp.example/"),
            new(Json("{}"), Json("{}"), null, [Json("""{"name":"search","inputSchema":{"type":"object"}}""")]),
            (name, input, call, locale, ct) => { executions++; return Task.FromResult(Json("""{"content":[],"structuredContent":{"ok":true}}""")); }) });
        foreach (var withOutput in new[] { false, true })
        foreach (var approved in new[] { false, true })
        {
            var reply = Reply(); var assembler = new MessageAssembler(reply);
            foreach (var item in withOutput ? ApprovalStream() : new[] { Input, Approval, Finish }) assembler.Apply(StreamEvent.Parse(item));
            DesktopToolApprovals.Respond(reply.Message, DesktopToolApprovals.Pending(reply.Message).Single(), new(approved));
            var before = executions; var executed = new HashSet<string>();
            var calls = await DesktopMcpToolExecution.ExecutePendingAsync(reply, snapshot, executed, "en", CancellationToken.None);
            check(calls == (!withOutput && approved ? 1 : 0) && executions - before == calls, "MCP executes only approved calls without existing output: " + withOutput + "/" + approved);
            check(await DesktopMcpToolExecution.ExecutePendingAsync(reply, snapshot, executed, "en", CancellationToken.None) == 0, "MCP existing/denied results are never rerun");
        }
    }

    private static async IAsyncEnumerable<StreamEvent> Events(IEnumerable<string> payloads, [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var payload in payloads) { ct.ThrowIfCancellationRequested(); yield return StreamEvent.Parse(payload); await Task.Yield(); }
    }
    private static DesktopSettings RemoteSettings() => new() { Ai = new() { Location = RuntimeLocation.Remote, RemoteUrl = "https://example.com/ai/" }, Agents = new() { Location = RuntimeLocation.Remote, RemoteUrl = "https://example.com/agents/" } };
    private sealed class Host : IDesktopHost
    {
        public string ProfileId => "approval-tests"; public bool AllowLocal => false; public string AccountLabel => "test"; public string HistoryIdentity => "test";
        public Task AuthenticateAsync(HttpRequestMessage request, ServiceKind service, CancellationToken ct) => Task.CompletedTask;
        public Task ManageAccountAsync(object root, CancellationToken ct) => Task.CompletedTask;
    }
    private sealed class ApprovalHandler : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(ct));
            var events = Bodies.Count == 1 ? ApprovalStream() : ["""{"type":"text-delta","id":"answer","delta":"continued"}""", Finish];
            return new(HttpStatusCode.OK) { Content = new StringContent(string.Join("", events.Select(e => "data: " + e + "\n\n")) + "data: [DONE]\n\n", Encoding.UTF8, "text/event-stream") };
        }
    }
}
