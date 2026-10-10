using System.Text.Json;

namespace AIHappey.Desktop.Core;

public sealed record SharedFileReference(string Id, string Path, bool IsFolder, DateTimeOffset AddedAt)
{
    public string Name => System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(Path)) is { Length: > 0 } name ? name : Path;
}

public sealed record SharedFileView(SharedFileReference Reference, string Status, long? Size = null, DateTimeOffset? ModifiedAt = null)
{
    public bool Available => Status == "available";
}

/// <summary>Only user-selected paths and metadata. No document bytes, extracted text, or directory snapshots.</summary>
public sealed class SharedFileStore(string directory)
{
    private const int MaximumReferences = 10_000;
    private const long MaximumDocumentBytes = 8 * 1024 * 1024;
    private readonly SemaphoreSlim gate = new(1, 1);
    private sealed class Document
    {
        public int Version { get; set; } = 1;
        public List<SharedFileReference> Items { get; set; } = [];
    }
    private string FilePath(string partition)
    {
        if (partition.Length != 64 || !partition.All(Uri.IsHexDigit)) throw new LocalToolException(DesktopResources.Get("FilesInvalidStore"));
        return System.IO.Path.Combine(directory, partition + ".json");
    }
    private async Task<List<SharedFileReference>> LoadAsync(string partition, CancellationToken ct)
    {
        var path = FilePath(partition);
        if (!File.Exists(path)) return [];
        await using var stream = File.OpenRead(path);
        if (stream.Length > MaximumDocumentBytes) throw new LocalToolException(DesktopResources.Get("FilesInvalidStore"));
        var document = await JsonSerializer.DeserializeAsync<Document>(stream, JsonSerializerOptions.Web, ct);
        if (document is null || document.Version != 1 || document.Items is null || document.Items.Count > MaximumReferences)
            throw new LocalToolException(DesktopResources.Get("FilesInvalidStore"));
        var ids = new HashSet<string>(StringComparer.Ordinal); var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in document.Items)
        {
            ct.ThrowIfCancellationRequested();
            if (item is null || !Guid.TryParseExact(item.Id, "N", out _) || !ids.Add(item.Id)
                || item.Path != System.IO.Path.TrimEndingDirectorySeparator(LocalFilePaths.Normalize(item.Path)) || !paths.Add(item.Path))
                throw new LocalToolException(DesktopResources.Get("FilesInvalidStore"));
        }
        return document.Items;
    }
    public async Task<IReadOnlyList<SharedFileReference>> ListAsync(string partition, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try { return (await LoadAsync(partition, ct)).ToArray(); }
        finally { gate.Release(); }
    }
    public async Task<SharedFileReference> AddAsync(string partition, string path, bool isFolder, CancellationToken ct = default)
    {
        path = System.IO.Path.TrimEndingDirectorySeparator(LocalFilePaths.Normalize(path));
        await gate.WaitAsync(ct);
        try
        {
            ct.ThrowIfCancellationRequested();
            using var pinned = SharedFileAccess.PinParents(path, isFolder);
            var attributes = File.GetAttributes(path);
            if (attributes.HasFlag(FileAttributes.Directory) != isFolder) throw new LocalToolException(DesktopResources.Get("FilesKindChanged"));
            LocalFilePaths.CheckNoLinks(path);
            var items = await LoadAsync(partition, ct);
            var existing = items.FirstOrDefault(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                if (existing.IsFolder != isFolder) throw new LocalToolException(DesktopResources.Get("FilesKindChanged"));
                return existing;
            }
            if (items.Count >= MaximumReferences) throw new LocalToolException(DesktopResources.Get("FilesReferenceLimit"));
            var item = new SharedFileReference(Guid.NewGuid().ToString("N"), path, isFolder, DateTimeOffset.UtcNow);
            items.Add(item); await SaveAsync(partition, items, ct); return item;
        }
        finally { gate.Release(); }
    }
    public async Task RemoveAsync(string partition, string id, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            var items = await LoadAsync(partition, ct);
            if (items.RemoveAll(i => i.Id == id) > 0) await SaveAsync(partition, items, ct);
        }
        finally { gate.Release(); }
    }
    private async Task SaveAsync(string partition, List<SharedFileReference> items, CancellationToken ct)
    {
        var path = FilePath(partition); Directory.CreateDirectory(directory);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, new Document { Items = items }, JsonSerializerOptions.Web, ct);
                if (stream.Length > MaximumDocumentBytes) throw new LocalToolException(DesktopResources.Get("FilesReferenceLimit"));
                await stream.FlushAsync(ct);
            }
            ct.ThrowIfCancellationRequested(); File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static SharedFileView Inspect(SharedFileReference item)
    {
        try
        {
            using var pinned = SharedFileAccess.PinParents(item.Path, item.IsFolder);
            LocalFilePaths.CheckNoLinks(item.Path);
            var info = new FileInfo(item.Path); var attributes = File.GetAttributes(item.Path);
            if (attributes.HasFlag(FileAttributes.Directory) != item.IsFolder) return new(item, "changed");
            return new(item, "available", item.IsFolder ? null : info.Length, File.GetLastWriteTimeUtc(item.Path));
        }
        catch (FileNotFoundException) { return new(item, "missing"); }
        catch (DirectoryNotFoundException) { return new(item, "missing"); }
        catch (UnauthorizedAccessException) { return new(item, "denied"); }
        catch (Exception error) when (error is IOException or LocalToolException) { return new(item, "unavailable"); }
    }
}
