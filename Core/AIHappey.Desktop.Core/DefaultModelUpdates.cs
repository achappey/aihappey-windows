namespace AIHappey.Desktop.Core;

/// <summary>Offers configured lists, not arbitrary catalog upgrades. Dates are Unix seconds.</summary>
public static class DefaultModelUpdates
{
    public static IReadOnlyDictionary<string, string[]> Find(AiModelPreferences preferences,
        IReadOnlyDictionary<string, string[]>? defaults, IEnumerable<ChatTarget> models, DateTimeOffset now)
    {
        var updates = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var catalog = models.ToArray();
        var nowSeconds = now.ToUnixTimeMilliseconds() / 1000d;
        bool ValidDate(ChatTarget? model) => model?.Created is > 0 and var created && created <= nowSeconds;
        foreach (var type in AiModelPreferences.MultiModelTypes)
        {
            var saved = AiModelPreferences.Normalize(preferences.DefaultsFor(type) ?? []);
            if (saved.Length == 0 || defaults?.TryGetValue(type, out var ids) != true || ids is null) continue;
            var configured = AiModelPreferences.Normalize(ids);
            if (configured.Length == 0) continue;
            var replacements = configured.Select(id => catalog.FirstOrDefault(model => model.Id == id && model.ModelType == type)).ToArray();
            // The entire list is applied; never introduce unresolved, wrong-type or undated models.
            if (!replacements.All(ValidDate)) continue;
            var oldModels = saved.Select(id => catalog.FirstOrDefault(model => model.Id == id && model.ModelType == type))
                .Where(model => ValidDate(model) && nowSeconds - model!.Created!.Value > TimeSpan.FromDays(30).TotalSeconds);
            if (oldModels.Any(old => replacements.Any(model => !saved.Contains(model!.Id, StringComparer.Ordinal) && model.Created > old!.Created)))
                updates[type] = configured;
        }
        return updates;
    }
}
