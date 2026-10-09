using System.Globalization;
using System.Text.Json;

namespace AIHappey.Desktop.Core;

/// <summary>Browser-compatible filters, independent of WinUI. The search index is built once per catalog.</summary>
public sealed class ModelOverviewCatalog
{
    private sealed record Entry(ChatTarget Model, string Provider, HashSet<string> Tags, string Search);
    private readonly Entry[] entries;
    public IReadOnlyList<ChatTarget> Models { get; }

    public ModelOverviewCatalog(IEnumerable<ChatTarget> models)
    {
        Models = AiModelCatalog.NewestFirst(models);
        entries = Models.Select(m => new Entry(m, ProviderKey(m), new(m.Tags, StringComparer.Ordinal), string.Join(" ",
            new[] { m.Label, DisplayId(m), m.Description, m.OwnedBy, ProviderKey(m), ModelProviders.Get(ProviderKey(m)).Name,
                string.Join(" ", m.Tags) }).ToLowerInvariant())).ToArray();
    }

    public static string DisplayId(ChatTarget model) => DisplayId(model.Id, model.DisplayId, model.ProviderModelId);
    public static string DisplayId(string id, string? displayId = null, string? providerModelId = null) =>
        !string.IsNullOrWhiteSpace(displayId) ? displayId.Trim() : !string.IsNullOrWhiteSpace(providerModelId) ? providerModelId.Trim()
            : id.Trim().EndsWith("__direct", StringComparison.Ordinal) ? id.Trim()[..^8] : id.Trim();
    public static string ProviderKey(ChatTarget model) => !string.IsNullOrWhiteSpace(model.ProviderKey)
        ? model.ProviderKey.Trim().ToLowerInvariant() : model.Id.Split('/')[0].Trim().ToLowerInvariant();
    // A serialized tuple cannot collide when either the type or ID contains delimiters.
    public static string FavoriteKey(ChatTarget model) => JsonSerializer.Serialize(new[] { model.ModelType ?? "language", model.Id });
    public static bool CanLaunch(ChatTarget model) => model.ModelType is "language" or "image" or "transcription";
    public static bool IsNew(ChatTarget model, DateTimeOffset now) => model.Created is { } created
        && now.ToUnixTimeSeconds() - (double)created <= TimeSpan.FromDays(30).TotalSeconds;

    public ModelOverviewResult Filter(ModelOverviewFilter filter)
    {
        var active = entries.Where(e => e.Model.ModelType == filter.Type).ToArray();
        var context = ModelNumericRange.Create(active.Select(e => e.Model.ContextWindow));
        var input = ModelNumericRange.Create(active.Select(e => e.Model.InputPrice));
        var output = ModelNumericRange.Create(active.Select(e => e.Model.OutputPrice));
        var selectedContext = context.Selection(filter.ContextMin, filter.ContextMax);
        var selectedInput = input.Selection(filter.InputMin, filter.InputMax);
        var selectedOutput = output.Selection(filter.OutputMin, filter.OutputMax);
        var terms = filter.Search.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        bool Matches(Entry entry, bool omitTags = false) => terms.All(entry.Search.Contains)
            && (filter.Providers.Count == 0 || filter.Providers.Contains(entry.Provider))
            && (!filter.ContextEnabled || selectedContext.Contains(entry.Model.ContextWindow))
            && (!filter.PriceEnabled || selectedInput.Contains(entry.Model.InputPrice) && selectedOutput.Contains(entry.Model.OutputPrice))
            && (omitTags || filter.Tags.Count == 0 || entry.Tags.Overlaps(filter.Tags));
        var matched = entries.Where(e => Matches(e)).ToArray();
        var facets = active.Where(e => Matches(e, true)).ToArray();
        var tags = active.SelectMany(e => e.Tags).Distinct(StringComparer.Ordinal).Order(StringComparer.CurrentCultureIgnoreCase)
            .ToDictionary(tag => tag, tag => facets.Count(e => e.Tags.Contains(tag)), StringComparer.Ordinal);
        return new(matched.Where(e => e.Model.ModelType == filter.Type).Select(e => e.Model).ToArray(),
            matched.GroupBy(e => e.Model.ModelType ?? "language").ToDictionary(g => g.Key, g => g.Count()), tags,
            facets.Length, context, input, output);
    }

    public IReadOnlyList<ModelProviderCount> Providers => entries.GroupBy(e => e.Provider)
        .Select(g => new ModelProviderCount(g.Key, ModelProviders.Get(g.Key).Name, g.Count()))
        .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();

    public static string CompactNumber(double value) => value >= 1_000_000 ? (value / 1_000_000).ToString("0.#", CultureInfo.CurrentCulture) + "M"
        : value >= 1_000 ? (value / 1_000).ToString("0.#", CultureInfo.CurrentCulture) + "K" : value.ToString("0", CultureInfo.CurrentCulture);
}

public sealed class ModelOverviewFilter
{
    public string Type { get; set; } = "";
    public string Search { get; set; } = "";
    // Empty selections mean All. Provider keys are normalized; tag comparison remains case-sensitive as in the browser.
    public HashSet<string> Providers { get; } = new(StringComparer.Ordinal);
    public HashSet<string> Tags { get; } = new(StringComparer.Ordinal);
    public bool ContextEnabled { get; set; }
    public bool PriceEnabled { get; set; }
    public double? ContextMin { get; set; }
    public double? ContextMax { get; set; }
    public double? InputMin { get; set; }
    public double? InputMax { get; set; }
    public double? OutputMin { get; set; }
    public double? OutputMax { get; set; }
}

public sealed record ModelProviderCount(string Key, string Name, int Count);
public sealed record ModelOverviewResult(IReadOnlyList<ChatTarget> Models, IReadOnlyDictionary<string, int> TypeCounts,
    IReadOnlyDictionary<string, int> TagCounts, int AllTagCount, ModelNumericRange Context, ModelNumericRange Input, ModelNumericRange Output);

public sealed record ModelNumericRange(double Min, double Max, bool HasValues)
{
    public static ModelNumericRange Create(IEnumerable<double?> values)
    {
        var finite = values.Where(v => v.HasValue && double.IsFinite(v.Value)).Select(v => v!.Value).ToArray();
        return finite.Length == 0 ? new(0, 0, false) : new(finite.Min(), finite.Max(), true);
    }
    public ModelNumericRange Selection(double? min, double? max)
    {
        var a = Math.Clamp(min is { } lower && double.IsFinite(lower) ? lower : Min, Min, Max);
        var b = Math.Clamp(max is { } upper && double.IsFinite(upper) ? upper : Max, Min, Max);
        return new(Math.Min(a, b), Math.Max(a, b), HasValues);
    }
    public bool Contains(double? value) => HasValues && value is { } number && double.IsFinite(number) && number >= Min && number <= Max;
}
