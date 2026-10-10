using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Desktop.Core;
using AIHappey.Vercel.Models;

internal static class SkillRegressionTests
{
    private const string Markdown = "---\nname: sample\ndescription: Sample skill\n---\n# Instructions\nRead references/info.md. Never run scripts automatically.";
    public static async Task RunAsync(Action<bool, string> check, string root)
    {
        var legacy = JsonSerializer.Deserialize<ChatPreferences>("{}")!;
        check(legacy.EnabledSkillIds.Count == 0, "skills: old settings default to no enabled skills");
        var preferences = new ChatPreferences { EnabledSkillIds = ["provider/sample", "provider/sample", "", "mcp:x:y"] };
        var clone = preferences.Clone(); clone.EnabledSkillIds.RemoveAt(0);
        check(preferences.EnabledSkillIds.Count == 2 && clone.EnabledSkillIds.Count == 1, "skills: selections normalize and clones isolate edits");
        var settings = new DesktopSettings { Chat = preferences }; await SettingsStore.SaveAsync(Path.Combine(root, "skill-settings"), settings);
        var loadedSettings = await SettingsStore.LoadAsync(Path.Combine(root, "skill-settings"), new());
        check(loadedSettings.Chat.EnabledSkillIds.SequenceEqual(preferences.EnabledSkillIds), "skills: enabled IDs round-trip through existing settings store");
        check(new ChatPreferences().EnabledSkillIds.Count == 0, "skills: restore defaults clears selections");

        var descriptor = new DesktopSkill("provider/sample", "sample", "Sample skill", "remote", "2");
        var bytes = Archive(("sample/SKILL.md", Encoding.UTF8.GetBytes(Markdown)), ("sample/references/info.md", Encoding.UTF8.GetBytes("Bundled reference")),
            ("sample/assets/image.png", new byte[] { 1, 2, 3 }), ("sample/scripts/run.py", Encoding.UTF8.GetBytes("print('never execute')")));
        var content = await SkillFiles.ArchiveAsync(descriptor, bytes, default);
        check(content.Body.StartsWith("# Instructions") && !content.Body.StartsWith("---") && content.ResourcePaths.Count == 3,
            "skills: remote activation strips frontmatter and lists resources without executing scripts");
        check(Encoding.UTF8.GetString((await content.Read("references/info.md", default)).Data) == "Bundled reference", "skills: confined bundled resource read");
        foreach (var path in new[] { "../secret", "references/../secret", "C:/secret", "/secret", "references//secret", "references/%2e%2e/secret", "references/%2fsecret" })
            await RejectAsync(() => content.Read(path, default), check, "skills: rejects resource traversal " + path);
        foreach (var bad in new[] {
            Archive(("../SKILL.md", Encoding.UTF8.GetBytes(Markdown))),
            Archive(("sample/SKILL.md", Encoding.UTF8.GetBytes(Markdown)), ("sample/SKILL.md", Encoding.UTF8.GetBytes(Markdown))),
            Archive(("sample/SKILL.md", Encoding.UTF8.GetBytes(Markdown)), ("other/SKILL.md", Encoding.UTF8.GetBytes(Markdown))),
            Archive(("wrong/SKILL.md", Encoding.UTF8.GetBytes(Markdown))),
            Archive(("sample/SKILL.md", Encoding.UTF8.GetBytes(Markdown)), ("sample/huge.txt", new byte[SkillFiles.MaxBytes + 1])) })
            await RejectAsync(() => SkillFiles.ArchiveAsync(descriptor, bad, default), check, "skills: rejects unsafe, duplicate, ambiguous or oversized archives");
        var parsedYaml = SkillFiles.Markdown("---\nname: sample\ndescription: >-\n  Folded description\nmetadata:\n  key: value\n---\nBody");
        check(parsedYaml.Frontmatter["description"]!.GetValue<string>() == "Folded description" && parsedYaml.Frontmatter["metadata"]!["key"]!.GetValue<string>() == "value",
            "skills: real YAML supports folded descriptions and nested metadata");

        var downloads = 0; var fail = false;
        var item = new CatalogItem(CatalogKind.Skill, descriptor.Id, descriptor.Name, descriptor.Description) { Version = "1", LatestVersion = "2" };
        var store = new DesktopSkillStore(Path.Combine(root, "skill-cache"), async (_, version, ct) =>
        { downloads++; await Task.Delay(10, ct); if (fail) throw new IOException("offline"); check(version == "2", "skills: latest version preferred over default"); return bytes; });
        await store.SaveCatalogAsync("account-api-a", [item], default);
        check((await store.CatalogAsync("account-api-a", default)).Single().Id == item.Id && (await store.CatalogAsync("account-api-b", default)).Count == 0,
            "skills: cached catalog isolated by account/API partition");
        await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => store.ReadAsync("account-api-a", item, default)));
        check(downloads == 1, "skills: concurrent archive reads deduplicate downloads");
        fail = true; await store.ReadAsync("account-api-a", item, default);
        check(downloads == 1, "skills: cached archive available offline");
        await RejectAsync(() => store.ReadAsync("account-api-b", item, default), check, "skills: another account cannot read the cached archive");
        fail = false; await store.ReadAsync("account-api-b", item, default);
        check(downloads == 3, "skills: failed downloads retry on first use");
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel(); await RejectCanceledAsync(() => store.ReadAsync("cancel", item, canceled.Token), check, "skills: canceled cache read does not publish an archive");
        }

        var turn = new DesktopSkillTurn([(descriptor, _ => Task.FromResult(content))]);
        var snapshot = McpTurnSnapshot.Empty; turn.Register(snapshot);
        check(snapshot.Tools.Count == 2 && snapshot.Contains("activate_skill") && snapshot.Contains("read_skill_resource"), "skills: enabled selection exposes both local tools");
        var context = JsonNode.Parse(turn.Context!.Value.GetRawText())!["availableSkills"]!;
        check(context["skillIdRequired"]!.GetValue<bool>() && context["skills"]![0]!["skill_id"]!.GetValue<string>() == descriptor.Id
            && !turn.Context.Value.GetRawText().Contains("# Instructions"), "skills: system context advertises exact IDs and descriptions, not eager instructions");
        var system = new DesktopSystemContextComposer().Compose(new(new(), new(), null, "en", false, "User instructions", DateTimeOffset.UtcNow) { Mcp = snapshot });
        check(system.Parts.Any(p => PortableConversations.Text(p).Contains("availableSkills")) && DesktopSystemContext.RequestMessages(ServiceKind.Agents, [], system).Count == 0,
            "skills: system context includes skill catalog only for model chat, Agents remains unchanged");
        var activated = await turn.CallAsync("activate_skill", Json("{\"skill_id\":\"provider/sample\"}"), default);
        check(!activated.GetProperty("isError").GetBoolean() && activated.GetProperty("structuredContent").GetProperty("skill").GetProperty("instructions").GetString() == content.Body,
            "skills: activation returns browser-compatible structured instructions");
        var textResult = await turn.CallAsync("read_skill_resource", Json("{\"skill_id\":\"provider/sample\",\"path\":\"references/info.md\"}"), default);
        check(textResult.GetProperty("structuredContent").GetProperty("skillResource").GetProperty("text").GetString() == "Bundled reference", "skills: text tool result parity");
        var binary = await turn.CallAsync("read_skill_resource", Json("{\"skill_id\":\"provider/sample\",\"path\":\"assets/image.png\"}"), default);
        check(binary.GetProperty("content")[1].GetProperty("resource").GetProperty("blob").GetString() == "AQID", "skills: binary tool result preserves embedded resource");
        var unknown = await turn.CallAsync("activate_skill", Json("{\"skill_id\":\"sample\"}"), default);
        check(unknown.GetProperty("isError").GetBoolean(), "skills: name cannot bypass exact enabled-ID authorization");
        var empty = McpTurnSnapshot.Empty; new DesktopSkillTurn([]).Register(empty);
        check(empty.Tools.Count == 0 && empty.Context.Count == 0, "skills: disabled selections expose no context or tools");
        var discovery = new McpDiscovery(Json("{}"), Json("{}"), null, [DesktopSkillTurn.ActivationDefinition, DesktopSkillTurn.ResourceDefinition]);
        var mcpCalls = 0;
        var collision = McpTurnSnapshot.Create([(new McpCatalogItem("server", "Server", "", "https://mcp.example/"), discovery,
            (Func<string, JsonElement, string, string, CancellationToken, Task<JsonElement>>)((_, _, _, _, _) => { mcpCalls++; return Task.FromResult(Json("{}")); }))]);
        turn.Register(collision);
        check(collision.Tools.Count == 4 && collision.Tools.Select(t => t.GetProperty("name").GetString()).Distinct().Count() == 4,
            "skills: reserved local names cannot collide with MCP tools");
        await collision.CallAsync("activate_skill", Json("{\"skill_id\":\"provider/sample\"}"), "call", "en", default);
        check(mcpCalls == 0, "skills: exact activation name routes locally, not to a colliding MCP server");
        var output = new ConversationMessage { Message = new UIMessage { Role = Role.assistant, Parts = [PortableConversations.Part(Json("""
            {"type":"tool-activate_skill","toolCallId":"activation","state":"input-available","input":{"skill_id":"provider/sample"}}
            """))] } };
        var executed = new HashSet<string>();
        check(await DesktopMcpToolExecution.ExecutePendingAsync(output, snapshot, executed, "en", default) == 1
            && PortableConversations.Element(output.Message.Parts[0]).GetProperty("state").GetString() == "output-available",
            "skills: local activation executes through existing active-turn continuation boundary");
        check(await DesktopMcpToolExecution.ExecutePendingAsync(output, snapshot, executed, "en", default) == 0, "skills: completed activation never executes twice");

        await CheckLocalAsync(check, root);
        await CheckMcpAsync(check, root);
        await CheckSdkDiscoveryAsync(check);
        foreach (var lang in new[] { "en", "nl" })
        {
            var document = System.Xml.Linq.XDocument.Load(typeof(SkillRegressionTests).Assembly.GetManifestResourceStream("Desktop.Resources." + lang)!);
            var values = document.Root!.Elements("data").ToDictionary(e => e.Attribute("name")!.Value, e => e.Element("value")!.Value);
            check(values["SkillLoadFailed"].Length > 0 && string.Format(values["DisableSkill"], "sample").Contains("sample"), "skills: localized UI/tool failure messages " + lang);
        }
    }
    private static async Task CheckLocalAsync(Action<bool, string> check, string root)
    {
        var draft = new DesktopSkillDraft { Name = "local-sample", Description = "Use when: \"quoted\" values, YAML: punctuation, or Unicode café appear.\nNext line.",
            Instructions = "# Instructions\nRead references/info.md.", Files = [new("references/info.md", Encoding.UTF8.GetBytes("reference")), new("assets/raw.bin", [0, 1, 255])] };
        draft.Frontmatter["license"] = "LICENSE.txt"; draft.Frontmatter["compatibility"] = "Windows";
        draft.Frontmatter["metadata"] = new JsonObject { ["version"] = "1.0", ["numeric"] = "123", ["boolean"] = "true" };
        draft.Frontmatter["allowed-tools"] = "Read"; draft.Frontmatter["custom"] = new JsonObject { ["enabled"] = true, ["limit"] = 12 };
        var bytes = DesktopSkillPackages.Export(draft); var imported = await DesktopSkillPackages.ImportAsync(bytes, default);
        check(imported.Diagnostics.Count == 0 && imported.Skills.Count == 1, "local skills: generated YAML/ZIP round trip validates");
        var roundTrip = imported.Skills.Single();
        check(JsonNode.DeepEquals(draft.Frontmatter, roundTrip.Frontmatter) && roundTrip.Description == draft.Description
            && roundTrip.Files.Single(f => f.Path == "assets/raw.bin").Data.SequenceEqual(new byte[] { 0, 1, 255 }),
            "local skills: YAML special characters, optional/extension fields, and binary files preserved");
        using (var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read))
            check(archive.Entries.All(e => e.FullName.StartsWith("local-sample/")) && archive.GetEntry("local-sample/SKILL.md") is not null,
                "local skills: exported parent directory matches name and canonical manifest casing");
        var empty = draft.Clone(); empty.Instructions = "";
        var tagged = SkillFiles.Markdown("\uFEFF---\r\nname: !!str 123\r\ndescription: !!str true\r\nmetadata:\r\n  version: !!str 1\r\n---\r\nBody");
        check(tagged.Frontmatter["name"]!.GetValue<string>() == "123" && tagged.Frontmatter["metadata"]!["version"]!.GetValue<string>() == "1",
            "local skills: BOM, CRLF, and explicitly tagged YAML strings are supported");
        check((await DesktopSkillPackages.ImportAsync(DesktopSkillPackages.Export(empty), default)).Skills.Single().Instructions == "",
            "local skills: empty Markdown body is valid");
        foreach (var name in new[] { "", "Upper", "a--b", "-name", "name-", new string('a', 65), "a/b" })
        {
            var invalid = draft.Clone(); invalid.Name = name;
            await RejectAsync(() => Task.FromResult(DesktopSkillPackages.Export(invalid)), check, "local skills: strict name rules " + name);
        }
        foreach (var description in new[] { "", "  ", new string('a', 1025) })
        {
            var invalid = draft.Clone(); invalid.Description = description;
            await RejectAsync(() => Task.FromResult(DesktopSkillPackages.Export(invalid)), check, "local skills: description constraints");
        }
        foreach (var mutation in new Action<JsonObject>[] {
            f => f["compatibility"] = "", f => f["compatibility"] = new string('a', 501), f => f["license"] = 12,
            f => f["metadata"] = new JsonObject { ["key"] = 1 }, f => f["allowed-tools"] = new JsonArray("Read") })
        {
            var invalid = draft.Clone(); mutation(invalid.Frontmatter);
            await RejectAsync(() => Task.FromResult(DesktopSkillPackages.Export(invalid)), check, "local skills: optional field constraints");
        }
        foreach (var path in new[] { "SKILL.md", "skill.md", "nested/SKILL.md", "../bad", "/bad", "C:/bad", "a//b", "a/%2e%2e/b" })
        {
            var invalid = draft.Clone(); invalid.Files.Add(new(path, [1]));
            await RejectAsync(() => Task.FromResult(DesktopSkillPackages.Export(invalid)), check, "local skills: reserved/unsafe path " + path);
        }
        var conflict = draft.Clone(); conflict.Files.Add(new("REFERENCES/INFO.md", [1]));
        await RejectAsync(() => Task.FromResult(DesktopSkillPackages.Export(conflict)), check, "local skills: case-insensitive resource duplicate rejected");
        conflict = draft.Clone(); conflict.Files.Add(new("references", [1]));
        await RejectAsync(() => Task.FromResult(DesktopSkillPackages.Export(conflict)), check, "local skills: file/directory conflict rejected");
        foreach (var bad in new[] {
            Archive(("../SKILL.md", Encoding.UTF8.GetBytes(Markdown))),
            Archive(("sample/SKILL.md", Encoding.UTF8.GetBytes(Markdown)), ("sample/skill.md", [1])),
            Archive(("sample/SKILL.md", Encoding.UTF8.GetBytes(Markdown)), ("sample/other/SKILL.md", Encoding.UTF8.GetBytes(Markdown))),
            Archive(("sample/skill.md", Encoding.UTF8.GetBytes(Markdown))) })
            await RejectAsync(() => DesktopSkillPackages.ImportAsync(bad, default), check, "local skills: unsafe/ambiguous archives rejected");
        var collection = Archive(("collection/sample/SKILL.md", Encoding.UTF8.GetBytes(Markdown)),
            ("collection/second/SKILL.md", Encoding.UTF8.GetBytes(Markdown.Replace("name: sample", "name: second"))),
            ("collection/broken/SKILL.md", Encoding.UTF8.GetBytes("invalid")));
        var parsedCollection = await DesktopSkillPackages.ImportAsync(collection, default);
        check(parsedCollection.Skills.Count == 2 && parsedCollection.Diagnostics.Count == 1, "local skills: collection import retains valid skills and reports invalid manifest");

        var store = new DesktopLocalSkillStore(Path.Combine(root, "local-skills"));
        var first = await store.SaveAsync("account-a", draft, null, default);
        check(first.Id.StartsWith("local:") && first.Version == "1" && !CatalogRoutes.SupportsSkill(first.Id), "local skills: stable local IDs cannot route to remote API");
        check((await store.ListAsync("account-a", default)).Count == 1 && (await store.ListAsync("account-b", default)).Count == 0,
            "local skills: account/API isolation");
        await RejectAsync(() => store.ReadAsync("account-b", first, default), check, "local skills: cross-account archive access rejected");
        await RejectAsync(() => store.SaveAsync("account-a", draft, null, default), check, "local skills: duplicate create does not overwrite");
        var edited = draft.Clone(); edited.Description = "Edited"; edited.Files.RemoveAt(0);
        var second = await store.SaveAsync("account-a", edited, first.Id, default);
        check(second.Id == first.Id && second.Version == "2" && (await store.ListAsync("account-a", default)).Single().Version == "2",
            "local skills: edit appends version and advances stable head");
        check((await store.ReadAsync("account-a", first, default)).Description == draft.Description
            && (await store.ReadAsync("account-a", second, default)).Files.Count == 1, "local skills: earlier versions remain immutable and file removals persist");
        var renamed = edited.Clone(); renamed.Name = "renamed";
        await RejectAsync(() => store.SaveAsync("account-a", renamed, first.Id, default), check, "local skills: persisted names are immutable");
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel(); await RejectCanceledAsync(() => store.SaveAsync("account-a", edited, first.Id, canceled.Token), check, "local skills: canceled save cannot publish");
        }
        check((await store.ListAsync("account-a", default)).Single().Version == "2", "local skills: failed/canceled writes preserve prior head");
        var folder = Path.Combine(root, "local-skills", McpValidation.Hash("account-a"), McpValidation.Hash(first.Id));
        var blockedVersion = Path.Combine(folder, "3.zip"); Directory.CreateDirectory(blockedVersion);
        await RejectAsync(() => store.SaveAsync("account-a", edited, first.Id, default), check, "local skills: publication disk failure is surfaced");
        Directory.Delete(blockedVersion);
        check((await store.ReadAsync("account-a", second, default)).Description == "Edited", "local skills: publication failure leaves previous version intact");
        var contents = DesktopSkillPackages.Content(second, await store.ReadAsync("account-a", second, default));
        var reads = 0; var turn = new DesktopSkillTurn([(second, ct => { reads++; return Task.FromResult(contents); })]);
        var runtime = McpTurnSnapshot.Empty; turn.Register(runtime);
        check(reads == 0 && runtime.Context.Single().GetRawText().Contains(first.Id) && !runtime.Context.Single().GetRawText().Contains("# Instructions"),
            "local skills: catalog disclosure is lazy and excludes body");
        var activated = await turn.CallAsync("activate_skill", JsonSerializer.SerializeToElement(new { skill_id = second.Id }), default);
        check(reads == 1 && !activated.GetProperty("isError").GetBoolean(), "local skills: enabled local skill activates through existing tool");
        await store.DeleteAsync("account-a", first.Id, default);
        check((await store.ListAsync("account-a", default)).Count == 0, "local skills: delete removes all versions");
        await RejectAsync(() => store.ReadAsync("account-a", second, default), check, "local skills: deleted version is unavailable");
        foreach (var lang in new[] { "en", "nl" })
        {
            var document = System.Xml.Linq.XDocument.Load(typeof(SkillRegressionTests).Assembly.GetManifestResourceStream("Desktop.Resources." + lang)!);
            var values = document.Root!.Elements("data").ToDictionary(e => e.Attribute("name")!.Value, e => e.Element("value")!.Value);
            check(new[] { "SkillCreate", "SkillEdit", "SkillImport", "SkillFilesHint", "SkillNameInvalid", "SkillDeleteConfirm", "SkillLocalInlineHint" }.All(k => values[k].Length > 0),
                "local skills: editor actions and validation localized " + lang);
        }
    }
    private static async Task CheckMcpAsync(Action<bool, string> check, string root)
    {
        var uri = "skill://catalog/sample/SKILL.md"; var referenceUri = "skill://catalog/sample/references/info.md";
        var data = new Dictionary<string, byte[]> { [uri] = Encoding.UTF8.GetBytes(Markdown), [referenceUri] = Encoding.UTF8.GetBytes("MCP reference") };
        var manifestJson = JsonSerializer.SerializeToElement(new { uri, frontmatter = new { name = "sample", description = "Sample skill" },
            resources = data.Select(p => new { uri = p.Key, size = p.Value.Length, digest = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(p.Value)) }).ToArray() });
        var manifest = McpSkillManifest.Parse(manifestJson); var connected = true; var reads = 0; var corrupt = false;
        Task<JsonElement> Read(string requested, CancellationToken ct)
        { reads++; ct.ThrowIfCancellationRequested(); return Task.FromResult(JsonSerializer.SerializeToElement(new { contents = new[] { new { uri = requested,
            text = corrupt ? "tampered" : Encoding.UTF8.GetString(data[requested]) } } })); }
        var skill = new DesktopMcpSkill("https://mcp.example/", manifest, Read, () => connected);
        check(reads == 0 && skill.Descriptor.Id == "mcp:https%3A%2F%2Fmcp.example%2F:skill%3A%2F%2Fcatalog%2Fsample%2FSKILL.md", "skills: browser-compatible MCP identity and lazy discovery");
        var content = await skill.LoadAsync(default);
        check(reads == 1 && content.Body == Markdown && content.ResourcePaths.SequenceEqual(["references/info.md"]), "skills: MCP activation reads only verified manifest, preserving complete markdown");
        check(Encoding.UTF8.GetString((await content.Read("references/info.md", default)).Data) == "MCP reference", "skills: MCP declared resource digest and size verified");
        var before = reads;
        await RejectAsync(() => content.Read("secret.txt", default), check, "skills: undeclared MCP resource rejected");
        check(reads == before, "skills: undeclared resource never reaches server");
        corrupt = true; await RejectAsync(() => skill.LoadAsync(default), check, "skills: MCP integrity mismatch rejected"); corrupt = false;
        connected = false; await RejectAsync(() => content.Read("references/info.md", default), check, "skills: retained reader rejects disconnected/disabled MCP skill"); connected = true;
        var changed = new DesktopMcpSkill("server", manifest with { Frontmatter = new JsonObject { ["name"] = "sample", ["description"] = "Different" } }, Read, () => true);
        await RejectAsync(() => changed.LoadAsync(default), check, "skills: MCP frontmatter mismatch rejected even with valid digest");
        var dynamic = McpSkillManifest.Parse(Json("""{"uri":"skill://catalog/sample/SKILL.md","frontmatter":{"name":"sample","description":"Sample skill"},"resources":"dynamic"}"""));
        var dynamicContent = await new DesktopMcpSkill("server", dynamic, Read, () => true).LoadAsync(default);
        check(dynamicContent.ResourcePaths.Count == 0 && Encoding.UTF8.GetString((await dynamicContent.Read("references/info.md", default)).Data) == "MCP reference",
            "skills: dynamic MCP skills allow confined lazy resource reads");
        foreach (var malformed in new[] {
            """{"uri":"skill://catalog/wrong/SKILL.md","frontmatter":{"name":"sample","description":"Sample skill"},"resources":"dynamic"}""",
            """{"uri":"skill://catalog/sample/SKILL.md","frontmatter":{"name":"sample","description":"Sample skill"},"resources":[]}""" })
            await RejectAsync(() => Task.FromResult(McpSkillManifest.Parse(Json(malformed))), check, "skills: malformed MCP manifest rejected");
        var preference = new ModelContextPreferences();
        await using var manager = new DesktopMcpManager(new FakeFactory(manifest, Read), new DesktopMcpStore(Path.Combine(root, "skill-mcp")), () => preference);
        await manager.LoadAsync("account", default);
        await manager.InstallAsync(new DesktopMcpServer { Id = "server", Name = "Server", Url = "https://mcp.example/", Enabled = true }, default);
        var retained = manager.CaptureSkills().Single(); preference.EnableSkills = false;
        check(manager.CaptureSkills().Count == 0, "skills: global MCP Skills preference removes discovery from runtime selection");
        await RejectAsync(() => retained.LoadAsync(default), check, "skills: global preference enforced on in-flight retained readers");
        preference.EnableSkills = true; await manager.SetEnabledAsync("server", false, default);
        await RejectAsync(() => retained.LoadAsync(default), check, "skills: manager epoch prevents old readers after disconnect");
    }
    private sealed class FakeFactory(McpSkillManifest manifest, Func<string, CancellationToken, Task<JsonElement>> read) : IDesktopMcpClientFactory
    {
        public Task<IDesktopMcpConnection> ConnectAsync(DesktopMcpServer server, CancellationToken ct) => Task.FromResult<IDesktopMcpConnection>(new FakeConnection(manifest, read));
    }
    private static async Task CheckSdkDiscoveryAsync(Action<bool, string> check)
    {
        foreach (var mode in new[] { "enabled", "disabled", "no-extension", "no-resources", "repeated-cursor" })
        {
            var handler = new SkillHttpHandler(mode);
            var factory = new DesktopMcpClientFactory(new SkillAuthentication(handler));
            factory.Configure(() => new ModelContextPreferences { EnableSkills = mode != "disabled" }, (_, _, _) => throw new Exception("No elicitation expected"));
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            await using var connection = await factory.ConnectAsync(new DesktopMcpServer { Id = "sdk", Name = "SDK", Url = "https://skills-test.invalid/" }, stop.Token);
            if (mode == "repeated-cursor")
            {
                try { await connection.DiscoverAsync(stop.Token); throw new Exception("Expected repeated cursor rejection"); }
                catch (InvalidOperationException) { check(true, "skills: actual SDK discovery rejects repeated extension cursors"); }
            }
            else
            {
                var discovery = await connection.DiscoverAsync(stop.Token);
                check(mode == "enabled" ? discovery.Skills.Count == 1 && handler.SkillRequests == 2 : discovery.Skills.Count == 0 && handler.SkillRequests == 0,
                    "skills: actual SDK extension discovery capability/preference gating " + mode);
            }
        }
    }
    private sealed class SkillAuthentication(HttpMessageHandler handler) : IDesktopMcpAuthentication
    {
        public Task<HttpMessageHandler> ConfigureAsync(DesktopMcpServer server, ModelContextProtocol.Client.HttpClientTransportOptions options, CancellationToken ct) => Task.FromResult(handler);
    }
    private sealed class SkillHttpHandler(string mode) : HttpMessageHandler
    {
        public int SkillRequests;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method != HttpMethod.Post) return new(System.Net.HttpStatusCode.MethodNotAllowed);
            var message = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject();
            if (message["id"] is null) return new(System.Net.HttpStatusCode.Accepted);
            var method = message["method"]?.GetValue<string>(); JsonNode result;
            if (method == "server/discover")
            {
                var capabilities = new JsonObject();
                if (mode != "no-resources") capabilities["resources"] = new JsonObject();
                if (mode != "no-extension") capabilities["extensions"] = new JsonObject { ["io.modelcontextprotocol/skills"] = new JsonObject() };
                result = new JsonObject { ["supportedVersions"] = new JsonArray("2026-07-28"), ["capabilities"] = capabilities,
                    ["_meta"] = new JsonObject { ["io.modelcontextprotocol/serverInfo"] = new JsonObject { ["name"] = "Skills fixture", ["version"] = "1" } } };
            }
            else if (method == "resources/list") result = new JsonObject { ["resources"] = new JsonArray() };
            else if (method == "resources/templates/list") result = new JsonObject { ["resourceTemplates"] = new JsonArray() };
            else if (method == "skills/list")
            {
                SkillRequests++;
                result = SkillRequests == 1 ? JsonNode.Parse("""{"skills":[{"uri":"skill://catalog/sample/SKILL.md","frontmatter":{"name":"sample","description":"Sample skill"},"resources":"dynamic"}],"nextCursor":"next"}""")!
                    : JsonNode.Parse(mode == "repeated-cursor" ? """{"skills":[],"nextCursor":"next"}""" : """{"skills":[]}""")!;
            }
            else throw new Exception("Unexpected skill SDK request " + method);
            return new(System.Net.HttpStatusCode.OK) { Content = new StringContent(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = message["id"]!.DeepClone(), ["result"] = result }.ToJsonString(), Encoding.UTF8, "application/json") };
        }
    }
    private sealed class FakeConnection(McpSkillManifest manifest, Func<string, CancellationToken, Task<JsonElement>> read) : IDesktopMcpConnection
    {
        public event Action? ToolsChanged { add { } remove { } }
        public event Action? ResourcesChanged { add { } remove { } }
        public Task<McpDiscovery> DiscoverAsync(CancellationToken ct) => Task.FromResult(new McpDiscovery(Json("{}"), Json("{}"), null, []) { Skills = [manifest] });
        public Task<JsonElement> ReadAsync(string uri, string? cursor, int limit, CancellationToken ct) => read(uri, ct);
        public Task<JsonElement> CallAsync(string name, JsonElement input, string callId, string locale, CancellationToken ct) => throw new Exception("No server tools expected");
        public Task<IReadOnlyList<string>> CompleteAsync(string template, string name, string value, IReadOnlyDictionary<string, string> args, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>([]);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private static byte[] Archive(params (string Path, byte[] Data)[] files)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
            foreach (var file in files) { using var output = zip.CreateEntry(file.Path).Open(); output.Write(file.Data); }
        return stream.ToArray();
    }
    private static JsonElement Json(string text) => JsonSerializer.Deserialize<JsonElement>(text);
    private static async Task RejectAsync(Func<Task> action, Action<bool, string> check, string name)
    {
        try { await action(); } catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException or YamlDotNet.Core.YamlException) { check(true, name); return; }
        throw new Exception("Expected rejection: " + name);
    }
    private static async Task RejectCanceledAsync(Func<Task> action, Action<bool, string> check, string name)
    {
        try { await action(); } catch (OperationCanceledException) { check(true, name); return; }
        throw new Exception("Expected cancellation: " + name);
    }
}
