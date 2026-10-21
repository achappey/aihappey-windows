using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using AIHappey.Desktop.Core;
using AIHappey.Vercel.Models;

internal static class ToolOutputContentRegressionTests
{
    private static JsonElement Json(string value) => JsonSerializer.Deserialize<JsonElement>(value);
    private static bool Defaults(ToolOutputContentSettings settings) => settings.Text && !settings.Images && !settings.Audio && !settings.Video && !settings.Documents;
    private static JsonObject Part(JsonNode? output) => new()
    {
        ["type"] = "tool-fixture", ["toolCallId"] = "fixture", ["state"] = "output-available", ["providerExecuted"] = false, ["output"] = output
    };
    private static List<UIMessage> Messages(JsonObject part) => [new() { Id = "assistant", Role = Role.assistant, Parts = [PortableConversations.Part(part)] }];
    private static JsonObject Request(JsonObject part, ToolOutputContentSettings settings) => new ChatPreferences { ToolOutputContentSettings = settings }.RequestBody("p/model", "chat", Messages(part), "p");
    private static JsonArray Content(JsonObject request, int part = 0) => request["messages"]!.AsArray().OfType<JsonObject>().Last()["parts"]![part]!["output"]!["content"]!.AsArray();
    private static void Enable(ToolOutputContentSettings settings, string category)
    {
        switch (category)
        {
            case "Text": settings.Text = true; break;
            case "Image": settings.Images = true; break;
            case "Audio": settings.Audio = true; break;
            case "Video": settings.Video = true; break;
            case "Document": settings.Documents = true; break;
        }
    }

