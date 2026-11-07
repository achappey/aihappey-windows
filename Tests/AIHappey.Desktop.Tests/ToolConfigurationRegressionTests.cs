using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Desktop.Core;
using AIHappey.Vercel.Models;

internal static class ToolConfigurationRegressionTests
{
    private static JsonElement Json(string value) => JsonSerializer.Deserialize<JsonElement>(value);
    private static ChatToolSource Source(JsonElement tool)
    {
        if (!tool.TryGetProperty("source", out var source)) return ChatToolSource.Local;
        return new(CatalogProjection.Text(source, "kind") ?? "local", CatalogProjection.Text(source, "id") ?? "local",
            CatalogProjection.Text(source, "name") ?? "Local tools", CatalogProjection.Text(source, "description"),
            ChatToolRequestOptions.Read(source.TryGetProperty("requestOptions", out var options) ? options : null),
            source.TryGetProperty("namespace", out var ns) && ns.ValueKind == JsonValueKind.True);
    }
    private static Dictionary<string, ChatToolRequestOptions> Config(JsonElement value) => value.EnumerateObject()
        .ToDictionary(p => p.Name, p => ChatToolRequestOptions.Read(p.Value), StringComparer.Ordinal);

    public static async Task RunAsync(Action<bool, string> check, string root)
    {
        using var stream = typeof(ToolConfigurationRegressionTests).Assembly.GetManifestResourceStream("Desktop.ToolConfigurationFixture")!;
        using var fixture = await JsonDocument.ParseAsync(stream);
        foreach (var sample in fixture.RootElement.GetProperty("shapes").EnumerateArray())
        {
            var tools = sample.GetProperty("tools").EnumerateArray().ToArray();
            var sources = tools.ToDictionary(t => t.GetProperty("name").GetString()!, Source);
            var preferences = new ChatPreferences { ToolRequestConfig = Config(sample.GetProperty("config")), UseToolNamespaces = sample.GetProperty("namespaces").GetBoolean() };
            var original = sample.GetRawText();
            var result = ChatToolConfiguration.Shape(tools, preferences, name => sources[name]);
            check(JsonNode.DeepEquals(result, JsonNode.Parse(sample.GetProperty("expected").GetRawText())), "tool configuration: browser payload parity " + sample.GetProperty("name").GetString());
            check(sample.GetRawText() == original, "tool configuration: shaping never mutates input declarations or defaults");
        }
        var editTools = fixture.RootElement.GetProperty("editTools").EnumerateArray()
            .Select(t => new ChatToolOption(t.GetProperty("name").GetString()!, t.GetProperty("name").GetString()!, Source(t))).ToArray();
        foreach (var edit in fixture.RootElement.GetProperty("edits").EnumerateArray())
        {
            var preferences = new ChatPreferences { ToolRequestConfig = Config(edit.GetProperty("before")) };
            var op = edit.GetProperty("op"); var name = op.GetProperty("name").GetString()!;
            var bulk = name == "all"; var selected = bulk ? editTools : editTools.Where(t => t.Name == name).ToArray();
            if (op.GetProperty("kind").GetString() == "caller")
            {
                var values = bulk ? ChatToolConfiguration.Callers.Where(c => editTools.All(t => ChatToolConfiguration.Effective(preferences, t).AllowedCallers?.Contains(c) == true)).ToList()
                    : ChatToolConfiguration.Effective(preferences, selected[0]).AllowedCallers?.ToList() ?? [];
                var caller = op.GetProperty("value").GetString()!;
                if (values.Contains(caller)) values.RemoveAll(c => c == caller); else values.Add(caller);
                foreach (var tool in selected) ChatToolConfiguration.SetCallers(preferences, tool, values, bulk);
            }
            else foreach (var tool in selected) ChatToolConfiguration.SetDeferred(preferences, tool, op.GetProperty("value").GetBoolean(), bulk);
            check(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(preferences.ToolRequestConfig), JsonNode.Parse(edit.GetProperty("expected").GetRawText())),
                "tool configuration: browser edit parity " + op.GetRawText());
        }

