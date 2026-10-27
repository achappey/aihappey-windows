using System.Text.Json.Nodes;

namespace AIHappey.Desktop.Core;

/// <summary>Native Google Interactions configuration. Only browser-defined normalizations are applied.</summary>
public sealed partial class GoogleChatConfig : ProviderToolsChatConfig
{
    public static readonly string[] ToolTypes = ["code_execution", "url_context", "google_maps", "google_search", "retrieval"];
    public static readonly string[] ThinkingLevels = ["minimal", "low", "medium", "high"];
    public static readonly string[] SearchTypes = ["web_search", "image_search", "enterprise_web_search"];
    public static readonly string[] FormatTypes = ["text", "audio", "image", "video"];
    public static readonly string[] AudioMimeTypes = ["audio/mp3", "audio/ogg_opus", "audio/l16", "audio/wav", "audio/alaw", "audio/mulaw"];
    public static readonly string[] ImageAspectRatios = ["1:1", "2:3", "3:2", "3:4", "4:3", "4:5", "5:4", "9:16", "16:9", "21:9", "1:8", "8:1", "1:4", "4:1"];
    public static readonly string[] ImageSizes = ["512", "1K", "2K", "4K"];
    public static readonly string[] VideoTasks = ["text_to_video", "image_to_video", "reference_to_video", "edit", "extend"];
    public static readonly string[] ServiceTiers = ["flex", "standard", "priority"];
    public static readonly string[] SafetyTypes = ["hate_speech", "dangerous_content", "harassment", "sexually_explicit", "civic_integrity", "image_hate", "image_dangerous_content", "image_harassment", "image_sexually_explicit", "jailbreak"];
    public static readonly string[] SafetyMethods = ["severity", "probability"];
    public static readonly string[] SafetyThresholds = ["block_low_and_above", "block_medium_and_above", "block_only_high", "block_none", "off"];
    public GoogleChatConfig(JsonObject config) : base(config, ToolTypes) { }
    public static JsonObject Defaults() => JsonNode.Parse("""{"generation_config":{"thinking_level":"low","thinking_summaries":"auto"},"tools":[{"type":"url_context"},{"type":"google_search","search_types":["web_search"]}]}""")!.AsObject();
    public override JsonObject ToolDefault(string type) => type == "google_search"
        ? new() { ["type"] = type, ["search_types"] = Strings(["web_search"]) } : base.ToolDefault(type);
    public static JsonObject SafetyDefault() => new() { ["type"] = "hate_speech", ["method"] = "probability", ["threshold"] = "block_medium_and_above" };
    public bool ReasoningOn => Get("generation_config/thinking_level") is not null || Get("generation_config/thinking_summaries") is not null;
    public void Reasoning(bool enabled)
    {
        var generation = (JsonObject?)Get("generation_config")?.DeepClone() ?? new();
        if (enabled) { generation["thinking_level"] ??= "minimal"; generation["thinking_summaries"] ??= "auto"; }
        else { generation.Remove("thinking_level"); generation.Remove("thinking_summaries"); }
        Set("generation_config", generation, generation.Count == 0);
    }
    // Browser generation cards spread the fallback thinking settings before editing a field.
    public void Generation(string path, JsonNode? value, bool omit = false)
    {
        var generation = new JsonObject { ["thinking_level"] = "minimal", ["thinking_summaries"] = "auto" };
        if (Get("generation_config") is JsonObject current)
            foreach (var pair in current) generation[pair.Key] = pair.Value?.DeepClone();
        Set("generation_config", generation); Set("generation_config/" + path, value, omit);
    }
    public static JsonArray NormalizeFormats(JsonNode value)
    {
        var formats = value is JsonArray array ? array : new JsonArray(value.DeepClone());
        var result = new JsonArray();
        foreach (var item in formats)
        {
            if (item is not JsonObject format) { result.Add(item?.DeepClone()); continue; }
            var next = (JsonObject)format.DeepClone();
            switch (Text(next["type"]))
            {
                case "text": next = new() { ["type"] = "text" }; break;
                case "image": next.Remove("mime_type"); next["delivery"] = "inline"; break;
                case "video": next.Remove("gcs_uri"); next["delivery"] = "inline"; break;
                case "audio": next["delivery"] = "inline"; break;
            }
            result.Add(next);
        }
        return result;
    }
    public override JsonObject Commit()
    {
        var next = base.Commit();
        if (next["response_format"] is { } formats) next["response_format"] = NormalizeFormats(formats);
        return next;
    }
    public JsonArray Formats => Get("response_format") switch
    {
        JsonArray array => (JsonArray)array.DeepClone(),
        JsonObject obj => new JsonArray(obj.DeepClone()),
        _ => new()
    };
    public void FormatsChanged(JsonArray formats) => Set("response_format", NormalizeFormats(formats));
    public bool RetrievalOn(string provider) => (Get("retrieval/retrieval_types") as JsonArray)?.Any(n => Text(n) == provider + "_ai_search") == true
        || Get("retrieval/" + provider + "_ai_search_config") is not null;
    public void Retrieval(string provider, bool enabled, string? apiKey)
    {
        var retrieval = Get("retrieval") as JsonObject ?? new JsonObject { ["type"] = "retrieval" };
        var draft = new ProviderChatConfig(retrieval);
        draft.ToggleArray("retrieval_types", provider + "_ai_search", enabled);
        var key = provider + "_ai_search_config";
        if (enabled) draft.Optional(key + "/api_key", apiKey ?? ""); else draft.Set(key, null, true);
        // Enabling without a key still creates the provider config object, as in the browser.
        if (enabled && draft.Get(key) is null) draft.Set(key, new JsonObject());
        Set("retrieval", draft.Root, !draft.Root.Any(p => p.Key != "type"));
    }
}
