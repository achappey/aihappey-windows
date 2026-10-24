using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AIHappey.Desktop.Core;

public sealed record PluginDiagnostic(string Severity, string Boundary, string Code, string Message, string? Path = null, string? Entry = null);
public sealed record PluginSkill(string Directory, DesktopSkill Descriptor);
public sealed class DesktopAgentPlugin
{
    public JsonObject Manifest { get; set; } = new();
    public JsonObject? Mcp { get; set; }
    public List<DesktopSkillFile> Files { get; set; } = [];
    public List<PluginDiagnostic> Diagnostics { get; set; } = [];
    public long CreatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public long UpdatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public bool Favorite { get; set; }
    public string Id => DesktopAgent.Text(Manifest["name"]);
    public string Description => DesktopAgent.Text(Manifest["description"]);
    public IEnumerable<string> Keywords => (Manifest["keywords"] as JsonArray)?.Select(DesktopAgent.Text) ?? [];
    public string Author => DesktopAgent.Text(Manifest["author"]?["name"]) is { Length: > 0 } name ? name : DesktopAgent.Text(Manifest["author"]?["email"]);
    public DesktopAgentPlugin Clone() => new() { Manifest = (JsonObject)Manifest.DeepClone(), Mcp = Mcp?.DeepClone() as JsonObject,
        Files = Files.Select(f => new DesktopSkillFile(f.Path, f.Data.ToArray())).ToList(), Diagnostics = Diagnostics.ToList(),
        CreatedAt = CreatedAt, UpdatedAt = UpdatedAt, Favorite = Favorite };
    public IReadOnlyList<PluginSkill> Skills()
    {
        var result = new List<PluginSkill>();
        foreach (var file in Files.Where(f => Regex.IsMatch(f.Path, "^skills/[^/]+/SKILL\\.md$")))
        {
            try
            {
                var directory = file.Path.Split('/')[1]; var parsed = SkillFiles.Markdown(Encoding.UTF8.GetString(file.Data));
                if (DesktopAgent.Text(parsed.Frontmatter["name"]) != directory) throw new InvalidDataException("Skill name must match its directory.");
                result.Add(new(directory, new(AgentPluginPackages.SkillId(Id, directory), directory, DesktopAgent.Text(parsed.Frontmatter["description"]), "plugin", Server: Id)));
            }
            catch (Exception e) when (e is InvalidDataException or YamlDotNet.Core.YamlException or ArgumentException) { }
        }
        return result;
    }
    public DesktopSkillContent Content(PluginSkill skill)
    {
        var prefix = "skills/" + skill.Directory + "/";
        var files = Files.Where(f => f.Path.StartsWith(prefix, StringComparison.Ordinal)).ToDictionary(f => f.Path[prefix.Length..], f => new DesktopSkillFile(f.Path[prefix.Length..], f.Data.ToArray()), StringComparer.Ordinal);
        var parsed = SkillFiles.Markdown(Encoding.UTF8.GetString(files["SKILL.md"].Data));
        return new(skill.Descriptor, parsed.Body, files.Keys.Where(p => p != "SKILL.md").Order(StringComparer.Ordinal).ToArray(),
            (path, ct) => { ct.ThrowIfCancellationRequested(); return Task.FromResult(files.TryGetValue(AgentPluginPackages.PackagePath(path), out var file) ? file : throw new InvalidDataException("Plugin skill resource not found.")); });
    }
}

