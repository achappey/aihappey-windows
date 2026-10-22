namespace AIHappey.Desktop.Core;

/// <summary>Provider visibility is a UI preference, not authorization or a runtime allow-list.</summary>
public static class ProviderPreferences
{
    private static readonly Lazy<Dictionary<string, string>> names = new(() =>
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in ProviderCatalog.All.Values)
        {
            result[Lookup(provider.Id)] = provider.Name;
            result[Lookup(provider.Name)] = provider.Name;
        }
        return result;
    });
    private static string Lookup(string value) => new(value.Where(char.IsLetterOrDigit).ToArray());
    public static string Name(string value)
    {
        value = value.Trim();
        return names.Value.GetValueOrDefault(Lookup(value)) ?? value;
    }
    public static string Type(string value) => value.Trim().ToLowerInvariant() is var type && type == "realtime" ? "audio" : type;
    public static Dictionary<string, string[]> Normalize(IReadOnlyDictionary<string, string[]>? values)
    {
        var result = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var (key, providers) in values ?? new Dictionary<string, string[]>())
        {
            var type = Type(key);
            if (!AiModelCatalog.Types.Contains(type) || key == "realtime" && values!.ContainsKey("audio")) continue;
            result[type] = (providers ?? []).Where(p => !string.IsNullOrWhiteSpace(p)).Select(Name)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }
        return result;
    }
    public static bool SeedDefaults(DesktopSettings settings, IReadOnlyDictionary<string, string[]>? defaults)
    {
        var changed = false;
        foreach (var (type, providers) in Normalize(defaults))
            if (settings.EnabledProvidersByType.TryAdd(type, providers.ToArray())) changed = true;
        return changed;
    }
    public static bool IsEnabled(DesktopSettings settings, string type, string provider) =>
        settings.EnabledProvidersByType.TryGetValue(Type(type), out var providers)
        && providers.Contains(Name(provider), StringComparer.OrdinalIgnoreCase);
    public static void SetEnabled(DesktopSettings settings, string type, string provider, bool enabled)
    {
        type = Type(type); provider = Name(provider);
        if (!AiModelCatalog.Types.Contains(type) || provider.Length == 0) return;
        var current = settings.EnabledProvidersByType.GetValueOrDefault(type) ?? [];
        settings.EnabledProvidersByType[type] = enabled ? current.Append(provider).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
            : current.Where(p => !p.Equals(provider, StringComparison.OrdinalIgnoreCase)).ToArray();
    }
    public static IReadOnlyList<ChatTarget> Filter(IEnumerable<ChatTarget> models, DesktopSettings settings,
        IReadOnlySet<string>? favorites = null) => models.Where(model =>
            IsEnabled(settings, model.ModelType ?? AiModelCatalog.ResolveType(model.Id, null), ModelOverviewCatalog.ProviderKey(model))
            || favorites?.Contains(ModelOverviewCatalog.FavoriteKey(model)) == true).ToArray();
    public static IReadOnlyDictionary<string, string[]> Groups(IEnumerable<ChatTarget> models) => models
        .Where(m => AiModelCatalog.Types.Contains(m.ModelType ?? ""))
        .GroupBy(m => m.ModelType!, StringComparer.Ordinal)
        .ToDictionary(g => g.Key, g => g.Select(m => Name(ModelOverviewCatalog.ProviderKey(m)))
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.CurrentCultureIgnoreCase).ToArray(), StringComparer.Ordinal);

    /// <summary>Only actual edits to recognized key headers change enablement; unrelated headers are untouched.</summary>
    public static IReadOnlyDictionary<string, bool> CredentialChanges(IReadOnlyDictionary<string, string> previous,
        IReadOnlyDictionary<string, string> next)
    {
        var before = new Dictionary<string, string>(previous, StringComparer.OrdinalIgnoreCase);
        var after = new Dictionary<string, string>(next, StringComparer.OrdinalIgnoreCase);
        var changes = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var provider in ProviderCatalog.All.Values)
        {
            if (ProviderCatalog.ApiKeyHeader(provider) is not { } header) continue;
            before.TryGetValue(header, out var oldKey); after.TryGetValue(header, out var newKey);
            if (oldKey != newKey) changes[provider.Name] = !string.IsNullOrWhiteSpace(newKey);
        }
        return changes;
    }
    public static void ApplyCredentialChanges(DesktopSettings settings, IReadOnlyDictionary<string, bool> changes)
    {
        foreach (var (provider, enabled) in changes)
            foreach (var type in AiModelCatalog.Types) SetEnabled(settings, type, provider, enabled);
    }
}
