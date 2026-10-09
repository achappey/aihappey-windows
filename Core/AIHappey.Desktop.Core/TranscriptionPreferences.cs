using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Vercel.Models;

namespace AIHappey.Desktop.Core;

public sealed class TranscriptionPreferences
{
    private Dictionary<string, JsonObject> providerOptions = new(StringComparer.OrdinalIgnoreCase) { ["openai"] = new() { ["response_format"] = "json" } };
    public Dictionary<string, JsonObject> ProviderOptions
    {
        get => providerOptions;
        set => providerOptions = value is null ? new(StringComparer.OrdinalIgnoreCase) : new(value.Where(p => p.Value is not null).ToDictionary(p => p.Key, p => p.Value), StringComparer.OrdinalIgnoreCase);
    }
    public TranscriptionPreferences Clone() => new() { ProviderOptions = ProviderOptions.ToDictionary(p => p.Key, p => (JsonObject)p.Value.DeepClone(), StringComparer.OrdinalIgnoreCase) };
    public static JsonObject CleanOpenAI(JsonObject source)
    {
        var result = (JsonObject)source.DeepClone();
        foreach (var key in new[] { "temperature", "timestamp_granularities", "include", "known_speaker_names", "known_speaker_references", "chunking_strategy", "stream" }) result.Remove(key);
        if (string.IsNullOrWhiteSpace(OpenAIChatConfig.Text(result["response_format"]))) result["response_format"] = "json";
        return result;
    }
    public TranscriptionRequest Request(ChatTarget model, ComposerAttachment file)
    {
        TranscriptionFiles.Validate(file);
        var options = ProviderOptions.ToDictionary(p => p.Key, p => JsonSerializer.SerializeToElement(p.Key.Equals("openai", StringComparison.OrdinalIgnoreCase) ? CleanOpenAI(p.Value) : p.Value));
        return new() { Model = model.Id, Audio = Convert.ToBase64String(file.Content.Span), MediaType = file.MediaType, ProviderOptions = options };
    }
}

public static class TranscriptionFiles
{
    public static readonly string[] Extensions = [".flac", ".mp3", ".mp4", ".mpeg", ".mpga", ".m4a", ".ogg", ".wav", ".webm", ".aac", ".wma", ".mov", ".opus"];
    public static string? MediaType(string filename, string? contentType)
    {
        if (contentType?.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) == true || contentType?.StartsWith("video/", StringComparison.OrdinalIgnoreCase) == true) return contentType;
        return Path.GetExtension(filename).ToLowerInvariant() switch
        {
            ".flac" => "audio/flac", ".mp3" or ".mpeg" or ".mpga" => "audio/mpeg", ".mp4" => "video/mp4", ".m4a" => "audio/mp4",
            ".ogg" or ".opus" => "audio/ogg", ".wav" => "audio/wav", ".webm" => "audio/webm", ".aac" => "audio/aac", ".wma" => "audio/x-ms-wma", ".mov" => "video/quicktime", _ => null
        };
    }
    public static void Validate(ComposerAttachment file)
    {
        if (file.IsLink || file.Content.Length == 0 || MediaType(file.Name, file.MediaType) is null)
            throw new InvalidOperationException(DesktopResources.Get("TranscriptionUnsupported"));
        ComposerAttachments.ValidateSize(file.Content.Length);
    }
    public static string TextFilename(string filename) => Path.GetFileNameWithoutExtension(filename) + ".txt";
    public static string ExportText(JsonObject response)
    {
        var parts = (response["segments"] as JsonArray)?.OfType<JsonObject>().Where(s => !string.IsNullOrWhiteSpace(OpenAIChatConfig.Text(s["text"])))
            .Select(s => $"{s["startSecond"]}s – {s["endSecond"]}s\n{OpenAIChatConfig.Text(s["text"])?.Trim()}").ToArray();
        return parts?.Length > 0 ? string.Join("\n\n", parts) : OpenAIChatConfig.Text(response["text"]) ?? "";
    }
}
