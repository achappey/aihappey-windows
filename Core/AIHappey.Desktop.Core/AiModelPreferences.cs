using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Desktop.Core;

/// <summary>Durable UI defaults, independent of runtime routing availability.</summary>
public sealed class AiModelPreferences
{
    public static IReadOnlyList<string> MultiModelTypes { get; } = Array.AsReadOnly(new[] { "language", "image", "video", "speech" });
    [JsonConverter(typeof(ModelIdsJsonConverter))]
    public string[]? LanguageModel { get; set; }
    public string? EmbeddingModel { get; set; }
    [JsonConverter(typeof(ModelIdsJsonConverter))]
    public string[]? ImageModel { get; set; }
    public string? AudioModel { get; set; }
    public string? TranscriptionModel { get; set; }
    [JsonConverter(typeof(ModelIdsJsonConverter))]
    public string[]? SpeechModel { get; set; }
    public string? DecisionModel { get; set; }
    public string? RerankingModel { get; set; }
    [JsonConverter(typeof(ModelIdsJsonConverter))]
    public string[]? VideoModel { get; set; }
    public bool ChatWithImageModels { get; set; }
    public bool ChatWithVideoModels { get; set; }
    public bool ChatWithSpeechModels { get; set; }
    public bool ShowVoiceModels { get; set; } = true;
    public bool ChatWithTranscriptionModels { get; set; }
    public AiModelPreferences Clone()
    {
        var clone = (AiModelPreferences)MemberwiseClone();
        clone.LanguageModel = LanguageModel?.ToArray(); clone.ImageModel = ImageModel?.ToArray();
        clone.VideoModel = VideoModel?.ToArray(); clone.SpeechModel = SpeechModel?.ToArray();
        return clone;
    }

    public string[]? DefaultsFor(string type) => type switch
    {
        "language" => LanguageModel, "image" => ImageModel, "video" => VideoModel, "speech" => SpeechModel,
        _ => DefaultFor(type) is { } id ? [id] : null
    };

    public void SetDefaults(string type, IEnumerable<string> ids)
    {
        var values = Normalize(ids);
        switch (type)
        {
            case "language": LanguageModel = values; break;
            case "image": ImageModel = values; break;
            case "video": VideoModel = values; break;
            case "speech": SpeechModel = values; break;
            default: throw new ArgumentOutOfRangeException(nameof(type));
        }
    }

    public bool SeedDefaults(IReadOnlyDictionary<string, string[]>? defaults)
    {
        var changed = false;
        foreach (var type in MultiModelTypes)
            if (DefaultsFor(type) is null && defaults?.TryGetValue(type, out var ids) == true && ids is not null)
            { SetDefaults(type, ids); changed = true; }
        return changed;
    }

    internal static string[] Normalize(IEnumerable<string> ids) => ids.Where(id => !string.IsNullOrWhiteSpace(id))
        .Select(id => id.Trim()).Distinct(StringComparer.Ordinal).ToArray();

    public string? DefaultFor(string type) => type switch
    {
        "language" => LanguageModel?.FirstOrDefault(), "embedding" => EmbeddingModel, "image" => ImageModel?.FirstOrDefault(),
        "audio" => AudioModel, "transcription" => TranscriptionModel, "speech" => SpeechModel?.FirstOrDefault(),
        "decision" => DecisionModel, "reranking" => RerankingModel, "video" => VideoModel?.FirstOrDefault(), _ => null
    };

    public void SetDefault(string type, string? id)
    {
        id = string.IsNullOrWhiteSpace(id) ? null : id.Trim();
        if (MultiModelTypes.Contains(type)) { SetDefaults(type, id is null ? [] : [id]); return; }
        switch (type)
        {
            case "embedding": EmbeddingModel = id; break;
            case "audio": AudioModel = id; break;
            case "transcription": TranscriptionModel = id; break;
            case "decision": DecisionModel = id; break;
            case "reranking": RerankingModel = id; break;
            default: throw new ArgumentOutOfRangeException(nameof(type));
        }
    }

    public bool AllowsChat(string type) => type switch
    {
        "language" => true, "image" => ChatWithImageModels, "video" => ChatWithVideoModels,
        "speech" => ChatWithSpeechModels, "transcription" => ChatWithTranscriptionModels, _ => false
    };
}

/// <summary>Read legacy single IDs, but always write the four multi-model preferences as arrays.</summary>
public sealed class ModelIdsJsonConverter : JsonConverter<string[]>
{
    public override string[]? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String) return AiModelPreferences.Normalize([reader.GetString()!]);
        return AiModelPreferences.Normalize(JsonSerializer.Deserialize<List<string>>(ref reader, options) ?? []);
    }
    public override void Write(Utf8JsonWriter writer, string[] value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, AiModelPreferences.Normalize(value).ToList(), options);
}

public static class AiModelCatalog
{
    public static IReadOnlyList<string> Types { get; } = Array.AsReadOnly(new[]
        { "language", "embedding", "image", "audio", "transcription", "speech", "decision", "reranking", "video" });

