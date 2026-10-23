using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AIHappey.Desktop.Core;

public sealed record LocalClipboardImage(int Width, int Height, byte[] Png);
public sealed record LocalClipboardFiles(IReadOnlyList<string> Paths, bool HasMore = false, int UnsupportedCount = 0);

/// <summary>One-shot clipboard access; the shell supplies the UI-thread native boundary.</summary>
public sealed class LocalClipboardTools(Func<CancellationToken, Task<string?>> read,
    Func<string, CancellationToken, Task> write, Func<bool>? isCurrent = null,
    Func<CancellationToken, Task<LocalClipboardImage?>>? readImage = null,
    Func<string, CancellationToken, Task>? writeImage = null,
    Func<CancellationToken, Task<LocalClipboardFiles>>? readFiles = null,
    Func<IReadOnlyList<string>, CancellationToken, Task>? writeFiles = null)
{
    public const string GetTool = "local_clipboard_get";
    public const string SetTool = "local_clipboard_set";
    public const string GetImageTool = "local_clipboard_get_image";
    public const string SetImageTool = "local_clipboard_set_image";
    public const string GetFilesTool = "local_clipboard_get_files";
    public const string SetFilesTool = "local_clipboard_set_files";
    public const int MaximumCharacters = 50_000;
    public const int MaximumFiles = 100;
    public const int MaximumImageBytes = 20 * 1024 * 1024;
    public const int MaximumImagePixels = 16_777_216;

    private void Check(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (isCurrent?.Invoke() == false) throw new OperationCanceledException(ct);
    }

    public Task<JsonElement> CallAsync(string name, JsonElement input, CancellationToken ct) => DesktopLocalTools.SafeAsync(async () =>
    {
        Check(ct);
        switch (name)
        {
            case GetTool:
                var text = await read(ct); Check(ct);
                var truncated = text?.Length > MaximumCharacters;
                return DesktopLocalTools.Result(new { hasText = text is not null, text = text is null ? "" : text[..Math.Min(text.Length, MaximumCharacters)], truncated });
            case SetTool:
                if (!input.TryGetProperty("text", out var value) || value.ValueKind != JsonValueKind.String
                    || value.GetString() is not { } copy || copy.Length > MaximumCharacters || copy.Contains('\0'))
                    throw new LocalToolException(DesktopResources.Get("LocalClipboardTextInvalid"));
                Check(ct); await write(copy, ct); Check(ct);
                // Do not echo sensitive clipboard data into another output field.
                return DesktopLocalTools.Result(new { copied = true, characterCount = copy.Length });
            case GetImageTool:
                if (readImage is null) throw Unavailable();
                var image = await readImage(ct); Check(ct);
                if (image is null) return TextResult(new { hasImage = false });
                ValidateImage(image);
                return DesktopLocalTools.Result(null,
                    DesktopLocalTools.Text(JsonSerializer.Serialize(new { hasImage = true, image.Width, image.Height, mimeType = "image/png" }, JsonSerializerOptions.Web)),
                    new JsonObject { ["type"] = "image", ["mimeType"] = "image/png", ["data"] = Convert.ToBase64String(image.Png) });
            case SetImageTool:
                var path = ValidateFilePath(DesktopLocalTools.Required(input, "path"));
                if (Path.GetExtension(path).ToLowerInvariant() is not (".png" or ".jpg" or ".jpeg" or ".bmp" or ".tif" or ".tiff")
                    || new FileInfo(path).Length is < 1 or > MaximumImageBytes)
                    throw new LocalToolException(DesktopResources.Get("LocalClipboardImageInvalid"));
                if (writeImage is null) throw Unavailable();
                Check(ct); await writeImage(path, ct); Check(ct);
                return TextResult(new { copied = true, format = "image" });
            case GetFilesTool:
                if (readFiles is null) throw Unavailable();
                var files = await readFiles(ct); Check(ct);
                var paths = new List<string>(); var length = 0; var unsupported = files.UnsupportedCount;
                var more = files.HasMore || files.Paths.Count > MaximumFiles;
                foreach (var candidate in files.Paths.Take(MaximumFiles))
                {
                    Check(ct);
                    try
                    {
                        var local = ValidateFilePath(candidate);
                        if (length + local.Length > MaximumCharacters) { more = true; break; }
                        paths.Add(local); length += local.Length;
                    }
                    catch (Exception error) when (error is LocalToolException or IOException or UnauthorizedAccessException) { unsupported++; }
                }
                return TextResult(new { hasFiles = paths.Count > 0, paths, count = paths.Count, hasMore = more, unsupportedCount = unsupported });
            case SetFilesTool:
                if (!input.TryGetProperty("paths", out var array) || array.ValueKind != JsonValueKind.Array || array.GetArrayLength() is < 1 or > MaximumFiles)
                    throw new LocalToolException(DesktopResources.Get("LocalClipboardFilesInvalid"));
                var references = new List<string>(); var characters = 0;
                foreach (var item in array.EnumerateArray())
                {
                    Check(ct);
                    if (item.ValueKind != JsonValueKind.String) throw new LocalToolException(DesktopResources.Get("LocalClipboardFilesInvalid"));
                    var local = ValidateFilePath(item.GetString()!);
                    characters += local.Length;
                    if (characters > MaximumCharacters) throw new LocalToolException(DesktopResources.Get("LocalClipboardFilesInvalid"));
                    references.Add(local);
                }
                if (writeFiles is null) throw Unavailable();
                Check(ct); await writeFiles(references, ct); Check(ct);
                return TextResult(new { copied = true, format = "files", count = references.Count });
            default: throw new LocalToolException("Unsupported clipboard tool.");
        }
    }, ct);

    internal static string ValidateFilePath(string value)
    {
        var path = LocalFilePaths.Normalize(value); LocalFilePaths.CheckNoLinks(path);
        if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new LocalToolException(DesktopResources.Get("LocalClipboardFilesInvalid"));
        return path;
    }

    internal static void ValidateImage(LocalClipboardImage image)
    {
        if (image.Width < 1 || image.Height < 1 || (long)image.Width * image.Height > MaximumImagePixels
            || image.Png.Length is < 24 or > MaximumImageBytes
            || !image.Png.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
            || BinaryPrimitives.ReadUInt32BigEndian(image.Png.AsSpan(16, 4)) != image.Width
            || BinaryPrimitives.ReadUInt32BigEndian(image.Png.AsSpan(20, 4)) != image.Height)
            throw new LocalToolException(DesktopResources.Get("LocalClipboardImageInvalid"));
    }

    private static JsonElement TextResult(object value) => DesktopLocalTools.Result(null, DesktopLocalTools.Text(JsonSerializer.Serialize(value, JsonSerializerOptions.Web)));
    private static LocalToolException Unavailable() => new(DesktopResources.Get("LocalClipboardUnavailable"));
}
