using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

namespace AIHappey.Desktop.Core;

public sealed record DesktopSkill(string Id, string Name, string Description, string Origin, string? Version = null,
    string? Server = null, string? Uri = null)
{
    public string Label => Server is null ? Name : $"{Name} ({Server})";
}

public sealed record DesktopSkillFile(string Path, byte[] Data)
{
    public string MimeType => SkillFiles.MimeType(Path);
}

public sealed record DesktopSkillContent(DesktopSkill Skill, string Body, IReadOnlyList<string> ResourcePaths,
    Func<string, CancellationToken, Task<DesktopSkillFile>> Read);

/// <summary>Data only: skill files are never executed or extracted into a filesystem directory.</summary>
public static class SkillFiles
{
    public const int MaxBytes = 16 * 1024 * 1024;
    public const int MaxFiles = 512;
    public static string RelativePath(string path)
    {
        var normalized = path.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(normalized) || normalized.StartsWith('/') || normalized.Contains(':')
            || normalized.Any(char.IsControl) || normalized.Split('/').Any(p => p is "" or "." or "..")
            || Regex.IsMatch(normalized, "%2f|%5c|%2e", RegexOptions.IgnoreCase))
            throw new InvalidDataException("Resource must stay inside the skill directory.");
        return normalized;
    }
    public static string MimeType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".md" => "text/markdown", ".json" => "application/json", ".yaml" or ".yml" => "application/yaml",
        ".xml" or ".svg" => "application/xml", ".html" => "text/html", ".csv" => "text/csv",
        ".txt" or ".py" or ".js" or ".ts" or ".sh" or ".ps1" or ".css" or ".sql" or ".cs" or ".r" or ".toml" or ".ini" => "text/plain",
        ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".gif" => "image/gif", ".webp" => "image/webp",
        ".pdf" => "application/pdf", _ => "application/octet-stream"
    };
    public static bool IsText(string mime) => mime.StartsWith("text/", StringComparison.Ordinal)
        || mime is "application/json" or "application/yaml" or "application/xml";

    public static (JsonObject Frontmatter, string Body) Markdown(string text)
    {
        var match = Regex.Match(text, "\\A---[ \\t]*\\r?\\n([\\s\\S]*?)\\r?\\n---[ \\t]*(?:\\r?\\n|$)([\\s\\S]*)\\z");
        if (!match.Success) throw new InvalidDataException("Missing skill frontmatter.");
        var yaml = new YamlStream(); yaml.Load(new StringReader(match.Groups[1].Value));
        if (yaml.Documents.Count != 1 || yaml.Documents[0].RootNode is not YamlMappingNode mapping)
            throw new InvalidDataException("Invalid skill frontmatter.");
        var nodes = 0;
        JsonNode? Convert(YamlNode node, int depth)
        {
            if (++nodes > 2048 || depth > 16) throw new InvalidDataException("Skill frontmatter is too complex.");
            if (node is YamlMappingNode map)
            {
                var result = new JsonObject();
                foreach (var (key, value) in map.Children)
                {
                    if (key is not YamlScalarNode scalar || scalar.Value is not { } name || result.ContainsKey(name))
                        throw new InvalidDataException("Invalid skill frontmatter key.");
                    result[name] = Convert(value, depth + 1);
                }
                return result;
            }
            if (node is YamlSequenceNode sequence) return new JsonArray(sequence.Children.Select(n => Convert(n, depth + 1)).ToArray());
            if (node is not YamlScalarNode s) throw new InvalidDataException("Unsupported YAML node.");
            if (s.Style is YamlDotNet.Core.ScalarStyle.Plain)
            {
                if (s.Value is null or "null" or "~") return null;
                if (bool.TryParse(s.Value, out var boolean)) return JsonValue.Create(boolean);
                if (long.TryParse(s.Value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var integer)) return JsonValue.Create(integer);
                if (double.TryParse(s.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number) && double.IsFinite(number)) return JsonValue.Create(number);
            }
            return JsonValue.Create(s.Value ?? "");
        }
        var frontmatter = (JsonObject)Convert(mapping, 0)!;
        if (OpenAIChatConfig.Text(frontmatter["name"]) is not { Length: > 0 }
            || OpenAIChatConfig.Text(frontmatter["description"]) is not { Length: > 0 })
            throw new InvalidDataException("Skill name and description are required.");
        return (frontmatter, match.Groups[2].Value.Trim());
    }

    public static async Task<DesktopSkillContent> ArchiveAsync(DesktopSkill descriptor, byte[] bytes, CancellationToken ct)
    {
        if (bytes.Length > DesktopCatalogClient.MaxDownloadBytes) throw new InvalidDataException("Skill archive is too large.");
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        if (zip.Entries.Count > MaxFiles * 2) throw new InvalidDataException("Too many skill archive entries.");
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        long total = 0;
        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            var directory = entry.FullName.EndsWith('/');
            var path = RelativePath(directory ? entry.FullName.TrimEnd('/') : entry.FullName);
            // Unix symbolic links must not be interpreted as bundled file contents.
            if (((entry.ExternalAttributes >> 16) & 0xf000) == 0xa000) throw new InvalidDataException("Skill archive contains a link.");
            if (directory) continue;
            if (!entries.TryAdd(path, entry) || entries.Count > MaxFiles || (total += entry.Length) > MaxBytes)
                throw new InvalidDataException("Invalid or oversized skill archive.");
        }
        var manifests = entries.Keys.Where(p => p == "SKILL.md" || p.EndsWith("/SKILL.md", StringComparison.Ordinal)).ToArray();
        if (manifests.Length != 1) throw new InvalidDataException("A remote skill archive must contain exactly one SKILL.md.");
        var manifest = manifests[0]; var root = manifest[..^"SKILL.md".Length];
        var files = new Dictionary<string, DesktopSkillFile>(StringComparer.Ordinal);
        foreach (var (path, entry) in entries.Where(p => p.Key.StartsWith(root, StringComparison.Ordinal)))
        {
            await using var stream = entry.Open();
            using var output = new MemoryStream(); var chunk = new byte[81920]; int read;
            while ((read = await stream.ReadAsync(chunk, ct)) != 0)
            {
                if (output.Length + read > entry.Length || output.Length + read > MaxBytes) throw new InvalidDataException("Skill file is too large.");
                output.Write(chunk, 0, read);
            }
            var relative = RelativePath(path[root.Length..]); files.Add(relative, new(relative, output.ToArray()));
        }
        var parsed = Markdown(Encoding.UTF8.GetString(files["SKILL.md"].Data));
        var name = OpenAIChatConfig.Text(parsed.Frontmatter["name"])!;
        if (!Regex.IsMatch(name, "^(?!-)(?!.*--)[a-z0-9-]{1,64}(?<!-)$")
            || root.Length > 0 && root.TrimEnd('/').Split('/')[^1] != name)
            throw new InvalidDataException("Skill name does not match its archive directory.");
        var skill = descriptor with { Name = name, Description = OpenAIChatConfig.Text(parsed.Frontmatter["description"])! };
        return new(skill, parsed.Body, files.Keys.Where(p => p != "SKILL.md").Order(StringComparer.Ordinal).ToArray(),
            (path, token) => { token.ThrowIfCancellationRequested(); return Task.FromResult(files.TryGetValue(RelativePath(path), out var file)
                ? file : throw new InvalidDataException("Skill resource was not found.")); });
    }
}