    // Matches browser modelTypeEnrichment: trust explicit known types, then infer legacy IDs.
    public static string ResolveType(string id, string? type)
    {
        type = type?.Trim().ToLowerInvariant();
        if (type is not null && Types.Contains(type)) return type;
        id = id.Trim().ToLowerInvariant();
        if (Types.Contains(id)) return id;
        bool Has(params string[] fragments) => fragments.Any(id.Contains);
        if (Has("whisper", "transcribe", "transcription", "cartesia") || Has("voxtral") && !Has("tts")) return "transcription";
        if (Has("tts", "speech", "canopy", "kokoro", "chatterbox")) return "speech";
        if (Has("rerank")) return "reranking";
        if (Has("embed", "embedding")) return "embedding";
        if (Has("image", "flux", "stable-diffusion", "sdxl", "sd3.5", "dalle", "dall-e", "ideogram", "riverflow",
            "kandinsky", "datacte/proteus", "dreamshaper", "bria", "seedream", "recraft", "imagen")) return "image";
        if (Has("sora", "veo-", "t2v", "i2v", "video")) return "video";
        if (Has("realtime", "audio")) return "audio";
        return "language";
    }

    public static ChatTarget Project(JsonElement value, ServiceKind service)
    {
        var id = value.GetProperty("id").GetString()!;
        var displayId = CatalogProjection.Text(value, "displayId");
        var providerModelId = CatalogProjection.Text(value, "providerModelId");
        var label = CatalogProjection.Text(value, "name") ?? ModelOverviewCatalog.DisplayId(id, displayId, providerModelId);
        var provider = ChatPreferences.ResolveProvider(service, id,
            CatalogProjection.Text(value, "sourceProviderKey") ?? CatalogProjection.Text(value, "providerKey"));
        if (service == ServiceKind.Ai && provider is not null && label.StartsWith(provider + "/", StringComparison.OrdinalIgnoreCase)) label = label[(provider.Length + 1)..];
        return new(id, label)
        {
            ProviderKey = ChatPreferences.ResolveProvider(service, id,
                CatalogProjection.Text(value, "sourceProviderKey") ?? CatalogProjection.Text(value, "providerKey")),
            ModelType = service == ServiceKind.Ai ? ResolveType(id, CatalogProjection.Text(value, "type")) : null,
            Created = value.TryGetProperty("created", out var created) && created.ValueKind == JsonValueKind.Number
                && created.TryGetInt64(out var timestamp) ? timestamp : null,
            DisplayId = displayId, ProviderModelId = providerModelId,
            Description = CatalogProjection.Text(value, "description"), OwnedBy = CatalogProjection.Text(value, "owned_by"),
            Tags = value.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array
                ? tags.EnumerateArray().Where(t => t.ValueKind == JsonValueKind.String).Select(t => t.GetString()!.Trim())
                    .Where(t => t.Length > 0).Distinct(StringComparer.Ordinal).ToArray() : [],
            ContextWindow = Number(value, "context_window"), MaxTokens = Number(value, "max_tokens"),
            InputPrice = Price(value, "input"), OutputPrice = Price(value, "output"), ModelMetadata = value.Clone()
        };
    }

    private static double? Price(JsonElement value, string field) => value.TryGetProperty("pricing", out var pricing)
        && Number(pricing, field) is >= 0 and var price ? price : null;

    private static double? Number(JsonElement value, string field)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(field, out var raw)) return null;
        double number;
        return (raw.ValueKind == JsonValueKind.Number && raw.TryGetDouble(out number)
            || raw.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(raw.GetString())
                && double.TryParse(raw.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number))
            && double.IsFinite(number) ? number : null;
    }

    // OrderByDescending is stable: equal/missing dates keep the gateway's order, as in the browser.
    public static IReadOnlyList<ChatTarget> NewestFirst(IEnumerable<ChatTarget> items) => items.OrderByDescending(x => x.Created ?? 0).ToArray();
    public static IReadOnlyList<ChatTarget> OfType(IEnumerable<ChatTarget> items, string type) => NewestFirst(items.Where(x => x.ModelType == type));
    // Filter choices, never the catalog used to resolve saved defaults or explicit selections.
    public static IReadOnlyList<ChatTarget> PickerChoices(IEnumerable<ChatTarget> items, bool showVoiceModels) =>
        items.Where(item => showVoiceModels || !item.Tags.Any(tag => string.Equals(tag.Trim(), "voice", StringComparison.OrdinalIgnoreCase))).ToArray();
    public static IReadOnlyList<ChatTarget> ChatSuggestions(IEnumerable<ChatTarget> items, AiModelPreferences preferences, string query = "") =>
        NewestFirst(items.Where(x => preferences.AllowsChat(x.ModelType ?? ResolveType(x.Id, null)))
            .Where(x => string.IsNullOrWhiteSpace(query) || x.Label.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase)
                || x.Id.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase)));
    public static string NewChatModel(IEnumerable<ChatTarget> items, AiModelPreferences preferences)
    {
        return InitialModels(items, preferences, "language", false).FirstOrDefault() ?? "";
    }

    public static string[] InitialModels(IEnumerable<ChatTarget> items, AiModelPreferences preferences, string type,
        bool multiple, string? fallback = null)
    {
        var models = OfType(items, type);
        var defaults = preferences.DefaultsFor(type) ?? [];
        var selected = (multiple ? defaults : defaults.Take(1)).Where(id => models.Any(model => model.Id == id)).ToArray();
        if (selected.Length > 0) return selected;
        var first = models.FirstOrDefault(model => model.Id == fallback) ?? models.FirstOrDefault();
        return first is null ? [] : [first.Id];
    }
}
