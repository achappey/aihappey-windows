using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace AIHappey.Desktop.Core;

/// <summary>An isolated editor draft. App identity/version bookkeeping is not skill frontmatter.</summary>
public sealed class DesktopSkillDraft
{
    public JsonObject Frontmatter { get; set; } = new() { ["name"] = "", ["description"] = "" };
    public string Name { get => Frontmatter["name"]?.GetValue<string>() ?? ""; set => Frontmatter["name"] = value; }
    public string Description { get => Frontmatter["description"]?.GetValue<string>() ?? ""; set => Frontmatter["description"] = value; }
    public string Instructions { get; set; } = "";
    public List<DesktopSkillFile> Files { get; set; } = [];
    public DesktopSkillDraft Clone() => new() { Frontmatter = (JsonObject)Frontmatter.DeepClone(), Instructions = Instructions,
        Files = Files.Select(f => new DesktopSkillFile(f.Path, f.Data.ToArray())).ToList() };
}

public sealed record DesktopSkillImport(IReadOnlyList<DesktopSkillDraft> Skills, IReadOnlyList<string> Diagnostics);

/// <summary>Strict Agent Skills packages, kept entirely as data. Never extracts or executes files.</summary>
public static class DesktopSkillPackages
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    public static bool ValidName(string name) => Regex.IsMatch(name, "\\A(?!-)(?!.*--)[a-z0-9-]{1,64}(?<!-)\\z");
    public static string NormalizeName(string name)
    {
        var normalized = Regex.Replace(name.Trim().ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        return normalized[..Math.Min(64, normalized.Length)].TrimEnd('-');
    }
    public static void ValidateFrontmatter(JsonObject frontmatter)
    {
        string Text(string key, bool required, int? max = null)
        {
            if (!frontmatter.TryGetPropertyValue(key, out var node))
            { if (required) throw new InvalidDataException(DesktopResources.Format("SkillFieldRequired", key)); return ""; }
            if (node is not JsonValue value || !value.TryGetValue<string>(out var text)
                || required && string.IsNullOrWhiteSpace(text) || max.HasValue && (string.IsNullOrWhiteSpace(text) || text.Length > max))
                throw new InvalidDataException(DesktopResources.Format("SkillFieldInvalid", key));
            return text;
        }
        if (!ValidName(Text("name", true))) throw new InvalidDataException(DesktopResources.Get("SkillNameInvalid"));
        Text("description", true, 1024); Text("license", false); Text("compatibility", false, 500); Text("allowed-tools", false);
        if (frontmatter.TryGetPropertyValue("metadata", out var metadata)
            && (metadata is not JsonObject map || map.Any(p => p.Value is not JsonValue value || !value.TryGetValue<string>(out _))))
            throw new InvalidDataException(DesktopResources.Format("SkillFieldInvalid", "metadata"));
    }
    public static string ResourcePath(string path)
    {
        var relative = SkillFiles.RelativePath(path);
        if (relative.Split('/').Last().Equals("SKILL.md", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(DesktopResources.Get("SkillManifestReserved"));
        return relative;
    }
    public static void Validate(DesktopSkillDraft draft)
    {
        ValidateFrontmatter(draft.Frontmatter);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long total = Utf8.GetByteCount(Markdown(draft));
        foreach (var file in draft.Files)
        {
            if (!paths.Add(ResourcePath(file.Path))) throw new InvalidDataException(DesktopResources.Get("SkillDuplicatePath"));
            total += file.Data.LongLength;
        }
        // A file may not also be a directory, even on a case-insensitive Windows filesystem.
        if (paths.Any(p => p.Split('/').SkipLast(1).Select((_, i) => string.Join('/', p.Split('/').Take(i + 1))).Any(paths.Contains)))
            throw new InvalidDataException(DesktopResources.Get("SkillDuplicatePath"));
        if (draft.Files.Count + 1 > SkillFiles.MaxFiles || total > SkillFiles.MaxBytes)
            throw new InvalidDataException(DesktopResources.Get("SkillPackageTooLarge"));
    }
    public static string Markdown(DesktopSkillDraft draft)
    {
        YamlNode Convert(JsonNode? node) => node switch
        {
            JsonObject map => new YamlMappingNode(map.Select(p => new KeyValuePair<YamlNode, YamlNode>(
                new YamlScalarNode(p.Key) { Style = ScalarStyle.DoubleQuoted }, Convert(p.Value)))),
            JsonArray array => new YamlSequenceNode(array.Select(Convert)),
            JsonValue value when value.TryGetValue<string>(out var text) => new YamlScalarNode(text) { Style = ScalarStyle.DoubleQuoted },
            _ => new YamlScalarNode(node?.ToJsonString() ?? "null") { Style = ScalarStyle.Plain }
        };
        var yaml = new YamlStream(new YamlDocument(Convert(draft.Frontmatter)));
        using var writer = new StringWriter(CultureInfo.InvariantCulture); yaml.Save(writer, false);
        // YamlStream emits document end markers; the manifest uses frontmatter delimiters instead.
        var text = writer.ToString().Replace("\r\n", "\n").TrimEnd();
        if (text.StartsWith("---\n", StringComparison.Ordinal)) text = text[4..];
        if (text.EndsWith("\n...", StringComparison.Ordinal)) text = text[..^4];
        return "---\n" + text + "\n---\n" + draft.Instructions;
    }
    public static byte[] Export(DesktopSkillDraft draft)
    {
        Validate(draft);
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            void Add(string path, byte[] data) { using var output = zip.CreateEntry(draft.Name + "/" + path).Open(); output.Write(data); }
            Add("SKILL.md", Utf8.GetBytes(Markdown(draft)));
            foreach (var file in draft.Files.OrderBy(f => f.Path, StringComparer.Ordinal)) Add(ResourcePath(file.Path), file.Data);
        }
        return stream.ToArray();
    }
    public static async Task<DesktopSkillImport> ImportAsync(byte[] bytes, CancellationToken ct)
    {
        if (bytes.Length > DesktopCatalogClient.MaxDownloadBytes) throw new InvalidDataException(DesktopResources.Get("SkillPackageTooLarge"));
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        if (zip.Entries.Count > SkillFiles.MaxFiles * 2) throw new InvalidDataException(DesktopResources.Get("SkillPackageTooLarge"));
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase); long total = 0;
        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            var directory = entry.FullName.EndsWith('/'); var path = SkillFiles.RelativePath(directory ? entry.FullName.TrimEnd('/') : entry.FullName);
            if (((entry.ExternalAttributes >> 16) & 0xf000) == 0xa000) throw new InvalidDataException(DesktopResources.Get("SkillArchiveLink"));
            if (directory) continue;
            if (!entries.TryAdd(path, entry)) throw new InvalidDataException(DesktopResources.Get("SkillDuplicatePath"));
            if (entries.Count > SkillFiles.MaxFiles || (total += entry.Length) > SkillFiles.MaxBytes)
                throw new InvalidDataException(DesktopResources.Get("SkillPackageTooLarge"));
        }
        var manifests = entries.Keys.Where(p => p == "SKILL.md" || p.EndsWith("/SKILL.md", StringComparison.Ordinal)).ToArray();
        if (manifests.Length == 0) throw new InvalidDataException(DesktopResources.Get("SkillArchiveEmpty"));
        var roots = manifests.Select(p => p[..^"SKILL.md".Length]).ToArray();
        if (roots.Any(root => roots.Any(other => other != root && other.StartsWith(root, StringComparison.OrdinalIgnoreCase))))
            throw new InvalidDataException(DesktopResources.Get("SkillArchiveOverlapping"));
        var skills = new List<DesktopSkillDraft>(); var errors = new List<string>(); var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var manifest in manifests)
        {
            ct.ThrowIfCancellationRequested(); var root = manifest[..^"SKILL.md".Length];
            try
            {
                async Task<byte[]> Read(ZipArchiveEntry entry)
                {
                    await using var input = entry.Open(); using var content = new StreamContent(input);
                    return await DesktopCatalogClient.ReadBoundedAsync(content, (int)Math.Min(entry.Length, SkillFiles.MaxBytes), ct);
                }
                var parsed = SkillFiles.Markdown(Utf8.GetString(await Read(entries[manifest])));
                ValidateFrontmatter(parsed.Frontmatter);
                var draft = new DesktopSkillDraft { Frontmatter = parsed.Frontmatter, Instructions = parsed.Body };
                if (root.Length > 0 && root.TrimEnd('/').Split('/').Last() != draft.Name)
                    throw new InvalidDataException(DesktopResources.Get("SkillDirectoryMismatch"));
                foreach (var (path, entry) in entries.Where(p => p.Key.StartsWith(root, StringComparison.OrdinalIgnoreCase) && p.Key != manifest))
                    draft.Files.Add(new(ResourcePath(path[root.Length..]), await Read(entry)));
                Validate(draft);
                if (!names.Add(draft.Name)) throw new InvalidDataException(DesktopResources.Get("SkillAlreadyExists"));
                skills.Add(draft);
            }
            catch (Exception error) when (error is InvalidDataException or YamlException or DecoderFallbackException)
            { errors.Add(manifest + ": " + error.Message); }
        }
        return new(skills, errors);
    }
    public static DesktopSkillContent Content(DesktopSkill descriptor, DesktopSkillDraft draft)
    {
        var copy = draft.Clone(); var files = copy.Files.ToDictionary(f => f.Path, StringComparer.Ordinal);
        files.Add("SKILL.md", new("SKILL.md", Utf8.GetBytes(Markdown(copy))));
        return new(descriptor, copy.Instructions, copy.Files.Select(f => f.Path).Order(StringComparer.Ordinal).ToArray(), (path, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(files.TryGetValue(SkillFiles.RelativePath(path), out var file)
                ? file with { Data = file.Data.ToArray() } : throw new InvalidDataException(DesktopResources.Get("SkillResourceMissing")));
        });
    }
}

