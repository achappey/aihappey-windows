using System.Text.Json;
using System.Xml.Linq;
using AIHappey.Desktop.Core;

internal static class ProviderRegressionTests
{
    public static async Task RunAsync(Action<bool, string> check, string root)
    {
        var all = ProviderCatalog.All;
        check(all.Count > 600 && all.All(p => p.Key == p.Value.Id && p.Value.Name.Length > 0), "complete indexed provider snapshot loads with stable browser IDs");
        var openai = all["openai"];
        check(openai.Urls.Pricing == "https://openai.com/api/pricing" && openai.ProviderCountry == "US" && openai.Category == "model_provider"
            && openai.InferenceRegions.Contains("World") && openai.AdditionalMetadata!.ContainsKey("apiBaseUrl"), "shared catalog retains complete browser metadata and unprojected fields");
        check(ProviderCatalog.Links(openai).Select(l => l.Key).SequenceEqual(["homepage", "pricing", "console", "docs", "termsOfService", "privacyPolicy"]), "all six browser link actions retain card order");
        var bad = openai with { Urls = new("file:///C:/secret", "javascript:alert(1)", "https://user:pass@example.com", "https://localhost", "https://example.com/terms", null) };
        check(ProviderCatalog.Links(bad).Single().Key == "termsOfService", "unsafe provider actions are removed rather than passed to the launcher");
        check(ProviderCatalog.FaviconUri(openai.Urls.Homepage)!.OriginalString == "https://t0.gstatic.com/faviconV2?client=SOCIAL&type=FAVICON&fallback_opts=TYPE,SIZE,URL&url=https%3A%2F%2Fopenai.com&size=128", "Google favicon URL matches browser encoding and size");
        check(ProviderCatalog.FaviconUri("file:///C:/secret") is null && ProviderCatalog.FaviconUri(null) is null, "missing or unsafe homepage cannot generate remote favicon requests");
        check(ProviderCatalog.IconSources(openai, true).First().Contains("/dark/") && ProviderCatalog.IconSources(openai, false).First().Contains("/light/")
            && ProviderCatalog.IconSources(openai, true).Last().StartsWith("https://t0.gstatic.com/"), "theme icons precede Google fallback even when explicit icons fail");
        check(ProviderCatalog.IconSources(openai with { Icons = [] }, true).Count == 1 && ProviderCatalog.IconSources(new() { Name = "No homepage" }, false).Count == 0, "logo fallback covers missing explicit icons without inventing URLs");
        var image = new ChatTarget("openai/image", "Image") { ProviderKey = "openai", ModelType = "image" };
        var language = image with { Id = "openai/chat__direct", ModelType = "language" };
        var anthropic = all["anthropic"];
        var catalog = new ProviderOverviewCatalog([openai, anthropic], [image, language, image]);
        var favorites = new HashSet<string>(["openai"], StringComparer.Ordinal);
        var filter = new ProviderOverviewFilter();
        check(catalog.Providers.First().Id == "anthropic" && catalog.Types("openai").SequenceEqual(["image", "language"]), "providers sort by name and model types come only from discovered provider identity");
        filter.Search = "OPENAI"; check(catalog.Filter(filter, favorites).Single().Id == "openai", "provider search ignores casing");
        filter.Search = "artificial general intelligence"; check(catalog.Filter(filter, favorites).Any(p => p.Id == "openai"), "provider search covers description");
        filter.Search = ""; filter.Countries.Add("NL"); check(catalog.Filter(filter, favorites).Count == 0, "country filter excludes nonmatching providers");
        filter.Countries.Add("US"); check(catalog.Filter(filter, favorites).Count == 2, "country multiselect ORs values");
        filter.Regions.Add("World"); filter.Categories.Add("model_provider"); filter.ModelTypes.Add("image"); filter.ModelTypes.Add("language");
        check(catalog.Filter(filter, favorites).Single().Id == "openai", "facets combine with AND and all selected discovered model types are required");
        check(catalog.Filter(filter, favorites, "modelType").Count == 2, "facet counts can omit their own selection");
        filter.ModelTypes.Clear(); filter.FavoritesOnly = true; check(catalog.Filter(filter, favorites).Single().Id == "openai", "favorites tab preserves other filters");
        check(catalog.Filter(filter, new HashSet<string>()).Count == 0 && new ProviderOverviewCatalog([]).Filter(new(), favorites).Count == 0, "empty catalog and favorites are real empty states");
        var store = new CatalogFavoritesStore(Path.Combine(root, "provider-favorites"));
        var partition = HistoryStore.Partition("host", "identity", "endpoint");
        await store.SaveAsync(partition, ["openai", "openai"]);
        check((await store.LoadAsync(partition)).SetEquals(["openai"]) && (await store.LoadAsync(HistoryStore.Partition("host", "other", "endpoint"))).Count == 0,
            "provider favorites persist, deduplicate and isolate identities");
        check((await store.LoadAsync(HistoryStore.Partition("host", "identity", "other-endpoint"))).Count == 0, "provider favorites isolate endpoints");
        await store.SaveAsync(partition, []); check((await store.LoadAsync(partition)).Count == 0, "provider unfavorite persists");
        foreach (var lang in new[] { "en", "nl" })
        {
            using var stream = typeof(ProviderRegressionTests).Assembly.GetManifestResourceStream("Desktop.Resources." + lang)!;
            var values = XDocument.Load(stream).Root!.Elements("data").ToDictionary(e => (string)e.Attribute("name")!, e => e.Element("value")!.Value);
            foreach (var key in new[] { "ProvidersDescription", "SearchProviders", "RefreshProviders", "ProviderCategory", "ProviderCountry", "ProviderInferenceRegions", "ProviderAvailableModelTypes", "ProviderNoDiscoveredModels", "ProviderDiscoveryUnavailable", "ProvidersNoFavorites", "ProviderLogoLabel", "ProviderLinkFailed", "ProviderExperimental", "Website", "ProviderPricing", "ProviderConsole", "ProviderDocumentation", "ProviderTerms", "ProviderPrivacy" }
                .Concat(all.Values.Select(p => p.Category).OfType<string>().Distinct().Select(c => "ProviderCategory_" + c))
                .Concat(all.Values.Select(p => p.ProviderCountry).OfType<string>().Distinct().Select(c => "ProviderCountry_" + c))
                .Concat(all.Values.SelectMany(p => p.InferenceRegions).Distinct().Select(r => "ProviderRegion_" + r)))
                check(values.TryGetValue(key, out var value) && value.Length > 0, "provider localization " + lang + "/" + key);
        }
    }
}
