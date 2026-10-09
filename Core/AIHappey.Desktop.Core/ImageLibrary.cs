using System.Text.Json;
using System.Text.Json.Nodes;

namespace AIHappey.Desktop.Core;

public sealed class StoredImageOutput
{
    public string File { get; set; } = "";
    public string MediaType { get; set; } = "image/png";
    public double? Cost { get; set; }
}
public sealed class StoredImageGeneration
{
    public int Version { get; set; } = 1;
    public string Id { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public string Model { get; set; } = "";
    public string Prompt { get; set; } = "";
    public JsonObject Options { get; set; } = new();
    public JsonObject Response { get; set; } = new();
    public List<StoredImageOutput> Outputs { get; set; } = [];
}
public sealed record LibraryImage(StoredImageGeneration Generation, StoredImageOutput Output, string Path)
{
    public string Model => Generation.Model;
    public double? Cost => Output.Cost;
}

/// <summary>Ordinary files plus a versioned manifest. All paths in manifests are app-generated leaves.</summary>
public sealed class ImageLibraryStore(string root, string partition)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerOptions.Web) { WriteIndented = true };
    public string Folder { get; } = Path.Combine(Path.GetFullPath(root), HistoryStore.Partition(partition));
    public static string Partition(DesktopSession session) => HistoryStore.Partition(session.Host.ProfileId, session.Host.HistoryIdentity,
        session.Settings.Ai.Location.ToString(), session.Settings.Ai.Location == RuntimeLocation.Remote
            ? DesktopSettings.RemoteUri(session.Settings.Ai.RemoteUrl).AbsoluteUri : "managed-ai");
    private static bool SafeId(string id) => id.Length == 32 && id.All(char.IsAsciiHexDigit);
    private static bool SafeFile(string file) => file.Length > 0 && file == Path.GetFileName(file)
        && !file.Contains(':') && !file.Contains('/') && !file.Contains('\\') && file is not "." and not "..";
    private string GenerationFolder(string id) => SafeId(id) ? Path.Combine(Folder, id) : throw new IOException("Invalid image identifier.");
    public async Task<IReadOnlyList<LibraryImage>> ListAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(Folder);
        var items = new List<LibraryImage>();
        foreach (var directory in Directory.EnumerateDirectories(Folder))
        {
            ct.ThrowIfCancellationRequested();
            var id = Path.GetFileName(directory);
            if (!SafeId(id) || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
            try
            {
                var manifest = Path.Combine(directory, "generation.json");
                if (!File.Exists(manifest) || new FileInfo(manifest).Length > 4 * 1024 * 1024) continue;
                await using var input = File.OpenRead(manifest);
                var generation = await JsonSerializer.DeserializeAsync<StoredImageGeneration>(input, Json, ct);
                if (generation is null || generation.Version != 1 || generation.Id != id || generation.Outputs is null) continue;
                foreach (var output in generation.Outputs)
                {
                    if (output is null || output.File is null || !SafeFile(output.File) || !ImageAttachments.IsImage(output.MediaType)) continue;
                    var path = Path.Combine(directory, output.File);
                    if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0)
                        items.Add(new(generation, output, path));
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { /* An externally edited file cannot break the library. */ }
        }
        return items.OrderByDescending(i => i.Generation.CreatedAt).ToArray();
    }
    public async Task SaveAsync(ImageGenerationBatch batch, IReadOnlyList<ComposerAttachment> inputs, ComposerAttachment? mask)
    {
        Directory.CreateDirectory(Folder);
        var id = Guid.NewGuid().ToString("N");
        var stage = Path.Combine(Folder, "." + id + ".tmp");
        Directory.CreateDirectory(stage);
        try
        {
            var options = JsonSerializer.SerializeToNode(batch.Request, Json)!.AsObject();
            options.Remove("files"); options.Remove("mask"); options.Remove("prompt"); options.Remove("model");
            var response = JsonNode.Parse(batch.Response.GetRawText())!.AsObject(); response.Remove("images");
            // Headers may contain cookies or signed URLs. They are not library metadata.
            if (response["response"] is JsonObject details) details.Remove("headers");
            var generation = new StoredImageGeneration { Id = id, CreatedAt = DateTimeOffset.UtcNow, Model = batch.Request.Model,
                Prompt = batch.Request.Prompt, Options = options, Response = response };
            if (response["response"] is JsonObject info && OpenAIChatConfig.Text(info["timestamp"]) is { } timestamp
                && DateTimeOffset.TryParse(timestamp, out var created)) generation.CreatedAt = created;
            var sources = new JsonArray(); var index = 0;
            foreach (var input in inputs)
            {
                ImageAttachments.Validate(input);
                if (input.IsLink) sources.Add(new JsonObject { ["type"] = "url", ["url"] = input.RemoteUrl });
                else
                {
                    var name = "input-" + (++index) + ImageAttachments.Extension(input.MediaType);
                    await File.WriteAllBytesAsync(Path.Combine(stage, name), input.Content.ToArray());
                    sources.Add(new JsonObject { ["type"] = "file", ["file"] = name, ["mediaType"] = input.MediaType });
                }
            }
            options["files"] = sources;
            if (mask is not null)
            {
                ImageAttachments.Validate(mask);
                var name = "mask" + ImageAttachments.Extension(mask.MediaType);
                await File.WriteAllBytesAsync(Path.Combine(stage, name), mask.Content.ToArray());
                options["mask"] = new JsonObject { ["file"] = name, ["mediaType"] = mask.MediaType };
            }
            var images = batch.Images;
            for (var i = 0; i < images.Count; i++)
            {
                var (bytes, type) = ImageAttachments.Decode(images[i]);
                var name = $"image-{i + 1}{ImageAttachments.Extension(type)}";
                await File.WriteAllBytesAsync(Path.Combine(stage, name), bytes);
                generation.Outputs.Add(new() { File = name, MediaType = type, Cost = batch.Cost(i) });
            }
            await File.WriteAllTextAsync(Path.Combine(stage, "generation.json"), JsonSerializer.Serialize(generation, Json));
            Directory.Move(stage, GenerationFolder(id));
        }
        finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
    }
    public async Task DeleteAsync(LibraryImage image)
    {
        var directory = GenerationFolder(image.Generation.Id);
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new IOException("Invalid image directory.");
        if (!SafeFile(image.Output.File) || Path.GetFullPath(image.Path) != Path.GetFullPath(Path.Combine(directory, image.Output.File)))
            throw new IOException("Invalid image path.");
        // Re-read, rather than overwriting with an old UI snapshot after a previous deletion.
        var manifest = Path.Combine(directory, "generation.json");
        var generation = JsonSerializer.Deserialize<StoredImageGeneration>(await File.ReadAllTextAsync(manifest), Json)
            ?? throw new IOException("Invalid image manifest.");
        if (generation.Version != 1 || generation.Id != image.Generation.Id || generation.Outputs is null
            || !generation.Outputs.Any(o => o.File == image.Output.File)) throw new IOException("Invalid image manifest.");
        generation.Outputs.RemoveAll(o => o.File == image.Output.File);
        if (generation.Outputs.Count == 0)
        {
            // Only remove the known app-created generation directory, never the selected root.
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new IOException("Invalid image directory.");
            Directory.Delete(directory, true); return;
        }
        var temp = manifest + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(generation, Json)); File.Move(temp, manifest, true);
        File.Delete(image.Path);
    }
}