/// <summary>Immutable version archives plus an atomically replaced head. Orphaned staged versions are never published.</summary>
public sealed class DesktopLocalSkillStore(string directory)
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> gates = new(StringComparer.Ordinal);
    private string Partition(string partition) => Path.Combine(directory, McpValidation.Hash(partition));
    private string DirectoryFor(string partition, string id) => Path.Combine(Partition(partition), McpValidation.Hash(id));
    private async Task<DesktopSkill?> HeadAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 16384) throw new InvalidDataException(DesktopResources.Get("SkillPackageTooLarge"));
        var skill = JsonSerializer.Deserialize<DesktopSkill>(await File.ReadAllBytesAsync(path, ct));
        if (skill is null || skill.Origin != "local" || !skill.Id.StartsWith("local:", StringComparison.Ordinal)
            || !DesktopSkillPackages.ValidName(skill.Name) || !long.TryParse(skill.Version, NumberStyles.None, CultureInfo.InvariantCulture, out var version) || version < 1)
            throw new InvalidDataException(DesktopResources.Get("SkillStoredInvalid"));
        return skill;
    }
    public async Task<IReadOnlyList<DesktopSkill>> ListAsync(string partition, CancellationToken ct)
    {
        var root = Partition(partition); if (!Directory.Exists(root)) return [];
        var result = new List<DesktopSkill>();
        foreach (var folder in Directory.EnumerateDirectories(root))
        {
            ct.ThrowIfCancellationRequested();
            var skill = await HeadAsync(Path.Combine(folder, "head.json"), ct);
            if (skill is not null)
            {
                if (Path.GetFileName(folder) != McpValidation.Hash(skill.Id)) throw new InvalidDataException(DesktopResources.Get("SkillStoredInvalid"));
                result.Add(skill);
            }
        }
        return result.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }
    public async Task<DesktopSkillDraft> ReadAsync(string partition, DesktopSkill skill, CancellationToken ct)
    {
        if (!long.TryParse(skill.Version, NumberStyles.None, CultureInfo.InvariantCulture, out var version) || version < 1)
            throw new InvalidDataException(DesktopResources.Get("SkillStoredInvalid"));
        var path = Path.Combine(DirectoryFor(partition, skill.Id), version.ToString(CultureInfo.InvariantCulture) + ".zip");
        if (!File.Exists(path)) throw new InvalidDataException(DesktopResources.Get("SkillUnavailableHint"));
        if (new FileInfo(path).Length > DesktopCatalogClient.MaxDownloadBytes) throw new InvalidDataException(DesktopResources.Get("SkillPackageTooLarge"));
        var parsed = await DesktopSkillPackages.ImportAsync(await File.ReadAllBytesAsync(path, ct), ct);
        if (parsed.Diagnostics.Count > 0 || parsed.Skills.Count != 1 || parsed.Skills[0].Name != skill.Name)
            throw new InvalidDataException(DesktopResources.Get("SkillStoredInvalid"));
        return parsed.Skills[0];
    }
    public async Task<DesktopSkill> SaveAsync(string partition, DesktopSkillDraft draft, string? existingId, CancellationToken ct)
    {
        var copy = draft.Clone(); var bytes = DesktopSkillPackages.Export(copy);
        var gate = gates.GetOrAdd(Partition(partition), _ => new(1, 1)); await gate.WaitAsync(ct);
        try
        {
            var items = await ListAsync(partition, ct); var previous = items.FirstOrDefault(s => s.Id == existingId);
            if (existingId is not null && previous is null) throw new InvalidDataException(DesktopResources.Get("SkillUnavailableHint"));
            if (previous is not null && previous.Name != copy.Name) throw new InvalidDataException(DesktopResources.Get("SkillNameLocked"));
            if (items.Any(s => s.Name == copy.Name && s.Id != existingId)) throw new InvalidDataException(DesktopResources.Get("SkillAlreadyExists"));
            var version = previous is null ? 1L : checked(long.Parse(previous.Version!, CultureInfo.InvariantCulture) + 1);
            var skill = new DesktopSkill(previous?.Id ?? "local:" + Guid.NewGuid().ToString("N"), copy.Name, copy.Description, "local", version.ToString(CultureInfo.InvariantCulture));
            var folder = DirectoryFor(partition, skill.Id); Directory.CreateDirectory(folder);
            var archive = Path.Combine(folder, skill.Version + ".zip"); var head = Path.Combine(folder, "head.json");
            var temporary = head + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                // A previous interrupted commit may leave this unpublished version; replacing it is safe under the partition gate.
                await File.WriteAllBytesAsync(archive, bytes, ct);
                await File.WriteAllBytesAsync(temporary, JsonSerializer.SerializeToUtf8Bytes(skill), ct);
                ct.ThrowIfCancellationRequested(); File.Move(temporary, head, true); return skill;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        finally { gate.Release(); }
    }
    public async Task DeleteAsync(string partition, string id, CancellationToken ct)
    {
        var gate = gates.GetOrAdd(Partition(partition), _ => new(1, 1)); await gate.WaitAsync(ct);
        try { ct.ThrowIfCancellationRequested(); var folder = DirectoryFor(partition, id); if (Directory.Exists(folder)) Directory.Delete(folder, true); }
        finally { gate.Release(); }
    }
}
