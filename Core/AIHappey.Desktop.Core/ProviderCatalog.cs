using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Desktop.Core;

public sealed record ProviderUrls(string? Homepage = null, string? Pricing = null, string? Console = null,
    string? Docs = null, string? TermsOfService = null, string? PrivacyPolicy = null);

public sealed record CatalogProvider
{
    [JsonIgnore] public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string? Description { get; init; }
    public bool Experimental { get; init; }
    public string? Category { get; init; }
    public string? ProviderCountry { get; init; }
    public IReadOnlyList<string> InferenceRegions { get; init; } = [];
    public IReadOnlyList<ModelProviderIcon> Icons { get; init; } = [];
    public ProviderUrls Urls { get; init; } = new();
    // Keep metadata unknown to this UI (including future schema additions).
    [JsonExtensionData] public Dictionary<string, JsonElement>? AdditionalMetadata { get; init; }
}

public sealed record ProviderLink(string Key, string ResourceKey, string Glyph, Uri Uri);

/// <summary>Read-only, offline JSON snapshot copied from chat. Never configures a runtime or stores credentials.</summary>
public static class ProviderCatalog
{
    private sealed record IndexEntry(string Id, string File);
    private static readonly Lazy<IReadOnlyDictionary<string, CatalogProvider>> catalog = new(Load);
    public static IReadOnlyDictionary<string, CatalogProvider> All => catalog.Value;
    public static CatalogProvider Get(string id) => All.GetValueOrDefault(id) ?? new() { Id = id, Name = id };
    private static IReadOnlyDictionary<string, CatalogProvider> Load()
    {
        var assembly = typeof(ProviderCatalog).Assembly;
        using var index = assembly.GetManifestResourceStream("Desktop.ProviderCatalog.index.json")
            ?? throw new InvalidDataException("Missing provider catalog index.");
        var entries = JsonSerializer.Deserialize<IndexEntry[]>(index, JsonSerializerOptions.Web)
            ?? throw new InvalidDataException("Invalid provider catalog index.");
        var result = new Dictionary<string, CatalogProvider>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            using var stream = assembly.GetManifestResourceStream("Desktop.ProviderCatalog." + entry.File)
                ?? throw new InvalidDataException("Missing provider metadata: " + entry.Id);
            var provider = JsonSerializer.Deserialize<CatalogProvider>(stream, JsonSerializerOptions.Web)
                ?? throw new InvalidDataException("Invalid provider metadata: " + entry.Id);
            result.Add(entry.Id, provider with { Id = entry.Id });
        }
        return new System.Collections.ObjectModel.ReadOnlyDictionary<string, CatalogProvider>(result);
    }

    public static IReadOnlyList<ProviderLink> Links(CatalogProvider provider)
    {
        var urls = provider.Urls;
        var candidates = new[] {
            ("homepage", "Website", "\uE774", urls.Homepage), ("pricing", "ProviderPricing", "\uE8D4", urls.Pricing),
            ("console", "ProviderConsole", "\uE756", urls.Console), ("docs", "ProviderDocumentation", "\uE8A5", urls.Docs),
            ("termsOfService", "ProviderTerms", "\uE8A5", urls.TermsOfService), ("privacyPolicy", "ProviderPrivacy", "\uE72E", urls.PrivacyPolicy)
        };
        return candidates.Where(c => ModelProviders.SafeWebUri(c.Item4) is not null)
            .Select(c => new ProviderLink(c.Item1, c.Item2, c.Item3, ModelProviders.SafeWebUri(c.Item4)!)).ToArray();
    }
    public static Uri? FaviconUri(string? homepage) => ModelProviders.SafeWebUri(homepage) is { } uri
        ? new Uri("https://t0.gstatic.com/faviconV2?client=SOCIAL&type=FAVICON&fallback_opts=TYPE,SIZE,URL&url="
            + Uri.EscapeDataString(homepage!) + "&size=128") : null;
    public static IReadOnlyList<string> IconSources(CatalogProvider provider, bool dark)
    {
        var theme = dark ? "dark" : "light";
        return provider.Icons.OrderBy(i => i.Theme == theme ? 0 : i.Theme is null ? 1 : 2).Select(i => i.Src)
            .Concat(FaviconUri(provider.Urls.Homepage) is { } favicon ? [favicon.AbsoluteUri] : [])
            .Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.Ordinal).ToArray();
    }
}

public sealed class ProviderOverviewFilter
{
    public string Search { get; set; } = "";
    public bool FavoritesOnly { get; set; }
    public HashSet<string> Categories { get; } = new(StringComparer.Ordinal);
    public HashSet<string> Countries { get; } = new(StringComparer.Ordinal);
    public HashSet<string> Regions { get; } = new(StringComparer.Ordinal);
    public HashSet<string> ModelTypes { get; } = new(StringComparer.Ordinal);
}

/// <summary>Country/category/region selections OR within a facet; model types AND, matching chat.</summary>
public sealed class ProviderOverviewCatalog
{
    public IReadOnlyList<CatalogProvider> Providers { get; }
    public IReadOnlyDictionary<string, string[]> ModelTypes { get; }
    public ProviderOverviewCatalog(IEnumerable<CatalogProvider> providers, IEnumerable<ChatTarget>? models = null)
    {
        Providers = providers.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(p => p.Id, StringComparer.Ordinal).ToArray();
        ModelTypes = (models ?? []).GroupBy(ModelOverviewCatalog.ProviderKey, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(m => m.ModelType).Where(t => !string.IsNullOrWhiteSpace(t)).Cast<string>().Distinct().Order().ToArray(), StringComparer.Ordinal);
    }
    public IReadOnlyList<CatalogProvider> Filter(ProviderOverviewFilter filter, IReadOnlySet<string> favorites, string? omittedFacet = null)
    {
        var search = filter.Search.Trim();
        return Providers.Where(p =>
            (search.Length == 0 || $"{p.Id} {p.Name} {p.Description} {p.Urls.Homepage}".Contains(search, StringComparison.CurrentCultureIgnoreCase))
            && (!filter.FavoritesOnly || favorites.Contains(p.Id))
            && (omittedFacet == "category" || filter.Categories.Count == 0 || p.Category is { } category && filter.Categories.Contains(category))
            && (omittedFacet == "country" || filter.Countries.Count == 0 || p.ProviderCountry is { } country && filter.Countries.Contains(country))
            && (omittedFacet == "region" || filter.Regions.Count == 0 || p.InferenceRegions.Any(filter.Regions.Contains))
            && (omittedFacet == "modelType" || filter.ModelTypes.Count == 0 || filter.ModelTypes.All(t => Types(p.Id).Contains(t)))).ToArray();
    }
    public string[] Types(string id) => ModelTypes.GetValueOrDefault(id) ?? [];
}
