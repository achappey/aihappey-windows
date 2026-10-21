using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Desktop.Core;
using AIHappey.Vercel.Models;

internal static class AuthoringToolRegressionTests
{
    private static JsonElement Args(object value) => JsonSerializer.SerializeToElement(value);
    private static JsonElement Data(JsonElement value) => value.GetProperty("structuredContent");
    private static bool Error(JsonElement value) => value.GetProperty("isError").GetBoolean();
    private static JsonElement Json(string value) => JsonSerializer.Deserialize<JsonElement>(value);
    public static async Task RunAsync(Action<bool, string> check, string root)
    {
        root = Path.Combine(root, "authoring"); Directory.CreateDirectory(root);
        foreach (var (id, count) in new[] { (DesktopLocalTools.Images, 5), (DesktopLocalTools.AgentsEditor, 4), (DesktopLocalTools.AgentsRuntime, 1), (DesktopLocalTools.SkillEditor, 9) })
        {
            var plugin = DesktopLocalTools.Plugins.Single(p => p.Id == id);
            check(plugin.Tools.Count == count && plugin.Tools.All(t => t.GetProperty("inputSchema").GetProperty("type").GetString() == "object"
                && t.TryGetProperty("annotations", out _)) && !new ChatPreferences().PluginEnabled(id), "authoring: exact opt-in plugin contract count " + id);
            check(!string.IsNullOrWhiteSpace(DesktopResources.Get(plugin.ResourceKey)), "authoring: plugin label " + id);
        }
        check(DesktopLocalTools.Plugins.Single(p => p.Id == AgentPluginTools.EditorId).Tools.Count == AgentPluginTools.Definitions.Count,
            "authoring: package editor remains independently registered");
        var current = true; var partition = McpValidation.Hash("authoring-account");
        var shared = new SharedFileStore(Path.Combine(root, "shared")); var fileNotices = 0;
        var files = new LocalToolFiles(shared, partition, Path.Combine(root, "outputs"), () => current, _ => { fileNotices++; return Task.CompletedTask; });
        var bytes = new byte[] { 0, 1, 2, 255, 42 };
        var saved = await files.SaveAsync("fixture.pdf", bytes, default);
        check((await files.ReadAsync(saved.Id, false, default)).Content.Span.SequenceEqual(bytes)
            && (await files.ReadAsync("fixture.pdf", true, default)).Content.Span.SequenceEqual(bytes) && fileNotices == 1, "authoring: byte-preserving file ID/exact-name reads and refresh");
        await files.SaveAsync("fixture.pdf", bytes, default);
        try { await files.ReadAsync("fixture.pdf", true, default); throw new Exception("Expected ambiguity"); }
        catch (LocalToolException) { check(true, "authoring: duplicate filenames are rejected rather than attaching the wrong file"); }
        foreach (var name in new[] { "../escape.png", "C:\\secret.png", "CON", "mask.png:secret" })
        {
            try { await files.SaveAsync(name, bytes, default); throw new Exception("Expected path refusal"); }
            catch (LocalToolException) { check(true, "authoring: generated output cannot use unsafe name " + name); }
        }
        var foreign = new LocalToolFiles(shared, McpValidation.Hash("another-account"), Path.Combine(root, "foreign"), () => true);
        try { await foreign.ReadAsync(saved.Id, false, default); throw new Exception("Expected isolation"); }
        catch (LocalToolException) { check(true, "authoring: file references are account-isolated"); }
        await CheckAgentsAsync(check, root, files);
        await CheckSkillsAsync(check, root, files);
        await CheckImagesAsync(check, root, files);
        await CheckTransportAsync(check);
        CheckImagePolicy(check);
        current = false;
        try { await files.ReadAsync(saved.Id, false, default); throw new Exception("Expected cancellation"); }
        catch (OperationCanceledException) { check(true, "authoring: retained file adapter cancels after account change"); }
    }
    private static async Task CheckAgentsAsync(Action<bool, string> check, string root, LocalToolFiles files)
    {
        var store = new DesktopAgentStore(Path.Combine(root, "agents")); var current = true; var notices = 0; var remoteLookups = 0;
        JsonObject? request = null;
        var tools = new LocalAgentTools(store, "agents", [], () => current, _ => { notices++; return Task.CompletedTask; },
            _ => { remoteLookups++; return Task.FromResult<IReadOnlyList<ChatTarget>>([new("shared-name", "Remote"), new("remote-only", "Remote")]); }, files,
            (body, _) => { request = (JsonObject)body.DeepClone(); return Task.FromResult(Json("""{"id":"response-1","status":"queued","future":{"preserved":true}}""")); });
        var create = new { agentName = "shared-name", agentDescription = "", agentInstructions = "", modelId = "p/model", modelTemperature = .2,
            mcpServerUrls = new[] { "https://mcp.example/" }, policyOpenWorld = true };
        check(!Error(await tools.CallAsync("local_agents_create", Args(create), default)) && notices == 1, "authoring agents: browser-valid empty descriptions/instructions can be stored");
        check(Error(await tools.CallAsync("local_agents_create", Args(create), default)) && notices == 1, "authoring agents: duplicate create never overwrites");
        var get = Data(await tools.CallAsync("local_agents_get", Args(new { agentName = create.agentName }), default)).GetProperty("agent");
        check(get.GetProperty("model").GetProperty("options").GetProperty("temperature").GetDouble() == .2
            && !get.GetProperty("mcpClient").GetProperty("policy").GetProperty("destructiveHint").GetBoolean()
            && get.GetProperty("mcpServers").GetProperty("https://mcp.example/").GetProperty("type").GetString() == "streamable-http", "authoring agents: full portable model/MCP/policy configuration");
        check(Data(await tools.CallAsync("local_agents_list", Json("{}"), default)).GetProperty("agents").GetArrayLength() == 1
            && (await new DesktopAgentStore(Path.Combine(root, "agents")).ListAsync("agents", [], default)).Count == 1, "authoring agents: list/get use fresh persistent reads across instances");
        var file = await files.SaveAsync("agent-input.txt", Encoding.UTF8.GetBytes("agent attachment"), default);
        var response = Data(await tools.CallAsync("local_agents_run", Args(new { agentId = "shared-name", prompt = " Work ", filename = file.Name, background = true }), default));
        check(remoteLookups == 0 && request!["metadata"]!["agents"]![0]!["name"]!.GetValue<string>() == "shared-name"
            && request["background"]!.GetValue<bool>() && request["store"]!.GetValue<bool>() && !request["stream"]!.GetValue<bool>(), "authoring agents: local-first invocation and exact background/store/stream flags");
        check(request!["input"]![0]!["content"]![1]!["file_data"]!.GetValue<string>().EndsWith(Convert.ToBase64String(Encoding.UTF8.GetBytes("agent attachment")))
            && response.GetProperty("future").GetProperty("preserved").GetBoolean(), "authoring agents: exact filename input_file and raw response fields preserved");
        await tools.CallAsync("local_agents_run", Args(new { agentId = "remote-only", prompt = "Run" }), default);
        check(remoteLookups == 1 && request!["model"]!.GetValue<string>() == "remote-only" && !request.ContainsKey("metadata")
            && !request["store"]!.GetValue<bool>(), "authoring agents: remote ID resolution and foreground flags");
        check(Error(await tools.CallAsync("local_agents_run", Args(new { agentId = "missing", prompt = "Run" }), default))
            && Error(await tools.CallAsync("local_agents_run", Args(new { agentId = "remote-only", prompt = " " }), default)), "authoring agents: missing agent and blank prompt rejected");
        var snapshot = McpTurnSnapshot.Empty; DesktopLocalTools.Register(snapshot, DesktopLocalTools.AgentsEditor, tools.CallAsync);
        check(snapshot.Tools.Count == 4 && !Error(await snapshot.CallAsync("local_agents_list", Json("{}"), "call", "en", default)), "authoring agents: contracts dispatch through existing turn snapshot");
        await tools.CallAsync("local_agents_delete", Args(new { agentName = "shared-name" }), default);
        check(Data(await tools.CallAsync("local_agents_list", Json("{}"), default)).GetProperty("agents").GetArrayLength() == 0
            && !Error(await tools.CallAsync("local_agents_delete", Args(new { agentName = "shared-name" }), default)), "authoring agents: delete is safe when absent and never reseeds defaults");
        current = false;
        try { await tools.CallAsync("local_agents_list", Json("{}"), default); throw new Exception("Expected cancellation"); }
        catch (OperationCanceledException) { check(true, "authoring agents: stale handlers cannot access another account"); }
    }
    private static async Task CheckSkillsAsync(Action<bool, string> check, string root, LocalToolFiles files)
    {
        var store = new DesktopLocalSkillStore(Path.Combine(root, "skills")); var notices = 0; var downloads = 0;
        var remoteDraft = new DesktopSkillDraft { Name = "downloaded-skill", Description = "Remote", Instructions = "Downloaded instructions" };
        var remoteStore = new DesktopSkillStore(Path.Combine(root, "skill-cache"), (_, _, _) => { downloads++; return Task.FromResult(DesktopSkillPackages.Export(remoteDraft)); });
        var current = true;
        var tools = new LocalSkillEditorTools(store, remoteStore, "skills", files, () => current, _ => { notices++; return Task.CompletedTask; });
        var created = Data(await tools.CallAsync("local_skill_editor_create", Args(new { name = "test-skill", description = "Test", instructions = "Version one", allowedTools = "Read", metadata = new { author = "tester" } }), default));
        var id = created.GetProperty("skillId").GetString()!;
        check(created.GetProperty("version").GetString() == "1" && notices == 1, "authoring skills: create returns version/identity and refreshes catalog");
        var upsert = Data(await tools.CallAsync("local_skill_editor_upsert_file", Args(new { skillId = id, relativePath = "references/info.txt", content = "héllo", mimeType = "text/x-test" }), default));
        var head = (await store.ListAsync("skills", default)).Single(); var draft = await store.ReadAsync("skills", head, default);
        check(upsert.GetProperty("version").GetString() == "1" && draft.Files.Single().MimeType == "text/x-test"
            && Encoding.UTF8.GetString(draft.Files.Single().Data) == "héllo", "authoring skills: resource edits are in-place and preserve custom MIME/UTF-8");
        var updated = Data(await tools.CallAsync("local_skill_editor_update_manifest", Args(new { skillId = "test-skill", instructions = "Version two" }), default));
        check(updated.GetProperty("version").GetString() == "2" && (await store.ReadAsync("skills", head, default)).Instructions == "Version one", "authoring skills: manifest update increments version and preserves prior archive");
        var inspected = Data(await tools.CallAsync("local_skill_editor_inspect", Args(new { skillId = id, version = "1" }), default));
        check(inspected.GetProperty("body").GetString() == "Version one" && inspected.GetProperty("latestVersion").GetString() == "2"
            && inspected.GetProperty("frontmatter").GetProperty("allowed-tools").GetString() == "Read" && inspected.GetProperty("files").GetArrayLength() == 2, "authoring skills: exact-version inspect includes body, preserved metadata, inventory, and latest/default version");
        var binary = new byte[] { 0, 255, 42, 2 }; await files.SaveAsync("document.docx", binary, default);
        var imported = Data(await tools.CallAsync("local_skill_editor_import_local_file", Args(new { skillId = id, localFileName = "document.docx", relativePath = "assets/document.docx" }), default));
        head = (await store.ListAsync("skills", default)).Single(); draft = await store.ReadAsync("skills", head, default);
        check(imported.GetProperty("version").GetString() == "2" && draft.Files.Single(f => f.Path.EndsWith("docx")).Data.SequenceEqual(binary), "authoring skills: Office imports preserve original bytes without conversion or version increment");
        foreach (var path in new[] { "SKILL.md", "skill.MD", "../escape", "folder/../escape", "C:\\secret", "references/%2e%2e/escape" })
            check(Error(await tools.CallAsync("local_skill_editor_upsert_file", Args(new { skillId = id, relativePath = path, content = "bad" }), default)), "authoring skills: protected/unsafe package path " + path);
        check(Error(await tools.CallAsync("local_skill_editor_update_manifest", Args(new { skillId = id, description = "" }), default))
            && (await store.ListAsync("skills", default)).Single().Version == "2", "authoring skills: invalid metadata patch publishes no version");
        var restored = Data(await tools.CallAsync("local_skill_editor_restore_version", Args(new { skillId = id, version = "1" }), default));
        head = (await store.ListAsync("skills", default)).Single(); draft = await store.ReadAsync("skills", head, default);
        check(restored.GetProperty("version").GetString() == "3" && draft.Instructions == "Version one" && draft.Files.Count == 1, "authoring skills: restore creates new head and restores resources as well as instructions");
        await tools.CallAsync("local_skill_editor_delete_file", Args(new { skillId = id, relativePath = "references/info.txt" }), default);
        check((await store.ReadAsync("skills", head, default)).Files.Count == 0 && (await store.ListAsync("skills", default)).Single().Version == "3", "authoring skills: resource deletion updates current version only");
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => new DesktopLocalSkillStore(Path.Combine(root, "skills"))
            .EditAsync("skills", id, d => d.Instructions += "x", true, null, default)));
        head = (await store.ListAsync("skills", default)).Single();
        check(head.Version == "7" && (await store.ReadAsync("skills", head, default)).Instructions == "Version onexxxx", "authoring skills: parallel stores serialize read/modify/version commits without lost edits");
        check(Data(await tools.CallAsync("local_skill_editor_list", Json("{}"), default)).GetProperty("skills").GetArrayLength() == 1 && downloads == 0, "authoring skills: editor discovery is local and performs no implicit downloads");
        var catalogItem = new CatalogItem(CatalogKind.Skill, "remote-id", remoteDraft.Name, remoteDraft.Description) { Version = "7", LatestVersion = "7" };
        await remoteStore.SaveCatalogAsync("skills", [catalogItem], default); await remoteStore.ReadAsync("skills", catalogItem, default);
        check(Data(await tools.CallAsync("local_skill_editor_list", Json("{}"), default)).GetProperty("skills").GetArrayLength() == 2 && downloads == 1, "authoring skills: already-downloaded catalog packages are available for editing");
        await remoteStore.ReadAsync("skills", catalogItem with { Version = "6", LatestVersion = "6" }, default);
        var historical = Data(await tools.CallAsync("local_skill_editor_inspect", Args(new { skillId = "remote-id", version = "6" }), default));
        check(historical.GetProperty("version").GetString() == "6" && historical.GetProperty("latestVersion").GetString() == "7"
            && downloads == 2, "authoring skills: older downloaded versions remain inspectable without another download");
        var remoteUpdate = Data(await tools.CallAsync("local_skill_editor_update_manifest", Args(new { skillId = "remote-id", instructions = "Local edit" }), default));
        var remoteHead = (await store.ListAsync("skills", default)).Single(s => s.Id == "remote-id");
        check(remoteUpdate.GetProperty("skillId").GetString() == "remote-id" && remoteHead.Version == "8" && downloads == 2
            && (await store.InspectAsync("skills", remoteHead, "7", default)).Draft.Instructions == "Downloaded instructions"
            && (await store.InspectAsync("skills", remoteHead, "6", default)).Draft.Instructions == "Downloaded instructions", "authoring skills: downloaded editing retains identity/all cached versions and never writes to remote service");
        await tools.CallAsync("local_skill_editor_delete_skill", Args(new { skillId = "remote-id" }), default);
        check((await remoteStore.DownloadedAsync("skills", default)).Count == 0, "authoring skills: delete removes downloaded copy instead of letting it reappear");
        await tools.CallAsync("local_skill_editor_delete_skill", Args(new { skillId = id }), default);
        check((await store.ListAsync("skills", default)).Count == 0 && Error(await tools.CallAsync("local_skill_editor_inspect", Args(new { skillId = id, version = "1" }), default)), "authoring skills: delete removes all versions");
        current = false;
        try { await tools.CallAsync("local_skill_editor_list", Json("{}"), default); throw new Exception("Expected cancellation"); }
        catch (OperationCanceledException) { check(true, "authoring skills: retained handler rejects changed partition"); }
    }
    private static async Task CheckImagesAsync(Action<bool, string> check, string root, LocalToolFiles files)
    {
        var png = LocalImageMask.Create(Json("""{"width":3,"height":2,"boxes":[{"x":1,"y":0,"w":1,"h":1}]}"""), default);
        var length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(33));
        using (var zip = new ZLibStream(new MemoryStream(png, 41, length), CompressionMode.Decompress))
        {
            using var pixels = new MemoryStream(); zip.CopyTo(pixels); var data = pixels.ToArray();
            check(data.Length == 26 && data[4] == 255 && data[8] == 0 && data[12] == 255 && data[21] == 255, "authoring images: PNG mask pixels use transparent edit holes and opaque keep regions");
        }
        check(ImageAttachments.Decode(Convert.ToBase64String(png)).Bytes.SequenceEqual(png), "authoring images: bare base64 API output is normalized without URL fetching");
        var library = new ImageLibraryStore(Path.Combine(root, "images"), "account"); var current = true; var calls = 0;
        ImagePreferences? seenSettings = null; IReadOnlyList<ComposerAttachment>? seenInputs = null; ComposerAttachment? seenMask = null; JsonObject? questionRequest = null;
        LocalImageGenerate generate = async (prefs, model, prompt, inputs, mask, completed, _) =>
        {
            calls++; seenSettings = prefs.Clone(); seenInputs = inputs; seenMask = mask;
            await completed(new(prefs.Request(model, prompt, inputs, mask, prefs.N), Args(new { images = Enumerable.Repeat(Convert.ToBase64String(png), prefs.N), response = new { modelId = model.Id } })));
        };
        var settings = new ImagePreferences { Size = "512x512", Seed = 42, MaxImagesPerCall = 1 };
        var tools = new LocalImageTools(library, files, settings, generate, (body, _, _) =>
        {
            questionRequest = (JsonObject)body.DeepClone(); return Task.FromResult(Json("""{"status":"completed","output":[{"type":"message","role":"assistant","content":[{"type":"output_text","text":"The center is transparent."}]}]}"""));
        }, () => current);
        var generated = await tools.CallAsync("local_images_generate", Args(new { model = "p/image", prompt = "Draw", n = 2, inputImages = Array.Empty<object>(), mask = (object?)null, size = (string?)null }), default);
        var refs = Data(generated).GetProperty("images");
        check(!Error(generated) && calls == 2 && refs.GetArrayLength() == 2 && seenSettings!.Size == "512x512" && seenSettings.Seed == 42
            && seenInputs!.Count == 0 && seenMask is null, "authoring images: nullable options use configured defaults, batching, and no invented masks");
        check(!Data(generated).GetRawText().Contains(Convert.ToBase64String(png)) && generated.GetProperty("content").GetArrayLength() == 3, "authoring images: bytes appear only in image content, never structured metadata");
        var listed = Data(await tools.CallAsync("local_images_list", Args(new { offset = 1, limit = (int?)null }), default));
        check(listed.GetProperty("total").GetInt32() == 2 && listed.GetProperty("items").GetArrayLength() == 1, "authoring images: fresh generation/index references and nullable pagination");
        var reference = refs[0].Clone();
        check(!Error(await tools.CallAsync("local_images_read", Args(new { image = reference }), default)), "authoring images: generate-to-read references work in the same turn");
        var stable = await library.SaveAsync(new(new ImagePreferences { N = 2 }.Request(new("p/image", "Image") { ModelType = "image" }, "Two", [], null, 2),
            Args(new { images = new[] { Convert.ToBase64String(png), Convert.ToBase64String(png) } })), [], null);
        var firstOutput = (await library.ListAsync()).Single(i => i.Generation.Id == stable.Id && i.ImageIndex == 0);
        await library.DeleteAsync(firstOutput);
        check(!Error(await tools.CallAsync("local_images_read", Args(new { image = new { source = "images", id = stable.Id, imageIndex = 1 } }), default))
            && Error(await tools.CallAsync("local_images_read", Args(new { image = new { source = "images", id = stable.Id, imageIndex = 0 } }), default)), "authoring images: deleting one output never retargets another saved imageIndex");
        var maskResult = Data(await tools.CallAsync("local_mask_create", Args(new { name = "edit-mask.png", width = 3, height = 2, boxes = new[] { new { x = 0, y = 0, w = 1, h = 2 } } }), default));
        var maskReference = maskResult.GetProperty("image").Clone();
        await tools.CallAsync("local_images_generate", Args(new { model = "p/image", prompt = "Refine", inputImages = new[] { reference }, mask = maskReference }), default);
        check(seenInputs!.Count == 1 && seenMask!.MediaType == "image/png", "authoring images: create-mask-to-refine uses native Files references and actual image inputs");
        var answer = await tools.CallAsync("local_images_ask", Args(new { model = "p/vision", question = "What is transparent?", images = new[] { reference } }), default);
        check(answer.GetProperty("content")[0].GetProperty("text").GetString() == "The center is transparent."
            && questionRequest!["tools"]!.AsArray().Count == 0 && questionRequest["input"]![0]!["content"]![1]!["type"]!.GetValue<string>() == "input_image", "authoring images: vision questions return only text and explicitly send selected images without tools");
        var oldCalls = calls;
        foreach (var invalid in new[] {
            Args(new { model = "bare", prompt = "Draw" }), Args(new { model = "p/image", prompt = "Draw", mask = maskReference }),
            Args(new { model = "p/image", prompt = "Draw", n = 101 }), Args(new { model = "p/image", prompt = "Draw", size = "invalid" }) })
            check(Error(await tools.CallAsync("local_images_generate", invalid, default)), "authoring images: invalid generation arguments fail before inference");
        check(calls == oldCalls && Error(await tools.CallAsync("local_images_read", Args(new { image = new { source = "files", id = "https://example/image.png" } }), default)), "authoring images: no inference for invalid requests and no arbitrary remote image fetching");
        var blocked = Path.Combine(root, "blocked-library"); await File.WriteAllTextAsync(blocked, "not a directory");
        var fallback = new LocalImageTools(new ImageLibraryStore(blocked, "account"), files, settings, generate, (_, _, _) => throw new Exception(), () => true);
        var fallbackResult = await fallback.CallAsync("local_images_generate", Args(new { model = "p/image", prompt = "Draw" }), default);
        check(!Error(fallbackResult) && Data(fallbackResult).GetProperty("images")[0].GetProperty("source").GetString() == "files"
            && !Error(await tools.CallAsync("local_images_read", Args(new { image = Data(fallbackResult).GetProperty("images")[0] }), default)), "authoring images: library failure falls back to reusable Files references without regeneration");
        var attempts = 0;
        var partial = new LocalImageTools(library, files, settings, async (p, m, text, inputs, mask, completed, ct) =>
        { if (++attempts == 2) throw new IOException("secret transport failure"); await generate(p, m, text, inputs, mask, completed, ct); }, (_, _, _) => throw new Exception(), () => true);
        var partialResult = await partial.CallAsync("local_images_generate", Args(new { model = "p/image", prompt = "Draw", n = 2 }), default);
        check(Error(partialResult) && Data(partialResult).GetProperty("count").GetInt32() == 1 && attempts == 2
            && !partialResult.GetRawText().Contains("secret"), "authoring images: later batch failure retains paid outputs and never retries or leaks errors");
        current = false;
        try { await tools.CallAsync("local_images_list", Json("{}"), default); throw new Exception("Expected cancellation"); }
        catch (OperationCanceledException) { check(true, "authoring images: changed account/root invalidates retained tools"); }
    }
    private static async Task CheckTransportAsync(Action<bool, string> check)
    {
        var host = new TestHost(); var runtime = new TestRuntime(); var session = new DesktopSession(host, runtime, new());
        using var handler = new ResponsesHandler(); using var http = new HttpClient(handler); var client = new LocalResponsesClient(new DesktopChatClient(session, http), http);
        var prefs = new ChatPreferences { ProviderHeaders = new() { ["p"] = new() { ["p-version"] = "test", ["Authorization"] = "must-not-leak" } } };
        var response = await client.CreateAsync(ServiceKind.Agents, new() { ["background"] = true }, () => true, default);
        check(runtime.LastService == ServiceKind.Agents && host.LastService == ServiceKind.Agents && handler.Path == "/v1/responses" && handler.Auth
            && response.GetProperty("status").GetString() == "queued", "authoring HTTP: agent Responses use configured agent runtime and host authentication");
        await client.CreateAsync(ServiceKind.Ai, new() { ["model"] = "p/vision" }, () => true, default, prefs, "p/vision");
        check(host.LastService == ServiceKind.Ai && handler.ProviderHeader && !handler.BadBearer, "authoring HTTP: vision uses AI runtime and scoped provider headers without overriding auth");
        handler.Fail = true;
        var before = handler.Calls;
        var failed = await DesktopLocalTools.SafeAsync(() => client.CreateAsync(ServiceKind.Ai, new(), () => true, default), default);
        check(Error(failed) && handler.Calls == before + 1 && !failed.GetRawText().Contains("secret"), "authoring HTTP: failed inference is sanitized and never retried");
        before = handler.Calls;
        try { await client.CreateAsync(ServiceKind.Ai, new(), () => false, default); throw new Exception("Expected cancellation"); }
        catch (OperationCanceledException) { check(handler.Calls == before, "authoring HTTP: stale partition never sends a request"); }
    }
    private static void CheckImagePolicy(Action<bool, string> check)
    {
        var part = PortableConversations.Part(JsonNode.Parse("""{"type":"tool-local_images_read","providerExecuted":false,"state":"output-available","toolCallId":"image","output":{"content":[{"type":"text","text":"saved"},{"type":"image","mimeType":"image/png","data":"retained"}],"structuredContent":{"image":{"source":"images","id":"reference"}}}}""")!.AsObject());
        var messages = new List<UIMessage> { new() { Id = "assistant", Role = Role.assistant, Parts = [part] } };
        var settings = new ChatPreferences();
        var filtered = settings.RequestBody("p/model", "chat", messages, "p");
        check(filtered["messages"]![0]!["parts"]![0]!["output"]!["content"]![1]!["text"]!.GetValue<string>() == "Image content omitted by user settings."
            && PortableConversations.Element(part).GetProperty("output").GetProperty("content")[1].GetProperty("data").GetString() == "retained", "authoring policy: default request filtering replaces image bytes without changing saved/UI results");
        settings.ToolOutputContentSettings.Images = true;
        check(settings.Clone().ToolOutputContentSettings.Images && settings.RequestBody("p/model", "chat", messages, "p")["messages"]![0]!["parts"]![0]!["output"]!["content"]![1]!["data"]!.GetValue<string>() == "retained",
            "authoring policy: explicit opt-in includes image content and survives cloning");
    }
    private sealed class ResponsesHandler : HttpMessageHandler
    {
        public int Calls; public string? Path; public bool Auth, ProviderHeader, BadBearer, Fail;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++; Path = request.RequestUri!.AbsolutePath; Auth = request.Headers.Contains("X-Test-Key"); ProviderHeader = request.Headers.Contains("p-version"); BadBearer = request.Headers.Contains("Authorization");
            return Task.FromResult(new HttpResponseMessage(Fail ? HttpStatusCode.InternalServerError : HttpStatusCode.OK)
            { Content = new StringContent(Fail ? "secret transport body" : "{\"id\":\"response\",\"status\":\"queued\"}", Encoding.UTF8, "application/json") });
        }
    }
}
