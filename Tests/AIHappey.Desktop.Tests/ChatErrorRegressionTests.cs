using System.Net;
using System.Text.Json;
using AIHappey.Desktop.Core;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Mapping;
using AIHappey.Unified.Models;
using AIHappey.Responses.Streaming;
using AIHappey.Responses.Mapping;

internal static class ChatErrorRegressionTests
{
    public static async Task RunAsync(Action<bool, string> check, string root)
    {
        const string exact = "Unsupported parameter: reasoning.effort is not supported with this model.";
        UIMessagePart gatewayPart = new ErrorUIPart { ErrorText = exact };
        var gatewayWire = JsonSerializer.Serialize(gatewayPart, JsonSerializerOptions.Web);
        check(GatewayErrors.Message(JsonSerializer.Deserialize<JsonElement>(gatewayWire)) == exact,
            "chat errors: real gateway polymorphic ErrorUIPart serialization retains error text");
        var mappedError = new AIEventEnvelope { Type = "error", Data = new AIErrorEventData { ErrorText = exact } };
        var gatewayMappedPart = mappedError.ToUIMessagePart("openai").Single();
        check(gatewayMappedPart is ErrorUIPart { ErrorText: exact },
            "chat errors: real unified-to-Vercel error mapper retains provider message");
        var nestedResponseError = JsonSerializer.Deserialize<ResponseStreamPart>(JsonSerializer.Serialize(new
            { type = "error", error = new { message = exact, code = "invalid_request_error" } }))!;
        var nestedEvents = nestedResponseError.ToUnifiedStreamEvent("openai").ToArray();
        check(nestedEvents.Length == 1 && nestedEvents[0].Event.Data is AIErrorEventData { ErrorText: exact },
            "chat errors: nested OpenAI Responses error survives real stream converter and mapper");
        var failedResponse = JsonSerializer.Deserialize<ResponseStreamPart>(JsonSerializer.Serialize(new { type = "response.failed",
            response = new { id = "response", status = "failed", model = "gpt-6-luna", output = Array.Empty<object>(), error = new { message = exact, code = "invalid_request_error" } } }))!;
        var failedEvents = failedResponse.ToUnifiedStreamEvent("openai").ToArray();
        check(failedEvents.Length == 1 && failedEvents[0].Event.Data is AIErrorEventData { ErrorText: exact },
            "chat errors: response.failed preserves response.error.message instead of silently dropping failure");
        var message = new ConversationMessage { Message = new UIMessage { Id = "assistant", Role = Role.assistant }, Status = "streaming" };
        var assembler = new MessageAssembler(message);
        try { assembler.Apply(StreamEvent.Parse(JsonSerializer.Serialize(new { type = "error", errorText = exact }))); throw new Exception("Expected stream failure"); }
        catch (GatewayException error) { check(error.Message == exact && message.ErrorMessage == exact && message.Status == "failed", "chat errors: exact SSE service error preserved instead of generic generation text"); }
        check(TranscriptProjection.Project(message).Count == 0, "chat errors: empty failed assistant output renders no duplicate error card or Busy label");
        message.Message.Parts.Add(new TextUIPart { Text = "Partial answer" });
        check(TranscriptProjection.Project(message).Count == 1 && message.Text == "Partial answer", "chat errors: retain partial answer without duplicate error block");
        var conversation = new Conversation { Id = "failed", Messages = [message] };
        var restored = PortableConversations.Read(PortableConversations.Write(conversation));
        check(restored.Messages[0].ErrorMessage == exact && restored.Messages[0].Status == "failed" && !PortableConversations.CanReplay(restored.Messages[0]),
            "chat errors: persisted error remains visible and failed assistant output is not replayed");
        var fallback = new ConversationMessage { Message = new UIMessage { Id = "fallback", Role = Role.assistant } };
        try { new MessageAssembler(fallback).Apply(StreamEvent.Parse("{\"type\":\"error\"}")); } catch (GatewayException) { }
        check(fallback.ErrorMessage!.Contains("without an error message") && fallback.ErrorMessage.Contains("type (String)"),
            "chat errors: genuinely missing error text reports event shape instead of hiding it behind a generic error");
        check(GatewayErrors.Message(JsonSerializer.Deserialize<JsonElement>("{\"data\":{\"Error\":{\"Message\":\"actual nested message\"}}}")) == "actual nested message",
            "chat errors: nested envelopes and message casing supported");
        check(GatewayErrors.Display("ordinary exact error") == "ordinary exact error"
            && !GatewayErrors.Display("Authorization: Bearer private-token api_key=private-key sk-12345678901234567890")!.Contains("private"),
            "chat errors: preserve normal error text but redact credentials");
        using var jsonResponse = new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent(JsonSerializer.Serialize(new { error = new { message = exact } }), System.Text.Encoding.UTF8, "application/json") };
        try { await GatewayErrors.CheckResponseAsync(jsonResponse, default); throw new Exception("Expected HTTP failure"); }
        catch (GatewayException error) { check(error.Message == "HTTP 400: " + exact, "chat errors: HTTP JSON error message displayed with status"); }
        using var html = new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("<html>private diagnostics</html>") };
        try { await GatewayErrors.CheckResponseAsync(html, default); throw new Exception("Expected HTTP failure"); }
        catch (GatewayException error) { check(!error.Message.Contains("private"), "chat errors: arbitrary non-error response dumps stay out of UI"); }
        foreach (var (json, field) in new[] { ("{\"role\":\"user\",\"parts\":[]}", "id"), ("{\"id\":\"x\",\"parts\":[]}", "role"), ("{\"id\":\"x\",\"role\":\"user\"}", "parts") })
        {
            try { JsonSerializer.Deserialize<UIMessage>(json, PortableConversations.Json); throw new Exception("Expected invalid message"); }
            catch (JsonException error) { check(error.Message.Contains(field), "chat errors: invalid portable message identifies " + field); }
        }
        var store = new HistoryStore(Path.Combine(root, "error-history")); var partition = HistoryStore.Partition("error-tests");
        await store.SaveAsync(partition, conversation);
        var dir = Path.Combine(root, "error-history", partition); await File.WriteAllTextAsync(Path.Combine(dir, "invalid.json"), "{\"id\":\"invalid\",\"messages\":[{}]}");
        check((await store.ListAsync(partition)).Single().Id == conversation.Id && File.Exists(Path.Combine(dir, "invalid.json")),
            "chat errors: invalid history document isolated without deleting it or failing valid chat");

        foreach (var enabled in new[] { false, true })
        {
            var preferences = new ChatPreferences { ActivePlugins = enabled ? DesktopLocalTools.Plugins.Select(p => p.Id).ToList() : [] };
            var snapshot = McpTurnSnapshot.Empty;
            if (enabled)
                foreach (var plugin in DesktopLocalTools.Plugins) DesktopLocalTools.Register(snapshot, plugin.Id, (_, _, _) => Task.FromResult(DesktopLocalTools.Result(new { })));
            var user = new UIMessage { Id = "user", Role = Role.user, Parts = [new TextUIPart { Text = "test" }] };
            var context = new DesktopSystemContextComposer().Compose(new(new(), new(), null, "nl", true, DateTimeOffset.UtcNow) { Mcp = snapshot });
            var body = preferences.RequestBody("openai/gpt-6-luna", "chat", DesktopSystemContext.RequestMessages(ServiceKind.Ai, [user], context), "openai", snapshot);
            // Use the real backend deserializer, not the desktop's portable converters or a fake accepting anything.
            var backend = body.Deserialize<ChatRequest>(JsonSerializerOptions.Web)!;
            check(backend.Messages.Count == 2 && backend.Messages[0].Role == Role.system && backend.Messages[1].Role == Role.user
                && backend.Messages.All(m => !string.IsNullOrWhiteSpace(m.Id) && m.Parts.All(p => p.Type == "text"))
                && backend.Tools!.Count == (enabled ? 13 : 0), "chat errors: actual backend request contract accepts plain model chat, plugins " + (enabled ? "on" : "off"));
            var desktopRoundTrip = body["messages"]!.Deserialize<List<UIMessage>>(PortableConversations.Json)!;
            check(desktopRoundTrip.Count == 2, "chat errors: outgoing messages are valid portable UI messages, plugins " + (enabled ? "on" : "off"));
        }
        var requests = 0; var failed = new ConversationMessage { Message = new UIMessage { Id = "failed-turn", Role = Role.assistant }, Status = "streaming" };
        var active = new Conversation { Messages = [new() { Message = new UIMessage { Id = "user", Role = Role.user, Parts = [new TextUIPart { Text = "test" }] } }, failed] };
        async IAsyncEnumerable<StreamEvent> Stream(List<UIMessage> _, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield(); requests++; yield return StreamEvent.Parse(JsonSerializer.Serialize(new { type = "error", errorText = exact }));
        }
        try { await DesktopChatTurn.RunAsync(active, failed, Stream, (_, _) => Task.FromResult(new ToolApprovalDecision(true)), (_, _) => Task.CompletedTask,
            McpTurnSnapshot.Empty, "nl", default); throw new Exception("Expected turn failure"); }
        catch (GatewayException error) { check(requests == 1 && error.Message == exact && failed.ErrorMessage == exact, "chat errors: turn runner propagates actual error and never retries inference"); }
    }
}
