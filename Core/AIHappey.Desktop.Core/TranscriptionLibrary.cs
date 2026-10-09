using System.Text.Json;
using System.Text.Json.Nodes;

namespace AIHappey.Desktop.Core;

public sealed class StoredTranscription
{
    public int Version { get; set; } = 1;
    public string Id { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public string Filename { get; set; } = "";
    public string MediaType { get; set; } = "";
    public string Model { get; set; } = "";
    public string File { get; set; } = "";
    public JsonObject Options { get; set; } = new();
    public JsonObject Response { get; set; } = new();
}
public sealed record LibraryTranscription(StoredTranscription Item, string Path);

public sealed class TranscriptionLibraryStore(string root, string partition)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerOptions.Web) { WriteIndented = true };
    public string Folder { get; } = Path.Combine(Path.GetFullPath(root), HistoryStore.Partition(partition));
    public static string Partition(DesktopSession session) => ImageLibraryStore.Partition(session);
    private static bool SafeId(string id) => id.Length == 32 && id.All(char.IsAsciiHexDigit);
    private static bool SafeLeaf(string value) => !string.IsNullOrWhiteSpace(value) && value == Path.GetFileName(value) && !value.Contains(':') && !value.Contains('/') && !value.Contains('\\') && value is not "." and not "..";
    private string ItemFolder(string id) => SafeId(id) ? Path.Combine(Folder, id) : throw new IOException("Invalid transcription identifier.");
    public async Task<IReadOnlyList<LibraryTranscription>> ListAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(Folder); var result = new List<LibraryTranscription>();
        foreach (var directory in Directory.EnumerateDirectories(Folder))
        {
            ct.ThrowIfCancellationRequested(); var id = Path.GetFileName(directory);
            if (!SafeId(id) || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
            try
            {
                var manifest = Path.Combine(directory, "transcription.json");
                if (!File.Exists(manifest) || new FileInfo(manifest).Length > 32 * 1024 * 1024 || (File.GetAttributes(manifest) & FileAttributes.ReparsePoint) != 0) continue;
                var item = JsonSerializer.Deserialize<StoredTranscription>(await File.ReadAllTextAsync(manifest, ct), Json);
                if (item is null || item.Version != 1 || item.Id != id || item.File is null || !SafeLeaf(item.File) || item.Response is null || item.Options is null) continue;
                var path = Path.Combine(directory, item.File);
                if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0) result.Add(new(item, path));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { }
        }
        return result.OrderByDescending(r => r.Item.CreatedAt).ToArray();
    }
    public async Task SaveAsync(TranscriptionResult result, ComposerAttachment source)
    {
        TranscriptionFiles.Validate(source); Directory.CreateDirectory(Folder);
        var id = Guid.NewGuid().ToString("N"); var stage = Path.Combine(Folder, "." + id + ".tmp"); Directory.CreateDirectory(stage);
        try
        {
            var file = "source" + (TranscriptionFiles.Extensions.Contains(Path.GetExtension(source.Name).ToLowerInvariant()) ? Path.GetExtension(source.Name).ToLowerInvariant() : ".bin");
            var response = (JsonObject)result.Response.DeepClone();
            if (response["response"] is JsonObject info) info.Remove("headers");
            var item = new StoredTranscription { Id = id, CreatedAt = DateTimeOffset.UtcNow, Filename = source.Name, File = file, MediaType = source.MediaType,
                Model = result.Model, Options = (JsonObject)result.Options.DeepClone(), Response = response };
            await File.WriteAllBytesAsync(Path.Combine(stage, file), source.Content.ToArray());
            await File.WriteAllTextAsync(Path.Combine(stage, "transcription.json"), JsonSerializer.Serialize(item, Json));
            Directory.Move(stage, ItemFolder(id));
        }
        finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
    }
    public Task DeleteAsync(LibraryTranscription value)
    {
        var directory = ItemFolder(value.Item.Id);
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0 || !SafeLeaf(value.Item.File)
            || !string.Equals(Path.GetFullPath(value.Path), Path.GetFullPath(Path.Combine(directory, value.Item.File)), StringComparison.OrdinalIgnoreCase)) throw new IOException("Invalid transcription path.");
        Directory.Delete(directory, true); return Task.CompletedTask;
    }
}