        var session = new DesktopSettings { Chat = new() { UseToolNamespaces = true,
            ToolRequestConfig = new() { ["lookup"] = new() { AllowedCallers = ["programmatic"], DeferLoading = true } } } };
        var clone = session.Clone(); clone.Chat.ToolRequestConfig["lookup"].AllowedCallers!.Clear(); clone.Chat.UseToolNamespaces = false;
        check(session.Chat.UseToolNamespaces && session.Chat.ToolRequestConfig["lookup"].AllowedCallers!.SequenceEqual(["programmatic"]), "tool configuration: nested drafts and turn snapshots deeply isolate session state");
        var defaults = session.Chat.InferenceDefaults();
        check(!defaults.UseToolNamespaces && defaults.ToolRequestConfig.Count == 0, "tool configuration: restore inference defaults clears all request-only overrides");
        var serialized = JsonSerializer.Serialize(session, JsonSerializerOptions.Web);
        check(!serialized.Contains("toolRequestConfig") && !serialized.Contains("useToolNamespaces"), "tool configuration: request-only settings never serialize into user settings/history");
        var directory = Path.Combine(root, "tool-configuration"); await SettingsStore.SaveAsync(directory, session);
        var restored = await SettingsStore.LoadAsync(directory, new());
        check(!restored.Chat.UseToolNamespaces && restored.Chat.ToolRequestConfig.Count == 0, "tool configuration: app restart clears request configuration");
        var injected = JsonSerializer.Deserialize<ChatPreferences>("""{"useToolNamespaces":true,"toolRequestConfig":{"lookup":{"defer_loading":true}}}""", JsonSerializerOptions.Web)!;
        check(!injected.UseToolNamespaces && injected.ToolRequestConfig.Count == 0, "tool configuration: disk properties cannot reactivate session-only settings");

