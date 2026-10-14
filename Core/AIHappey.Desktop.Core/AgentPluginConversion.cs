using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AIHappey.Desktop.Core;

public static class DesktopAgentPluginSnapshots
{
    public static JsonObject Embed(DesktopAgentPlugin plugin) => new() { ["type"] = "base64", ["media_type"] = "application/zip", ["data"] = Convert.ToBase64String(AgentPluginPackages.Export(plugin)) };
    public static async Task<DesktopAgentPlugin?> ReadAsync(JsonNode? entry, CancellationToken ct)
    {
        if (DesktopAgent.Text(entry?["type"]) != "base64" || DesktopAgent.Text(entry?["media_type"]) != "application/zip") return null;
        var data = DesktopAgent.Text(entry?["data"]); if (data.Length > AgentPluginPackages.MaxBytes * 4L / 3 + 4) throw new InvalidDataException("Embedded plugin too large.");
        var plugins = await AgentPluginPackages.ImportAsync(Convert.FromBase64String(data), ct);
        if (plugins.Count != 1) throw new InvalidDataException("Embedded archive must contain one plugin."); return plugins[0];
    }
    public static async Task<DesktopAgentPlugin> ConvertAsync(DesktopAgent agent, IEnumerable<string> existingNames, string extensionNamespace,
        Func<string, string?, CancellationToken, Task<IReadOnlyList<DesktopSkillFile>>> skillResolver, CancellationToken ct)
    {
        var files = new List<DesktopSkillFile>(); var usedSkills = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in agent.Definition["skills"] as JsonArray ?? new())
        {
            ct.ThrowIfCancellationRequested(); IReadOnlyList<DesktopSkillFile> snapshot;
            if (DesktopAgent.Text(entry?["type"]) == "skill_reference") snapshot = await skillResolver(DesktopAgent.Text(entry?["skill_id"]), DesktopAgent.Text(entry?["version"]) is { Length: > 0 } version ? version : null, ct);
            else if (DesktopAgent.Text(entry?["type"]) == "inline" && DesktopAgent.Text(entry?["source"]?["type"]) == "base64")
            {
                var data = DesktopAgent.Text(entry?["source"]?["data"]); if (data.Length > AgentPluginPackages.MaxBytes * 4L / 3 + 4) throw new InvalidDataException("Inline skill archive too large.");
                var imported = await DesktopSkillPackages.ImportAsync(Convert.FromBase64String(data), ct);
                if (imported.Skills.Count == 0 || imported.Diagnostics.Count > 0) throw new InvalidDataException("Inline agent skill is invalid.");
                snapshot = imported.Skills.SelectMany(s => s.Files.Append(new("SKILL.md", Encoding.UTF8.GetBytes(DesktopSkillPackages.Markdown(s)))).Select(f => new DesktopSkillFile("skills/" + s.Name + "/" + f.Path, f.Data))).ToArray();
            }
            else throw new InvalidDataException("Agent skill cannot be snapshotted.");
            foreach (var group in snapshot.GroupBy(f => f.Path.Split('/')[1]))
            {
                var directory = group.Key; var next = directory; var i = 2;
                while (!usedSkills.Add(next)) { var suffix = "-" + i++; next = directory[..Math.Min(directory.Length, 64 - suffix.Length)].TrimEnd('-') + suffix; }
                foreach (var file in group)
                {
                    var path = "skills/" + next + "/" + string.Join('/', file.Path.Split('/').Skip(2)); var data = file.Data.ToArray();
                    if (next != directory && path.EndsWith("/SKILL.md", StringComparison.Ordinal))
                    { var parsed = SkillFiles.Markdown(Encoding.UTF8.GetString(data)); parsed.Frontmatter["name"] = next; data = Encoding.UTF8.GetBytes(DesktopSkillPackages.Markdown(new() { Frontmatter = parsed.Frontmatter, Instructions = parsed.Body })); }
                    files.Add(new(path, data));
                }
            }
        }
        var servers = new JsonObject(); var settings = new JsonObject();
        foreach (var (name, entry) in agent.Definition["mcpServers"] as JsonObject ?? new())
        {
            if (entry is not JsonObject server) throw new InvalidDataException("Invalid agent MCP server."); if (DesktopAgent.Boolean(server["disabled"])) continue;
            var portable = new JsonObject { ["type"] = "streamable-http", ["url"] = server["url"]?.DeepClone() };
            if (server.ContainsKey("headers")) portable["headers"] = server["headers"]?.DeepClone(); servers[name] = AgentPluginPackages.Server(name, portable);
            var options = new JsonObject(); foreach (var key in new[] { "allowed_callers", "defer_loading", "namespace" }) if (server.ContainsKey(key)) options[key] = server[key]?.DeepClone(); if (options.Count > 0) settings[name] = options;
        }
        if (files.Count == 0 && servers.Count == 0) throw new InvalidDataException(DesktopResources.Get("PluginAgentEmpty"));
        var nameBase = Regex.Replace(agent.Name, "([a-z0-9])([A-Z])", "$1-$2").ToLowerInvariant().Replace("agent", "plugin", StringComparison.Ordinal);
        nameBase = Regex.Replace(nameBase, "[^a-z0-9.-]+", "-").Trim('-', '.'); nameBase = Regex.Replace(nameBase, "[-.]{2,}", "-"); if (nameBase.Length == 0) nameBase = "agent-plugin"; nameBase = nameBase[..Math.Min(nameBase.Length, 64)].TrimEnd('-', '.');
        var names = existingNames.ToHashSet(StringComparer.OrdinalIgnoreCase); var pluginName = nameBase; var index = 2;
        while (names.Contains(pluginName)) { var suffix = "-" + index++; pluginName = nameBase[..Math.Min(nameBase.Length, 64 - suffix.Length)].TrimEnd('-', '.') + suffix; }
        var manifest = new JsonObject { ["$schema"] = AgentPluginPackages.Schema, ["name"] = pluginName, ["version"] = "1.0.0", ["description"] = agent.Description };
        if (extensionNamespace.Length > 0 && settings.Count > 0) manifest["extensions"] = new JsonObject { [extensionNamespace] = new JsonObject { ["mcpServers"] = settings } };
        return AgentPluginPackages.Validate(new() { Manifest = manifest, Files = files, Mcp = servers.Count == 0 ? null : new() { ["$schema"] = AgentPluginPackages.McpSchema, ["mcpServers"] = servers } });
    }
}
