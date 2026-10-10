using System.Text;
using System.Text.Json;

namespace AIHappey.Desktop.Core;

/// <summary>Explicit shared references supplement, but do not restrict, the existing absolute-path reader.
/// Directory cursors are bounded, opaque, single-use and owned by this turn; no unbounded recursive scans.</summary>
public sealed class LocalSharedFileTools(SharedFileStore store, string partition, DocumentTextExtraction extraction,
    Func<bool>? isCurrent = null, Func<CancellationToken, Task>? created = null) : IDisposable
{
    public const string ListTool = "local_files_list";
    public const string DirectoryTool = "local_files_list_directory";
    public const string ReadTool = "local_files_read_shared";
    public const string CreateTool = "local_files_create";
    public const int MaximumCreateBytes = 1024 * 1024;
    private const int MaximumPageCharacters = 50_000;
    private readonly SemaphoreSlim calls = new(1, 1);
    private readonly Dictionary<string, DirectoryCursor> cursors = new(StringComparer.Ordinal);
    private bool disposed;
    private sealed class DirectoryCursor(SharedFileReference root, string path, SharedFileAccess.DirectoryPins pins, IEnumerator<string> entries) : IDisposable
    {
        public SharedFileReference Root { get; } = root;
        public string Path { get; } = path;
        public IEnumerator<string> Entries { get; } = entries;
        public string? Pending { get; set; }
        public void Dispose() { entries.Dispose(); pins.Dispose(); }
    }
    private void Check(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (disposed || isCurrent?.Invoke() == false) throw new OperationCanceledException(ct);
    }
    private static string? Optional(JsonElement input, string field)
    {
        if (!input.TryGetProperty(field, out var value)) return null;
        if (value.ValueKind != JsonValueKind.String) throw new LocalToolException(field + " must be a string.");
        return value.GetString();
    }
    private static string? Cursor(JsonElement input)
    {
        var cursor = Optional(input, "cursor");
        // Models often supply an empty optional string on the initial call. It is not
        // a continuation token; keep non-empty tokens intact for strict validation.
        return string.IsNullOrWhiteSpace(cursor) ? null : cursor;
    }
    private async Task<SharedFileReference> RootAsync(JsonElement input, CancellationToken ct)
    {
        var id = DesktopLocalTools.Required(input, "id");
        var items = await store.ListAsync(partition, ct); Check(ct);
        return items.FirstOrDefault(i => i.Id == id) ?? throw new LocalToolException(DesktopResources.Get("FilesNotShared"));
    }
    public Task<JsonElement> CallAsync(string name, JsonElement input, CancellationToken ct) => DesktopLocalTools.SafeAsync(async () =>
    {
        await calls.WaitAsync(ct);
        try
        {
            Check(ct);
            try
            {
                if (name == LocalFileTools.ToolName) return await new LocalFileTools(extraction, isCurrent).CallAsync(name, input, ct);
                if (name == ListTool) return await ListAsync(input, ct);
                var root = await RootAsync(input, ct);
                var path = SharedFileAccess.Resolve(root, Optional(input, "relativePath"), name is DirectoryTool or CreateTool);
                return name switch
                {
                    DirectoryTool => Browse(root, path, input, ct),
                    ReadTool => await ReadAsync(path, input, ct),
                    CreateTool => await CreateAsync(root, path, input, ct),
                    _ => throw new LocalToolException("Unsupported shared file tool.")
                };
            }
            catch (FileNotFoundException) { throw new LocalToolException(DesktopResources.Get("LocalFileNotFound")); }
            catch (DirectoryNotFoundException) { throw new LocalToolException(DesktopResources.Get("LocalFileNotFound")); }
            catch (UnauthorizedAccessException) { throw new LocalToolException(DesktopResources.Get("LocalFileAccessDenied")); }
        }
        finally { calls.Release(); }
    }, ct);
    private async Task<JsonElement> ListAsync(JsonElement input, CancellationToken ct)
    {
        var items = (await store.ListAsync(partition, ct)).OrderBy(i => i.Id, StringComparer.Ordinal).ToArray();
        var cursor = Cursor(input); var start = 0;
        if (cursor is not null)
        {
            start = Array.FindIndex(items, i => i.Id == cursor) + 1;
            if (start == 0) throw new LocalToolException(DesktopResources.Get("FilesCursorExpired"));
        }
        var limit = DesktopLocalTools.Integer(input, "limit", 50, 100);
        var data = new List<object>(); var characters = 0; var index = start;
        while (index < items.Length && data.Count < limit)
        {
            Check(ct); var item = items[index];
            var entry = Projection(SharedFileStore.Inspect(item));
            var length = JsonSerializer.Serialize(entry).Length;
            if (data.Count > 0 && characters + length > MaximumPageCharacters) break;
            data.Add(entry); characters += length; index++;
        }
        Check(ct); var more = index < items.Length;
        return DesktopLocalTools.Result(new { data, total = items.Length, hasMore = more, nextCursor = more ? items[index - 1].Id : null });
    }
    private static object Projection(SharedFileView view) => new
    {
        id = view.Reference.Id, name = view.Reference.Name, path = view.Reference.Path,
        kind = view.Reference.IsFolder ? "folder" : "file", addedAt = view.Reference.AddedAt,
        status = view.Status, view.Size, view.ModifiedAt
    };
    private JsonElement Browse(SharedFileReference root, string path, JsonElement input, CancellationToken ct)
    {
        DirectoryCursor state;
        var cursor = Cursor(input);
        if (cursor is not null)
        {
            if (!cursors.Remove(cursor, out state!)) throw new LocalToolException(DesktopResources.Get("FilesCursorExpired"));
            if (state.Root != root || !string.Equals(state.Path, path, StringComparison.OrdinalIgnoreCase))
            { state.Dispose(); throw new LocalToolException(DesktopResources.Get("FilesCursorExpired")); }
        }
        else
        {
            if (cursors.Count >= 32) throw new LocalToolException(DesktopResources.Get("FilesCursorLimit"));
            var pins = SharedFileAccess.PinParents(path, true);
            try { state = new(root, path, pins, Directory.EnumerateFileSystemEntries(path, "*", new EnumerationOptions { IgnoreInaccessible = false, AttributesToSkip = 0 }).GetEnumerator()); }
            catch { pins.Dispose(); throw; }
        }
        try
        {
            var data = new List<object>(); var limit = DesktopLocalTools.Integer(input, "limit", 50, 100); var characters = 0;
            var hasMore = false;
            while (true)
            {
                Check(ct);
                var entryPath = state.Pending;
                if (entryPath is null)
                {
                    if (!state.Entries.MoveNext()) break;
                    entryPath = state.Entries.Current;
                }
                state.Pending = null;
                var entry = DirectoryEntry(root, entryPath);
                var length = JsonSerializer.Serialize(entry).Length;
                if (data.Count >= limit || data.Count > 0 && characters + length > MaximumPageCharacters)
                { state.Pending = entryPath; hasMore = true; break; }
                data.Add(entry); characters += length;
            }
            Check(ct);
            var next = hasMore ? Guid.NewGuid().ToString("N") : null;
            if (next is not null) cursors.Add(next, state);
            else state.Dispose();
            return DesktopLocalTools.Result(new { id = root.Id, relativePath = Path.GetRelativePath(root.Path, path), data, hasMore, nextCursor = next,
                cursorScope = "Current turn only; continuation cursors are single-use. Directory contents may change while browsing." });
        }
        catch { state.Dispose(); throw; }
    }
    private static object DirectoryEntry(SharedFileReference root, string path)
    {
        var relative = Path.GetRelativePath(root.Path, path); var name = Path.GetFileName(path);
        try
        {
            // Validate names from the OS too: a persisted ADS/device path must never become a tool argument.
            SharedFileAccess.Resolve(root, relative); var attributes = File.GetAttributes(path);
            if (attributes.HasFlag(FileAttributes.ReparsePoint)) return new { name, relativePath = relative, kind = "link", status = "unsupported" };
            var folder = attributes.HasFlag(FileAttributes.Directory);
            return new { name, relativePath = relative, kind = folder ? "folder" : "file", status = "available",
                size = folder ? (long?)null : new FileInfo(path).Length, modifiedAt = File.GetLastWriteTimeUtc(path) };
        }
        catch (UnauthorizedAccessException) { return new { name, relativePath = relative, kind = "unknown", status = "denied" }; }
        catch (Exception error) when (error is IOException or LocalToolException) { return new { name, relativePath = relative, kind = "unknown", status = "unavailable" }; }
    }
    private async Task<JsonElement> ReadAsync(string path, JsonElement input, CancellationToken ct)
    {
        using var pins = SharedFileAccess.PinParents(path);
        Check(ct);
        // Pin the ancestors for the complete read/extract operation; the existing reader validates its own file handle.
        return await new LocalFileTools(extraction, () => !disposed && isCurrent?.Invoke() != false).CallAsync(LocalFileTools.ToolName,
            JsonSerializer.SerializeToElement(new { path, startLine = DesktopLocalTools.Integer(input, "startLine", 1),
                maxLines = DesktopLocalTools.Integer(input, "maxLines", 200, LocalFileTools.MaximumLines) }), ct);
    }
    private async Task<JsonElement> CreateAsync(SharedFileReference root, string path, JsonElement input, CancellationToken ct)
    {
        if (string.Equals(path, root.Path, StringComparison.OrdinalIgnoreCase)) throw new LocalToolException(DesktopResources.Get("FilesRelativePathInvalid"));
        if (!input.TryGetProperty("data", out var value) || value.ValueKind != JsonValueKind.String) throw new LocalToolException("data must be a string.");
        var data = value.GetString()!; byte[] bytes;
        var encoding = Optional(input, "encoding") ?? "utf8";
        if (encoding == "utf8")
        {
            var utf8 = new UTF8Encoding(false, true);
            if (data.Length > MaximumCreateBytes || utf8.GetByteCount(data) > MaximumCreateBytes) throw new LocalToolException(DesktopResources.Get("FilesCreateLimit"));
            bytes = utf8.GetBytes(data);
        }
        else if (encoding == "base64")
        {
            if (data.Length > (MaximumCreateBytes + 2L) / 3 * 4) throw new LocalToolException(DesktopResources.Get("FilesCreateLimit"));
            try { bytes = Convert.FromBase64String(data); }
            catch (FormatException) { throw new LocalToolException(DesktopResources.Get("FilesEncodingInvalid")); }
            if (bytes.Length > MaximumCreateBytes) throw new LocalToolException(DesktopResources.Get("FilesCreateLimit"));
        }
        else throw new LocalToolException(DesktopResources.Get("FilesEncodingInvalid"));
        using (var pins = SharedFileAccess.PinParents(path))
        {
            Check(ct);
            using var handle = SharedFileAccess.CreateNew(path);
            await using var stream = new FileStream(handle, FileAccess.Write, 81920, false);
            try { await stream.WriteAsync(bytes, ct); await stream.FlushAsync(ct); Check(ct); }
            catch
            {
                // Never delete by path: only the fresh output owned by this handle may be removed.
                if (!SharedFileAccess.DeleteOwned(handle)) throw new LocalToolException(DesktopResources.Get("FilesIncompleteOutput"));
                throw;
            }
        }
        // Commit point: the real file now exists. Reference/refresh failure must not report the write as failed.
        SharedFileReference? reference = null; string? warning = null;
        try
        {
            Check(ct); reference = await store.AddAsync(partition, path, false, ct);
            if (created is not null) await created(ct);
        }
        catch (Exception error) when (error is not OutOfMemoryException) { warning = DesktopResources.Get("FilesCreatedReferenceWarning"); }
        return DesktopLocalTools.Result(new { created = true, id = reference?.Id, folderId = root.Id, relativePath = Path.GetRelativePath(root.Path, path), path,
            size = bytes.Length, encoding, warning });
    }
    public void Dispose()
    {
        disposed = true;
        foreach (var cursor in cursors.Values) cursor.Dispose(); cursors.Clear();
    }
}