/// <summary>Account/API-isolated archive cache. Failed/canceled downloads never replace a good cache.</summary>
public sealed class DesktopSkillStore(string directory, Func<CatalogItem, string, CancellationToken, Task<byte[]>> download)
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> gates = new(StringComparer.Ordinal);
    private string PartitionDirectory(string partition) => Path.Combine(directory, McpValidation.Hash(partition));
    public async Task SaveCatalogAsync(string partition, IReadOnlyList<CatalogItem> items, CancellationToken ct)
    {
        var path = Path.Combine(PartitionDirectory(partition), "catalog.json");
        await WriteAsync(path, JsonSerializer.SerializeToUtf8Bytes(items), ct);
    }
    public async Task<IReadOnlyList<CatalogItem>> CatalogAsync(string partition, CancellationToken ct)
    {
        var path = Path.Combine(PartitionDirectory(partition), "catalog.json");
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 8 * 1024 * 1024) return [];
            return JsonSerializer.Deserialize<CatalogItem[]>(await File.ReadAllBytesAsync(path, ct)) ?? [];
        }
        catch (Exception e) when (e is IOException or JsonException) { return []; }
    }
    public async Task<DesktopSkillContent> ReadAsync(string partition, CatalogItem item, CancellationToken ct)
    {
        var version = item.LatestVersion ?? item.Version ?? "1";
        var path = Path.Combine(PartitionDirectory(partition), McpValidation.Hash(item.Id + "|" + version) + ".zip");
        var gate = gates.GetOrAdd(path, _ => new(1, 1)); await gate.WaitAsync(ct);
        try
        {
            var skill = new DesktopSkill(item.Id, item.Name, item.Description, "remote", version);
            if (File.Exists(path) && new FileInfo(path).Length <= DesktopCatalogClient.MaxDownloadBytes)
            {
                try { return await SkillFiles.ArchiveAsync(skill, await File.ReadAllBytesAsync(path, ct), ct); }
                catch (Exception e) when (e is InvalidDataException or YamlDotNet.Core.YamlException) { File.Delete(path); }
            }
            var bytes = await download(item, version, ct);
            var result = await SkillFiles.ArchiveAsync(skill, bytes, ct);
            await WriteAsync(path, bytes, ct); return result;
        }
        finally { gate.Release(); }
    }
    private static async Task WriteAsync(string path, byte[] bytes, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { await File.WriteAllBytesAsync(temporary, bytes, ct); ct.ThrowIfCancellationRequested(); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
