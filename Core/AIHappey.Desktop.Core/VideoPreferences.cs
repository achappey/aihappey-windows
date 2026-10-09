using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Vercel.Models;

namespace AIHappey.Desktop.Core;

public sealed class VideoPreferences
{
    public string? Resolution { get; set; }
    public string? AspectRatio { get; set; }
    public int N { get; set; } = 1;
    public int? MaxVideosPerCall { get; set; }
    public int? Duration { get; set; }
    public int? Fps { get; set; }
    public int? Seed { get; set; }
    public bool GenerateAudio { get; set; }
    public string? StorageRoot { get; set; }
    public int PollingIntervalSeconds { get; set; } = 10;
    public List<VideoInputFile> InputReferences { get; set; } = [];
    public VideoInputFile? FirstFrame { get; set; }
    public VideoInputFile? LastFrame { get; set; }
    public Dictionary<string, JsonObject> ProviderOptions { get; set; } = [];
    public static readonly string[] ResolutionPresets = ["640x480", "854x480", "960x540", "1280x720", "1920x1080", "3840x2160", "720x1280", "1024x1792", "1792x1024", "1600x900"];
    public static readonly string[] AspectPresets = ["16:9", "9:16", "1:1", "4:3", "3:4", "21:9"];
    [System.Text.Json.Serialization.JsonIgnore]
    public string EffectiveRoot => Path.GetFullPath(string.IsNullOrWhiteSpace(StorageRoot)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), DesktopBranding.StorageFolderName, "Videos") : StorageRoot);
    public VideoPreferences Clone() => new()
    {
        Resolution = Resolution, AspectRatio = AspectRatio, N = N, MaxVideosPerCall = MaxVideosPerCall,
        Duration = Duration, Fps = Fps, Seed = Seed, GenerateAudio = GenerateAudio, StorageRoot = StorageRoot,
        PollingIntervalSeconds = PollingIntervalSeconds, InputReferences = (InputReferences ?? []).Select(f => f with { }).ToList(),
        FirstFrame = FirstFrame is null ? null : FirstFrame with { }, LastFrame = LastFrame is null ? null : LastFrame with { },
        ProviderOptions = (ProviderOptions ?? []).ToDictionary(p => p.Key, p => (JsonObject)p.Value.DeepClone())
    };
    public void ValidateStorage()
    {
        if (PollingIntervalSeconds is < 5 or > 60 || !string.IsNullOrWhiteSpace(StorageRoot) && !Path.IsPathFullyQualified(StorageRoot))
            throw new InvalidOperationException(DesktopResources.Get("VideoSettingsInvalid"));
        _ = EffectiveRoot;
    }
    public void Validate()
    {
        ValidateStorage();
        if (N is < 1 or > 10 || MaxVideosPerCall is < 1 || Duration is < 1 || Fps is < 1
            || !ImagePreferences.ValidDimensions(Resolution, 'x') || !ImagePreferences.ValidDimensions(AspectRatio, ':')
            || (InputReferences?.Count ?? 0) > 20)
            throw new InvalidOperationException(DesktopResources.Get("VideoSettingsInvalid"));
    }
    public VideoRequest Request(ChatTarget model, string prompt, ComposerAttachment? input,
        IReadOnlyList<ComposerAttachment> references, ComposerAttachment? first, ComposerAttachment? last, int count)
    {
        Validate();
        if (model.ModelType != "video" || string.IsNullOrWhiteSpace(prompt) || count < 1 || count > N)
            throw new InvalidOperationException(DesktopResources.Get("VideoPromptRequired"));
        var all = references.Concat(new[] { input, first, last }.OfType<ComposerAttachment>()).ToArray();
        if (references.Count > 40 || all.Sum(f => (long)f.Content.Length) > 100L * 1024 * 1024)
            throw new InvalidOperationException(DesktopResources.Get("ImageAttachmentTotalLimit"));
        foreach (var reference in references.Where(f => !f.IsLink)) ImageAttachments.Validate(reference);
        if (first is not null) ImageAttachments.Validate(first);
        if (last is not null) ImageAttachments.Validate(last);
        return new()
        {
            Model = model.Id, Prompt = prompt, N = count, Resolution = string.IsNullOrWhiteSpace(Resolution) ? null : Resolution,
            AspectRatio = string.IsNullOrWhiteSpace(AspectRatio) ? null : AspectRatio, Duration = Duration, Fps = Fps,
            Seed = Seed, GenerateAudio = GenerateAudio, Image = input is null ? null : VideoAttachments.ToFile(input),
            InputReferences = references.Count == 0 ? null : references.Select(VideoAttachments.ToFile).ToArray(),
            FrameImages = new[] { ("first_frame", first), ("last_frame", last) }.Where(f => f.Item2 is not null)
                .Select(f => new VideoFrameImage { FrameType = f.Item1, Image = VideoAttachments.ToFile(f.Item2!) }).ToArray(),
            ProviderOptions = ProviderOptions.Count == 0 ? null : ProviderOptions.ToDictionary(p => p.Key, p => JsonSerializer.SerializeToElement(p.Value))
        };
    }
    public IReadOnlyList<JsonObject> Requests(ChatTarget model, string prompt, ComposerAttachment? input,
        IReadOnlyList<ComposerAttachment> references, ComposerAttachment? first, ComposerAttachment? last)
    {
        Validate(); var requests = new List<JsonObject>(); var limit = Math.Min(MaxVideosPerCall ?? N, N);
        for (var remaining = N; remaining > 0; remaining -= limit)
            requests.Add(JsonSerializer.SerializeToNode(Request(model, prompt, input, references, first, last, Math.Min(limit, remaining)), JsonSerializerOptions.Web)!.AsObject());
        return requests;
    }
}

public sealed record VideoInputFile(string File, string Name, string MediaType);

public static class VideoAttachments
{
    public static readonly string[] Extensions = [".png", ".jpg", ".jpeg", ".webp", ".mp4", ".webm", ".mov"];
    public static bool IsVideo(string type) => type is "video/mp4" or "video/webm" or "video/quicktime";
    public static bool IsInput(string type) => type is "image/png" or "image/jpeg" or "image/webp" || IsVideo(type);
    public static void Validate(ComposerAttachment file)
    {
        if (!IsInput(file.MediaType) && !ImageAttachments.IsImage(file.MediaType) || !file.IsLink && file.Content.IsEmpty)
            throw new InvalidOperationException(DesktopResources.Get("VideoAttachmentRequired"));
        if (file.IsLink && !UrlAttachments.IsHttpUrl(file.RemoteUrl)) throw new InvalidOperationException(DesktopResources.Get("VideoAttachmentRequired"));
        ComposerAttachments.ValidateSize(file.Content.Length);
    }
    public static VideoFile ToFile(ComposerAttachment file)
    {
        Validate(file);
        return file.IsLink ? new VideoFileUrl { Url = file.RemoteUrl! } : new VideoFile { MediaType = file.MediaType, Data = Convert.ToBase64String(file.Content.Span) };
    }
    public static string Extension(string type) => type switch
    { "video/mp4" => ".mp4", "video/webm" => ".webm", "video/quicktime" => ".mov", _ => ImageAttachments.Extension(type) };
}
