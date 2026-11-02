using System.Text;

namespace AIHappey.Desktop.Core;

/// <summary>One-time, bounded folder snapshots. No files are executed, watched, or written to the selected tree.</summary>
public static class DesktopSkillDirectories
{
    public const int MaxDepth = 6, MaxDirectories = 2000, MaxEntries = 10000;
    public const long MaxTotalBytes = 64 * 1024 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static bool Ignored(string name) => name is ".git" or "node_modules";
    private static void NoLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException(DesktopResources.Get("SkillFolderLink"));
    }

    public static async Task<DesktopSkillImport> ImportAsync(string directory, CancellationToken ct = default)
    {
        var root = Path.GetFullPath(directory); NoLinks(root);
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        var blocked = new List<string>(); var errors = new List<string>();
        var visited = 0; var directories = 0; var exhausted = false;
        void Error(string path, string key) => errors.Add(path + ": " + DesktopResources.Get(key));
        void Walk(string path, int depth)
        {
            ct.ThrowIfCancellationRequested();
            if (++directories > MaxDirectories) { exhausted = true; return; }
            try
            {
                NoLinks(path);
                foreach (var child in Directory.EnumerateFileSystemEntries(path))
                {
                    ct.ThrowIfCancellationRequested();
                    if (++visited > MaxEntries) { exhausted = true; return; }
                    var name = Path.GetFileName(child); if (Ignored(name)) continue;
                    var relative = SkillFiles.RelativePath(Path.GetRelativePath(root, child).Replace('\\', '/'));
                    try
                    {
                        NoLinks(child);
                        if (Directory.Exists(child))
                        {
                            if (depth >= MaxDepth) { blocked.Add(relative + "/"); Error(relative, "SkillFolderLimit"); }
                            else Walk(child, depth + 1);
                            if (exhausted) return;
                        }
                        else files.Add(relative, child);
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
                    { blocked.Add(relative); errors.Add(relative + ": " + e.Message); }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                blocked.Add(relative == "." ? "" : relative + "/"); errors.Add(relative + ": " + e.Message);
            }
        }
        Walk(root, 0);
        if (exhausted) return new([], [.. errors, DesktopResources.Get("SkillFolderLimit")]);
        var manifests = files.Keys.Where(p => p == "SKILL.md" || p.EndsWith("/SKILL.md", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal).ToArray();
        var prefixes = manifests.Select(p => p[..^"SKILL.md".Length]).ToArray();
        if (manifests.Length == 0) errors.Add(DesktopResources.Get("SkillFolderEmpty"));
        var drafts = new List<DesktopSkillDraft>(); var names = new HashSet<string>(StringComparer.Ordinal); long total = 0;
        foreach (var manifest in manifests)
        {
            ct.ThrowIfCancellationRequested(); var prefix = manifest[..^"SKILL.md".Length];
            try
            {
                if (prefixes.Any(p => p != prefix && (p.StartsWith(prefix, StringComparison.Ordinal) || prefix.StartsWith(p, StringComparison.Ordinal))))
                    throw new InvalidDataException(DesktopResources.Get("SkillArchiveOverlapping"));
                if (blocked.Any(p => p.StartsWith(prefix, StringComparison.Ordinal) || prefix.StartsWith(p, StringComparison.Ordinal)))
                    throw new InvalidDataException(DesktopResources.Get("SkillFolderIncomplete"));
                var scoped = files.Where(f => f.Key.StartsWith(prefix, StringComparison.Ordinal)).OrderBy(f => f.Key, StringComparer.Ordinal).ToArray();
                if (scoped.Length > SkillFiles.MaxFiles) throw new InvalidDataException(DesktopResources.Get("SkillPackageTooLarge"));
                long size = 0;
                foreach (var file in scoped) size = checked(size + new FileInfo(file.Value).Length);
                if (size > SkillFiles.MaxBytes) throw new InvalidDataException(DesktopResources.Get("SkillPackageTooLarge"));
                if ((total += size) > MaxTotalBytes) return new([], [.. errors, DesktopResources.Get("SkillFolderLimit")]);
                async Task<byte[]> Read(string path)
                {
                    ct.ThrowIfCancellationRequested(); NoLinks(path);
                    var length = new FileInfo(path).Length;
                    if (length > SkillFiles.MaxBytes) throw new InvalidDataException(DesktopResources.Get("SkillPackageTooLarge"));
                    await using var stream = File.OpenRead(path); using var content = new StreamContent(stream);
                    var bytes = await DesktopCatalogClient.ReadBoundedAsync(content, (int)length, ct);
                    if (bytes.LongLength != length) throw new InvalidDataException(DesktopResources.Get("SkillFolderIncomplete"));
                    return bytes;
                }
                var parsed = SkillFiles.Markdown(Utf8.GetString(await Read(files[manifest])));
                var draft = new DesktopSkillDraft { Frontmatter = parsed.Frontmatter, Instructions = parsed.Body };
                if (Path.GetFileName(Path.GetDirectoryName(files[manifest])) != draft.Name)
                    throw new InvalidDataException(DesktopResources.Get("SkillDirectoryMismatch"));
                foreach (var (logical, path) in scoped.Where(f => f.Key != manifest))
                    draft.Files.Add(new(DesktopSkillPackages.ResourcePath(logical[prefix.Length..]), await Read(path)));
                DesktopSkillPackages.Validate(draft);
                if (!names.Add(draft.Name)) throw new InvalidDataException(DesktopResources.Get("SkillFolderDuplicate"));
                drafts.Add(draft);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or YamlDotNet.Core.YamlException or DecoderFallbackException or OverflowException)
            { errors.Add(manifest + ": " + e.Message); }
        }
        return new(drafts, errors);
    }
}