/// <summary>Local, version-selected validation. No schema downloads, subprocesses, or script execution.</summary>
public static class AgentPluginPackages
{
    public const string Schema = "https://agent-plugins.org/schemas/1.0.0/plugin.schema.json";
    public const string McpSchema = "https://agent-plugins.org/schemas/1.0.0/mcp.schema.json";
    public const string DefaultNamespace = "net.aihappey.agents";
    public const int MaxBytes = 64 * 1024 * 1024, MaxFiles = 2048;
    public static string SkillId(string id, string directory) => "plugin/" + Uri.EscapeDataString(id) + "/" + Uri.EscapeDataString(directory);
    public static string ServerId(string id, string name) => "agent-plugin/" + Uri.EscapeDataString(id) + "/" + Uri.EscapeDataString(name);
    public static bool ValidName(string name) => Regex.IsMatch(name, "\\A(?!.*--)(?!.*\\.\\.)[a-z0-9](?:[a-z0-9.-]{0,62}[a-z0-9])?\\z");
    public static string PackagePath(string path)
    {
        path = SkillFiles.RelativePath(path);
        if (path.Split('/').Any(p => p.EndsWith('.') || p.EndsWith(' ') || p.IndexOfAny(['<', '>', '"', '|', '?', '*']) >= 0
            || Regex.IsMatch(p.Split('.')[0], "^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", RegexOptions.IgnoreCase)))
            throw new InvalidDataException("Ambiguous Windows package path.");
        return path;
    }
    public static JsonObject Manifest(JsonNode? input, List<PluginDiagnostic> diagnostics)
    {
        if (input is not JsonObject obj) throw new InvalidDataException("plugin.json must contain an object.");
        var result = (JsonObject)obj.DeepClone();
        var fields = new HashSet<string>(["$schema", "name", "version", "description", "author", "homepage", "repository", "license", "keywords", "extensions"]);
        foreach (var field in result.Select(p => p.Key).Where(k => !fields.Contains(k)).ToArray())
        { diagnostics.Add(new("warning", "manifest", "manifest-unknown-field", "Unknown field ignored: " + field, "plugin.json")); result.Remove(field); }
        if (DesktopAgent.Text(result["$schema"]) != Schema) throw new InvalidDataException("Unsupported Agent Plugins manifest schema.");
        if (!ValidName(DesktopAgent.Text(result["name"]))) throw new InvalidDataException("Invalid plugin name: use 1–64 lowercase letters, digits, hyphens or periods, without repeated separators.");
        foreach (var field in new[] { "version", "description", "homepage", "repository", "license" })
            if (result.ContainsKey(field) && !IsString(result[field])) throw new InvalidDataException(field + " must be a string.");
        if (result.ContainsKey("author") && (result["author"] is not JsonObject author || author.Any(p => p.Key is not ("name" or "email" or "url") || !IsString(p.Value))))
            throw new InvalidDataException("Author must contain only string name, email and url fields.");
        if (result.ContainsKey("keywords") && (result["keywords"] is not JsonArray keywords || keywords.Any(k => !IsString(k))))
            throw new InvalidDataException("Keywords must be an array of strings.");
        if (result.ContainsKey("extensions") && result["extensions"] is not JsonObject)
        { diagnostics.Add(new("warning", "extension", "manifest-invalid-extensions", "Non-object extensions ignored.", "plugin.json")); result.Remove("extensions"); }
        // Unknown extension values are opaque and never validated.
        return result;
    }
    private static bool IsString(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out _);
    private static bool Strings(JsonNode? node) => node is JsonObject obj && obj.All(p => IsString(p.Value));
    public static JsonObject Server(string name, JsonNode? input)
    {
        if (input is not JsonObject obj) throw new InvalidDataException("Server must be an object with a declared type.");
        var type = DesktopAgent.Text(obj["type"]);
        var allowed = type switch { "stdio" => new[] { "type", "command", "args", "env", "cwd" }, "streamable-http" or "sse" => ["type", "url", "headers"], _ => throw new InvalidDataException("Unknown MCP transport.") };
        if (obj.Any(p => !allowed.Contains(p.Key))) throw new InvalidDataException("Unknown MCP server field.");
        if (type == "stdio")
        {
            var command = DesktopAgent.Text(obj["command"]);
            if (string.IsNullOrWhiteSpace(command) || command.Contains("${", StringComparison.Ordinal)) throw new InvalidDataException("Command must be one executable token without expansion.");
            if (command.StartsWith("./", StringComparison.Ordinal)) _ = PackagePath(command[2..]);
            else if (command.Any(c => char.IsWhiteSpace(c) || c is '/' or '\\' or ':' or '"' or '\'' or ';' or '|' or '&' or '<' or '>')) throw new InvalidDataException("Command must be a bare executable or ./ package path.");
            if (obj.ContainsKey("args") && (obj["args"] is not JsonArray args || args.Any(a => !IsString(a)))) throw new InvalidDataException("Args must be strings.");
            if (obj.ContainsKey("env") && (!Strings(obj["env"]) || ((JsonObject)obj["env"]!).Any(p => p.Key.Equals("PLUGIN_ROOT", StringComparison.OrdinalIgnoreCase) || p.Key.Equals("PLUGIN_DATA", StringComparison.OrdinalIgnoreCase)))) throw new InvalidDataException("Invalid or reserved environment variable.");
            if (obj.ContainsKey("cwd"))
            {
                var cwd = DesktopAgent.Text(obj["cwd"]);
                if (cwd.StartsWith("./", StringComparison.Ordinal)) { if (cwd != "./") _ = PackagePath(cwd[2..]); }
                else if (cwd is not ("${PLUGIN_ROOT}" or "${PLUGIN_DATA}"))
                {
                    var root = new[] { "${PLUGIN_ROOT}/", "${PLUGIN_DATA}/" }.FirstOrDefault(r => cwd.StartsWith(r, StringComparison.Ordinal));
                    if (root is null) throw new InvalidDataException("Invalid working directory.");
                    _ = PackagePath(cwd[root.Length..]);
                }
            }
        }
        else
        {
            var url = DesktopAgent.Text(obj["url"]);
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http") || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0
                || uri.Scheme == "http" && uri.Host != "localhost" && !(IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip))) throw new InvalidDataException("MCP URL must be HTTPS, or HTTP on localhost/loopback, without credentials or fragment.");
            if (obj.ContainsKey("headers"))
            {
                if (!Strings(obj["headers"])) throw new InvalidDataException("Headers must be strings.");
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var (key, value) in (JsonObject)obj["headers"]!)
                    if (!seen.Add(key) || !Regex.IsMatch(key, "^[!#$%&'*+.^_`|~0-9A-Za-z-]+$") || DesktopAgent.Text(value).Any(c => c is '\r' or '\n' or '\0' || c < 32 && c != '\t' || c == 127)) throw new InvalidDataException("Invalid or duplicate HTTP header.");
            }
        }
        return (JsonObject)obj.DeepClone();
    }
    public static JsonObject? McpConfiguration(JsonNode? input, List<PluginDiagnostic> diagnostics)
    {
        if (input is not JsonObject obj || obj.Any(p => p.Key is not ("$schema" or "mcpServers")) || DesktopAgent.Text(obj["$schema"]) != McpSchema || obj["mcpServers"] is not JsonObject servers)
        { diagnostics.Add(new("error", "mcp", "mcp-invalid-document", "Invalid or unsupported mcp.json; MCP disabled.", "mcp.json")); return null; }
        var valid = new JsonObject();
        foreach (var (name, server) in servers)
        {
            try
            {
                valid[name] = Server(name, server);
                if (DesktopAgent.Text(valid[name]?["type"]) != "streamable-http") diagnostics.Add(new("info", "server", "server-unsupported-transport", "Transport preserved but not executed by this client.", "mcp.json", name));
            }
            catch (InvalidDataException e) { diagnostics.Add(new("error", "server", "server-invalid", e.Message, "mcp.json", name)); }
        }
        return new() { ["$schema"] = McpSchema, ["mcpServers"] = valid };
    }
    public static DesktopAgentPlugin Validate(DesktopAgentPlugin draft)
    {
        var result = draft.Clone(); result.Diagnostics = [];
        result.Manifest = Manifest(result.Manifest, result.Diagnostics);
        result.Mcp = result.Mcp is null ? null : McpConfiguration(result.Mcp, result.Diagnostics);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long bytes = 0;
        if (result.Files.Count > MaxFiles) throw new InvalidDataException("Too many plugin files.");
        foreach (var file in result.Files)
        {
            var path = PackagePath(file.Path);
            if (path is "plugin.json" or "mcp.json" || !seen.Add(path) || (bytes += file.Data.LongLength) > MaxBytes) throw new InvalidDataException("Duplicate, reserved or oversized plugin file.");
            if (!Regex.IsMatch(path, "^skills/[^/]+/SKILL\\.md$")) continue;
            try
            {
                var parsed = SkillFiles.Markdown(Encoding.UTF8.GetString(file.Data));
                if (DesktopAgent.Text(parsed.Frontmatter["name"]) != path.Split('/')[1]) throw new InvalidDataException("Skill name must match directory.");
            }
            catch (Exception e) when (e is InvalidDataException or YamlDotNet.Core.YamlException or ArgumentException)
            { result.Diagnostics.Add(new("error", "skill", "skill-invalid", "Invalid skill: " + e.Message, path, path.Split('/')[1])); }
        }
        if (seen.Any(p => p.Split('/').SkipLast(1).Select((_, i) => string.Join('/', p.Split('/').Take(i + 1))).Any(seen.Contains))) throw new InvalidDataException("A package path cannot be both file and directory.");
        result.Files = result.Files.Select(f => new DesktopSkillFile(PackagePath(f.Path), f.Data)).ToList();
        return result;
    }
    public static async Task<IReadOnlyList<DesktopAgentPlugin>> ImportAsync(byte[] bytes, CancellationToken ct = default)
    {
        if (bytes.Length > MaxBytes) throw new InvalidDataException("Plugin archive too large.");
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        if (zip.Entries.Count > MaxFiles * 2) throw new InvalidDataException("Too many plugin entries.");
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal); var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long total = 0;
        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            var path = PackagePath(entry.FullName.TrimEnd('/'));
            if (entry.FullName.Contains('\\') || ((entry.ExternalAttributes >> 16) & 0xf000) == 0xa000 || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0 || !seen.Add(path)) throw new InvalidDataException("Archive contains a link, unsafe or duplicate path.");
            if (entry.FullName.EndsWith('/')) continue;
            if ((total += entry.Length) > MaxBytes || files.Count >= MaxFiles) throw new InvalidDataException("Plugin archive too large.");
            await using var stream = entry.Open(); files.Add(path, await ReadBoundedAsync(stream, entry.Length, ct));
        }
        var roots = files.ContainsKey("plugin.json") ? new[] { "" } : files.Keys.Where(p => p.EndsWith("/plugin.json", StringComparison.Ordinal)).Select(p => p[..^11]).Order(StringComparer.Ordinal).ToArray();
        if (roots.Length == 0) throw new InvalidDataException("plugin.json is required.");
        var results = new List<DesktopAgentPlugin>(); var failures = new List<string>();
        foreach (var root in roots)
        {
            try { results.Add(FromFiles(files.Where(p => p.Key.StartsWith(root, StringComparison.Ordinal)).ToDictionary(p => p.Key[root.Length..], p => p.Value, StringComparer.Ordinal))); }
            catch (Exception e) when (e is InvalidDataException or JsonException) { failures.Add(root + ": " + e.Message); }
        }
        if (results.Count == 0) throw new InvalidDataException(string.Join("\n", failures));
        foreach (var failure in failures) results[0].Diagnostics.Add(new("error", "plugin", "plugin-rejected", failure));
        return results;
    }
    private static DesktopAgentPlugin FromFiles(Dictionary<string, byte[]> files)
    {
        var diagnostics = new List<PluginDiagnostic>();
        var manifest = Manifest(JsonNode.Parse(files["plugin.json"]), diagnostics); JsonObject? mcp = null;
        if (files.TryGetValue("mcp.json", out var config))
        {
            try { mcp = McpConfiguration(JsonNode.Parse(config), diagnostics); }
            catch (JsonException) { diagnostics.Add(new("error", "mcp", "mcp-invalid-json", "Invalid MCP JSON; MCP disabled.", "mcp.json")); }
        }
        var result = Validate(new() { Manifest = manifest, Mcp = mcp, Files = files.Where(p => p.Key is not ("plugin.json" or "mcp.json")).Select(p => new DesktopSkillFile(p.Key, p.Value)).ToList() });
        if (files.ContainsKey("skills")) diagnostics.Add(new("error", "skills", "skills-invalid-location", "skills must be a directory.", "skills"));
        if (!files.ContainsKey("mcp.json") && files.Keys.Any(p => p.StartsWith("mcp.json/", StringComparison.Ordinal))) diagnostics.Add(new("error", "mcp", "mcp-invalid-location", "mcp.json must be a regular file.", "mcp.json"));
        result.Diagnostics = diagnostics.Concat(result.Diagnostics).Distinct().ToList(); return result;
    }
    public static byte[] Export(DesktopAgentPlugin plugin)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            var entries = plugin.Files.Concat(new[] { new DesktopSkillFile("plugin.json", JsonSerializer.SerializeToUtf8Bytes(plugin.Manifest, new JsonSerializerOptions { WriteIndented = true })) });
            if (plugin.Mcp is not null) entries = entries.Append(new DesktopSkillFile("mcp.json", JsonSerializer.SerializeToUtf8Bytes(plugin.Mcp, new JsonSerializerOptions { WriteIndented = true })));
            foreach (var file in entries.OrderBy(f => f.Path, StringComparer.Ordinal))
            { var entry = zip.CreateEntry(PackagePath(file.Path), CompressionLevel.Optimal); entry.LastWriteTime = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero); using var stream = entry.Open(); stream.Write(file.Data); }
        }
        if (output.Length > MaxBytes) throw new InvalidDataException("Plugin archive too large."); return output.ToArray();
    }
    internal static async Task<byte[]> ReadBoundedAsync(Stream stream, long size, CancellationToken ct)
    {
        if (size > MaxBytes) throw new InvalidDataException("Plugin file too large.");
        using var output = new MemoryStream(); var buffer = new byte[81920]; int read;
        while ((read = await stream.ReadAsync(buffer, ct)) != 0)
        { if (output.Length + read > size || output.Length + read > MaxBytes) throw new InvalidDataException("Plugin file too large."); output.Write(buffer, 0, read); }
        return output.ToArray();
    }
    // Resolve every ancestor, not only the final file: Windows junctions may occur anywhere.
    private static string Resolved(string path)
    {
        path = Path.GetFullPath(path); var current = Path.GetPathRoot(path)!;
        foreach (var segment in path[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment); FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                current = info.ResolveLinkTarget(true)?.FullName ?? throw new InvalidDataException("Unresolvable package link.");
        }
        return Path.GetFullPath(current);
    }
    private static string Contained(string root, string path)
    {
        var resolved = Resolved(path);
        if (!resolved.Equals(root, StringComparison.OrdinalIgnoreCase) && !resolved.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Package path resolves outside plugin root.");
        return resolved;
    }
    public static async Task<DesktopAgentPlugin> LoadDirectoryAsync(string directory, CancellationToken ct = default)
    {
        var root = Resolved(directory); var files = new Dictionary<string, byte[]>(StringComparer.Ordinal); var diagnostics = new List<PluginDiagnostic>(); long total = 0; var visited = 0;
        async Task Read(string logical, string path)
        {
            ct.ThrowIfCancellationRequested(); var resolved = Contained(root, path); var size = new FileInfo(resolved).Length;
            if (files.Count >= MaxFiles || (total += size) > MaxBytes) throw new InvalidDataException("Plugin package too large.");
            await using var stream = File.OpenRead(resolved); files.Add(PackagePath(logical), await ReadBoundedAsync(stream, size, ct));
        }
        await Read("plugin.json", Path.Combine(root, "plugin.json"));
        _ = Manifest(JsonNode.Parse(files["plugin.json"]), diagnostics); // Reject before component discovery.
        async Task Walk(string logical, string path, HashSet<string> ancestors)
        {
            var resolved = Contained(root, path);
            if (!ancestors.Add(resolved)) throw new InvalidDataException("Cyclic package link.");
            try
            {
                foreach (var child in Directory.EnumerateFileSystemEntries(resolved))
                {
                    if (++visited > MaxFiles * 2 || ancestors.Count > 64) throw new InvalidDataException("Plugin discovery limit exceeded.");
                    ct.ThrowIfCancellationRequested(); var name = logical + Path.GetFileName(child);
                    try { if (Directory.Exists(child)) await Walk(name + "/", child, ancestors); else await Read(name, child); }
                    catch (InvalidDataException e) { diagnostics.Add(new("error", name.EndsWith("/SKILL.md", StringComparison.Ordinal) ? "skill" : "plugin", "path-denied", e.Message, name)); }
                }
            }
            finally { ancestors.Remove(resolved); }
        }
        foreach (var child in Directory.EnumerateFileSystemEntries(root))
        {
            var name = Path.GetFileName(child); if (name == "plugin.json") continue;
            try
            {
                var resolved = Contained(root, child);
                if (name == "skills" && !Directory.Exists(resolved) || name == "mcp.json" && !File.Exists(resolved)) throw new InvalidDataException("Invalid fixed component location.");
                if (Directory.Exists(resolved)) await Walk(name + "/", resolved, new(StringComparer.OrdinalIgnoreCase)); else await Read(name, resolved);
            }
            catch (InvalidDataException e) { diagnostics.Add(new("error", name == "skills" ? "skills" : name == "mcp.json" ? "mcp" : "plugin", "component-invalid", e.Message, name)); }
        }
        var result = FromFiles(files);
        if (result.Mcp?["mcpServers"] is JsonObject servers)
            foreach (var (name, node) in servers.ToArray())
                if (node is JsonObject server && DesktopAgent.Text(server["type"]) == "stdio")
                {
                    try
                    {
                        var command = DesktopAgent.Text(server["command"]); if (command.StartsWith("./", StringComparison.Ordinal)) _ = Contained(root, Path.Combine(root, command[2..]));
                        var cwd = DesktopAgent.Text(server["cwd"]); if (cwd.StartsWith("./", StringComparison.Ordinal)) _ = Contained(root, Path.Combine(root, cwd[2..]));
                        else if (cwd.StartsWith("${PLUGIN_ROOT}/", StringComparison.Ordinal)) _ = Contained(root, Path.Combine(root, cwd[15..]));
                    }
                    catch (Exception e) when (e is InvalidDataException or IOException) { servers.Remove(name); diagnostics.Add(new("error", "server", "server-path-denied", "Server package path is invalid.", "mcp.json", name)); }
                }
        result.Diagnostics.AddRange(diagnostics); return result;
    }
}

