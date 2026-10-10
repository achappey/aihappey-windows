using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Desktop.Core;
using AIHappey.Vercel.Models;

internal static class LocalToolRegressionTests
{
    private static JsonElement Json(string value) => JsonSerializer.Deserialize<JsonElement>(value);
    private static JsonElement Args(object value) => JsonSerializer.SerializeToElement(value);
    private static JsonElement Data(JsonElement value) => value.GetProperty("structuredContent");
    private static bool Error(JsonElement value) => value.GetProperty("isError").GetBoolean();
    public static async Task RunAsync(Action<bool, string> check, string root)
    {
        var defaults = new ChatPreferences();
        check(defaults.ActivePlugins.Count == 0 && JsonSerializer.Deserialize<ChatPreferences>("{}")!.ActivePlugins.Count == 0,
            "local tools: new and old settings default all plugins off");
        defaults.ActivePlugins = ["local-conversations", "", "local-conversations", " skill-search "];
        var clone = defaults.Clone(); clone.ActivePlugins.Add(DesktopLocalTools.ArtificialIntelligence);
        check(defaults.ActivePlugins.Count == 2 && clone.ActivePlugins.Count == 3, "local tools: normalized selections and isolated clone");
        var directory = Path.Combine(root, "local-settings"); await SettingsStore.SaveAsync(directory, new() { Chat = clone });
        check((await SettingsStore.LoadAsync(directory, new())).Chat.ActivePlugins.SequenceEqual(clone.ActivePlugins), "local tools: selections persist");
        check(DesktopLocalTools.Plugins.Count == 5 && DesktopLocalTools.Plugins.SelectMany(p => p.Tools).Count() == 19 && DesktopLocalTools.Plugins.SelectMany(p => p.Tools)
            .Select(t => t.GetProperty("name").GetString()).Distinct().Count() == 19, "local tools: 13 unchanged browser and six desktop-only unique contracts");
        check(DesktopLocalTools.Definition("local_conversations_delete_conversation").GetProperty("annotations").GetProperty("destructiveHint").GetBoolean(),
            "local tools: destructive delete annotation preserved");
        check(McpTurnSnapshot.Empty.Tools.Count == 0, "local tools: disabled plugins expose no tools");

        var store = new HistoryStore(Path.Combine(root, "local-history")); var partition = HistoryStore.Partition("local", "account");
        var extraction = new DocumentTextExtraction(); var notified = 0; var currentPartition = true;
        var runtime = new LocalConversationTools(store, partition, extraction, () => currentPartition, (_, _) => { notified++; return Task.CompletedTask; });
        var conversation = new Conversation { Id = "chat", Title = "Fixture", Messages = [new() { Message = new UIMessage { Id = "user", Role = Role.user,
            Parts = [new TextUIPart { Text = "Alpha far away from BETA" }, new TextUIPart { Text = "alpha" }, new TextUIPart { Text = "beta" },
                new ReasoningUIPart { Text = "alpha beta hidden" }, ComposerAttachment.Local("notes.txt", "text/plain", Encoding.UTF8.GetBytes("héllo attachment")).FilePart(),
                ComposerAttachment.Local("report.pdf", "application/pdf", Pdf()).FilePart()] } }] };
        await store.SaveAsync(partition, conversation);
        await store.SaveAsync(HistoryStore.Partition("other-account"), new() { Id = "foreign" });
        var list = Data(await runtime.CallAsync("local_conversations_list_all", Json("{}"), default)).GetProperty("conversations");
        check(list.GetArrayLength() == 1 && list[0].GetProperty("metadata").GetProperty("name").GetString() == "Fixture"
            && list[0].GetProperty("messageCount").GetInt32() == 1, "local tools: list shape and account isolation");
        var sanitized = Data(await runtime.CallAsync("local_conversations_get_conversation", Args(new { conversationId = "chat" }), default));
        check(!sanitized.GetProperty("messages")[0].GetProperty("parts")[4].TryGetProperty("url", out _)
            && (await store.GetAsync(partition, "chat"))!.Messages[0].Message.Parts[4] is { } file && PortableConversations.Element(file).TryGetProperty("url", out _),
            "local tools: portable get omits attachment bytes without mutating stored originals");
        var missing = await runtime.CallAsync("local_conversations_get_conversation", Args(new { conversationId = "missing" }), default);
        check(!missing.TryGetProperty("structuredContent", out _) && missing.GetProperty("content")[0].GetProperty("text").GetString() == "null",
            "local tools: missing conversation uses a null text fallback, not invalid MCP structured content");
        var search = Data(await runtime.CallAsync("local_conversations_search_text", Args(new { query = "BETA alpha", limit = 90 }), default));
        check(search.GetProperty("results").GetArrayLength() == 1 && search.GetProperty("limit").GetInt32() == 50
            && search.GetProperty("results")[0].GetProperty("messageId").GetString() == "user",
            "local tools: unordered multiword search matches one text part, not cross-part or reasoning content");
        check(Error(await runtime.CallAsync("local_conversations_search_text", Args(new { query = " " }), default)), "local tools: empty search rejected safely");
        var read = Data(await runtime.CallAsync("local_conversations_read_attachment", Args(new { conversationId = "chat", messageId = "user", filename = "notes.txt" }), default));
        check(read.GetProperty("text").GetString() == "héllo attachment" && read.GetProperty("mediaType").GetString() == "text/plain",
            "local tools: inline UTF-8 attachment read returns text and browser envelope");
        var pdf = Data(await runtime.CallAsync("local_conversations_read_attachment", Args(new { conversationId = "chat", messageId = "user", filename = "report.pdf" }), default));
        check(pdf.GetProperty("text").GetString()!.Contains("PDF local tools"), "local tools: actual PDF attachment uses shared PdfPig extractor");
        var prepared = await ComposerAttachments.PrepareAsync("", [ComposerAttachment.Local("notes.txt", null, Encoding.UTF8.GetBytes("composer text")),
            ComposerAttachment.Local("report.pdf", "application/pdf", Pdf())], ServiceKind.Ai, true, extraction, default);
        check(prepared.Message.Parts.Count(p => p.Type == "text") == 2 && prepared.Message.Parts.Count(p => p.Type == "file") == 2,
            "local tools: composer reuses PDF/plain-text dispatcher and retains files");
        check(await extraction.ExtractAsync("unknown.bin", "application/octet-stream", new byte[] { 0 }, default) is null,
            "local tools: unsupported formats do not fabricate text");
        var utf16 = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("BOM ✓")).ToArray();
        check(await extraction.ExtractAsync("notes.txt", "application/octet-stream", utf16, default) == "BOM ✓", "local tools: plain-text fallback recognizes BOM");
        foreach (var (url, error) in new[] { ("https://private.example/file", "inline"), ("file:///C:/secret", "inline"), ("data:text/plain,secret", "non-base64"), ("%%notbase64", "base64") })
        {
            var original = conversation.Messages[0].Message.Parts[4]; var node = PortableConversations.Node(original); node["url"] = url;
            conversation.Messages[0].Message.Parts[4] = PortableConversations.Part(node); await store.SaveAsync(partition, conversation);
            var result = await runtime.CallAsync("local_conversations_read_attachment", Args(new { conversationId = "chat", messageId = "user", filename = "notes.txt" }), default);
            check(Error(result) && result.GetRawText().Contains(error), "local tools: attachment rejects " + error + " source");
            conversation.Messages[0].Message.Parts[4] = original;
        }
        conversation.Messages[0].Message.Parts.Add(conversation.Messages[0].Message.Parts[4]); await store.SaveAsync(partition, conversation);
        check(Error(await runtime.CallAsync("local_conversations_read_attachment", Args(new { conversationId = "chat", messageId = "user", filename = "notes.txt" }), default)),
            "local tools: duplicate exact filename is ambiguous");
        conversation.Messages[0].Message.Parts.RemoveAt(conversation.Messages[0].Message.Parts.Count - 1);
        var deleted = Data(await runtime.CallAsync("local_conversations_delete_conversation", Args(new { conversationId = "chat" }), default));
        await runtime.SaveAsync(conversation); await runtime.SaveAsync(conversation);
        check(deleted.GetProperty("deletedId").GetString() == "chat" && runtime.IsDeleted("chat") && notified == 1
            && await store.GetAsync(partition, "chat") is null, "local tools: active deletion notifies sidebar and periodic/final saves never resurrect chat");
        check(Error(await runtime.CallAsync("local_conversations_delete_conversation", Args(new { conversationId = "../escape" }), default)), "local tools: opaque IDs never permit traversal");
        currentPartition = false;
        var checkpoint = new Conversation { Id = "shutdown-checkpoint" };
        await runtime.SaveAsync(checkpoint);
        check(await store.GetAsync(partition, checkpoint.Id) is not null, "local tools: shutdown checkpoint still saves to captured partition after handlers become unavailable");
        try { await runtime.CallAsync("local_conversations_list_all", Json("{}"), default); throw new Exception("Expected cancellation"); }
        catch (OperationCanceledException) { check(true, "local tools: retained history handler rejects changed account/endpoint"); }
        var safe = await DesktopLocalTools.SafeAsync(() => throw new IOException("Authorization: secret https://internal"), default);
        check(Error(safe) && !safe.GetRawText().Contains("secret"), "local tools: arbitrary exceptions never leak transport secrets");

