namespace AIHappey.Desktop.Core;

/// <summary>Browser-style file references over the native Files list. No recursive scans or URL fetches.</summary>
public sealed class LocalToolFiles(SharedFileStore store, string partition, string outputDirectory, Func<bool> isCurrent,
    Func<CancellationToken, Task>? changed = null)
{
    public void Check(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!isCurrent()) throw new OperationCanceledException(ct);
    }
    public async Task<ComposerAttachment> ReadAsync(string idOrName, bool nameOnly, CancellationToken ct)
    {
        Check(ct);
        var items = (await store.ListAsync(partition, ct)).Where(i => !i.IsFolder).ToArray();
        var item = nameOnly ? null : items.FirstOrDefault(i => i.Id == idOrName);
        if (item is null)
        {
            var matches = items.Where(i => i.Name == idOrName).Take(2).ToArray();
            if (matches.Length > 1) throw new LocalToolException("Local filename is ambiguous; use a file ID.");
            item = matches.SingleOrDefault();
        }
        if (item is null) throw new LocalToolException("Local file was not found in Files.");
        return await ReadPathAsync(item.Path, item.Name, ct);
    }
    public async Task<ComposerAttachment> ReadPathAsync(string path, string name, CancellationToken ct)
    {
        Check(ct); path = LocalFilePaths.Normalize(path);
        using var pins = SharedFileAccess.PinParents(path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
        LocalFilePaths.CheckOpenedFile(stream.SafeFileHandle, path);
        var result = await ComposerAttachments.ReadAsync(name, SkillFiles.MimeType(name), stream, ct);
        Check(ct);
        return ImageAttachments.Detect(result.Content.Span) is { } mime ? ComposerAttachment.Local(result.Name, mime, result.Content) : result;
    }
    public async Task<SharedFileReference> SaveAsync(string name, byte[] bytes, CancellationToken ct)
    {
        Check(ct); ComposerAttachments.ValidateSize(bytes.Length);
        if (string.IsNullOrWhiteSpace(name) || name != Path.GetFileName(name) || name.Contains('/') || name.Contains('\\'))
            throw new LocalToolException("name must be a file name, not a path.");
        var folder = Path.Combine(outputDirectory, Guid.NewGuid().ToString("N"));
        var path = LocalFilePaths.Normalize(Path.Combine(folder, name));
        LocalFilePaths.CheckNoLinks(folder); Directory.CreateDirectory(folder);
        using (var pins = SharedFileAccess.PinParents(path))
        {
            using var handle = SharedFileAccess.CreateNew(path);
            await using var stream = new FileStream(handle, FileAccess.Write, 81920, false);
            try { Check(ct); await stream.WriteAsync(bytes, ct); await stream.FlushAsync(ct); Check(ct); }
            catch { SharedFileAccess.DeleteOwned(handle); throw; }
        }
        Check(ct); var saved = await store.AddAsync(partition, path, false, ct);
        // A committed file is not a failed operation if a view refresh fails.
        try { if (changed is not null) await changed(ct); }
        catch (Exception e) when (e is not OutOfMemoryException) { }
        return saved;
    }
}
