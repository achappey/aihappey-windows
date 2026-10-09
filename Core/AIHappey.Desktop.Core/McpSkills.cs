using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AIHappey.Desktop.Core;

public sealed record McpSkillManifest(string Uri, JsonObject Frontmatter, IReadOnlyList<McpSkillFile>? Files)
{
    public string Name => OpenAIChatConfig.Text(Frontmatter["name"])!;
    public string Description => OpenAIChatConfig.Text(Frontmatter["description"])!;
    public string Root => Uri[..^"SKILL.md".Length];
    public static McpSkillManifest Parse(JsonElement json)
    {
        var uri = CatalogProjection.Text(json, "uri") ?? throw new InvalidDataException("Missing MCP skill URI.");
        DesktopMcpResources.ValidateUri(uri);
        if (!uri.EndsWith("/SKILL.md", StringComparison.Ordinal)
            || !json.TryGetProperty("frontmatter", out var frontmatter) || frontmatter.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Invalid MCP skill manifest.");
        var name = CatalogProjection.Text(frontmatter, "name");
        if (name is null || CatalogProjection.Text(frontmatter, "description") is null
            || uri[..^"/SKILL.md".Length].Split('/')[^1] != name)
            throw new InvalidDataException("Invalid MCP skill frontmatter.");
        var resources = json.GetProperty("resources");
        List<McpSkillFile>? files = null;
        if (resources.ValueKind != JsonValueKind.String || resources.GetString() != "dynamic")
        {
            if (resources.ValueKind != JsonValueKind.Array || resources.GetArrayLength() is 0 or > SkillFiles.MaxFiles)
                throw new InvalidDataException("Invalid MCP skill resources.");
            files = []; var seen = new HashSet<string>(StringComparer.Ordinal); long total = 0;
            var root = uri[..^"SKILL.md".Length];
            foreach (var resource in resources.EnumerateArray())
            {
                var fileUri = CatalogProjection.Text(resource, "uri") ?? "";
                var digest = CatalogProjection.Text(resource, "digest") ?? "";
                if (!fileUri.StartsWith(root, StringComparison.Ordinal) || !seen.Add(fileUri)
                    || !System.Text.RegularExpressions.Regex.IsMatch(digest, "^sha256:[0-9a-f]{64}$")
                    || !resource.TryGetProperty("size", out var size) || !size.TryGetInt64(out var length)
                    || length < 0 || length > SkillFiles.MaxBytes || (total += length) > SkillFiles.MaxBytes)
                    throw new InvalidDataException("Invalid MCP skill resource manifest.");
                SkillFiles.RelativePath(fileUri[root.Length..]); files.Add(new(fileUri, digest, length));
            }
            if (!seen.Contains(uri)) throw new InvalidDataException("MCP manifest is missing SKILL.md.");
        }
        return new(uri, JsonNode.Parse(frontmatter.GetRawText())!.AsObject(), files);
    }
}
public sealed record McpSkillFile(string Uri, string Digest, long Size);

public sealed class DesktopMcpSkill(string server, McpSkillManifest manifest,
    Func<string, CancellationToken, Task<JsonElement>> read, Func<bool> available)
{
    public McpSkillManifest Manifest { get; } = manifest;
    public DesktopSkill Descriptor { get; } = new(Id(server, manifest.Uri), manifest.Name, manifest.Description, "mcp", Server: server, Uri: manifest.Uri);
    public static string Id(string server, string uri) => "mcp:" + Encode(server) + ":" + Encode(uri);
    // JS encodeURIComponent leaves these characters unescaped; identity must match the browser.
    private static string Encode(string value) => Uri.EscapeDataString(value).Replace("%21", "!").Replace("%27", "'").Replace("%28", "(").Replace("%29", ")").Replace("%2A", "*");
    private void AssertAvailable()
    {
        if (!available()) throw new InvalidDataException("MCP skill is disabled or disconnected.");
    }
    private async Task<DesktopSkillFile> ReadAsync(string path, CancellationToken ct)
    {
        path = SkillFiles.RelativePath(path); var uri = Manifest.Root + path;
        var file = Manifest.Files?.FirstOrDefault(f => f.Uri == uri);
        if (Manifest.Files is not null && file is null) throw new InvalidDataException("Resource is not in the MCP skill manifest.");
        AssertAvailable(); var result = await read(uri, ct); ct.ThrowIfCancellationRequested(); AssertAvailable();
        if (!result.TryGetProperty("contents", out var contents) || contents.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("MCP server returned no resource.");
        var content = contents.EnumerateArray().FirstOrDefault(c => CatalogProjection.Text(c, "uri") == uri);
        byte[] bytes;
        if (content.ValueKind == JsonValueKind.Object && content.TryGetProperty("blob", out var blob) && blob.ValueKind == JsonValueKind.String)
        {
            if (blob.GetString()!.Length > SkillFiles.MaxBytes * 4L / 3 + 4) throw new InvalidDataException("Skill resource is too large.");
            bytes = Convert.FromBase64String(blob.GetString()!);
        }
        else if (content.ValueKind == JsonValueKind.Object && content.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
            bytes = Encoding.UTF8.GetBytes(text.GetString()!);
        else throw new InvalidDataException("MCP server did not return the requested resource.");
        if (bytes.Length > SkillFiles.MaxBytes || file is not null && (bytes.LongLength != file.Size
            || "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes)) != file.Digest))
            throw new InvalidDataException("MCP skill file failed integrity verification.");
        if (uri == Manifest.Uri)
        {
            var parsed = SkillFiles.Markdown(Encoding.UTF8.GetString(bytes));
            if (!JsonNode.DeepEquals(parsed.Frontmatter, Manifest.Frontmatter))
                throw new InvalidDataException("MCP skill frontmatter does not match its manifest.");
        }
        AssertAvailable(); return new(path, bytes);
    }
    public async Task<DesktopSkillContent> LoadAsync(CancellationToken ct)
    {
        var file = await ReadAsync("SKILL.md", ct);
        // Browser MCP activation returns the verified complete markdown, including frontmatter.
        return new(Descriptor, Encoding.UTF8.GetString(file.Data), Manifest.Files?.Where(f => f.Uri != Manifest.Uri)
            .Select(f => f.Uri[Manifest.Root.Length..]).ToArray() ?? [], ReadAsync);
    }
}