        await CheckAiAsync(check);
        await CheckSkillsAsync(check);
        await CheckMixedAsync(check, store, partition, extraction);
        await SharedFileCursorRegressionTests.RunAsync(check, root);
        await LocalFileToolRegressionTests.RunAsync(check, root, Pdf());
    }
    private static async Task CheckAiAsync(Action<bool, string> check)
    {
        var providers = new[] { new CatalogProvider { Id = "p", Name = "Provider", ProviderCountry = "NL", InferenceRegions = ["Europe"], Urls = new("https://provider.example") },
            new CatalogProvider { Id = "q", Name = "Other", ProviderCountry = "US", InferenceRegions = ["World"] } };
        var raw = Json("""{"id":"p/model10","name":"Model 10","type":"image","owned_by":"Owner","tags":["drawing"],"future":{"retain":true}}""");
        var tools = new LocalArtificialIntelligenceTools([AiModelCatalog.Project(raw, ServiceKind.Ai),
            AiModelCatalog.Project(Json("""{"id":"p/model2","name":"Model 2","type":"language"}"""), ServiceKind.Ai)], providers);
        var listed = Data(await tools.CallAsync("local_ai_providers_list", Args(new { country = "nl", inferenceRegion = "europe" }), default));
        check(listed.GetProperty("total").GetInt32() == 1 && listed.GetProperty("items")[0].GetProperty("key").GetString() == "p",
            "local tools: provider country/region case-insensitive exact filters and key shape");
        var countries = Data(await tools.CallAsync("local_ai_provider_countries_list", Json("{}"), default));
        check(countries.GetProperty("items")[0].GetProperty("code").GetString() == "NL" && countries.GetProperty("items")[0].GetProperty("providerCount").GetInt32() == 1,
            "local tools: provider country counts sorted by uppercase code");
        check(Data(await tools.CallAsync("local_ai_providers_search", Args(new { query = "PROV", limit = 1 }), default)).GetProperty("count").GetInt32() == 1,
            "local tools: provider key/name substring search");
        check(Error(await tools.CallAsync("local_ai_providers_search", Args(new { query = " " }), default)), "local tools: empty provider query rejected");
        var search = Data(await tools.CallAsync("local_ai_models_search", Args(new { query = "DRAWING", limit = 500 }), default));
        check(search.GetProperty("data").GetArrayLength() == 1 && search.GetProperty("data")[0].GetProperty("future").GetProperty("retain").GetBoolean()
            && !search.TryGetProperty("total", out _), "local tools: model tags searched, non-language and full metadata preserved, search envelope matches browser");
        var models = Data(await tools.CallAsync("local_ai_models_list_by_provider", Args(new { provider = "Provider" }), default));
        check(models.GetProperty("data")[0].GetProperty("name").GetString() == "Model 2" && models.GetProperty("total").GetInt32() == 2,
            "local tools: provider display-name resolution and numeric model ordering");
        check(Data(await tools.CallAsync("local_ai_models_list_by_provider", Args(new { provider = "unknown" }), default)).GetProperty("count").GetInt32() == 0,
            "local tools: unknown provider returns empty model result");
        check(Data(await tools.CallAsync("local_ai_models_search", Args(new { query = "", type = "image", limit = 1 }), default)).GetProperty("data").GetArrayLength() == 1,
            "local tools: empty model query and optional type filter match browser");
        var offline = new LocalArtificialIntelligenceTools([]);
        check(Data(await offline.CallAsync("local_ai_providers_list", Args(new { limit = 900 }), default)).GetProperty("count").GetInt32() == 500,
            "local tools: real offline provider catalog and limit capped at 500");
    }
    private static async Task CheckSkillsAsync(Action<bool, string> check)
    {
        var items = new DesktopSkill[] { new("pdf", "PDF", "Extract documents", "local"), new("excel", "Excel", "Spreadsheet CSV formulas", "remote", "2"),
            new("mcp:sheet", "Spreadsheet CSV", "Excel processing", "mcp") };
        var ranked = DesktopSkillSearch.Find(items, "Please find a skill for Excel spreadsheet CSV");
        check(ranked.Keywords.SequenceEqual(["excel", "spreadsheet", "csv"]) && ranked.Skills[0].Id == "excel" && ranked.TotalMatches == 2,
            "local tools: browser skill stopwords and keyword coverage/relevance ranking");
        check(DesktopSkillSearch.Find([new("résumé", "RésuméReview", "Review", "local")], "resume review").TotalMatches == 1,
            "local tools: skill search normalizes accents and camel case");
        var loads = 0; var allowed = true;
        var readers = items.Select(s => (s, (Func<CancellationToken, Task<DesktopSkillContent>>)(_ =>
        { loads++; return Task.FromResult(new DesktopSkillContent(s, "Body instructions", ["readme.txt"], (_, _) => Task.FromResult(new DesktopSkillFile("readme.txt", Encoding.UTF8.GetBytes("resource"))))); }))).ToArray();
        var turn = new DesktopSkillTurn(readers, true, new HashSet<string>(), s => s.Origin != "mcp" || allowed);
        var snapshot = McpTurnSnapshot.Empty; turn.Register(snapshot);
        var search = Data(await turn.CallAsync("search_skills", Args(new { query = "excel", limit = 100 }), default)).GetProperty("skillSearch");
        check(loads == 0 && snapshot.Tools.Count == 3 && snapshot.Context.Count == 0 && search.GetProperty("skills")[0].GetProperty("skill_id").GetString() == "excel",
            "local tools: discovery loads descriptors only and exposes exact IDs without injecting whole catalog");
        var activated = Data(await turn.CallAsync("activate_skill", Args(new { skill_id = "excel" }), default));
        check(loads == 1 && activated.GetProperty("skill").GetProperty("instructions").GetString() == "Body instructions",
            "local tools: discovered but not explicitly enabled skill activates lazily");
        var resource = Data(await turn.CallAsync("read_skill_resource", Args(new { skill_id = "excel", path = "readme.txt" }), default));
        check(resource.GetProperty("skillResource").GetProperty("text").GetString() == "resource", "local tools: discovered skill uses existing bundled-resource reader");
        allowed = false;
        check(Data(await turn.CallAsync("search_skills", Json("{}"), default)).GetProperty("skillSearch").GetProperty("returned").GetInt32() == 2
            && Error(await turn.CallAsync("activate_skill", Args(new { skill_id = "mcp:sheet" }), default)), "local tools: retained skill discovery and activation respect MCP gate");
        var off = new DesktopSkillTurn(readers.Take(1)); var explicitSnapshot = McpTurnSnapshot.Empty; off.Register(explicitSnapshot);
        check(explicitSnapshot.Tools.Count == 2 && explicitSnapshot.Context.Count == 1 && Error(await off.CallAsync("search_skills", Json("{}"), default)),
            "local tools: discovery off preserves explicit skill tools/context without search");
        var empty = McpTurnSnapshot.Empty; new DesktopSkillTurn([], true).Register(empty);
        check(empty.Tools.Count == 3, "local tools: enabled discovery plugin keeps contracts even for empty catalog");
    }
    private static async Task CheckMixedAsync(Action<bool, string> check, HistoryStore store, string partition, DocumentTextExtraction extraction)
    {
        var calls = 0;
        var discovery = new McpDiscovery(Json("{}"), Json("{}"), null, [DesktopLocalTools.Definition("local_ai_providers_list")]);
        var snapshot = McpTurnSnapshot.Create([(new McpCatalogItem("server", "Server", "", "https://mcp.example/"), discovery,
            (Func<string, JsonElement, string, string, CancellationToken, Task<JsonElement>>)((name, _, _, _, _) =>
            { calls++; check(name == "local_ai_providers_list", "local tools: MCP alias routes original tool name"); return Task.FromResult(DesktopLocalTools.Result(new { mcp = true })); }))]);
        var alias = snapshot.Tools[0].GetProperty("name").GetString()!;
        var ai = new LocalArtificialIntelligenceTools([]); DesktopLocalTools.Register(snapshot, DesktopLocalTools.ArtificialIntelligence, ai.CallAsync);
        check(alias != "local_ai_providers_list" && snapshot.Tools.Count == 6, "local tools: reserved names alias colliding MCP tools without duplicate local routes");
        var result = await snapshot.CallAsync(alias, Json("{}"), "call", "en", default);
        check(Data(result).GetProperty("mcp").GetBoolean() && calls == 1, "local tools: mixed registry preserves MCP execution");
        var conversation = new Conversation(); var output = new ConversationMessage { Message = new UIMessage { Id = "output", Role = Role.assistant }, Status = "streaming" };
        conversation.Messages.Add(output); var rounds = 0;
        async IAsyncEnumerable<StreamEvent> Stream(List<UIMessage> messages, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield(); rounds++;
            if (rounds == 1)
            {
                yield return StreamEvent.Parse("""{"type":"tool-input-available","toolCallId":"local","toolName":"local_ai_providers_list","input":{"limit":1}}""");
                yield return StreamEvent.Parse(JsonSerializer.Serialize(new { type = "tool-input-available", toolCallId = "remote", toolName = alias, input = new { } }));
            }
            else yield return StreamEvent.Parse("""{"type":"text-delta","id":"text","delta":"continued"}""");
            yield return StreamEvent.Parse("""{"type":"finish"}""");
        }
        await DesktopChatTurn.RunAsync(conversation, output, Stream, (_, _) => Task.FromResult(new ToolApprovalDecision(true)), (_, _) => Task.CompletedTask,
            snapshot, "en", default);
        check(rounds == 2 && output.Text == "continued" && calls == 2 && output.Message.Parts.Count(p => PortableConversations.IsTool(p)
            && PortableConversations.String(PortableConversations.Element(p), "state") == "output-available") == 2,
            "local tools: mixed MCP/local tool results use existing continuation and execute once");
    }
    private static byte[] Pdf()
    {
        const string text = "BT /F1 12 Tf 10 100 Td (PDF local tools) Tj ET";
        var objects = new[] { "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 200] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>", $"<< /Length {text.Length} >>\nstream\n{text}\nendstream" };
        var document = new StringBuilder("%PDF-1.4\n"); var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++) { offsets.Add(document.Length); document.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n"); }
        var xref = document.Length; document.Append("xref\n0 6\n0000000000 65535 f \n");
        foreach (var offset in offsets) document.Append(offset.ToString("D10") + " 00000 n \n");
        document.Append($"trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(document.ToString());
    }
}
