using System.Text.Json;
using System.Text.Json.Nodes;

namespace AIHappey.Desktop.Core;

/// <summary>Read-only local discovery, never inference or a credential/network API.</summary>
public sealed class LocalArtificialIntelligenceTools(IEnumerable<ChatTarget> models, IEnumerable<CatalogProvider>? providers = null)
{
    private readonly ChatTarget[] models = models.ToArray();
    private readonly CatalogProvider[] providers = (providers ?? ProviderCatalog.All.Values)
        .OrderBy(p => p.Name, NaturalTextComparer.Instance).ThenBy(p => p.Id, NaturalTextComparer.Instance).ToArray();
    private static string Normalize(string? value) => (value ?? "").Trim().ToLowerInvariant();
    private static string? Text(JsonElement input, string key) => CatalogProjection.Text(input, key);
    private static int? Limit(JsonElement input) => input.TryGetProperty("limit", out var raw) && raw.ValueKind == JsonValueKind.Number
        && raw.TryGetDouble(out var n) && double.IsFinite(n) && n > 0 ? (int)Math.Min(500, Math.Floor(n)) : null;
    private static T[] Take<T>(IEnumerable<T> items, int? limit) => limit is > 0 ? items.Take(limit.Value).ToArray() : items.ToArray();
    private static object Provider(CatalogProvider p) => new { key = p.Id, name = p.Name, description = p.Description,
        experimental = p.Experimental, providerCountry = p.ProviderCountry, inferenceRegions = p.InferenceRegions, urls = p.Urls };
    private IEnumerable<CatalogProvider> FilterProviders(JsonElement input) => providers.Where(p =>
        (Normalize(Text(input, "country")) is not { Length: > 0 } country || Normalize(p.ProviderCountry) == country)
        && (Normalize(Text(input, "inferenceRegion")) is not { Length: > 0 } region || p.InferenceRegions.Any(r => Normalize(r) == region)));
    // Browser derives this from the ID prefix (not the UI's source-provider override).
    private static string ProviderKey(ChatTarget model) => Normalize(model.Id.Split('/')[0]);
    private IEnumerable<ChatTarget> FilterModels(JsonElement input)
    {
        IEnumerable<ChatTarget> result = models; var provider = Normalize(Text(input, "provider"));
        if (provider.Length > 0)
        {
            var exact = providers.Where(p => Normalize(p.Id) == provider || Normalize(p.Name) == provider).ToArray();
            var keys = (exact.Length > 0 ? exact : providers.Where(p => Normalize(p.Id).Contains(provider) || Normalize(p.Name).Contains(provider)))
                .Select(p => Normalize(p.Id)).ToHashSet(StringComparer.Ordinal);
            result = result.Where(m => keys.Contains(ProviderKey(m)));
        }
        var type = Normalize(Text(input, "type"));
        return result.Where(m => type.Length == 0 || Normalize(m.ModelType).Contains(type));
    }
    private static JsonObject Model(ChatTarget m)
    {
        var raw = m.ModelMetadata is { ValueKind: JsonValueKind.Object } metadata ? JsonNode.Parse(metadata.GetRawText())!.AsObject()
            : new JsonObject { ["id"] = m.Id, ["name"] = m.Label, ["owned_by"] = m.OwnedBy, ["tags"] = OpenAIChatConfig.Strings(m.Tags) };
        raw["type"] = m.ModelType; return raw;
    }
    public Task<JsonElement> CallAsync(string name, JsonElement input, CancellationToken ct) => DesktopLocalTools.SafeAsync(() =>
    {
        var limit = Limit(input);
        object result;
        if (name == "local_ai_provider_countries_list")
        {
            var items = providers.Where(p => !string.IsNullOrWhiteSpace(p.ProviderCountry)).GroupBy(p => p.ProviderCountry!.Trim().ToUpperInvariant())
                .OrderBy(g => g.Key, NaturalTextComparer.Instance).Select(g => new { code = g.Key, providerCount = g.Count() }).ToArray();
            result = new { total = items.Length, count = items.Length, items };
        }
        else if (name is "local_ai_providers_list" or "local_ai_providers_search")
        {
            var query = name == "local_ai_providers_search" ? Normalize(DesktopLocalTools.Required(input, "query")) : "";
            if (name == "local_ai_providers_search" && query.Length == 0) throw new LocalToolException("Missing query.");
            var filtered = FilterProviders(input).Where(p => query.Length == 0 || Normalize(p.Id).Contains(query) || Normalize(p.Name).Contains(query)).ToArray();
            var items = Take(filtered, limit).Select(Provider).ToArray(); result = new { total = filtered.Length, count = items.Length, items };
        }
        else if (name is "local_ai_models_search" or "local_ai_models_list_by_provider")
        {
            if (name == "local_ai_models_list_by_provider" && string.IsNullOrWhiteSpace(DesktopLocalTools.Required(input, "provider")))
                throw new LocalToolException("Missing provider.");
            var query = name == "local_ai_models_search" ? Normalize(Text(input, "query")) : "";
            var filtered = FilterModels(input).Where(m => query.Length == 0 || Normalize(string.Join(' ', new[]
                { m.Id, m.Label, m.ModelType, m.OwnedBy, string.Join(' ', m.Tags), ProviderKey(m), providers.FirstOrDefault(p => Normalize(p.Id) == ProviderKey(m))?.Name })).Contains(query))
                .OrderBy(m => Normalize(m.Label.Length > 0 ? m.Label : m.Id), NaturalTextComparer.Instance).ThenBy(m => m.Id, NaturalTextComparer.Instance).ToArray();
            var data = Take(filtered, limit).Select(Model).ToArray();
            result = name == "local_ai_models_search" ? (object)new { data } : new { total = filtered.Length, count = data.Length, data };
        }
        else throw new LocalToolException("Unsupported AI discovery tool.");
        return Task.FromResult(DesktopLocalTools.Result(result));
    }, ct);
}

/// <summary>Case-insensitive numeric sorting, matching browser localeCompare numeric ordering.</summary>
public sealed class NaturalTextComparer : IComparer<string>
{
    public static NaturalTextComparer Instance { get; } = new();
    public int Compare(string? left, string? right)
    {
        left ??= ""; right ??= ""; var a = 0; var b = 0;
        while (a < left.Length && b < right.Length)
        {
            if (char.IsAsciiDigit(left[a]) && char.IsAsciiDigit(right[b]))
            {
                var ae = a; var be = b;
                while (ae < left.Length && char.IsAsciiDigit(left[ae])) ae++;
                while (be < right.Length && char.IsAsciiDigit(right[be])) be++;
                var an = left[a..ae].TrimStart('0'); var bn = right[b..be].TrimStart('0');
                var numeric = an.Length.CompareTo(bn.Length);
                if (numeric == 0) numeric = string.Compare(an, bn, StringComparison.Ordinal);
                if (numeric != 0) return numeric; a = ae; b = be;
            }
            else
            {
                var comparison = char.ToUpperInvariant(left[a++]).CompareTo(char.ToUpperInvariant(right[b++]));
                if (comparison != 0) return comparison;
            }
        }
        return (left.Length - a).CompareTo(right.Length - b);
    }
}
