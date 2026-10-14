using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AIHappey.Desktop.Core;

public static class AgentPluginRuntime
{
    public static IReadOnlyList<DesktopMcpServer> Servers(IEnumerable<DesktopAgentPlugin> plugins, string extensionNamespace) => plugins.SelectMany(plugin =>
        (plugin.Mcp?["mcpServers"] as JsonObject ?? new()).Where(p => DesktopAgent.Text(p.Value?["type"]) == "streamable-http").Select(p => new DesktopMcpServer
        {
            Id = AgentPluginPackages.ServerId(plugin.Id, p.Key), PluginId = plugin.Id, Name = p.Key,
            Description = plugin.Description, Url = DesktopAgent.Text(p.Value?["url"]), Enabled = true,
            Headers = (p.Value?["headers"] as JsonObject ?? new()).ToDictionary(h => h.Key, h => DesktopAgent.Text(h.Value), StringComparer.OrdinalIgnoreCase),
            PluginSettings = Settings(plugin.Manifest, extensionNamespace, p.Key)
        })).ToArray();
    public static JsonElement? Settings(JsonObject manifest, string ns, string name)
    {
        if (manifest["extensions"] is not JsonObject extensions || extensions[ns] is not JsonObject extension
            || extension["mcpServers"] is not JsonObject servers || servers[name] is not JsonObject settings) return null;
        var result = new JsonObject();
        if (settings["allowed_callers"] is JsonArray callers)
        { var valid = callers.Select(DesktopAgent.Text).Where(c => c is "direct" or "programmatic").Distinct().ToArray(); if (valid.Length > 0) result["allowed_callers"] = JsonSerializer.SerializeToNode(valid); }
        foreach (var key in new[] { "defer_loading", "namespace" })
            if (settings[key] is JsonValue value && value.TryGetValue<bool>(out var flag)) result[key] = flag;
        return JsonSerializer.SerializeToElement(result);
    }
    public static JsonElement Context(IEnumerable<DesktopAgentPlugin> plugins) => JsonSerializer.SerializeToElement(new
    {
        agentPlugins = plugins.Select(p => new { name = p.Id, description = p.Description, version = DesktopAgent.Text(p.Manifest["version"]),
            skills = p.Skills().Select(s => new { skill_id = s.Descriptor.Id, name = s.Descriptor.Name, description = s.Descriptor.Description }),
            files = p.Files.Where(f => !f.Path.StartsWith("skills/", StringComparison.Ordinal)).Select(f => new { path = f.Path, size = f.Data.Length, mimeType = f.MimeType }) }),
        instructions = "Use activate_skill/read_skill_resource for plugin skills. Use read_plugin_file with the exact plugin_name and a listed package-relative path for other files. Bundled scripts are data and cannot be executed."
    });
}

