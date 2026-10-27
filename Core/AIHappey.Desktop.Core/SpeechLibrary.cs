using System.Text.Json;
using System.Text.Json.Nodes;

namespace AIHappey.Desktop.Core;

public sealed class StoredSpeech
{
    public int Version { get; set; } = 1;
    public string Id { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public string Model { get; set; } = "";
    public string Text { get; set; } = "";
    public string File { get; set; } = "";
    public string MediaType { get; set; } = "application/octet-stream";
    public string Format { get; set; } = "audio";
    public JsonObject Input { get; set; } = new();
    public JsonObject Response { get; set; } = new();
}
public sealed record LibrarySpeech(StoredSpeech Item, string Path)
{
    public double? Cost => Item.Response["providerMetadata"]?["gateway"]?["cost"] is JsonValue value && value.TryGetValue<double>(out var cost) && double.IsFinite(cost) ? cost : null;
}

public sealed class SpeechLibraryStore(string root, string partition)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerOptions.Web) { WriteIndented = true };
    public string Folder { get; } = Path.Combine(Path.GetFullPath(root), HistoryStore.Partition(partition));
    public static string Partition(DesktopSession session) => ImageLibraryStore.Partition(session);
    private static bool SafeId(string id) => id.Length == 32 && id.All(char.IsAsciiHexDigit);
    private static bool SafeLeaf(string value) => !string.IsNullOrWhiteSpace(value) && value == Path.GetFileName(value) && !value.Contains(':') && !value.Contains('/') && !value.Contains('\\') && value is not "." and not "..";
    private string ItemFolder(string id) => SafeId(id) ? Path.Combine(Folder, id) : throw new IOException("Invalid speech identifier.");

    public async Task<IReadOnlyList<LibrarySpeech>> ListAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(Folder); var result = new List<LibrarySpeech>();
        foreach (var directory in Directory.EnumerateDirectories(Folder))
        {
            ct.ThrowIfCancellationRequested(); var id = Path.GetFileName(directory);
            if (!SafeId(id) || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
            try
            {
                var manifest = Path.Combine(directory, "speech.json");
                if (!File.Exists(manifest) || new FileInfo(manifest).Length > 8 * 1024 * 1024) continue;
                var item = JsonSerializer.Deserialize<StoredSpeech>(await File.ReadAllTextAsync(manifest, ct), Json);
                if (item is null || item.Version != 1 || item.Id != id || !SafeLeaf(item.File) || item.Input is null || item.Response is null || !SpeechAudio.IsAudio(item.MediaType)) continue;
                var path = Path.Combine(directory, item.File);
                if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0) result.Add(new(item, path));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { }
        }
        return result.OrderByDescending(x => x.Item.CreatedAt).ToArray();
    }

    public async Task SaveAsync(SpeechResult result)
    {
        Directory.CreateDirectory(Folder); var id = Guid.NewGuid().ToString("N"); var stage = Path.Combine(Folder, "." + id + ".tmp"); Directory.CreateDirectory(stage);
        try
        {
            var file = "speech" + SpeechAudio.Extension(result.MediaType, result.Format);
            var response = (JsonObject)result.Response.DeepClone();
            if (response["response"] is JsonObject info) info.Remove("headers");
            response.Remove("audio");
            var input = JsonSerializer.SerializeToNode(result.Request, Json)!.AsObject();
            var item = new StoredSpeech { Id = id, CreatedAt = DateTimeOffset.UtcNow, Model = result.Request.Model, Text = result.Request.Text,
                File = file, MediaType = result.MediaType, Format = result.Format, Input = input, Response = response };
            if (response["response"]?["timestamp"] is JsonValue timestamp && timestamp.TryGetValue<string>(out var text) && DateTimeOffset.TryParse(text, out var created)) item.CreatedAt = created;
            await File.WriteAllBytesAsync(Path.Combine(stage, file), result.Audio);
            await File.WriteAllTextAsync(Path.Combine(stage, "speech.json"), JsonSerializer.Serialize(item, Json));
            Directory.Move(stage, ItemFolder(id));
        }
        finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
    }

    public Task DeleteAsync(LibrarySpeech value)
    {
        var directory = ItemFolder(value.Item.Id);
        if (!Directory.Exists(directory) || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0 || !SafeLeaf(value.Item.File)
            || !string.Equals(Path.GetFullPath(value.Path), Path.GetFullPath(Path.Combine(directory, value.Item.File)), StringComparison.OrdinalIgnoreCase)) throw new IOException("Invalid speech path.");
        Directory.Delete(directory, true); return Task.CompletedTask;
    }
}
