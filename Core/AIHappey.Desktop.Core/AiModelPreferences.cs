using System.Globalization;
using System.Text.Json;

namespace AIHappey.Desktop.Core;

/// <summary>UI defaults only; non-language defaults are reserved for future workflows.</summary>
public sealed class AiModelPreferences
{
    public string? LanguageModel { get; set; }
    public string? EmbeddingModel { get; set; }
    public string? ImageModel { get; set; }
    public string? AudioModel { get; set; }
    public string? TranscriptionModel { get; set; }
    public string? SpeechModel { get; set; }
    public string? DecisionModel { get; set; }
    public string? RerankingModel { get; set; }
    public string? VideoModel { get; set; }
    public bool ChatWithImageModels { get; set; }
    public bool ChatWithVideoModels { get; set; }
    public bool ChatWithSpeechModels { get; set; }
    public bool ChatWithTranscriptionModels { get; set; }
    public AiModelPreferences Clone() => (AiModelPreferences)MemberwiseClone();

    public string? DefaultFor(string type) => type switch
    {
        "language" => LanguageModel, "embedding" => EmbeddingModel, "image" => ImageModel,
        "audio" => AudioModel, "transcription" => TranscriptionModel, "speech" => SpeechModel,
        "decision" => DecisionModel, "reranking" => RerankingModel, "video" => VideoModel, _ => null
    };

    public void SetDefault(string type, string? id)
    {
        id = string.IsNullOrWhiteSpace(id) ? null : id.Trim();
        switch (type)
        {
            case "language": LanguageModel = id; break;
            case "embedding": EmbeddingModel = id; break;
            case "image": ImageModel = id; break;
            case "audio": AudioModel = id; break;
            case "transcription": TranscriptionModel = id; break;
            case "speech": SpeechModel = id; break;
            case "decision": DecisionModel = id; break;
            case "reranking": RerankingModel = id; break;
            case "video": VideoModel = id; break;
            default: throw new ArgumentOutOfRangeException(nameof(type));
        }
    }

    public bool AllowsChat(string type) => type switch
    {
        "language" => true, "image" => ChatWithImageModels, "video" => ChatWithVideoModels,
        "speech" => ChatWithSpeechModels, "transcription" => ChatWithTranscriptionModels, _ => false
    };
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
        return new(id, CatalogProjection.Text(value, "name") ?? ModelOverviewCatalog.DisplayId(id, displayId, providerModelId))
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
    public static IReadOnlyList<ChatTarget> ChatSuggestions(IEnumerable<ChatTarget> items, AiModelPreferences preferences, string query = "") =>
        NewestFirst(items.Where(x => preferences.AllowsChat(x.ModelType ?? ResolveType(x.Id, null)))
            .Where(x => string.IsNullOrWhiteSpace(query) || x.Label.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase)
                || x.Id.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase)));
    public static string NewChatModel(IEnumerable<ChatTarget> items, AiModelPreferences preferences)
    {
        var language = OfType(items, "language");
        return language.FirstOrDefault(x => x.Id == preferences.LanguageModel)?.Id ?? language.FirstOrDefault()?.Id ?? "";
    }
}