        await CheckWireAsync(check);
        foreach (var language in new[] { "en", "nl" })
        {
            using var resource = typeof(ToolConfigurationRegressionTests).Assembly.GetManifestResourceStream("Desktop.Resources." + language)!;
            var names = System.Xml.Linq.XDocument.Load(resource).Root!.Elements("data").Select(d => (string)d.Attribute("name")!).ToHashSet();
            foreach (var key in new[] { "title", "allowedCallers", "deferLoading", "allTools", "useNamespaces", "namespace", "empty", "select" })
                check(names.Contains("ChatForm_toolConfiguration_" + key), "tool configuration: native " + language + " label " + key);
        }
    }

    private static async Task CheckWireAsync(Action<bool, string> check)
    {
        var executions = 0;
        var snapshot = McpTurnSnapshot.Create([new McpConnectedServer(new("fixture", "Plugin service", "", "https://fixture.example/")
            { PluginId = "fixture-plugin", PluginSettings = Json("""{"allowed_callers":["programmatic"],"defer_loading":true,"namespace":true}""") },
            new(Json("""{"name":"Different discovery name"}"""), Json("{}"), null, [Json("""{"name":"lookup","description":"Lookup","inputSchema":{"type":"object"}}""")]),
            (_, _, _, _, _) => { executions++; return Task.FromResult(Json("""{"content":[]}""")); })]);
        var local = DesktopLocalTools.Definition("local_conversations_list_all");
        snapshot.AddLocal(local, (_, _) => Task.FromResult(Json("""{"content":[]}""")));
        var source = snapshot.SourceForTool("lookup");
        check(source.Name == "Plugin service" && source.Kind == "mcp" && source.Namespace && source.RequestOptions?.DeferLoading == true,
            "tool configuration: MCP/plugin snapshots retain source identity and inherited request defaults");
        check(snapshot.SettingsTools(new()).Single(t => t.Name == "lookup").Source == source && executions == 0,
            "tool configuration: declaration-only settings preview shares source defaults without running tools");
        var preferences = new ChatPreferences { ProviderMetadata = new() { ["openai"] = JsonNode.Parse("""{"future":{"keep":true},"tools":[{"type":"web_search"},{"type":"namespace","name":"plugin_service","tools":[]},{"type":"namespace","name":"unrelated","tools":[]}]}""")!.AsObject() } };
        var originalMetadata = preferences.ProviderMetadata["openai"].ToJsonString();
        var user = new UIMessage { Id = "user", Role = Role.user, Parts = [new TextUIPart { Text = "Hello" }] };
        var body = preferences.RequestBody("openai/model", "fixture", [user], "openai", snapshot);
        check(body["tools"]!.AsArray().Count == 1 && body["tools"]![0]!["name"]!.ToString() == "local_conversations_list_all", "tool configuration: selective namespace leaves ordinary client tools on the established path");
        var providerTools = body["providerMetadata"]!["openai"]!["tools"]!.AsArray();
        var ns = providerTools.Single(t => t?["name"]?.ToString() == "plugin_service")!;
        check(ns["tools"]![0]!["allowed_callers"]![0]!.ToString() == "programmatic" && ns["tools"]![0]!["defer_loading"]!.GetValue<bool>()
            && providerTools.Count == 3 && body["providerMetadata"]!["openai"]!["future"]!["keep"]!.GetValue<bool>(),
            "tool configuration: native namespaces merge by name without losing other provider tools or unknown options");
        check(preferences.ProviderMetadata["openai"].ToJsonString() == originalMetadata && snapshot.Tools[0].TryGetProperty("requestOptions", out _), "tool configuration: provider preferences and legacy plugin/realtime snapshot catalogs remain unmodified");
        await snapshot.CallAsync("lookup", Json("{}"), "call", "en", default);
        check(executions == 1 && snapshot.Contains("lookup"), "tool configuration: namespace shaping preserves execution aliases and routes");
        preferences.UseToolNamespaces = true;
        preferences.ToolRequestConfig["lookup"] = new() { AllowedCallers = ["direct"], DeferLoading = false };
        using var handler = new CaptureHandler(); using var http = new HttpClient(handler);
        await using var session = new DesktopSession(new TestHost(), new TestRuntime(), new());
        var client = new DesktopChatClient(session, http);
        var turn = preferences.Clone();
        for (var step = 0; step < 2; step++)
            await foreach (var _ in client.StreamAsync(ServiceKind.Ai, "openai/model", "fixture", [user], default, turn, "openai", mcp: snapshot)) { }
        var first = JsonNode.Parse(handler.Bodies[0])!; var second = JsonNode.Parse(handler.Bodies[1])!;
        check(handler.Bodies.Count == 2 && JsonNode.DeepEquals(first["tools"], second["tools"])
            && JsonNode.DeepEquals(first["providerMetadata"], second["providerMetadata"]), "tool configuration: initial and continuation transports shape identically from the frozen turn preferences");
        var wire = JsonNode.Parse(handler.Bodies[0])!;
        var function = wire["providerMetadata"]!["openai"]!["tools"]!.AsArray().Single(t => t?["name"]?.ToString() == "plugin_service")!["tools"]![0]!;
        check(wire["tools"]!.AsArray().Count == 0 && function["allowed_callers"]![0]!.ToString() == "direct" && function["defer_loading"] is null,
            "tool configuration: actual HTTP request contains exact raw caller/deferred properties, not settings wrappers");
        foreach (var localAgent in new[] { false, true })
        {
            var agent = localAgent ? DesktopAgent.Parse("""{"name":"Fixture","description":"Isolation fixture","instructions":"Remain unchanged","model":{"id":"openai/model"}}""") : null;
            foreach (var options in new[] { new ChatPreferences(), preferences })
                await foreach (var _ in client.StreamAsync(ServiceKind.Agents, "Fixture", "fixture", [user], default, options, localAgent: agent)) { }
            check(handler.Bodies[^2] == handler.Bodies[^1], "tool configuration: unchanged " + (localAgent ? "local" : "remote") + " agent request");
        }
        var realtime = new DesktopRealtimeTokenClient(client, http);
        foreach (var options in new[] { new ChatPreferences(), preferences })
            await realtime.GetAsync(new JsonObject { ["model"] = "openai/realtime", ["tools"] = new JsonArray() }, options, "openai", default);
        check(handler.Bodies[^2] == handler.Bodies[^1], "tool configuration: realtime token requests ignore model-chat configuration");
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(ct));
            return new(HttpStatusCode.OK) { Content = request.RequestUri!.AbsolutePath.Contains("realtime")
                ? new StringContent("{\"value\":\"fixture\",\"expires_at\":4102444800}", Encoding.UTF8, "application/json")
                : new StringContent("data: {\"type\":\"finish\"}\n\ndata: [DONE]\n\n", Encoding.UTF8, "text/event-stream") };
        }
    }
}