/// <summary>Committed snapshots, account/profile isolated. Names are identities; import replaces in place.</summary>
public sealed class DesktopAgentPluginStore(string directory)
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    private string Root(string partition) => Path.Combine(directory, McpValidation.Hash(partition));
    private string FilePath(string partition, string id) => Path.Combine(Root(partition), McpValidation.Hash(id) + ".json");
    public async Task<IReadOnlyList<DesktopAgentPlugin>> ListAsync(string partition, CancellationToken ct = default)
    {
        var root = Root(partition); if (!Directory.Exists(root)) return [];
        var result = new List<DesktopAgentPlugin>();
        foreach (var path in Directory.EnumerateFiles(root, "*.json"))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (new FileInfo(path).Length > AgentPluginPackages.MaxBytes * 2L) continue;
                var stored = JsonSerializer.Deserialize<DesktopAgentPlugin>(await File.ReadAllBytesAsync(path, ct), JsonSerializerOptions.Web);
                if (stored is null) continue; var valid = AgentPluginPackages.Validate(stored); valid.Diagnostics = stored.Diagnostics.Concat(valid.Diagnostics).Distinct().ToList(); result.Add(valid);
            }
            catch (Exception e) when (e is JsonException or InvalidDataException or IOException) { }
        }
        return result.OrderBy(p => p.Id, StringComparer.Ordinal).ToArray();
    }
    public async Task<DesktopAgentPlugin> SaveAsync(string partition, DesktopAgentPlugin draft, bool replace, CancellationToken ct = default)
    {
        var copy = AgentPluginPackages.Validate(draft); copy.Diagnostics = draft.Diagnostics.Concat(copy.Diagnostics).Distinct().ToList();
        var path = FilePath(partition, copy.Id); var gate = Gates.GetOrAdd(Root(partition), _ => new(1, 1)); await gate.WaitAsync(ct);
        try
        {
            if (File.Exists(path))
            {
                if (!replace) throw new InvalidDataException("A plugin with this name already exists.");
                var previous = JsonSerializer.Deserialize<DesktopAgentPlugin>(await File.ReadAllBytesAsync(path, ct), JsonSerializerOptions.Web)!;
                copy.CreatedAt = previous.CreatedAt; copy.Favorite = previous.Favorite;
            }
            copy.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); Directory.CreateDirectory(Root(partition));
            await WriteAsync(path, copy, ct); return copy;
        }
        finally { gate.Release(); }
    }
    private static async Task WriteAsync(string path, DesktopAgentPlugin value, CancellationToken ct)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { await File.WriteAllBytesAsync(temp, JsonSerializer.SerializeToUtf8Bytes(value, JsonSerializerOptions.Web), ct); ct.ThrowIfCancellationRequested(); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public async Task FavoriteAsync(string partition, string id, bool value, CancellationToken ct = default)
    {
        var gate = Gates.GetOrAdd(Root(partition), _ => new(1, 1)); await gate.WaitAsync(ct);
        try { var path = FilePath(partition, id); var stored = JsonSerializer.Deserialize<DesktopAgentPlugin>(await File.ReadAllBytesAsync(path, ct), JsonSerializerOptions.Web)!; stored.Favorite = value; await WriteAsync(path, stored, ct); }
        finally { gate.Release(); }
    }
    public async Task DeleteAsync(string partition, string id, CancellationToken ct = default)
    {
        var gate = Gates.GetOrAdd(Root(partition), _ => new(1, 1)); await gate.WaitAsync(ct);
        try { ct.ThrowIfCancellationRequested(); File.Delete(FilePath(partition, id)); }
        finally { gate.Release(); }
    }
}
