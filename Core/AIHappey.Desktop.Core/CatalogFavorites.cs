using System.Text.Json;

namespace AIHappey.Desktop.Core;

/// <summary>Stores only catalog keys, never credentials or agent definitions. No dependence on loopback ports.</summary>
public sealed class CatalogFavoritesStore(string directory)
{
    private readonly SemaphoreSlim writes = new(1, 1);
    public async Task<HashSet<string>> LoadAsync(string partition, CancellationToken ct = default)
    {
        var path = FilePath(partition);
        if (!File.Exists(path)) return new(StringComparer.Ordinal);
        await using var stream = File.OpenRead(path);
        var value = await JsonSerializer.DeserializeAsync<Document>(stream, cancellationToken: ct);
        if (value is null || value.Version != 1) throw new InvalidOperationException("The saved catalog favorites format is unsupported.");
        return new(value.Keys.Where(key => !string.IsNullOrWhiteSpace(key)), StringComparer.Ordinal);
    }

    public async Task SaveAsync(string partition, IEnumerable<string> keys, CancellationToken ct = default)
    {
        var path = FilePath(partition);
        var value = new Document { Keys = keys.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray() };
        await writes.WaitAsync(ct);
        var temp = path + ".tmp";
        try
        {
            Directory.CreateDirectory(directory);
            await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { await JsonSerializer.SerializeAsync(stream, value, cancellationToken: ct); await stream.FlushAsync(ct); }
            ct.ThrowIfCancellationRequested();
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); writes.Release(); }
    }

    private string FilePath(string partition)
    {
        if (partition.Length != 64 || partition.Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidOperationException("Invalid catalog favorites partition.");
        return Path.Combine(directory, partition + ".json");
    }
    private sealed class Document
    {
        public int Version { get; set; } = 1;
        public string[] Keys { get; set; } = [];
    }
}
