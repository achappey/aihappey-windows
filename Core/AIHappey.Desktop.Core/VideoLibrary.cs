using System.Text.Json;
using System.Text.Json.Nodes;

namespace AIHappey.Desktop.Core;

/// <summary>Shared disk boundary. Reject links anywhere in an app-owned subtree.</summary>
public static class VideoDisk
{
    public const int MaximumJsonBytes = 160 * 1024 * 1024;
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public static bool SafeId(string? id) => id is { Length: 32 } && id.All(char.IsAsciiHexDigit);
    public static string Leaf(string parent, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name != Path.GetFileName(name) || name.Contains(':') || name.Contains('/') || name.Contains('\\') || name is "." or "..")
            throw new IOException("Invalid video file name.");
        var path = Path.Combine(parent, name); CheckPath(path); return path;
    }
    public static void CheckPath(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linked video paths are not supported.");
    }
    public static void Prepare(string folder) { CheckPath(folder); Directory.CreateDirectory(folder); CheckPath(folder); }
    public static async Task<JsonObject> ReadObjectAsync(Stream stream, int maximum, CancellationToken ct = default)
    {
        await using (stream)
        {
            using var buffer = new MemoryStream(); var block = new byte[81920];
            while (await stream.ReadAsync(block, ct) is var count && count > 0)
            {
                if (buffer.Length + count > maximum) throw new InvalidOperationException(DesktopResources.Get("VideoResponseInvalid"));
                await buffer.WriteAsync(block.AsMemory(0, count), ct);
            }
            buffer.Position = 0;
            return (await JsonNode.ParseAsync(buffer, cancellationToken: ct)) as JsonObject ?? throw new JsonException("Invalid video document.");
        }
    }
    public static async Task WriteAsync(string path, object value)
    {
        CheckPath(path); Prepare(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, value, Json); await stream.FlushAsync(); stream.Flush(true);
            }
            CheckPath(path); File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public static JsonObject Metadata(JsonObject data)
    {
        var copy = (JsonObject)data.DeepClone();
        void Clean(JsonNode? node)
        {
            if (node is JsonObject obj)
                foreach (var key in obj.Select(p => p.Key).ToArray())
                {
                    if (key.Equals("headers", StringComparison.OrdinalIgnoreCase) || key.Equals("authorization", StringComparison.OrdinalIgnoreCase)
                        || key.Contains("apikey", StringComparison.OrdinalIgnoreCase) || key.Contains("api_key", StringComparison.OrdinalIgnoreCase)
                        || key.Equals("cookie", StringComparison.OrdinalIgnoreCase)) obj.Remove(key);
                    else Clean(obj[key]);
                }
            else if (node is JsonArray array) foreach (var item in array) Clean(item);
        }
        Clean(copy); return copy;
    }
    public static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    public static IReadOnlyList<string> Warnings(JsonObject response) => response["warnings"] is JsonArray array
        ? array.Select(w => Text(w) ?? w?.ToJsonString() ?? "").Where(w => w.Length > 0).Take(100).ToArray() : [];
}