    public static async Task RunAsync(Action<bool, string> check, string root)
    {
        await CheckPreferencesAsync(check, root);
        CheckMapping(check);
        await CheckExecutionAndTransportAsync(check, root);
        foreach (var language in new[] { "en", "nl" })
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Desktop.Resources." + language)!;
            var resources = XDocument.Load(stream).Root!.Elements("data").ToDictionary(e => e.Attribute("name")!.Value, e => e.Element("value")!.Value);
            check(resources["SettingsToolOutputContent"].Length > 0 && !resources.ContainsKey("ToolOutputImages") && !resources.ContainsKey("ToolOutputImagesHint"), language + ": tool output card replaces obsolete image resources");
            foreach (var category in new[] { "Text", "Images", "Audio", "Video", "Documents" })
                foreach (var property in new[] { "Header", "OnContent", "OffContent", "[using:Microsoft.UI.Xaml.Automation]AutomationProperties.Name" })
                    check(resources["SettingsToolOutput" + category + "." + property].Length > 0, language + ": tool output localized " + category + " " + property);
        }
    }

    private static async Task CheckPreferencesAsync(Action<bool, string> check, string root)
    {
        check(Defaults(new ChatPreferences().ToolOutputContentSettings), "tool output: browser defaults send text only");
        foreach (var raw in new[] { "{}", "{\"sendToolOutputImages\":true}", "{\"sendToolOutputImages\":false}", "{\"toolOutputContentSettings\":null}" })
            check(Defaults(JsonSerializer.Deserialize<ChatPreferences>(raw, JsonSerializerOptions.Web)!.ToolOutputContentSettings), "tool output: missing/null new settings discard obsolete image preference: " + raw);
        var preferences = JsonSerializer.Deserialize<ChatPreferences>("""{"sendToolOutputImages":true,"toolOutputContentSettings":{"text":false,"audio":true,"future":true}}""", JsonSerializerOptions.Web)!;
        check(!preferences.ToolOutputContentSettings.Text && !preferences.ToolOutputContentSettings.Images && preferences.ToolOutputContentSettings.Audio
            && !preferences.ToolOutputContentSettings.Video && !preferences.ToolOutputContentSettings.Documents, "tool output: partial settings keep explicit values and default missing categories");
        var clone = preferences.Clone(); clone.ToolOutputContentSettings.Images = true; clone.ToolOutputContentSettings.Text = true;
        check(!preferences.ToolOutputContentSettings.Images && !preferences.ToolOutputContentSettings.Text && clone.ToolOutputContentSettings.Audio, "tool output: cloned settings retain values without sharing mutable draft");
        preferences.ToolOutputContentSettings = new() { Text = false, Images = true, Audio = true, Video = true, Documents = true };
        var directory = Path.Combine(root, "tool-output-settings");
        await SettingsStore.SaveAsync(directory, new() { Chat = preferences });
        var loaded = (await SettingsStore.LoadAsync(directory, new())).Chat;
        check(!loaded.ToolOutputContentSettings.Text && loaded.ToolOutputContentSettings.Images && loaded.ToolOutputContentSettings.Audio
            && loaded.ToolOutputContentSettings.Video && loaded.ToolOutputContentSettings.Documents, "tool output: all five explicit values survive settings save/load");
        loaded.MaxOutputTokens = 123; loaded.ApiDebugEnabled = loaded.ShowTokens = true; loaded.ShowProviderLogo = false;
        var reset = loaded.InferenceDefaults();
        check(reset.MaxOutputTokens is null && reset.ApiDebugEnabled && reset.ShowTokens && !reset.ShowProviderLogo
            && !reset.ToolOutputContentSettings.Text && reset.ToolOutputContentSettings.Images && reset.ToolOutputContentSettings.Audio
            && reset.ToolOutputContentSettings.Video && reset.ToolOutputContentSettings.Documents, "tool output: inference reset preserves all five output settings and other user-level preferences");
        reset.ToolOutputContentSettings.Images = false;
        check(loaded.ToolOutputContentSettings.Images, "tool output: inference reset clones output settings independently");
        var persisted = await File.ReadAllTextAsync(Path.Combine(directory, "settings.json"));
        check(!persisted.Contains("sendToolOutputImages") && persisted.Contains("toolOutputContentSettings"), "tool output: obsolete property is not saved");
        preferences.ToolOutputContentSettings = new();
        await SettingsStore.SaveAsync(directory, new() { Chat = preferences });
        check(Defaults((await SettingsStore.LoadAsync(directory, new())).Chat.ToolOutputContentSettings), "tool output: explicit disabled media values round-trip");
        check(!preferences.RequestBody("p/model", "chat", [], "p").ContainsKey("toolOutputContentSettings"), "tool output: preferences are not inference body fields");
    }

    private static void CheckMapping(Action<bool, string> check)
    {
        (string Raw, string? Category)[] cases =
        [
            ("""{"type":"text","text":"keep","mimeType":"image/png"}""", "Text"),
            ("""{"type":"image","mimeType":" image/JPEG ","data":"bytes"}""", "Image"),
            ("""{"type":"audio","mimeType":"audio/wav","data":"bytes"}""", "Audio"),
            ("""{"type":"resource_link","mimeType":"video/mp4","uri":"https://example/video"}""", "Video"),
            ("""{"type":"resource_link","mimeType":"application/pdf","uri":"file:///report"}""", "Document"),
            ("""{"type":"resource","resource":{"mimeType":"image/png","blob":"bytes","uri":"image"}}""", "Image"),
            ("""{"type":"resource","resource":{"mimeType":"audio/mpeg","blob":"bytes"}}""", "Audio"),
            ("""{"type":"resource","resource":{"mimeType":"video/webm","blob":"bytes"}}""", "Video"),
            ("""{"type":"resource","resource":{"mimeType":"application/octet-stream","blob":"bytes"}}""", "Document"),
            ("""{"type":"resource","resource":{"mimeType":"text/plain","text":"keep"}}""", "Text"),
            ("""{"type":"resource","mimeType":"image/png","resource":{"mimeType":"text/html","text":"keep"}}""", "Text"),
            ("""{"type":"file","mimeType":"application/json","data":"keep"}""", "Text"),
            ("""{"type":"file","mimeType":"application/ld+json","data":"keep"}""", "Text"),
            ("""{"type":"file","mimeType":"application/xml","data":"keep"}""", "Text"),
            ("""{"type":"file","mimeType":"application/custom+xml","data":"keep"}""", "Text"),
            ("""{"type":"file","mimeType":"application/javascript","data":"keep"}""", "Text"),
            ("""{"type":"file","mimeType":"application/x-javascript","data":"keep"}""", "Text"),
            ("""{"type":"file","mimeType":"application/graphql","data":"keep"}""", "Text"),
            ("""{"type":"file","mimeType":"application/zip","data":"bytes"}""", "Document"),
            ("""{"type":"file","mimeType":"image/svg+xml","data":"keep"}""", "Text"),
            ("""{"type":"future","mimeType":"image/webp","data":"bytes"}""", "Image"),
            ("""{"type":"image","data":"do not guess without MIME"}""", null),
            ("""{"type":"file","name":"looks-like.pdf","url":"data:image/png;base64,bytes","mediaType":"image/png","data":"bytes"}""", null),
            ("""{"type":"resource","mimeType":"image/png","resource":{"uri":"looks-like.jpg","blob":"bytes"}}""", null),
            ("""{"type":"resource","resource":[]}""", null),
            ("""{"type":"image","mimeType":7,"data":"bytes"}""", null),
            ("""{"type":"file","mimeType":"model/gltf-binary","data":"bytes"}""", null),
            ("""{"type":"file","mimeType":"","data":"bytes"}""", null),
            ("null", null), ("42", null), ("[]", null), ("\"image/png\"", null)
        ];
        foreach (var (raw, category) in cases)
        {
            var original = JsonNode.Parse(raw);
            var output = new JsonObject { ["content"] = new JsonArray(original?.DeepClone()), ["isError"] = true, ["structuredContent"] = new JsonObject { ["keep"] = "original" }, ["future"] = 7 };
            var part = Part(output); var before = part.ToJsonString();
            var disabled = new ToolOutputContentSettings { Text = false };
            var request = Request(part, disabled);
            var expected = category is null ? original : new JsonObject { ["type"] = "text", ["text"] = $"{category} content omitted by user settings." };
            check(JsonNode.DeepEquals(Content(request)[0], expected), "tool output: explicit MCP classification " + raw);
            check(part.ToJsonString() == before && request["messages"]![0]!["parts"]![0]!["output"]!["isError"]!.GetValue<bool>()
                && JsonNode.DeepEquals(request["messages"]![0]!["parts"]![0]!["output"]!["structuredContent"], output["structuredContent"])
                && request["messages"]![0]!["parts"]![0]!["output"]!["future"]!.GetValue<int>() == 7, "tool output: mapping preserves source, envelope, and unknown fields");
            if (category is not null) Enable(disabled, category);
            check(JsonNode.DeepEquals(Content(Request(part, disabled))[0], original), "tool output: enabled category restores exact original block");
        }

        var sample = Part(JsonNode.Parse("""{"content":[{"type":"image","mimeType":"image/png","data":"keep"}]}"""));
        foreach (var marker in new[] { "true", "null", "\"false\"", "0", "{}" })
        {
            sample["providerExecuted"] = JsonNode.Parse(marker);
            check(Content(Request(sample, new()))[0]!["data"]!.GetValue<string>() == "keep", "tool output: only explicit client-executed false is filtered, not " + marker);
        }
        sample.Remove("providerExecuted");
        check(Content(Request(sample, new()))[0]!["data"]!.GetValue<string>() == "keep", "tool output: absent execution marker remains untouched like browser");
        foreach (var raw in new[] { "null", "[]", "\"plain output\"", "{\"content\":{\"data\":\"keep\"}}", "{\"structuredContent\":{\"mimeType\":\"image/png\",\"data\":\"keep\"}}" })
        {
            var part = Part(JsonNode.Parse(raw));
            check(JsonNode.DeepEquals(Request(part, new())["messages"]![0]!["parts"]![0]!["output"], part["output"]), "tool output: do not inspect or guess non-MCP output " + raw);
        }
        var mixed = Part(JsonNode.Parse("""{"content":[{"type":"text","text":"text"},{"type":"image","mimeType":"image/jpeg","data":"image"},{"type":"audio","mimeType":"audio/wav","data":"audio"}]}"""));
        var mixedResult = Content(Request(mixed, new() { Text = false, Images = true }));
        check(mixedResult.Count == 3 && mixedResult[0]!["text"]!.GetValue<string>() == "Text content omitted by user settings."
            && mixedResult[1]!["data"]!.GetValue<string>() == "image" && mixedResult[2]!["text"]!.GetValue<string>() == "Audio content omitted by user settings.", "tool output: toggles independently replace blocks without re-filtering placeholders or changing order");
        mixed["type"] = "dynamic-tool";
        check(Content(Request(mixed, new())).Count == 3, "tool output: dynamic tool parts use same mapping");
    }

    private static async Task CheckExecutionAndTransportAsync(Action<bool, string> check, string root)
    {
        var result = Json("""{"content":[{"type":"text","text":"result"},{"type":"image","mimeType":"image/jpeg","data":"retained bytes"}],"isError":false,"structuredContent":{"reference":"keep"}}""");
        var definition = Json("""{"name":"remote_image","inputSchema":{"type":"object"}}""");
        var discovery = new McpDiscovery(Json("{}"), Json("{}"), null, [definition]);
        var calls = 0;
        var snapshot = McpTurnSnapshot.Create([(new McpCatalogItem("fixture", "Fixture", "", "https://mcp.example/"), discovery,
            (Func<string, JsonElement, string, string, CancellationToken, Task<JsonElement>>)((_, _, _, _, _) => { calls++; return Task.FromResult(result); }))]);
        snapshot.AddLocal(Json("""{"name":"local_image","inputSchema":{"type":"object"}}"""), (_, _) => { calls++; return Task.FromResult(result); });
        var output = new ConversationMessage { Message = new() { Id = "output", Role = Role.assistant, Parts = [] } };
        foreach (var name in new[] { "remote_image", "local_image" })
            output.Message.Parts.Add(PortableConversations.Part(new JsonObject { ["type"] = "tool-" + name, ["toolCallId"] = name, ["state"] = "input-available", ["input"] = new JsonObject() }));
        var executed = await DesktopMcpToolExecution.ExecutePendingAsync(output, snapshot, [], "en", default);
        check(executed == 2 && calls == 2 && output.Message.Parts.All(part => PortableConversations.Element(part).GetProperty("providerExecuted").ValueKind == JsonValueKind.False), "tool output: shared executor marks both MCP and local results client-executed");
        var conversation = new Conversation { Id = "tool-output", Messages = [output] };
        var store = new HistoryStore(Path.Combine(root, "tool-output-history"));
        var partition = HistoryStore.Partition("fixture", "account");
        await store.SaveAsync(partition, conversation);
        // History saving updates the conversation timestamp; take the request baseline afterwards.
        var before = PortableConversations.Write(conversation);
        var restored = (await store.GetAsync(partition, conversation.Id))!;
        check(restored.Messages[0].Message.Parts.All(part => PortableConversations.Element(part).GetProperty("output").GetProperty("content")[1].GetProperty("data").GetString() == "retained bytes"), "tool output: history retains original bytes and client marker");
        var session = new DesktopSession(new TestHost(), new TestRuntime(), new());
        using var handler = new CaptureHandler(); using var http = new HttpClient(handler); var client = new DesktopChatClient(session, http);
        var agent = DesktopAgent.Parse("""{"name":"Fixture","description":"Transport fixture","model":{"id":"p/model"},"instructions":"Test"}""");
        foreach (var (service, localAgent) in new[] { (ServiceKind.Ai, (DesktopAgent?)null), (ServiceKind.Agents, null), (ServiceKind.Agents, agent) })
        {
            await foreach (var _ in client.StreamAsync(service, "p/model", "chat", [output.Message], default, mcp: snapshot, localAgent: localAgent)) { }
            check(Enumerable.Range(0, 2).All(i => Content(handler.Body!, i)[1]!["text"]!.GetValue<string>() == "Image content omitted by user settings."), "tool output: actual HTTP request filters both local/MCP results for " + service + (localAgent is null ? "" : " local agent"));
            session.Settings.Chat.ToolOutputContentSettings.Images = true;
            await foreach (var _ in client.StreamAsync(service, "p/model", "chat", [output.Message], default, mcp: snapshot, localAgent: localAgent)) { }
            check(Enumerable.Range(0, 2).All(i => Content(handler.Body!, i)[1]!["data"]!.GetValue<string>() == "retained bytes"), "tool output: actual HTTP opt-in includes original local/MCP bytes for " + service);
            session.Settings.Chat.ToolOutputContentSettings.Images = false;
        }
        check(PortableConversations.Write(conversation) == before && calls == 2, "tool output: requests never mutate conversation or rerun tools");
        var userFile = new UIMessage { Id = "user", Role = Role.user, Parts = [ComposerAttachment.Local("photo.jpg", "image/jpeg", new byte[] { 1, 2, 3 }).FilePart()] };
        var userBefore = JsonSerializer.Serialize(userFile, PortableConversations.Json);
        var body = new ChatPreferences().RequestBody("p/model", "chat", [userFile], "p");
        check(body["messages"]![0]!.ToJsonString(PortableConversations.Json) == userBefore, "tool output: user-uploaded files are outside tool result policy");
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public JsonObject? Body;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject();
            return new(HttpStatusCode.OK) { Content = new StringContent("data: [DONE]\n\n", Encoding.UTF8, "text/event-stream") };
        }
    }
}
