using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using System.Xml.Linq;
using AIHappey.Desktop.Core;
using AIHappey.Vercel.Models;
using ModelContextProtocol.Protocol;
using Role = AIHappey.Vercel.Models.Role;

internal static class ElicitationRegressionTests
{
    internal const string Schema = """
    {"type":"object","properties":{
      "text":{"type":"string","title":"Name","description":"Your name","minLength":2,"maxLength":5,"default":"Ada"},
      "email":{"type":"string","format":"email"},"uri":{"type":"string","format":"uri"},
      "date":{"type":"string","format":"date"},"timestamp":{"type":"string","format":"date-time"},
      "number":{"type":"number","minimum":0.5,"maximum":4.5,"default":1.5},
      "integer":{"type":"integer","minimum":1,"maximum":10},"boolean":{"type":"boolean","default":false},
      "single":{"type":"string","enum":["a","b"],"default":"a"},
      "titled":{"type":"string","oneOf":[{"const":"a","title":"Alpha"},{"const":"b","title":"Beta"}]},
      "legacy":{"type":"string","enum":["a","b"],"enumNames":["Alpha","Beta"]},
      "multi":{"type":"array","items":{"type":"string","enum":["a","b","c"]},"minItems":1,"maxItems":2,"default":["a"]},
      "titledMulti":{"type":"array","items":{"anyOf":[{"const":"a","title":"Alpha"},{"const":"b","title":"Beta"}]},"minItems":1,"maxItems":2}
    },"required":["text","integer","boolean","titledMulti"]}
    """;
    private static JsonElement Json(string text) => JsonSerializer.Deserialize<JsonElement>(text);
    internal static ElicitRequestParams Request() => JsonSerializer.Deserialize<ElicitRequestParams>("{\"message\":\"Please complete the form\",\"requestedSchema\":" + Schema + "}")!;
    public static async Task RunAsync(Action<bool, string> check, string root)
    {
        using var english = typeof(ElicitationRegressionTests).Assembly.GetManifestResourceStream("Desktop.Resources.en")!;
        using var dutch = typeof(ElicitationRegressionTests).Assembly.GetManifestResourceStream("Desktop.Resources.nl")!;
        var en = XDocument.Load(english).Root!.Elements("data").ToDictionary(e => (string)e.Attribute("name")!, e => (string)e.Element("value")!);
        var nl = XDocument.Load(dutch).Root!.Elements("data").ToDictionary(e => (string)e.Attribute("name")!, e => (string)e.Element("value")!);
        var keys = en.Keys.Where(k => k.StartsWith("Elicitation") || k.StartsWith("SettingsMcp") || k.StartsWith("SettingsModelContext")).ToArray();
        check(keys.Length >= 37 && keys.All(k => !string.IsNullOrWhiteSpace(en[k]) && nl.TryGetValue(k, out var text) && !string.IsNullOrWhiteSpace(text)),
            "all Model context and elicitation resources have English and Dutch translations");
        var settings = JsonSerializer.Deserialize<DesktopSettings>("{}", JsonSerializerOptions.Web)!;
        check(settings.ModelContext.EnableFormElicitation && settings.ModelContext.EnableApps && settings.ModelContext.EnableSkills
            && settings.ModelContext.ResetTimeoutOnProgress && settings.ModelContext.ToolTimeoutMinutes == 5, "older settings receive browser-aligned Model context defaults");
        var nullSettings = JsonSerializer.Deserialize<DesktopSettings>("{\"modelContext\":null}", JsonSerializerOptions.Web)!;
        check(nullSettings.ModelContext.EnableFormElicitation, "null Model context preferences safely use defaults");
        settings.ModelContext = new() { EnableFormElicitation = false, EnableApps = false, EnableSkills = false, ResetTimeoutOnProgress = false, ToolTimeoutMinutes = 25 };
        await SettingsStore.SaveAsync(Path.Combine(root, "elicitation-settings"), settings);
        var loaded = await SettingsStore.LoadAsync(Path.Combine(root, "elicitation-settings"), new());
        check(!loaded.ModelContext.EnableFormElicitation && !loaded.ModelContext.EnableApps && !loaded.ModelContext.EnableSkills
            && !loaded.ModelContext.ResetTimeoutOnProgress && loaded.ModelContext.ToolTimeoutMinutes == 25, "all Model context preferences persist including disabled switches");
        var copy = loaded.ModelContext.Clone(); copy.EnableApps = true;
        check(!loaded.ModelContext.EnableApps, "preference clone isolates canceled edits");
        copy.ToolTimeoutMinutes = 0; check(copy.ToolTimeoutMinutes == 1, "timeout lower bound");
        copy.ToolTimeoutMinutes = int.MaxValue; check(copy.ToolTimeoutMinutes == 60, "timeout upper bound");

        var form = new DesktopElicitationForm(Request());
        check(form.Fields.Count == 13 && form.Fields.Single(f => f.Name == "text").Title == "Name"
            && form.Fields.Single(f => f.Name == "text").Description == "Your name", "all MCP schema types project with labels and descriptions");
        check(form.Values["boolean"].GetBoolean() == false && form.Values["multi"].GetArrayLength() == 1
            && form.Values["number"].GetDouble() == 1.5, "typed boolean numeric and multi-select defaults retained");
        check(form.Errors().Count == 2, "required missing integer and multi-select are rejected");
        form.Values["integer"] = Json("2"); form.Values["titledMulti"] = Json("[\"a\"]");
        var accepted = form.Accept();
        check(accepted.Action == "accept" && accepted.Content!["boolean"].ValueKind == JsonValueKind.False
            && !accepted.Content.ContainsKey("email"), "accept preserves false and omits unset optional fields");
        foreach (var (name, valid, invalid) in new[]
        {
            ("text", "\"😀😀\"", "\"a\""), ("number", "0.5", "4.6"), ("integer", "10", "1.1"),
            ("boolean", "false", "\"false\""), ("single", "\"a\"", "\"z\""), ("titled", "\"b\"", "\"z\""),
            ("legacy", "\"a\"", "\"z\""), ("multi", "[\"a\",\"b\"]", "[\"a\",\"b\",\"c\"]"),
            ("titledMulti", "[\"b\"]", "[]"), ("email", "\"ada@example.com\"", "\"bad email\""),
            ("uri", "\"mcp://example/resource\"", "\"relative\""), ("date", "\"2024-02-29\"", "\"2025-02-29\""),
            ("timestamp", "\"2026-10-09T12:30:00+02:00\"", "\"2026-10-09T12:30:00\"")
        })
        {
            var field = form.Fields.Single(f => f.Name == name);
            check(field.Validate(Json(valid)) is null && field.Validate(Json(invalid)) is not null, "MCP field validation: " + name);
        }
        check(form.Fields.Single(f => f.Name == "multi").Validate(Json("[\"a\",\"a\"]")) is not null
            && form.Fields.Single(f => f.Name == "multi").Validate(Json("[\"unknown\"]")) is not null, "multi-select rejects duplicates and unknown values");
        check(form.Fields.Single(f => f.Name == "legacy").Options[0].Title == "Alpha"
            && form.Fields.Single(f => f.Name == "titledMulti").Options[1].Title == "Beta", "legacy and current enum titles retained");
        foreach (var input in new[] { "{\"method\":\"other\",\"params\":{}}", "{\"method\":\"elicitation/create\",\"params\":{\"mode\":\"url\"}}" })
        {
            try { DesktopElicitationForm.ParseProviderRequest(Json(input)); throw new Exception("Expected invalid request"); }
            catch (InvalidOperationException) { check(true, "provider rejects non-form input request"); }
        }
        var malformed = Request(); malformed.RequestedSchema!.Required!.Add("missing");
        try { _ = new DesktopElicitationForm(malformed); throw new Exception("Expected invalid schema"); }
        catch (InvalidOperationException) { check(true, "required keys must exist in schema"); }

        foreach (var action in new[] { "accept", "decline", "cancel" })
        {
            var output = new ConversationMessage { Message = new() { Role = Role.assistant, Id = "provider" } };
            var input = new JsonObject { ["method"] = "elicitation/create", ["params"] = JsonSerializer.SerializeToNode(Request()) };
            output.Message.Parts.Add(PortableConversations.Part(new JsonObject { ["type"] = "tool-ai_input_required", ["toolCallId"] = action,
                ["state"] = "input-available", ["input"] = input }));
            var requests = 0; var executed = new HashSet<string>();
            DesktopElicitationHandler handler = (_, request, _) => { requests++; return Task.FromResult(action == "accept" ? form.Accept() : new ElicitResult { Action = action }); };
            var count = await DesktopMcpToolExecution.ExecutePendingAsync(output, McpTurnSnapshot.Empty, executed, "en", default, handler, executeMcp: false);
            var result = PortableConversations.Element(output.Message.Parts[0]);
            check(count == 1 && requests == 1 && result.GetProperty("state").GetString() == "output-available"
                && result.GetProperty("output").GetProperty("structuredContent").GetProperty("action").GetString() == action
                && result.GetProperty("output").GetProperty("content").GetArrayLength() == 0, "provider " + action + " uses browser-compatible result on Agents path");
            await DesktopMcpToolExecution.ExecutePendingAsync(output, McpTurnSnapshot.Empty, executed, "en", default, handler, executeMcp: false);
            check(requests == 1, "provider prompt is not executed twice: " + action);
        }
        using (var stopped = new CancellationTokenSource())
        using (var timeout = new McpToolTimeout(TimeSpan.FromMilliseconds(200), true, stopped.Token))
        {
            stopped.Cancel(); check(timeout.Token.IsCancellationRequested, "progress timer preserves stop cancellation");
            timeout.Report(default!); check(timeout.Token.IsCancellationRequested, "progress cannot resurrect a canceled call");
        }
        using (var timeout = new McpToolTimeout(TimeSpan.FromMilliseconds(300), true, default))
        {
            await Task.Delay(180); timeout.Report(default!); await Task.Delay(180);
            check(!timeout.Token.IsCancellationRequested, "SDK-correlated progress resets timeout");
            await Task.Delay(220); check(timeout.Token.IsCancellationRequested, "reset timer still expires without further progress");
        }
        using (var timeout = new McpToolTimeout(TimeSpan.FromMilliseconds(200), false, default))
        {
            await Task.Delay(100); timeout.Report(default!); await Task.Delay(160);
            check(timeout.Token.IsCancellationRequested, "progress does not reset timeout when disabled");
        }
        await CheckSdkAsync(check);
        await CheckProviderContinuationAsync(check);
    }

