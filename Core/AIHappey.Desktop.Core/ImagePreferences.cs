using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AIHappey.Vercel.Models;

namespace AIHappey.Desktop.Core;

/// <summary>Image-only preferences. Unknown provider fields survive editing and cloning.</summary>
public sealed class ImagePreferences
{
    public string? Size { get; set; }
    public string? AspectRatio { get; set; }
    public int N { get; set; } = 1;
    public int? MaxImagesPerCall { get; set; }
    public int? Seed { get; set; }
    public string? StorageRoot { get; set; }
    public string? MaskPath { get; set; }
    public Dictionary<string, JsonObject> ProviderOptions { get; set; } = new()
    {
        ["openai"] = new() { ["quality"] = "auto", ["background"] = "auto", ["moderation"] = "auto" }
    };
    public static readonly string[] SizePresets = ["256x256", "512x512", "768x768", "1024x1024", "1024x1536", "1536x1024"];
    public static readonly string[] AspectPresets = ["1:1", "4:3", "3:2", "16:9", "21:9", "5:2", "9:16", "2:3", "3:4"];
    public string EffectiveRoot => Path.GetFullPath(string.IsNullOrWhiteSpace(StorageRoot)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), DesktopBranding.StorageFolderName, "Images") : StorageRoot);
    public ImagePreferences Clone() => new()
    {
        Size = Size, AspectRatio = AspectRatio, N = N, MaxImagesPerCall = MaxImagesPerCall, Seed = Seed,
        StorageRoot = StorageRoot, MaskPath = MaskPath,
        ProviderOptions = (ProviderOptions ?? []).ToDictionary(p => p.Key, p => (JsonObject)p.Value.DeepClone())
    };
    public static bool ValidDimensions(string? value, char separator) => string.IsNullOrWhiteSpace(value)
        || Regex.IsMatch(value, separator == 'x' ? @"^[1-9]\d{0,5}x[1-9]\d{0,5}$" : @"^[1-9]\d{0,5}:[1-9]\d{0,5}$");
    public void Validate()
    {
        if (N is < 1 or > 20 || MaxImagesPerCall is < 1 || !ValidDimensions(Size, 'x') || !ValidDimensions(AspectRatio, ':'))
            throw new InvalidOperationException(DesktopResources.Get("ImageSettingsInvalid"));
        if (!string.IsNullOrWhiteSpace(StorageRoot) && !Path.IsPathFullyQualified(StorageRoot))
            throw new InvalidOperationException(DesktopResources.Get("ImageFolderInvalid"));
        _ = EffectiveRoot;
    }
    public ImageRequest Request(ChatTarget model, string prompt, IReadOnlyList<ComposerAttachment> attachments, ComposerAttachment? mask, int count)
    {
        Validate();
        if (attachments.Count > 20 || attachments.Sum(f => (long)f.Content.Length) + (mask?.Content.Length ?? 0) > 100L * 1024 * 1024)
            throw new InvalidOperationException(DesktopResources.Get("ImageAttachmentTotalLimit"));
        if (model.ModelType != "image" || string.IsNullOrWhiteSpace(prompt) || count < 1 || count > N)
            throw new InvalidOperationException(DesktopResources.Get("ImagePromptRequired"));
        var provider = ChatPreferences.ResolveProvider(ServiceKind.Ai, model.Id, model.ProviderKey);
        return new()
        {
            Model = model.Id, Prompt = prompt, N = count, Size = string.IsNullOrWhiteSpace(Size) ? null : Size,
            AspectRatio = string.IsNullOrWhiteSpace(AspectRatio) ? null : AspectRatio, Seed = Seed,
            Files = attachments.Select(ImageAttachments.ToFile).ToArray(), Mask = mask is null ? null : ImageAttachments.ToFile(mask),
            ProviderOptions = provider is not null && ProviderOptions.TryGetValue(provider, out var options)
                ? new() { [provider] = JsonSerializer.SerializeToElement(options) } : null
        };
    }
}

public static class ImageAttachments
{
    public static readonly string[] Extensions = [".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp", ".tif", ".tiff"];
    public static bool IsImage(string type) => type is "image/png" or "image/jpeg" or "image/webp" or "image/gif" or "image/bmp" or "image/tiff" or "image/avif";
    public static void Validate(ComposerAttachment file)
    {
        if (!IsImage(file.MediaType) || !file.IsLink && file.Content.IsEmpty)
            throw new InvalidOperationException(DesktopResources.Get("ImageAttachmentRequired"));
        ComposerAttachments.ValidateSize(file.Content.Length);
    }
    public static ImageFile ToFile(ComposerAttachment file)
    {
        Validate(file);
        return file.IsLink ? new ImageFileUrl { Url = file.RemoteUrl! }
            : new ImageFile { MediaType = file.MediaType, Data = Convert.ToBase64String(file.Content.Span) };
    }
    public static string Extension(string type) => type switch
    {
        "image/png" => ".png", "image/jpeg" => ".jpg", "image/webp" => ".webp", "image/gif" => ".gif",
        "image/bmp" => ".bmp", "image/tiff" => ".tiff", "image/avif" => ".avif", _ => throw new InvalidOperationException(DesktopResources.Get("ImageResponseInvalid"))
    };
    public static string? Detect(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return "image/png";
        if (bytes.StartsWith(new byte[] { 255, 216, 255 })) return "image/jpeg";
        if (bytes.StartsWith("GIF8"u8)) return "image/gif";
        if (bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes.Slice(8, 4).SequenceEqual("WEBP"u8)) return "image/webp";
        if (bytes.StartsWith("BM"u8)) return "image/bmp";
        if (bytes.StartsWith(new byte[] { 73, 73, 42, 0 }) || bytes.StartsWith(new byte[] { 77, 77, 0, 42 })) return "image/tiff";
        if (bytes.Length >= 12 && bytes.Slice(4, 4).SequenceEqual("ftyp"u8)
            && (bytes.Slice(8, Math.Min(24, bytes.Length - 8)).IndexOf("avif"u8) >= 0 || bytes.Slice(8, Math.Min(24, bytes.Length - 8)).IndexOf("avis"u8) >= 0)) return "image/avif";
        return null;
    }
    public static (byte[] Bytes, string MediaType) Decode(string data)
    {
        var comma = data.IndexOf(',');
        var dataUrl = comma >= 0;
        if (dataUrl && (!data.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)
            || !data[..comma].EndsWith(";base64", StringComparison.OrdinalIgnoreCase)
            ) || (dataUrl ? data.Length - comma - 1 : data.Length) > (ComposerAttachments.MaximumFileBytes + 2L) / 3 * 4)
            throw new InvalidOperationException(DesktopResources.Get("ImageResponseInvalid"));
        byte[] bytes;
        try { bytes = Convert.FromBase64String(dataUrl ? data[(comma + 1)..] : data); }
        catch (FormatException) { throw new InvalidOperationException(DesktopResources.Get("ImageResponseInvalid")); }
        ComposerAttachments.ValidateSize(bytes.Length);
        var type = dataUrl ? data[5..(comma - 7)].ToLowerInvariant() : Detect(bytes);
        if (type is null || !IsImage(type)) throw new InvalidOperationException(DesktopResources.Get("ImageResponseInvalid"));
        if (bytes.Length == 0) throw new InvalidOperationException(DesktopResources.Get("ImageResponseInvalid"));
        return (bytes, type);
    }
}
