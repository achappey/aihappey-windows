using System.Text.Json;
using System.Xml.Linq;
using AIHappey.Desktop.Core;

internal static class ModelsOverviewRegressionTests
{
    public static async Task RunAsync(Action<bool, string> check, string root)
    {
        using var document = JsonDocument.Parse("""
        {"id":"openai/route__direct","name":"Same display name","displayId":" visible-id ","providerModelId":"upstream-id",
         "sourceProviderKey":" OpenAI ","type":"language","description":"Fast reasoning assistant","owned_by":"Research Lab",
         "created":200,"context_window":"32000","max_tokens":4096,"tags":[" reasoning ","reasoning","real-time",7,""],
         "pricing":{"input":"0.000001","output":0.000002}}
        """);
        var first = AiModelCatalog.Project(document.RootElement, ServiceKind.Ai);
        check(first.Id == "openai/route__direct" && ModelOverviewCatalog.DisplayId(first) == "visible-id"
            && first.ProviderKey == "openai" && first.ContextWindow == 32000 && first.MaxTokens == 4096
            && first.Description == "Fast reasoning assistant" && first.OwnedBy == "Research Lab"
            && first.Tags.SequenceEqual(["reasoning", "real-time"]) && first.InputPrice == 0.000001 && first.OutputPrice == 0.000002,
            "overview projection retains full metadata and cleans tags without changing backend identity");
        check(ModelOverviewCatalog.DisplayId("p/model__direct") == "p/model"
            && ModelOverviewCatalog.DisplayId("p/model", null, " original ") == "original"
            && ModelOverviewCatalog.DisplayId("p/__direct-middle") == "p/__direct-middle", "copied IDs follow browser precedence and strip only the hidden suffix");
        using var invalidDocument = JsonDocument.Parse("""{"id":"p/bad","type":"language","context_window":"NaN","max_tokens":null,"pricing":{"input":-1,"output":"Infinity"},"tags":null} """);
        var invalid = AiModelCatalog.Project(invalidDocument.RootElement, ServiceKind.Ai);
        check(invalid.ContextWindow is null && invalid.MaxTokens is null && invalid.InputPrice is null && invalid.OutputPrice is null
            && invalid.Tags.Count == 0, "invalid/missing numeric model fields never invent zero values or break projection");
        var second = first with { Id = "anthropic/second", ProviderKey = "anthropic", DisplayId = null, Created = 100,
            ContextWindow = 8000, Tags = ["vision"], InputPrice = 0, OutputPrice = 0 };
        var image = first with { Id = "openai/image", ModelType = "image", Tags = ["vision"], Created = 300 };
        var catalog = new ModelOverviewCatalog([first, second, image, invalid]);
        var filter = new ModelOverviewFilter { Type = "language" };
        var result = catalog.Filter(filter);
        check(result.Models.Select(m => m.Id).SequenceEqual([first.Id, second.Id, invalid.Id]) && result.TypeCounts["image"] == 1,
            "overview is newest-first with per-type counts independent of the selected tab");
        check(catalog.Providers.Single(p => p.Key == "openai").Count == 2 && catalog.Providers.Single(p => p.Key == "openai").Name == "OpenAI",
            "provider options count the complete catalog and use browser display names");
        filter.Search = "OPENAI research reasoning";
        check(catalog.Filter(filter).Models.Single().Id == first.Id, "search ANDs all words across provider, owner, description and tags case-insensitively");
        filter.Search = "visible-id";
        check(catalog.Filter(filter).Models.Single().Id == first.Id, "overview search includes the copied display ID");
        filter.Search = "not-found";
        check(catalog.Filter(filter).Models.Count == 0, "no results is a real empty selection");
        filter.Search = ""; filter.Providers.Add("anthropic");
        check(catalog.Filter(filter).Models.Single().Id == second.Id, "named-provider filtering selects provider identity, not model owner/name");
        filter.Providers.Add("openai");
        check(catalog.Filter(filter).Models.Count == 2, "multiple providers are ORed");
        filter.Providers.Clear(); filter.Tags.Add("reasoning");
        result = catalog.Filter(filter);
        check(result.Models.Single().Id == first.Id && result.TagCounts["vision"] == 1 && result.AllTagCount == 3,
            "tag facets count other available tags while omitting their own selection");
        filter.Tags.Add("vision");
        check(catalog.Filter(filter).Models.Count == 2, "tag multi-selection is ORed");
        filter.Tags.Clear(); filter.ContextEnabled = true; filter.ContextMin = 9000;
        check(catalog.Filter(filter).Models.Single().Id == first.Id, "enabled context filter excludes missing values and applies minimum");
        filter.ContextMin = 999999; filter.ContextMax = 0;
        check(catalog.Filter(filter).Models.Count == 2, "context range is clamped to the active type and reversed ends are normalized");
        filter.ContextEnabled = false; filter.PriceEnabled = true; filter.InputMax = 0; filter.OutputMax = 0;
        check(catalog.Filter(filter).Models.Single().Id == second.Id, "zero pricing is valid and price filtering excludes unknown/negative values");
        filter.PriceEnabled = false;
        check(catalog.Filter(filter).Models.Count == 3, "disabling ranges includes models with missing metadata again");
        var empty = new ModelOverviewCatalog([]).Filter(filter);
        check(empty.Models.Count == 0 && empty.TypeCounts.Count == 0 && !empty.Context.HasValues, "empty catalogs and absent numeric ranges are safe");
        check(new ModelNumericRange(4, 4, true).Contains(4) && !new ModelNumericRange(0, 0, false).Contains(0), "constant ranges differ from absent ranges");
        var many = new ModelOverviewCatalog(Enumerable.Range(0, 153059).Select(i => new ChatTarget("p/" + i, "Model " + i) { ModelType = "language", Created = i }));
        filter = new() { Type = "language", Search = "Model 153058" };
        check(many.Filter(filter).Models.Single().Id == "p/153058", "large catalogs search all models before paging");
        filter.Search = "";
        check(many.Filter(filter).Models.Take(50).Count() == 50 && many.Filter(filter).Models.First().Id == "p/153058", "card batches preserve global newest-first order");
        foreach (var type in AiModelCatalog.Types)
            check(ModelOverviewCatalog.CanLaunch(first with { ModelType = type }) == (type is "language" or "image" or "video" or "transcription"), "native launch availability: " + type);
        var now = DateTimeOffset.FromUnixTimeSeconds(4_000_000);
        check(ModelOverviewCatalog.IsNew(first with { Created = now.AddDays(-30).ToUnixTimeSeconds() }, now)
            && !ModelOverviewCatalog.IsNew(first with { Created = now.AddDays(-31).ToUnixTimeSeconds() }, now)
            && !ModelOverviewCatalog.IsNew(first with { Created = null }, now), "New badge follows browser 30-day boundary");
        check(ModelProviders.Get("openai").Homepage == "https://openai.com" && ModelProviders.IconUri(ModelProviders.Get("openai"), true)!.AbsoluteUri.Contains("/dark/")
            && ModelProviders.IconUri(ModelProviders.Get("openai"), false)!.AbsoluteUri.Contains("/light/") && ModelProviders.Get("future-provider").Name == "future-provider",
            "provider display catalog supports theme-aware icons, homepage and unknown-provider fallback");
        check(ModelProviders.SafeWebUri("https://example.com/") is not null && ModelProviders.SafeWebUri("file:///C:/secret") is null
            && ModelProviders.SafeWebUri("https://user:secret@example.com") is null && ModelProviders.SafeWebUri("https://localhost") is null,
            "provider actions cannot launch local files, credentials or loopback URLs");
        var key = ModelOverviewCatalog.FavoriteKey(first);
        check(key != ModelOverviewCatalog.FavoriteKey(second) && key != ModelOverviewCatalog.FavoriteKey(first with { ModelType = "image" })
            && key != ModelOverviewCatalog.FavoriteKey(first with { Id = "visible-id" }), "favorites use type and exact backend identity, never display IDs or labels");
        var store = new CatalogFavoritesStore(Path.Combine(root, "model-favorites"));
        var partition = HistoryStore.Partition("profile", "identity", "endpoint");
        await store.SaveAsync(partition, [key, key]);
        check((await store.LoadAsync(partition)).SetEquals([key]) && (await store.LoadAsync(HistoryStore.Partition("profile", "other", "endpoint"))).Count == 0
            && (await store.LoadAsync(HistoryStore.Partition("profile", "identity", "other-endpoint"))).Count == 0,
            "model favorites persist, deduplicate and isolate account/endpoint partitions");
        await store.SaveAsync(partition, []);
        check((await store.LoadAsync(partition)).Count == 0, "removing model favorites persists");
        foreach (var language in new[] { "en", "nl" })
        {
            using var stream = typeof(ModelsOverviewRegressionTests).Assembly.GetManifestResourceStream("Desktop.Resources." + language)!;
            var resources = XDocument.Load(stream).Root!.Elements("data").ToArray();
            check(resources.GroupBy(e => (string?)e.Attribute("name")).All(g => g.Count() == 1), "resource keys remain unique: " + language);
            foreach (var name in new[] { "ArtificialIntelligence", "ModelsDescription", "SearchModels", "RefreshModels", "NoModels", "Providers", "Filters", "CloseFilters", "ContextWindow", "ModelsPricePerMillion", "Min", "Max", "Tags", "ModelsNoRange", "ModelsContextBadge", "ModelsOutputBadge", "New", "Copy", "ModelsLaunch", "ModelsProviderWebsite", "ModelsWebsiteFailed", "ModelsUnavailable" })
                check(resources.Any(e => (string?)e.Attribute("name") == name && !string.IsNullOrWhiteSpace(e.Element("value")?.Value)), "overview localization: " + language + "/" + name);
        }
    }
}