    private static async Task CheckProviderContinuationAsync(Action<bool, string> check)
    {
        var request = new JsonObject { ["method"] = "elicitation/create", ["params"] = JsonSerializer.SerializeToNode(Request()) };
        var tool = new JsonObject { ["type"] = "tool-ai_input_required", ["toolCallId"] = "current-input", ["state"] = "input-available", ["input"] = request };
        foreach (var service in new[] { ServiceKind.Ai, ServiceKind.Agents })
        {
            var historical = new ConversationMessage { Status = "complete", Message = new() { Role = Role.assistant, Id = "historical",
                Parts = [PortableConversations.Part(new JsonObject { ["type"] = "tool-ai_input_required", ["toolCallId"] = "historical-input", ["state"] = "input-available", ["input"] = request.DeepClone() })] } };
            var output = new ConversationMessage { Message = new() { Role = Role.assistant, Id = "current" } };
            var conversation = new Conversation { Service = service, Messages = [historical, output] };
            var rounds = 0; var prompts = 0; var saves = 0;
            await DesktopChatTurn.RunAsync(conversation, output, (messages, ct) =>
            {
                rounds++;
                if (rounds == 2) check(PortableConversations.Element(messages.Last().Parts[0]).GetProperty("output")
                    .GetProperty("structuredContent").GetProperty("action").GetString() == "decline", service + ": input response is replayed on the next inference round");
                return Events(rounds == 1 ? [tool.ToJsonString(), "{\"type\":\"finish\"}"]
                    : ["{\"type\":\"text-delta\",\"id\":\"answer\",\"delta\":\"Continued\"}", "{\"type\":\"finish\"}"], ct);
            }, (_, _) => throw new Exception("Elicitation must not become a tool-approval request"), (_, _) => { saves++; return Task.CompletedTask; },
                McpTurnSnapshot.Empty, "en", default, (_, _, _) => { prompts++; return Task.FromResult(new ElicitResult { Action = "decline" }); });
            check(rounds == 2 && prompts == 1 && saves > 0 && output.Text == "Continued" && output.Status == "complete",
                service + ": active provider elicitation persists response and continues without executing historical prompts");
        }
        var settings = new DesktopSettings { ModelContext = new() { EnableFormElicitation = false } };
        await using var session = new DesktopSession(new TestHost(), new RemoteRuntimeResolver(), settings);
        session.ElicitationHandler = (_, _, _) => throw new Exception("Disabled preference must never present a form");
        check((await session.ElicitAsync("provider", Request(), default)).Action == "decline", "disabled preference declines provider-originated elicitation");
        var canceled = new ConversationMessage { Message = new() { Role = Role.assistant, Id = "stop" } };
        var stoppedConversation = new Conversation { Service = ServiceKind.Ai, Messages = [canceled] };
        using var stop = new CancellationTokenSource(); var canceledRounds = 0;
        try
        {
            await DesktopChatTurn.RunAsync(stoppedConversation, canceled, (_, ct) => { canceledRounds++; return Events([tool.ToJsonString(), "{\"type\":\"finish\"}"], ct); },
                (_, _) => throw new Exception("Unexpected approval"), (_, _) => Task.CompletedTask, McpTurnSnapshot.Empty, "en", stop.Token,
                (_, _, ct) => { stop.Cancel(); return Task.FromCanceled<ElicitResult>(ct); });
            throw new Exception("Expected stopped turn");
        }
        catch (OperationCanceledException) { check(canceledRounds == 1 && canceled.Status == "stopped", "Stop during provider elicitation does not fabricate response or continue inference"); }
        check(DesktopMcpManager.SafeError(new InvalidOperationException("sanitized", new OperationCanceledException())) == DesktopResources.Get("McpTimedOut"),
            "wrapped tool timeout retains sanitized timeout message");
    }
    private static async IAsyncEnumerable<StreamEvent> Events(IEnumerable<string> payloads, [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var payload in payloads) { ct.ThrowIfCancellationRequested(); yield return StreamEvent.Parse(payload); await Task.Yield(); }
    }
    private sealed class TestHost : IDesktopHost
    {
        public string ProfileId => "elicitation-tests";
        public bool AllowLocal => false;
        public string AccountLabel => "test";
        public string HistoryIdentity => "test";
        public Task AuthenticateAsync(HttpRequestMessage request, ServiceKind service, CancellationToken ct) => Task.CompletedTask;
        public Task ManageAccountAsync(object xamlRoot, CancellationToken ct) => Task.CompletedTask;
    }