public sealed class StoredVideoGeneration
{
    public int Version { get; set; } = 1;
    public string Id { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public string Model { get; set; } = "";
    public string Prompt { get; set; } = "";
    public JsonObject Options { get; set; } = new();
    public JsonObject Response { get; set; } = new();
    public List<StoredVideoOutput> Outputs { get; set; } = [];
}
public sealed record StoredVideoOutput(string File, string MediaType, double? Cost);
public sealed record LibraryVideo(StoredVideoGeneration Generation, StoredVideoOutput Output, string Path)
{
    public string Key => Generation.Id + ":" + Output.File;
    public string Model => Generation.Model;
    public double? Cost => Output.Cost;
}

public sealed class VideoLibraryStore(string root, string partition)
{
    public string Folder { get; } = Path.Combine(Path.GetFullPath(root), HistoryStore.Partition(partition));
    private string GenerationFolder(string id) => VideoDisk.SafeId(id) ? VideoDisk.Leaf(Folder, id) : throw new IOException("Invalid video identifier.");
    public async Task<IReadOnlyList<LibraryVideo>> ListAsync(CancellationToken ct = default)
    {
        VideoDisk.Prepare(Folder); var items = new List<LibraryVideo>();
        foreach (var directory in Directory.EnumerateDirectories(Folder))
        {
            ct.ThrowIfCancellationRequested(); var id = Path.GetFileName(directory); if (!VideoDisk.SafeId(id)) continue;
            try
            {
                var path = VideoDisk.Leaf(directory, "generation.json");
                if (!File.Exists(path) || new FileInfo(path).Length > 4 * 1024 * 1024) continue;
                var data = (await VideoDisk.ReadObjectAsync(File.OpenRead(path), 4 * 1024 * 1024, ct)).Deserialize<StoredVideoGeneration>(VideoDisk.Json);
                if (data?.Version != 1 || data.Id != id || data.Outputs is null) continue;
                foreach (var output in data.Outputs)
                {
                    if (output is null || !VideoAttachments.IsVideo(output.MediaType)) continue;
                    var file = VideoDisk.Leaf(directory, output.File); if (File.Exists(file)) items.Add(new(data, output, file));
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException) { }
        }
        return items.OrderByDescending(i => i.Generation.CreatedAt).ToArray();
    }
    public async Task SaveAsync(VideoJob job, JsonObject response, HttpClient anonymousHttp, CancellationToken ct = default)
    {
        VideoDisk.Prepare(Folder); var destination = GenerationFolder(job.Id);
        // A manifest is the commit marker. A poll repeated after a crash must not resurrect deleted outputs.
        if (Directory.Exists(destination))
        {
            var existing = await VideoDisk.ReadObjectAsync(File.OpenRead(VideoDisk.Leaf(destination, "generation.json")), 4 * 1024 * 1024, ct);
            if (VideoDisk.Text(existing["id"]) != job.Id || existing["version"]?.GetValue<int>() != 1) throw new IOException("Invalid video commit.");
            return;
        }
        if (response["videos"] is not JsonArray videos || videos.Count is < 1 or > 20) throw new InvalidOperationException(DesktopResources.Get("VideoResponseInvalid"));
        var stage = VideoDisk.Leaf(Folder, "." + job.Id + "." + Guid.NewGuid().ToString("N") + ".tmp"); VideoDisk.Prepare(stage);
        try
        {
            var metadata = VideoDisk.Metadata(response); metadata.Remove("videos");
            var options = VideoDisk.Metadata(job.Request); options.Remove("prompt"); options.Remove("model"); options.Remove("image"); options.Remove("inputReferences"); options.Remove("frameImages");
            var generation = new StoredVideoGeneration { Id = job.Id, CreatedAt = job.CreatedAt, Prompt = job.Prompt,
                Model = VideoDisk.Text(response["response"]?["modelId"]) ?? job.Model, Options = options, Response = metadata };
            double? total = response["providerMetadata"]?["gateway"]?["cost"] is JsonValue cost && cost.TryGetValue<double>(out var number) && double.IsFinite(number) ? number : null;
            var index = 0;
            foreach (var node in videos)
            {
                if (node is not JsonObject video) throw new JsonException("Invalid video output.");
                var type = VideoDisk.Text(video["mediaType"]) ?? VideoDisk.Text(video["mimeType"]) ?? "video/mp4";
                if (!VideoAttachments.IsVideo(type)) throw new InvalidOperationException(DesktopResources.Get("VideoResponseInvalid"));
                var file = "video-" + (++index) + VideoAttachments.Extension(type);
                await using var output = new FileStream(VideoDisk.Leaf(stage, file), FileMode.CreateNew, FileAccess.Write, FileShare.None);
                if (VideoDisk.Text(video["type"]) == "url")
                {
                    var uri = AttachmentDownloads.RemoteUri(VideoDisk.Text(video["url"]));
                    if (uri is null) throw new InvalidOperationException(DesktopResources.Get("VideoResponseInvalid"));
                    using var download = await anonymousHttp.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
                    // Anonymous client has redirects disabled; never forward gateway credentials to an output URL.
                    download.EnsureSuccessStatusCode(); await using var source = await download.Content.ReadAsStreamAsync(ct);
                    var buffer = new byte[81920]; long written = 0;
                    while (await source.ReadAsync(buffer, ct) is var read && read > 0)
                    { written += read; if (written > 100L * 1024 * 1024) throw new IOException(DesktopResources.Get("VideoResponseInvalid")); await output.WriteAsync(buffer.AsMemory(0, read), ct); }
                }
                else
                {
                    var data = VideoDisk.Text(video["data"]);
                    byte[] bytes;
                    if (data is not null)
                    {
                        if (data.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                        {
                            var comma = data.IndexOf(',');
                            if (comma < 0 || !data[..comma].Equals("data:" + type + ";base64", StringComparison.OrdinalIgnoreCase)) throw new JsonException("Invalid video data URL.");
                            data = data[(comma + 1)..];
                        }
                        if (data.Length > 140 * 1024 * 1024) throw new IOException(DesktopResources.Get("VideoResponseInvalid"));
                        bytes = Convert.FromBase64String(data);
                    }
                    else if (video["data"] is JsonArray binary && binary.Count <= 100 * 1024 * 1024)
                        bytes = binary.Select(b => b!.GetValue<byte>()).ToArray();
                    else throw new JsonException("Invalid binary video.");
                    if (bytes.Length > 100 * 1024 * 1024) throw new IOException(DesktopResources.Get("VideoResponseInvalid"));
                    await output.WriteAsync(bytes, ct);
                }
                if (output.Length == 0) throw new JsonException("Empty video output.");
                await output.FlushAsync(ct); output.Flush(true);
                generation.Outputs.Add(new(file, type, total / videos.Count));
            }
            await VideoDisk.WriteAsync(VideoDisk.Leaf(stage, "generation.json"), generation);
            VideoDisk.CheckPath(destination); Directory.Move(stage, destination);
        }
        finally { if (Directory.Exists(stage)) { VideoDisk.CheckPath(stage); Directory.Delete(stage, true); } }
    }
    public async Task DeleteAsync(LibraryVideo item)
    {
        var directory = GenerationFolder(item.Generation.Id); var path = VideoDisk.Leaf(directory, item.Output.File);
        if (!string.Equals(Path.GetFullPath(item.Path), path, StringComparison.OrdinalIgnoreCase)) throw new IOException("Invalid video path.");
        var manifest = VideoDisk.Leaf(directory, "generation.json");
        var data = (await VideoDisk.ReadObjectAsync(File.OpenRead(manifest), 4 * 1024 * 1024)).Deserialize<StoredVideoGeneration>(VideoDisk.Json) ?? throw new JsonException();
        if (data.Version != 1 || data.Id != item.Generation.Id || !data.Outputs.Any(o => o.File == item.Output.File)) throw new IOException("Invalid video manifest.");
        data.Outputs.RemoveAll(o => o.File == item.Output.File);
        // Keep the empty manifest as a tombstone until the completed job journal is acknowledged.
        await VideoDisk.WriteAsync(manifest, data); File.Delete(path);
    }
}

/// <summary>Immutable settings input files. Dialog cancellation never overwrites a saved frame.</summary>
public sealed class VideoInputStore(string folder)
{
    public string Folder { get; } = Path.GetFullPath(folder);
    public string PathFor(VideoInputFile file) => VideoDisk.Leaf(Folder, file.File);
    public async Task<VideoInputFile> AddAsync(ComposerAttachment input, CancellationToken ct = default)
    {
        ImageAttachments.Validate(input); if (input.IsLink) throw new InvalidOperationException(DesktopResources.Get("VideoAttachmentRequired"));
        VideoDisk.Prepare(Folder); var file = Guid.NewGuid().ToString("N") + ImageAttachments.Extension(input.MediaType);
        await File.WriteAllBytesAsync(VideoDisk.Leaf(Folder, file), input.Content.ToArray(), ct); return new(file, input.Name, input.MediaType);
    }
    public async Task<ComposerAttachment> ReadAsync(VideoInputFile file, CancellationToken ct = default)
    {
        await using var stream = File.OpenRead(PathFor(file)); var input = await ComposerAttachments.ReadAsync(file.Name, file.MediaType, stream, ct);
        ImageAttachments.Validate(input); return input;
    }
}
