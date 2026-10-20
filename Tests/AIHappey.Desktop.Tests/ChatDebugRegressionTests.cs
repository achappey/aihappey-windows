using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Desktop.Core;
using AIHappey.Vercel.Models;

internal static class ChatDebugRegressionTests
{
    public static async Task RunAsync(Action<bool, string> check, string root)
    {
        using var handler = new DebugHandler();
        using var http = new HttpClient(handler);
        var session = new DesktopSession(new DebugHost(), new TestRuntime(), new());
        var client = new DesktopChatClient(session, http);
        foreach (var service in new[] { ServiceKind.Ai, ServiceKind.Agents })
        {
            foreach (var enabled in new[] { false, true, false })
            {
                session.Settings.Chat.ApiDebugEnabled = !enabled;
                var preferences = new ChatPreferences { ApiDebugEnabled = enabled };
                await foreach (var _ in client.StreamAsync(service, "fixture/model", "chat", [], default, preferences)) { }
                check(handler.DebugHeader == (enabled ? "true" : null), $"chat debug: {service} request snapshot overrides host header, enabled={enabled}");
                check(!handler.Body!.Contains("apiDebugEnabled") && !handler.Body.Contains("X-AIHappey-Debug"), "chat debug: preference stays out of inference body");
            }
        }
        session.Settings.Chat.ApiDebugEnabled = true;
        await foreach (var _ in client.StreamAsync(ServiceKind.Ai, "fixture/model", "chat", [], default)) { }
        check(handler.DebugHeader == "true", "chat debug: requests use saved preference when no snapshot is supplied");
        await client.ListAsync(ServiceKind.Ai, default);
        check(handler.Path == "/v1/models" && handler.DebugHeader is null, "chat debug: catalog discovery is not opted in by Chat preference");

        var buffer = new TransientConversationData();
        var scope = buffer.SetScope("account", "chat");
        var output = new ConversationMessage { Message = new() { Id = "assistant", Role = Role.assistant }, Status = "streaming" };
        var conversation = new Conversation { Id = "chat", Messages = [output] };
        var debug = StreamEvent.Parse("""{"type":"data-aihappey-debug","id":"req:1","transient":true,"data":{"detail":"memory-only-debug"}}""");
        var otherTransient = StreamEvent.Parse("""{"type":"data-provider-status","transient":true,"data":"memory-only-status"}""");
        var persistent = StreamEvent.Parse("""{"type":"data-result","id":"result","data":{"value":42}}""");
        async IAsyncEnumerable<StreamEvent> Stream(List<UIMessage> _, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            yield return debug;
            yield return otherTransient;
            yield return persistent;
            yield return StreamEvent.Parse("""{"type":"text-delta","id":"answer","delta":"Normal answer"}""");
            yield return StreamEvent.Parse("""{"type":"finish"}""");
        }
        await DesktopChatTurn.RunAsync(conversation, output, Stream, (_, _) => Task.FromResult(new ToolApprovalDecision(true)),
            (_, _) => Task.CompletedTask, McpTurnSnapshot.Empty, "en", default,
            onTransientData: item => buffer.Add(scope, item, output));
        check(output.Status == "complete" && output.Text == "Normal answer" && buffer.Items.Count == 2,
            "chat debug: real turn path captures transient data without changing normal assistant answer");
        var projected = ConversationActivityProjection.Project(conversation.Messages, buffer.Items);
        check(projected.Data.Select(x => x.Part.Type).SequenceEqual(["data-provider-status", "data-aihappey-debug", "data-result"]),
            "chat debug: activity merges transient and persistent data newest first");
        check(output.Message.Parts.All(p => p.Type is not "data-aihappey-debug" and not "data-provider-status")
            && output.Message.Parts.Any(p => p.Type == "data-result"), "chat debug: persistent data stays in messages; transient data never does");
        var exported = PortableConversations.Write(conversation);
        var request = session.Settings.Chat.RequestBody("fixture/model", "chat", [output.Message], null).ToJsonString();
        check(!exported.Contains("memory-only") && !request.Contains("memory-only") && exported.Contains("data-result"),
            "chat debug: exports and replay bodies exclude transient diagnostics");
        var store = new HistoryStore(Path.Combine(root, "debug-history"));
        var partition = HistoryStore.Partition("debug-account");
        await store.SaveAsync(partition, conversation);
        var saved = (await store.ListAsync(partition)).Single();
        check(!PortableConversations.Write(saved).Contains("memory-only") && ConversationActivityProjection.Project(saved.Messages).Data.Count == 1,
            "chat debug: saved and reopened history contains only persistent activity");
        check(buffer.SetScope("account", "chat") == scope && buffer.Items.Count == 2, "chat debug: same conversation retains data across turns and refreshes");
        check(!buffer.Add(scope, persistent, output), "chat debug: persistent events cannot be duplicated in transient buffer");
        buffer.SetScope("account", "other-chat");
        check(buffer.Items.Count == 0 && !buffer.Add(scope, debug, output), "chat debug: switching chat clears data and rejects stale stream callbacks");
        var returnedScope = buffer.SetScope("account", "chat");
        check(!buffer.Add(scope, debug, output) && buffer.Add(returnedScope, debug, output), "chat debug: reopening chat cannot revive an earlier stream scope");
        buffer.SetScope("other-account", "chat");
        check(buffer.Items.Count == 0 && !buffer.Add(returnedScope, debug, output), "chat debug: account switch isolates identical conversation IDs");

        // Missing UI observers must not accidentally persist transient content either.
        var unobserved = new ConversationMessage { Message = new() { Id = "unobserved", Role = Role.assistant }, Status = "streaming" };
        await DesktopChatTurn.RunAsync(new() { Messages = [unobserved] }, unobserved, Stream,
            (_, _) => Task.FromResult(new ToolApprovalDecision(true)), (_, _) => Task.CompletedTask, McpTurnSnapshot.Empty, "en", default);
        check(!JsonSerializer.Serialize(unobserved.Message, PortableConversations.Json).Contains("memory-only"), "chat debug: transient data is never persisted without an observer");
    }

    private sealed class DebugHost : IDesktopHost
    {
        public string ProfileId => "debug";
        public bool AllowLocal => true;
        public string AccountLabel => "debug";
        public string HistoryIdentity => "debug";
        public Task AuthenticateAsync(HttpRequestMessage request, ServiceKind service, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/api/chat", StringComparison.Ordinal)) request.Headers.Add("X-AIHappey-Debug", "host-value");
            return Task.CompletedTask;
        }
        public Task ManageAccountAsync(object root, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class DebugHandler : HttpMessageHandler
    {
        public string? DebugHeader, Body, Path;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            DebugHeader = request.Headers.TryGetValues("X-AIHappey-Debug", out var values) ? string.Join(",", values) : null;
            Path = request.RequestUri!.AbsolutePath;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return new(HttpStatusCode.OK) { Content = request.Method == HttpMethod.Get
                ? new StringContent("""{"data":[]}""", Encoding.UTF8, "application/json")
                : new StringContent("data: {\"type\":\"finish\"}\n\ndata: [DONE]\n\n", Encoding.UTF8, "text/event-stream") };
        }
    }
}