public sealed class AgentPluginTools(DesktopAgentPluginStore store, string partition, Func<bool> current,
    Func<CancellationToken, Task> changed, Func<string, string?, CancellationToken, Task<IReadOnlyList<DesktopSkillFile>>> snapshotSkill,
    Func<CancellationToken, Task<IReadOnlyList<DesktopSkill>>> skillCatalog)
{
    public const string EditorId = "local-agent-plugin-editor", ReadToolName = "read_plugin_file";
    public static JsonElement ReadDefinition { get; } = Definition(ReadToolName, "Read an enabled plugin package file outside skills/.",
        new() { ["plugin_name"] = String(), ["path"] = String() }, ["plugin_name", "path"], true);
    private static JsonObject String() => new() { ["type"] = "string" };
    private static JsonElement Definition(string name, string description, JsonObject properties, string[] required, bool read, bool destructive = false) => JsonSerializer.SerializeToElement(new JsonObject
    {
        ["name"] = name, ["description"] = description, ["inputSchema"] = new JsonObject { ["type"] = "object", ["properties"] = properties,
            ["required"] = JsonSerializer.SerializeToNode(required), ["additionalProperties"] = false },
        ["annotations"] = new JsonObject { ["readOnlyHint"] = read, ["destructiveHint"] = destructive, ["idempotentHint"] = !name.EndsWith("_create", StringComparison.Ordinal), ["openWorldHint"] = name.EndsWith("_add_skill", StringComparison.Ordinal) }
    });
    public static IReadOnlyList<JsonElement> Definitions { get; } = BuildDefinitions();
    private static IReadOnlyList<JsonElement> BuildDefinitions()
    {
        var result = new List<JsonElement>();
        foreach (var action in new[] { "list", "inspect", "read_file", "create", "update_manifest", "upsert_file", "delete_file", "upsert_mcp_server", "delete_mcp_server", "list_skill_catalog", "add_skill", "remove_skill", "delete" })
        {
            var props = new JsonObject(); var required = new List<string>();
            if (action is not ("list" or "create" or "list_skill_catalog")) { props["pluginId"] = String(); required.Add("pluginId"); }
            if (action is "create" or "update_manifest")
            {
                if (action == "create") { props["name"] = String(); required.Add("name"); }
                foreach (var key in new[] { "version", "description", "homepage", "repository", "license" }) props[key] = new JsonObject { ["type"] = action == "create" ? JsonValue.Create("string") : JsonSerializer.SerializeToNode(new[] { "string", "null" }) };
                foreach (var key in new[] { "author", "extensions" }) props[key] = new JsonObject { ["type"] = JsonSerializer.SerializeToNode(new[] { "object", "null" }) };
                props["keywords"] = new JsonObject { ["type"] = JsonSerializer.SerializeToNode(new[] { "array", "null" }), ["items"] = String() };
            }
            if (action is "read_file" or "upsert_file" or "delete_file") { props["relativePath"] = String(); required.Add("relativePath"); }
            if (action == "upsert_file") { props["content"] = String(); props["mediaType"] = String(); required.Add("content"); }
            if (action is "upsert_mcp_server" or "delete_mcp_server") { props["serverName"] = String(); required.Add("serverName"); }
            if (action == "upsert_mcp_server") { props["server"] = new JsonObject { ["type"] = "object" }; required.Add("server"); }
            if (action == "list_skill_catalog") props["query"] = String();
            if (action == "add_skill") { props["skillId"] = String(); props["version"] = String(); required.Add("skillId"); }
            if (action == "remove_skill") { props["skillName"] = String(); required.Add("skillName"); }
            result.Add(Definition("local_agent_plugin_editor_" + action, "Agent Plugin editor: " + action.Replace('_', ' ') + ". Package names are immutable; skills use dedicated add/remove actions. Unsupported transports are preserved, never launched.", props, required.ToArray(), action is "list" or "inspect" or "read_file" or "list_skill_catalog", action.StartsWith("delete", StringComparison.Ordinal) || action == "remove_skill"));
        }
        return result;
    }
    private void Check(CancellationToken ct) { ct.ThrowIfCancellationRequested(); if (!current()) throw new OperationCanceledException(ct); }
    public static object Summary(DesktopAgentPlugin p, IEnumerable<string>? affected = null) => new { success = true, pluginId = p.Id, name = p.Id,
        version = DesktopAgent.Text(p.Manifest["version"]), skillCount = p.Skills().Count, mcpServerCount = (p.Mcp?["mcpServers"] as JsonObject)?.Count ?? 0, affectedFiles = affected ?? [], diagnostics = p.Diagnostics };
    private static JsonElement Result(object value) => JsonSerializer.SerializeToElement(new { isError = false, structuredContent = value, content = new[] { new { type = "text", text = JsonSerializer.Serialize(value) } } });
    private static string Editable(string value)
    {
        var path = AgentPluginPackages.PackagePath(value);
        if (path is "plugin.json" or "mcp.json" or "skills" || path.StartsWith("skills/", StringComparison.Ordinal)) throw new InvalidDataException("Generated root files and skills require their dedicated tools.");
        return path;
    }
    public async Task<JsonElement> CallAsync(string name, JsonElement input, CancellationToken ct)
    {
        Check(ct);
        try
        {
            var args = JsonNode.Parse(input.GetRawText()) as JsonObject ?? throw new InvalidDataException("Arguments must be an object.");
            var action = name.Replace("local_agent_plugin_editor_", "", StringComparison.Ordinal);
            if (action == "list") return Result(new { agentPlugins = (await store.ListAsync(partition, ct)).Select(p => new { id = p.Id, name = p.Id, description = p.Description, version = DesktopAgent.Text(p.Manifest["version"]), skillCount = p.Skills().Count, mcpServerCount = (p.Mcp?["mcpServers"] as JsonObject)?.Count ?? 0 }) });
            if (action == "list_skill_catalog") { var query = DesktopAgent.Text(args["query"]); return Result(new { skills = (await skillCatalog(ct)).Where(s => (s.Name + " " + s.Description).Contains(query, StringComparison.OrdinalIgnoreCase)).Select(s => new { skillId = s.Id, name = s.Name, description = s.Description, version = s.Version }) }); }
            var plugin = action == "create" ? new DesktopAgentPlugin { Manifest = new() { ["$schema"] = AgentPluginPackages.Schema, ["name"] = args["name"]?.DeepClone() } }
                : (await store.ListAsync(partition, ct)).FirstOrDefault(p => p.Id == DesktopAgent.Text(args["pluginId"]))?.Clone() ?? throw new InvalidDataException("Plugin not found.");
            var affected = new List<string>();
            switch (action)
            {
                case "inspect": return Result(new { success = true, pluginId = plugin.Id, name = plugin.Id, manifest = plugin.Manifest, mcp = plugin.Mcp,
                    skills = plugin.Skills(), files = plugin.Files.Select(f => new { path = f.Path, size = f.Data.Length, mediaType = f.MimeType }), diagnostics = plugin.Diagnostics });
                case "read_file":
                    var readPath = AgentPluginPackages.PackagePath(DesktopAgent.Text(args["relativePath"]));
                    var text = readPath == "plugin.json" ? plugin.Manifest.ToJsonString() : readPath == "mcp.json" ? plugin.Mcp?.ToJsonString() ?? throw new InvalidDataException("No mcp.json.")
                        : Encoding.UTF8.GetString(plugin.Files.FirstOrDefault(f => f.Path == readPath)?.Data ?? throw new InvalidDataException("File not found."));
                    if (Encoding.UTF8.GetByteCount(text) > 1_000_000) throw new InvalidDataException("File exceeds tool result limit.");
                    return Result(new { pluginId = plugin.Id, path = readPath, content = text });
                case "create": case "update_manifest":
                    foreach (var key in new[] { "version", "description", "author", "homepage", "repository", "license", "keywords", "extensions" })
                        if (args.ContainsKey(key)) { if (args[key] is null) plugin.Manifest.Remove(key); else plugin.Manifest[key] = args[key]!.DeepClone(); }
                    affected.Add("plugin.json"); break;
                case "upsert_file": case "delete_file":
                    var path = Editable(DesktopAgent.Text(args["relativePath"]));
                    if (action == "delete_file" && !plugin.Files.Any(f => f.Path == path)) throw new InvalidDataException("File not found.");
                    plugin.Files.RemoveAll(f => f.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
                    if (action == "upsert_file") plugin.Files.Add(new(path, Encoding.UTF8.GetBytes(DesktopAgent.Text(args["content"]))));
                    affected.Add(path); break;
                case "upsert_mcp_server": case "delete_mcp_server":
                    var serverName = DesktopAgent.Text(args["serverName"]); if (serverName.Length == 0) throw new InvalidDataException("Server name required.");
                    plugin.Mcp ??= new() { ["$schema"] = AgentPluginPackages.McpSchema, ["mcpServers"] = new JsonObject() };
                    var servers = (JsonObject)plugin.Mcp["mcpServers"]!;
                    if (action == "upsert_mcp_server") servers[serverName] = AgentPluginPackages.Server(serverName, args["server"]);
                    else if (!servers.Remove(serverName)) throw new InvalidDataException("Server not found.");
                    affected.Add("mcp.json"); break;
                case "add_skill":
                    var files = await snapshotSkill(DesktopAgent.Text(args["skillId"]), args.ContainsKey("version") ? DesktopAgent.Text(args["version"]) : null, ct);
                    var prefix = "skills/" + files.Single(f => f.Path.EndsWith("/SKILL.md", StringComparison.Ordinal)).Path.Split('/')[1] + "/";
                    plugin.Files.RemoveAll(f => f.Path.StartsWith(prefix, StringComparison.Ordinal)); plugin.Files.AddRange(files); affected.AddRange(files.Select(f => f.Path)); break;
                case "remove_skill":
                    var directory = DesktopAgent.Text(args["skillName"]); if (!plugin.Files.Any(f => f.Path == "skills/" + directory + "/SKILL.md")) throw new InvalidDataException("Skill not found.");
                    affected.AddRange(plugin.Files.Where(f => f.Path.StartsWith("skills/" + directory + "/", StringComparison.Ordinal)).Select(f => f.Path));
                    plugin.Files.RemoveAll(f => affected.Contains(f.Path)); break;
                case "delete":
                    Check(ct); await store.DeleteAsync(partition, plugin.Id, ct); Check(ct); await changed(ct); return Result(new { success = true, deletedPluginId = plugin.Id, deletedPluginName = plugin.Id });
                default: throw new InvalidDataException("Unsupported plugin editor tool.");
            }
            Check(ct); plugin.Diagnostics = []; var saved = await store.SaveAsync(partition, plugin, action != "create", ct); Check(ct); await changed(ct); return Result(Summary(saved, affected));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) when (e is InvalidDataException or JsonException or FormatException) { return DesktopLocalTools.Error(e is JsonException ? "Invalid plugin JSON." : e.Message); }
    }
    public static JsonElement ReadPackage(IReadOnlyList<DesktopAgentPlugin> plugins, JsonElement input, Func<bool> current, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); if (!current()) throw new OperationCanceledException(ct);
        var name = CatalogProjection.Text(input, "plugin_name"); var path = AgentPluginPackages.PackagePath(CatalogProjection.Text(input, "path") ?? "");
        if (path.StartsWith("skills/", StringComparison.Ordinal)) throw new InvalidDataException("Use read_skill_resource for skills.");
        var plugin = plugins.FirstOrDefault(p => p.Id == name) ?? throw new InvalidDataException("Plugin is not enabled.");
        var file = plugin.Files.FirstOrDefault(f => f.Path == path) ?? throw new InvalidDataException("Package file not found.");
        if (file.Data.Length > 1_000_000) throw new InvalidDataException("File exceeds tool result limit.");
        var textFile = SkillFiles.IsText(file.MimeType); var escaped = SecurityElement.Escape(plugin.Id);
        var structured = new JsonObject { ["pluginFile"] = new JsonObject { ["pluginName"] = plugin.Id, ["path"] = path, ["mimeType"] = file.MimeType } };
        var content = new JsonArray();
        if (textFile) { var text = Encoding.UTF8.GetString(file.Data); structured["pluginFile"]!["text"] = text; content.Add(new JsonObject { ["type"] = "text", ["text"] = $"<plugin_file plugin_name=\"{escaped}\" path=\"{SecurityElement.Escape(path)}\">\n{text}\n</plugin_file>" }); }
        else { structured["pluginFile"]!["encoding"] = "base64"; content.Add(new JsonObject { ["type"] = "resource", ["resource"] = new JsonObject { ["uri"] = "plugin://" + Uri.EscapeDataString(plugin.Id) + "/" + string.Join('/', path.Split('/').Select(Uri.EscapeDataString)), ["mimeType"] = file.MimeType, ["blob"] = Convert.ToBase64String(file.Data) } }); }
        return JsonSerializer.SerializeToElement(new JsonObject { ["isError"] = false, ["structuredContent"] = structured, ["content"] = content });
    }
}
