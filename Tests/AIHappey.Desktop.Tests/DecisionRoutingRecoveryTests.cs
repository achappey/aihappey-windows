using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Desktop.Core;
using AIHappey.Vercel.Models;

internal static class DecisionRoutingRecoveryTests
{
    private const string LimitMessage = "Decision request failed (400 Bad Request): {\"error\":{\"message\":\"Decision input exceeds the token limit.\",\"type\":\"invalid_request_error\",\"param\":null,\"code\":null}}";
    private static UIMessage Message(Role role, string text) => new()
    { Id = Guid.NewGuid().ToString("N"), Role = role, Parts = [new TextUIPart { Text = text }], Metadata = new() };
    private static List<UIMessage> History()
    {
        var messages = new List<UIMessage> { Message(Role.user, new('a', 80)), Message(Role.assistant, new('b', 80)),
            Message(Role.user, new('c', 80)), Message(Role.assistant, new('d', 80)), Message(Role.user, new('e', 80)) };
        messages[1].Metadata!["usage"] = new Dictionary<string, object> { ["totalTokens"] = 999999 };
        messages[3].Metadata!["usage"] = new Dictionary<string, object> { ["totalTokens"] = 1000 };
        return messages;
    }
    private static DecisionRequest Request(IReadOnlyList<UIMessage> messages) => DecisionRouting.Request("openai/decision", "Choose carefully", messages,
        new[] { new JsonObject { ["id"] = "p/first" }, new JsonObject { ["id"] = "q/second" } });
    private static HttpResponseMessage Limit() => new(HttpStatusCode.BadRequest)
    { Content = new StringContent(LimitMessage, Encoding.UTF8, "text/plain") };
    private static HttpResponseMessage Success() => Json(HttpStatusCode.OK, """{"answers":{"favorite":{"type":"choice","choice":"q/second"}}}""");
    private static HttpResponseMessage Json(HttpStatusCode status, string text) => new(status)
    { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    private static JsonArray Conversation(DecisionInput state) => JsonNode.Parse(state.Value.GetRawText())!["conversation"]!.AsArray();

    public static async Task RunAsync(Action<bool, string> check)
    {
        foreach (var message in new[] { "HTTP 400: " + LimitMessage, "HTTP 413: context_length_exceeded", "HTTP 422: prompt is too long", "HTTP 400: maximum context length is 600" })
            check(DecisionRouting.IsInputLimitError(new GatewayException(message)), "routing recovery: explicit input limit recognized: " + message);
        foreach (var error in new Exception[] { new HttpRequestException("Network failed"), new OperationCanceledException("HTTP 400: context_length_exceeded"),
            new GatewayException("HTTP 400: invalid_request_error"), new GatewayException("HTTP 400: output token limit exceeded"),
            new GatewayException("HTTP 400: maximum context length must be a positive integer"),
            new GatewayException("HTTP 401: context_length_exceeded"), new GatewayException("HTTP 503: context_length_exceeded") })
            check(!DecisionRouting.IsInputLimitError(error), "routing recovery: unrelated errors never retry: " + error.Message);

        var history = History();
        check(DecisionRouting.ConversationTokens(history) == 1000, "routing recovery: latest usage only, not cumulative turns");
        foreach (var metadata in new Dictionary<string, object>[] {
            new() { ["totalTokens"] = 1000 }, new() { ["usage"] = new { total_tokens = 1000 } },
            new() { ["usage"] = new { inputTokens = 900, outputTokens = 100 } }, new() { ["promptTokens"] = 900, ["completionTokens"] = 100 },
            new() { ["usage"] = new { input_tokens = 1000, output_tokens = 0 } }, new() { ["usage"] = new { prompt_tokens = 900, completion_tokens = 100 } },
            new() { ["usage"] = new { totalTokens = 1000, inputTokens = 9, outputTokens = 1 } },
            new() { ["usage"] = JsonSerializer.SerializeToElement(new { inputTokens = 900, outputTokens = 100 }) },
            new() { ["usage"] = new JsonObject { ["inputTokens"] = 900, ["outputTokens"] = 100 } }
        })
        {
            // Deserialize anonymous fixtures into the same JSON values used by restored messages.
            history[3].Metadata!.Clear();
            foreach (var field in JsonSerializer.Deserialize<Dictionary<string, object>>(JsonSerializer.Serialize(metadata))!)
                history[3].Metadata![field.Key] = field.Value;
            check(DecisionRouting.ConversationTokens(history) == 1000, "routing recovery: usage aliases and representations");
        }
        history[3].Metadata!.Clear();
        history[3].Metadata!["usage"] = new JsonObject { ["inputTokens"] = 900, ["outputTokens"] = 100 };
        check(DecisionRouting.ConversationTokens(history) == 1000, "routing recovery: in-memory JSON-node usage");
        foreach (var usage in new Dictionary<string, object>[] { new() { ["totalTokens"] = 0 }, new() { ["totalTokens"] = "1000" },
            new() { ["totalTokens"] = double.NaN }, new() { ["totalTokens"] = double.PositiveInfinity }, new() { ["inputTokens"] = 1000 },
            new() { ["inputTokens"] = -1, ["outputTokens"] = 1001 }, new() { ["inputTokens"] = double.MaxValue, ["outputTokens"] = double.MaxValue } })
        {
            history[3].Metadata!.Clear(); history[3].Metadata!["usage"] = usage;
            check(DecisionRouting.ConversationTokens(history) is null, "routing recovery: invalid/partial usage is unknown, not coerced or replaced by older usage");
        }

        history = History();
        var full = DecisionRouting.Context(history)["conversation"]!.AsArray();
        var contexts = DecisionRouting.RecoveryContexts(history, 600);
        check(contexts.Count == 2 && JsonNode.DeepEquals(Conversation(contexts[0]), new JsonArray(full.Skip(3).Select(n => n!.DeepClone()).ToArray()))
            && JsonNode.DeepEquals(Conversation(contexts[1]), new JsonArray(full[4]!.DeepClone())), "routing recovery: browser-equivalent 80% whole-message suffix and last-user fallback");
        history[0].Parts[0] = new TextUIPart { Text = string.Concat(Enumerable.Repeat("huge old message ", 1000)) };
        check(Conversation(DecisionRouting.RecoveryContexts(history, 600)[0]).Count == 4, "routing recovery: weight content, not message count");
        history[4].Parts[0] = new TextUIPart { Text = string.Concat(Enumerable.Repeat("huge latest message ", 1000)) };
        check(DecisionRouting.RecoveryContexts(history, 600).Count == 1
            && Conversation(DecisionRouting.RecoveryContexts(history, 600)[0])[0]!["parts"]![0]!["text"]!.GetValue<string>() == ((TextUIPart)history[4].Parts[0]).Text,
            "routing recovery: never split or omit latest user message even if it cannot fit");
        foreach (var capacity in new double?[] { null, 0, -1, double.NaN, double.PositiveInfinity, 1, 100000 })
        {
            contexts = DecisionRouting.RecoveryContexts(History(), capacity);
            check(contexts.Count == 1 && Conversation(contexts[0]).Count == 1, "routing recovery: unknown capacity, tiny suffix or non-reduction skips intermediate retry");
        }
        check(DecisionRouting.RecoveryContexts(new[] { Message(Role.user, "only") }, 600).Count == 0
            && DecisionRouting.RecoveryContexts(new[] { Message(Role.assistant, "no user") }, 600).Count == 0, "routing recovery: no duplicate single-message request or missing-user fallback");
        var attachmentOnly = History(); attachmentOnly[4].Parts.Clear();
        attachmentOnly[4].Parts.Add(new FileUIPart { MediaType = "image/png", Filename = "chart.png", Url = "data:private" });
        var attachmentContext = Conversation(DecisionRouting.RecoveryContexts(attachmentOnly, null)[0]);
        check(attachmentContext.Count == 1 && attachmentContext[0]!["parts"]![0]!["filename"]!.GetValue<string>() == "chart.png"
            && !attachmentContext.ToJsonString().Contains("data:private"), "routing recovery: last-user-only preserves attachment descriptors without their payloads");

        await using var session = new DesktopSession(new TestHost(), new TestRuntime(), new());
        foreach (var failures in new[] { 0, 1, 2, 3 })
        {
            using var handler = new Handler(attempt => attempt <= failures ? Limit() : Success());
            using var http = new HttpClient(handler);
            var client = new DesktopDecisionClient(new DesktopChatClient(session, http), http);
            history = History();
            history[4].Parts.Add(new FileUIPart { MediaType = "image/png", Filename = "chart.png", Url = "data:private" });
            var original = JsonSerializer.Serialize(history, PortableConversations.Json);
            var body = Request(history); var originalBody = JsonSerializer.Serialize(body, PortableConversations.Json);
            string? winner = null; GatewayException? failure = null;
            try { winner = await client.DecideChatModelAsync(body, history, 600, () => true, default); }
            catch (GatewayException error) { failure = error; }
            check(handler.Requests.Select(r => r.GetProperty("state").GetProperty("conversation").GetArrayLength())
                .SequenceEqual(new[] { 5, 2, 1 }.Take(Math.Min(3, failures + 1))), "routing recovery: exact bounded request sequence after " + failures + " failures");
            check(failures == 3 ? winner is null && failure?.Message == "HTTP 400: " + LimitMessage : winner == "q/second" && failure is null,
                "routing recovery: only a valid decision returns a winner; final error is preserved");
            check(JsonSerializer.Serialize(history, PortableConversations.Json) == original && JsonSerializer.Serialize(body, PortableConversations.Json) == originalBody,
                "routing recovery: original conversation, attachment and decision request remain unchanged");
            check(handler.Requests.All(r => r.GetProperty("model").GetString() == "openai/decision"
                && r.GetProperty("questions").GetRawText() == handler.Requests[0].GetProperty("questions").GetRawText()
                && !r.GetRawText().Contains("data:private")) && handler.Auth.All(auth => auth),
                "routing recovery: model, instructions, candidates, sanitation and host authentication persist across retries");
        }
        foreach (var missing in new[] { "capacity", "usage", "single", "agent-or-primitive" })
        {
            using var handler = new Handler(attempt => attempt == 1 ? Limit() : Success());
            using var http = new HttpClient(handler);
            var client = new DesktopDecisionClient(new DesktopChatClient(session, http), http);
            history = missing == "single" ? [Message(Role.user, "only")] : History();
            if (missing == "usage") foreach (var message in history) message.Metadata!.Clear();
            try
            {
                if (missing == "agent-or-primitive") await client.DecideAsync(Request(history), () => true, default);
                else await client.DecideChatModelAsync(Request(history), history, missing == "capacity" ? null : 600, () => true, default);
                check(missing is "capacity" or "usage", "routing recovery: expected last-user-only success");
            }
            catch (GatewayException) { check(missing is "single" or "agent-or-primitive", "routing recovery: expected single-shot failure"); }
            check(handler.Requests.Count == (missing is "capacity" or "usage" ? 2 : 1), "routing recovery: skip unavailable/redundant or non-chat retries: " + missing);
        }
        foreach (var precedingLimits in new[] { 0, 1 })
            foreach (var kind in new[] { "network", "auth", "invalid-request", "output-limit", "malformed", "unoffered", "refusal" })
            {
                using var handler = new Handler(attempt => attempt <= precedingLimits ? Limit() : kind switch
                {
                    "network" => throw new HttpRequestException("Network failed"),
                    "auth" => Json(HttpStatusCode.Unauthorized, """{"error":{"message":"unauthorized"}}"""),
                    "invalid-request" => Json(HttpStatusCode.BadRequest, """{"error":{"message":"invalid_request_error"}}"""),
                    "output-limit" => Json(HttpStatusCode.BadRequest, """{"error":{"message":"output token limit exceeded"}}"""),
                    "malformed" => Json(HttpStatusCode.OK, "not json"),
                    "unoffered" => Json(HttpStatusCode.OK, """{"answers":{"favorite":{"type":"choice","choice":"outside"}}}"""),
                    _ => Json(HttpStatusCode.OK, """{"answers":{"favorite":{"type":"refusal","reason":"No"}}}""")
                });
                using var http = new HttpClient(handler);
                var client = new DesktopDecisionClient(new DesktopChatClient(session, http), http);
                var dispatched = false;
                try { await client.DecideChatModelAsync(Request(History()), History(), 600, () => true, default); dispatched = true; }
                catch (Exception error) when (error is GatewayException or HttpRequestException) { }
                check(!dispatched && handler.Requests.Count == precedingLimits + 1, "routing recovery: " + kind + " never dispatches or triggers another context retry");
            }
        foreach (var stopAt in new[] { 1, 2 })
            foreach (var cancel in new[] { false, true })
            {
                using var cancellation = new CancellationTokenSource(); var current = true;
                using var handler = new Handler(attempt =>
                {
                    if (attempt == stopAt) { if (cancel) cancellation.Cancel(); else current = false; }
                    return Limit();
                });
                using var http = new HttpClient(handler);
                var client = new DesktopDecisionClient(new DesktopChatClient(session, http), http);
                var dispatched = false;
                try { await client.DecideChatModelAsync(Request(History()), History(), 600, () => current, cancellation.Token); dispatched = true; }
                catch (OperationCanceledException) { }
                check(!dispatched && handler.Requests.Count == stopAt, "routing recovery: cancellation/stale context stops before more requests or dispatch");
            }
    }

    private sealed class Handler(Func<int, HttpResponseMessage> reply) : HttpMessageHandler
    {
        public List<JsonElement> Requests { get; } = [];
        public List<bool> Auth { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (request.RequestUri!.AbsolutePath != "/api/decisions") throw new InvalidOperationException("Only the decision endpoint may be called.");
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Requests.Add(document.RootElement.Clone()); Auth.Add(request.Headers.Contains("X-Test-Key"));
            return reply(Requests.Count);
        }
    }
}