    private static async Task CheckSdkAsync(Action<bool, string> check)
    {
        var server = new DesktopMcpServer { Id = "elicitation", Name = "Test server", Url = "https://mcp-test.invalid/" };
        foreach (var action in new[] { "accept", "decline", "cancel" })
        foreach (var legacy in new[] { false, true })
        {
            var transport = new InputRequiredHttpHandler(check, legacy: legacy);
            var factory = new DesktopMcpClientFactory(new TestAuthentication(transport));
            var requests = 0;
            factory.Configure(() => new(), (_, request, _) =>
            {
                requests++; var form = new DesktopElicitationForm(request);
                return Task.FromResult(action == "accept" ? form.Accept() : new ElicitResult { Action = action });
            });
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            await using var connection = await factory.ConnectAsync(server, cancellation.Token);
            var result = await connection.CallAsync("test", Json("{}"), "call-" + action, "nl", cancellation.Token);
            check(requests == 1 && transport.Calls == (legacy ? 1 : 2) && transport.ResponseAction == action
                && result.GetProperty("content").GetArrayLength() == 0 && !result.TryGetProperty("requestState", out _),
                "SDK " + (legacy ? "legacy" : "stateless") + " elicitation resolves " + action + " without exposing intermediate input state");
        }
        foreach (var urlMode in new[] { false, true })
        {
            var preferences = new ModelContextPreferences { EnableFormElicitation = urlMode };
            var transport = new InputRequiredHttpHandler(check, expectForm: urlMode, urlMode: urlMode);
            var factory = new DesktopMcpClientFactory(new TestAuthentication(transport));
            var prompts = 0;
            factory.Configure(() => preferences, (_, _, _) => { prompts++; return Task.FromResult(new ElicitResult { Action = "accept" }); });
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            await using var connection = await factory.ConnectAsync(server, cancellation.Token);
            try { await connection.CallAsync("test", Json("{}"), "safety", "nl", cancellation.Token); }
            catch (Exception error) when (error is ModelContextProtocol.McpException or InvalidOperationException)
            { /* A request for an unadvertised capability is rejected by the SDK without opening UI. */ }
            check(prompts == 0, urlMode ? "URL elicitation never opens a form" : "disabled elicitation never opens a form or advertises capability");
        }
        foreach (var legacy in new[] { false, true })
        {
            var transport = new InputRequiredHttpHandler(check, legacy: legacy);
            var factory = new DesktopMcpClientFactory(new TestAuthentication(transport));
            var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            factory.Configure(() => new(), async (_, _, ct) => { requested.SetResult(); await Task.Delay(Timeout.Infinite, ct); return new(); });
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            await using var connection = await factory.ConnectAsync(server, cancellation.Token);
            var call = connection.CallAsync("test", Json("{}"), "stop", "nl", cancellation.Token);
            await requested.Task.WaitAsync(cancellation.Token); cancellation.Cancel();
            try { await call; throw new Exception("Expected call cancellation"); }
            catch (OperationCanceledException) { check(transport.Calls == 1, (legacy ? "legacy" : "stateless") + " Stop cancels form callback without retrying tool"); }
        }
    }
    private sealed class TestAuthentication(HttpMessageHandler handler) : IDesktopMcpAuthentication
    {
        public Task<HttpMessageHandler> ConfigureAsync(DesktopMcpServer server, ModelContextProtocol.Client.HttpClientTransportOptions options, CancellationToken ct) => Task.FromResult(handler);
    }
    private sealed class InputRequiredHttpHandler(Action<bool, string> check, bool legacy = false, bool expectForm = true, bool urlMode = false) : HttpMessageHandler
    {
        public int Calls;
        public string? ResponseAction;
        private SseStream? stream;
        private JsonNode? toolId;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method != HttpMethod.Post) return new(HttpStatusCode.MethodNotAllowed);
            var message = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject();
            var method = (string?)message["method"];
            if (message["id"] is null) return new(HttpStatusCode.Accepted);
            if (method is null)
            {
                ResponseAction = (string?)message["result"]?["action"];
                stream!.Emit(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = toolId!.DeepClone(),
                    ["result"] = JsonNode.Parse("""{"content":[],"_meta":{"secret":"client-only"}}""") });
                stream.Complete(); return new(HttpStatusCode.Accepted);
            }
            JsonNode result;
            if (method == "server/discover")
            {
                if (legacy) return new(HttpStatusCode.OK) { Content = new StringContent(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = message["id"]!.DeepClone(),
                    ["error"] = new JsonObject { ["code"] = -32601, ["message"] = "Not supported" } }.ToJsonString(), Encoding.UTF8, "application/json") };
                result = JsonNode.Parse("""{"supportedVersions":["2026-07-28"],"capabilities":{"tools":{}},"_meta":{"io.modelcontextprotocol/serverInfo":{"name":"Test","version":"1"}}} """)!;
            }
            else if (method == "initialize")
            {
                check(message["params"]?["capabilities"]?["elicitation"]?["form"] is JsonObject
                    && message["params"]?["capabilities"]?["elicitation"]?["url"] is null, "legacy initialize advertises form-only elicitation");
                result = new JsonObject { ["protocolVersion"] = message["params"]!["protocolVersion"]!.DeepClone(),
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() }, ["serverInfo"] = new JsonObject { ["name"] = "Legacy", ["version"] = "1" } };
            }
            else if (method == "tools/call")
            {
                Calls++;
                var parameters = message["params"]!.AsObject();
                check((string?)parameters["_meta"]?["chat/locale"] == "nl"
                    && parameters["_meta"]?["progressToken"] is not null, "SDK tool call retains locale and SDK-correlated progress metadata");
                if (!legacy) check((parameters["_meta"]?["io.modelcontextprotocol/clientCapabilities"]?["elicitation"]?["form"] is JsonObject) == expectForm
                    && parameters["_meta"]?["io.modelcontextprotocol/clientCapabilities"]?["elicitation"]?["url"] is null, "SDK advertises only enabled form capability per stateless request");
                var elicitation = JsonSerializer.Deserialize<ElicitRequestParams>("""{"message":"Confirm","requestedSchema":{"type":"object","properties":{"confirmed":{"type":"boolean","default":false}},"required":["confirmed"]}}""")!;
                if (urlMode) { elicitation.Mode = "url"; elicitation.Url = "https://untrusted.invalid/"; elicitation.ElicitationId = "url-test"; }
                if (legacy)
                {
                    toolId = message["id"]!.DeepClone(); stream = new();
                    stream.Emit(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = "legacy-user", ["method"] = "elicitation/create", ["params"] = JsonSerializer.SerializeToNode(elicitation) });
                    var content = new StreamContent(stream); content.Headers.ContentType = new("text/event-stream");
                    return new(HttpStatusCode.OK) { Content = content };
                }
                if (Calls == 1)
                    result = JsonSerializer.SerializeToNode(new InputRequiredResult
                    {
                        RequestState = "opaque-state-DO-NOT-PARSE", InputRequests = new Dictionary<string, InputRequest>
                        {
                            ["user"] = InputRequest.ForElicitation(elicitation)
                        }
                    })!;
                else
                {
                    check((string?)parameters["requestState"] == "opaque-state-DO-NOT-PARSE", "SDK echoes opaque stateless request state unchanged");
                    ResponseAction = (string?)parameters["inputResponses"]?["user"]?["action"];
                    result = JsonNode.Parse("""{"content":[],"_meta":{"secret":"client-only"}}""")!;
                }
            }
            else throw new Exception("Unexpected SDK request: " + method);
            var response = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = message["id"]!.DeepClone(), ["result"] = result };
            return new(HttpStatusCode.OK) { Content = new StringContent(response.ToJsonString(), Encoding.UTF8, "application/json") };
        }
    }
    private sealed class SseStream : Stream
    {
        private readonly Channel<byte[]> chunks = Channel.CreateUnbounded<byte[]>();
        private byte[]? current;
        private int offset;
        public void Emit(JsonNode message) => chunks.Writer.TryWrite(Encoding.UTF8.GetBytes("event: message\ndata: " + message.ToJsonString() + "\n\n"));
        public void Complete() => chunks.Writer.TryComplete();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (current is null || offset == current.Length)
            {
                if (!await chunks.Reader.WaitToReadAsync(ct)) return 0;
                current = await chunks.Reader.ReadAsync(ct); offset = 0;
            }
            var count = Math.Min(buffer.Length, current.Length - offset);
            current.AsMemory(offset, count).CopyTo(buffer); offset += count; return count;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, default).GetAwaiter().GetResult();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
