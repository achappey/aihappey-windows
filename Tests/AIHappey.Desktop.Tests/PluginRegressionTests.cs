using AIHappey.Desktop.Core;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

internal static class PluginRegressionTests
{
    private static DesktopAgentPlugin Package(string name = "test.plugin") => new() { Manifest = new() { ["$schema"] = AgentPluginPackages.Schema, ["name"] = name },
        Files = [new("skills/greet/SKILL.md", Encoding.UTF8.GetBytes("---\nname: greet\ndescription: Greet the user.\n---\nSay hello.")), new("references/info.txt", Encoding.UTF8.GetBytes("package data")), new("skills/greet/references/info.txt", Encoding.UTF8.GetBytes("skill data"))] };
    public static async Task RunAsync(Action<bool, string> check, string root)
    {
        foreach (var language in new[] { "en", "nl" })
        {
            using var stream = typeof(PluginRegressionTests).Assembly.GetManifestResourceStream("Desktop.Resources." + language)!;
            var resourceNames = XDocument.Load(stream).Root!.Elements("data").Select(d => (string)d.Attribute("name")!).ToHashSet(StringComparer.Ordinal);
            foreach (var key in new[] { "Search", "Description", "All", "Favorites", "Name", "Edit", "Close", "NoResults", "PluginsTitle", "PluginsDescription", "PluginWebsite", "PluginSpecification", "PluginKeywords", "PluginAuthor", "PluginCounts", "PluginDetails", "PluginFavorite", "PluginUnfavorite", "PluginUnavailable", "PluginCreate", "PluginVersion", "PluginHomepage", "PluginRepository", "PluginLicense", "PluginAuthorName", "PluginAuthorEmail", "PluginAuthorUrl", "PluginTransportHint", "PluginMcpCatalog", "PluginEndpoint", "PluginAllServers", "PluginFilesHint", "PluginAddFiles", "PluginDiagnostics", "PluginOperationFailed", "PluginSetting_defer_loading", "PluginSetting_namespace", "PluginSetting_direct", "PluginSetting_programmatic", "PluginConfiguration", "PluginApplyConfiguration", "PluginImport", "PluginLoadFolder", "PluginImported", "PluginZipOnly", "PluginArchive", "PluginDeleteConfirm", "PluginDisable", "PluginEditorTools", "PluginAgentEmpty", "PluginAgentSnapshotHint", "PluginEmbedded", "PluginFromAgent", "PluginAgentConverted" })
                check(resourceNames.Contains(key), language + " plugin native resource " + key);
        }
        void Reject(Action action, string label) { try { action(); } catch (Exception e) when (e is InvalidDataException or JsonException or ArgumentException) { check(true, label); return; } throw new Exception("Expected rejection: " + label); }
        async Task RejectAsync(Func<Task> action, string label) { try { await action(); } catch (Exception e) when (e is InvalidDataException or JsonException or ArgumentException) { check(true, label); return; } throw new Exception("Expected rejection: " + label); }
        var package = Package();
        foreach (var name in new[] { "a", "acme.tools", "my-plugin", "lint3r", new string('a', 64) }) check(AgentPluginPackages.ValidName(name), "valid plugin name " + name);
        foreach (var name in new[] { "", "My-Plugin", "-start", "end-", "has--double", "two..dots", "a\n", new string('a', 65) }) check(!AgentPluginPackages.ValidName(name), "invalid plugin name " + name);
        package.Manifest["unknown"] = true; package.Manifest["extensions"] = "wrong"; package.Manifest["version"] = "not semver"; package.Manifest["homepage"] = "not a URL";
        var valid = AgentPluginPackages.Validate(package);
        check(valid.Diagnostics.Count == 2 && !valid.Manifest.ContainsKey("unknown") && !valid.Manifest.ContainsKey("extensions"), "nonfatal unknown field and extensions diagnostics");
        package.Manifest["extensions"] = new JsonObject { ["com.unknown.client"] = new JsonArray(1, "opaque") };
        valid = AgentPluginPackages.Validate(package); check(valid.Manifest["extensions"]?["com.unknown.client"] is JsonArray, "unknown namespace values remain opaque");
        foreach (var field in new[] { "version", "description", "homepage", "repository", "license", "author", "keywords" })
        { var copy = Package(); copy.Manifest[field] = 1; Reject(() => AgentPluginPackages.Validate(copy), "fatal wrong type " + field); }
        var invalidAuthor = Package(); invalidAuthor.Manifest["author"] = new JsonObject { ["extra"] = "not allowed" }; Reject(() => AgentPluginPackages.Validate(invalidAuthor), "closed author object");
        foreach (var path in new[] { "../escape", "C:/escape", "/escape", "a/../b", "a//b", "a/CON.txt", "a/ends.", "a/space ", "a:stream", "a/%2e%2e/b" }) Reject(() => AgentPluginPackages.PackagePath(path), "unsafe Windows package path " + path);
        var duplicated = Package(); duplicated.Files.Add(new("REFERENCES/info.txt", [1])); Reject(() => AgentPluginPackages.Validate(duplicated), "case insensitive duplicate file");
        var collision = Package(); collision.Files.Add(new("references", [1])); Reject(() => AgentPluginPackages.Validate(collision), "file directory collision");
        var badSkill = Package(); badSkill.Files.Add(new("skills/bad/SKILL.md", Encoding.UTF8.GetBytes("not a skill"))); badSkill.Files.Add(new("skills/nested/deep/SKILL.md", Encoding.UTF8.GetBytes("---\nname: deep\ndescription: Nested\n---\nIgnored.")));
        var inspected = AgentPluginPackages.Validate(badSkill); check(inspected.Skills().Count == 1 && inspected.Diagnostics.Any(d => d.Boundary == "skill"), "bad skill skipped; no recursive discovery");
        var servers = new JsonObject {
            ["remote"] = new JsonObject { ["type"] = "streamable-http", ["url"] = "https://example.org/mcp?tenant=public", ["headers"] = new JsonObject { ["X-Tenant"] = "public" } },
            ["local"] = new JsonObject { ["type"] = "stdio", ["command"] = "./bin/server.exe", ["args"] = new JsonArray("${PLUGIN_ROOT}/config", "${UNKNOWN}"), ["cwd"] = "${PLUGIN_DATA}/cache", ["env"] = new JsonObject { ["DATA"] = "${PLUGIN_DATA}" } },
            ["legacy"] = new JsonObject { ["type"] = "sse", ["url"] = "https://example.org/sse" },
            ["bad"] = new JsonObject { ["type"] = "unknown" } };
        var mcp = new JsonObject { ["$schema"] = AgentPluginPackages.McpSchema, ["mcpServers"] = servers };
        var diagnostics = new List<PluginDiagnostic>(); var config = AgentPluginPackages.McpConfiguration(mcp, diagnostics)!;
        check(((JsonObject)config["mcpServers"]!).Count == 3 && diagnostics.Count == 3, "MCP per-entry isolation and preserved unsupported transports");
        foreach (var url in new[] { "http://example.org/mcp", "https://user:pass@example.org/mcp", "https://example.org/mcp#fragment", "file:///tmp/server", "http://localhost.example.org" }) Reject(() => AgentPluginPackages.Server("x", new JsonObject { ["type"] = "streamable-http", ["url"] = url }), "unsafe MCP URL " + url);
        foreach (var url in new[] { "http://localhost/mcp", "http://127.0.0.2/mcp", "http://[::1]/mcp", "https://example.org/mcp" }) check(AgentPluginPackages.Server("x", new JsonObject { ["type"] = "streamable-http", ["url"] = url }) is not null, "valid MCP URL " + url);
        foreach (var command in new[] { "node script.js", "../server", "C:\\server.exe", "./../server", "node&calc", "${PLUGIN_ROOT}/bin/server" }) Reject(() => AgentPluginPackages.Server("x", new JsonObject { ["type"] = "stdio", ["command"] = command }), "unsafe stdio command " + command);
        Reject(() => AgentPluginPackages.Server("x", new JsonObject { ["type"] = "stdio", ["command"] = "node", ["env"] = new JsonObject { ["plugin_root"] = "override" } }), "Windows case-insensitive reserved env");
        Reject(() => AgentPluginPackages.Server("x", new JsonObject { ["type"] = "streamable-http", ["url"] = "https://example.org", ["headers"] = new JsonObject { ["X-Test"] = "a", ["x-test"] = "b" } }), "case-insensitive duplicate headers");
        Reject(() => AgentPluginPackages.Server("x", new JsonObject { ["type"] = "streamable-http", ["url"] = "https://example.org", ["headers"] = new JsonObject { ["X-Test"] = "bad\r\nheader" } }), "header injection rejected");
        var wrongMcp = (JsonObject)mcp.DeepClone(); wrongMcp["$schema"] = "https://agent-plugins.org/schemas/2.0.0/mcp.schema.json"; diagnostics.Clear(); check(AgentPluginPackages.McpConfiguration(wrongMcp, diagnostics) is null && diagnostics.Single().Boundary == "mcp", "MCP schema mismatch disables only MCP");
        package = Package(); package.Mcp = config; package.Manifest["extensions"] = new JsonObject { ["com.unknown.client"] = new JsonObject { ["custom"] = 42 } };
        var bytes = AgentPluginPackages.Export(package); var roundTrip = (await AgentPluginPackages.ImportAsync(bytes)).Single();
        check(roundTrip.Id == package.Id && roundTrip.Skills().Count == 1 && roundTrip.Files.Count == 3 && roundTrip.Mcp?["mcpServers"]?["local"] is JsonObject && JsonNode.DeepEquals(roundTrip.Manifest["extensions"], package.Manifest["extensions"]), "browser-compatible ZIP roundtrip preserves opaque extensions/files/stdio");
        check(bytes.SequenceEqual(AgentPluginPackages.Export(package)), "deterministic ZIP export");
        byte[] Archive(params (string Path, byte[] Data, int Attributes)[] entries)
        { using var memory = new MemoryStream(); using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true)) foreach (var entry in entries) { var item = zip.CreateEntry(entry.Path); item.ExternalAttributes = entry.Attributes; using var stream = item.Open(); stream.Write(entry.Data); } return memory.ToArray(); }
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(Package().Manifest);
        foreach (var unsafePath in new[] { "../outside", "C:/outside", "x\\y", "plugin.json:stream" }) await RejectAsync(() => AgentPluginPackages.ImportAsync(Archive(("plugin.json", manifestBytes, 0), (unsafePath, [1], 0))), "ZIP path rejection " + unsafePath);
        await RejectAsync(() => AgentPluginPackages.ImportAsync(Archive(("plugin.json", manifestBytes, 0), ("link", [1], 0xa000 << 16))), "ZIP symlink rejection");
        await RejectAsync(() => AgentPluginPackages.ImportAsync(Archive(("plugin.json", manifestBytes, 0), ("PLUGIN.JSON", manifestBytes, 0))), "ZIP case-insensitive duplicates");
        var wrapped = await AgentPluginPackages.ImportAsync(Archive(("wrapper/plugin.json", manifestBytes, 0), ("wrapper/reference.txt", [1], 0))); check(wrapped.Single().Files.Single().Path == "reference.txt", "wrapped ZIP root loading");
        var dir = Path.Combine(root, "plugin-dir"); Directory.CreateDirectory(Path.Combine(dir, "skills", "greet")); await File.WriteAllBytesAsync(Path.Combine(dir, "plugin.json"), manifestBytes); await File.WriteAllBytesAsync(Path.Combine(dir, "skills", "greet", "SKILL.md"), Package().Files[0].Data);
        var directoryPackage = await AgentPluginPackages.LoadDirectoryAsync(dir); check(directoryPackage.Skills().Count == 1, "directory conformance floor");
        var outside = Path.Combine(root, "outside"); Directory.CreateDirectory(outside); await File.WriteAllTextAsync(Path.Combine(outside, "secret.txt"), "must not read");
        var junction = Path.Combine(dir, "references");
        using (var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{junction}\" \"{outside}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true })) { await process!.WaitForExitAsync(); check(process.ExitCode == 0, "junction fixture created"); }
        var escaped = await AgentPluginPackages.LoadDirectoryAsync(dir); check(!escaped.Files.Any(f => f.Path.EndsWith("secret.txt", StringComparison.Ordinal)) && escaped.Diagnostics.Any(d => d.Path == "references"), "resolved junction escape denied"); Directory.Delete(junction);
        var store = new DesktopAgentPluginStore(Path.Combine(root, "plugins")); var saved = await store.SaveAsync("account-a", package, false);
        check((await store.ListAsync("account-b")).Count == 0 && (await store.ListAsync("account-a")).Count == 1, "plugin store account isolation");
        await RejectAsync(() => store.SaveAsync("account-a", package, false), "create cannot overwrite");
        await store.FavoriteAsync("account-a", package.Id, true); package.Manifest["description"] = "updated"; saved = await store.SaveAsync("account-a", package, true);
        check(saved.CreatedAt > 0 && saved.Favorite && saved.Description == "updated", "replacement preserves favorite and creation time");
        var skill = saved.Skills().Single(); var content = saved.Content(skill); check(content.Body == "Say hello." && Encoding.UTF8.GetString((await content.Read("references/info.txt", default)).Data) == "skill data", "plugin skill activation/resource snapshot");
        Reject(() => saved.Content(skill).Read("../reference", default).GetAwaiter().GetResult(), "skill resource containment");
        var read = AgentPluginTools.ReadPackage([saved], JsonSerializer.SerializeToElement(new { plugin_name = saved.Id, path = "references/info.txt" }), () => true, default);
        check(read.GetProperty("structuredContent").GetProperty("pluginFile").GetProperty("text").GetString() == "package data", "read_plugin_file browser contract");
        Reject(() => AgentPluginTools.ReadPackage([saved], JsonSerializer.SerializeToElement(new { plugin_name = saved.Id, path = "skills/greet/SKILL.md" }), () => true, default), "package read separates skill files");
        var embedded = DesktopAgentPluginSnapshots.Embed(saved); var embeddedCopy = await DesktopAgentPluginSnapshots.ReadAsync(embedded, default); await store.DeleteAsync("account-a", saved.Id); check(embeddedCopy!.Id == saved.Id && (await store.ListAsync("account-a")).Count == 0, "agent snapshot survives library deletion");
        var changed = 0; var current = true;
        var tools = new AgentPluginTools(store, "account-a", () => current, _ => { changed++; return Task.CompletedTask; }, (_, _, _) => Task.FromResult<IReadOnlyList<DesktopSkillFile>>(Package().Files.Where(f => f.Path.StartsWith("skills/", StringComparison.Ordinal)).ToArray()), _ => Task.FromResult<IReadOnlyList<DesktopSkill>>([new("catalog/greet", "greet", "Greet", "remote")]));
        Task<JsonElement> Call(string action, object input) => tools.CallAsync("local_agent_plugin_editor_" + action, JsonSerializer.SerializeToElement(input), default);
        check(!(await Call("create", new { name = "tool-plugin", description = "created" })).GetProperty("isError").GetBoolean(), "plugin editor create contract");
        await Call("upsert_file", new { pluginId = "tool-plugin", relativePath = "README.md", content = "hello" }); await Call("update_manifest", new { pluginId = "tool-plugin", description = (string?)null, version = "2" });
        var toolPackage = (await store.ListAsync("account-a")).Single(); check(!toolPackage.Manifest.ContainsKey("description") && toolPackage.Manifest["version"]!.GetValue<string>() == "2", "manifest patch omission/null semantics");
        check((await Call("upsert_file", new { pluginId = "tool-plugin", relativePath = "skills/greet/SKILL.md", content = "forbidden" })).GetProperty("isError").GetBoolean(), "editor protects skill package paths");
        await Call("add_skill", new { pluginId = "tool-plugin", skillId = "catalog/greet" }); await Call("upsert_mcp_server", new { pluginId = "tool-plugin", serverName = "remote", server = new { type = "streamable-http", url = "https://example.org/mcp" } });
        check((await store.ListAsync("account-a")).Single().Skills().Count == 1 && changed == 5, "editor skill/MCP mutations and refresh");
        var agent = DesktopAgent.Empty(); agent.Definition["name"] = "TestAgent"; agent.Definition["description"] = "Test"; agent.Definition["mcpServers"] = new JsonObject { ["remote"] = new JsonObject { ["url"] = "https://example.org/mcp", ["namespace"] = true } };
        var converted = await DesktopAgentPluginSnapshots.ConvertAsync(agent, ["test-plugin"], AgentPluginPackages.DefaultNamespace, (_, _, _) => Task.FromResult<IReadOnlyList<DesktopSkillFile>>([]), default);
        check(converted.Id == "test-plugin-2" && AgentPluginRuntime.Servers([converted], AgentPluginPackages.DefaultNamespace).Single().PluginSettings!.Value.GetProperty("namespace").GetBoolean(), "agent conversion names and extension settings");
        var prefs = new ChatPreferences { EnabledAgentPluginIds = ["tool-plugin", "tool-plugin", "invalid Name"] }; check(prefs.Clone().EnabledAgentPluginIds.SequenceEqual(new[] { "tool-plugin" }), "separate normalized plugin enablement clone");
        current = false; try { await Call("list", new { }); throw new Exception("Expected cancellation."); } catch (OperationCanceledException) { check(true, "plugin tools reject stale account"); }
        check(AgentPluginTools.Definitions.Count == 13 && DesktopLocalTools.Plugins.Any(p => p.Id == AgentPluginTools.EditorId), "all browser plugin editor tools registered");
        await ManagerAsync(check, root, converted);
    }
    private static async Task ManagerAsync(Action<bool, string> check, string root, DesktopAgentPlugin plugin)
    {
        var factory = new Factory(); var store = new DesktopMcpStore(Path.Combine(root, "plugin-mcp")); await using var manager = new DesktopMcpManager(factory, store);
        await manager.LoadAsync("account", default); await manager.InstallAsync(new() { Id = "manual", Name = "Manual", Url = "https://manual.example.org/mcp", Enabled = true }, default);
        var desired = AgentPluginRuntime.Servers([plugin], AgentPluginPackages.DefaultNamespace); await manager.ReconcilePluginsAsync(desired, default);
        check(manager.Servers.Count == 2 && (await store.LoadAsync("account")).Servers.Single().Id == "manual", "plugin connections never pollute manual persistence");
        var captured = manager.Capture(); check(captured.Tools.Any(t => t.TryGetProperty("requestOptions", out _)), "client extension settings reach tool request options");
        var count = factory.Connections.Count; await manager.ReconcilePluginsAsync(desired, default); check(factory.Connections.Count == count, "unchanged reconciliation does not reconnect");
        await manager.ReconcilePluginsAsync([], default); check(manager.Servers.Single().Server.Id == "manual" && factory.Connections.Last().Disposed, "disable plugin disposes only owned connection");
        await manager.ReconcilePluginsAsync(desired, default); await manager.LoadAsync("other-account", default); check(manager.Servers.Count == 0, "account change clears plugin and manual runtime");
        count = factory.Connections.Count;
        await manager.LoadAsync("account", default, connect: false);
        await manager.ReconcilePluginsAsync(desired, default, connect: false);
        check(factory.Connections.Count == count && manager.Servers.Count == 2
            && manager.Servers.All(s => s.State == McpConnectionState.Connecting),
            "startup loads enabled manual and plugin configuration without contacting optional MCP services");
        await manager.ReconnectAsync(default);
        check(factory.Connections.Count == count + 2
            && manager.Servers.All(s => s.State == McpConnectionState.Connected)
            && manager.Capture().Tools.Count > 0,
            "deferred startup connects manual and plugin services with normal discovery");
    }
    private sealed class Factory : IDesktopMcpClientFactory
    {
        public List<Connection> Connections { get; } = [];
        public Task<IDesktopMcpConnection> ConnectAsync(DesktopMcpServer server, CancellationToken ct) { var connection = new Connection(); Connections.Add(connection); return Task.FromResult<IDesktopMcpConnection>(connection); }
    }
    private sealed class Connection : IDesktopMcpConnection
    {
        public bool Disposed;
        public event Action? ToolsChanged { add { } remove { } } public event Action? ResourcesChanged { add { } remove { } }
        public Task<McpDiscovery> DiscoverAsync(CancellationToken ct) => Task.FromResult(new McpDiscovery(JsonSerializer.SerializeToElement(new { name = "plugin-test" }), JsonSerializer.SerializeToElement(new { }), null, [JsonSerializer.SerializeToElement(new { name = "tool", inputSchema = new { type = "object" } })]));
        public Task<JsonElement> CallAsync(string name, JsonElement input, string callId, string locale, CancellationToken ct) => Task.FromResult(JsonSerializer.SerializeToElement(new { content = Array.Empty<object>() }));
        public Task<JsonElement> ReadAsync(string uri, string? cursor, int limit, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> CompleteAsync(string template, string name, string value, IReadOnlyDictionary<string, string> arguments, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>([]);
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
