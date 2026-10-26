using System.Text.Json;
using System.Text.Json.Nodes;

namespace AIHappey.Desktop.Core;

/// <summary>Agent artwork uses the provider catalog's icon contract and renderer, never its homepage favicon.</summary>
public static class DesktopAgentIcons
{
    public static IReadOnlyList<string> ModelIds(CatalogItem item)
    {
        if (item.Definition is { ValueKind: JsonValueKind.Object } definition)
        {
            var ids = definition.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array
                ? models.EnumerateArray().Select(model => CatalogProjection.Text(model, "id"))
                    .Where(id => id is not null).Cast<string>().Distinct(StringComparer.Ordinal).ToArray() : [];
            if (ids.Length > 0) return ids;
            if (definition.TryGetProperty("model", out var legacy) && CatalogProjection.Text(legacy, "id") is { } id) return [id];
        }
        return string.IsNullOrWhiteSpace(item.Model) ? [] : [item.Model];
    }

    public static CatalogProvider Artwork(CatalogItem item)
    {
        var custom = item.Icons.Where(icon => !string.IsNullOrWhiteSpace(icon.Source))
            .Select(icon => new ModelProviderIcon(icon.Source, string.IsNullOrEmpty(icon.Theme) ? null : icon.Theme)).ToArray();
        var icons = custom.Length > 0 ? custom : ModelIds(item)
            .Where(id => id.IndexOf('/') > 0)
            .Select(id => DesktopAgent.Provider(id).ToLowerInvariant()).Distinct(StringComparer.Ordinal)
            .SelectMany(id => ProviderCatalog.All.GetValueOrDefault(id)?.Icons ?? [])
            .Where(icon => !string.IsNullOrWhiteSpace(icon.Src)).Distinct().ToArray();
        return new() { Name = item.Name, Icons = icons };
    }

    public static bool Add(DesktopAgent agent, string source, string theme)
    {
        if (string.IsNullOrWhiteSpace(source)) return false;
        var icons = agent.Definition["icons"] as JsonArray;
        if (icons is null) agent.Definition["icons"] = icons = new JsonArray();
        var icon = new JsonObject { ["src"] = source.Trim() };
        SetTheme(icon, theme); icons.Add(icon); return true;
    }

    public static void SetTheme(JsonObject icon, string theme)
    {
        if (string.IsNullOrEmpty(theme)) icon.Remove("theme"); else icon["theme"] = theme;
    }

    public static void PrepareForSave(DesktopAgent agent)
    {
        if (agent.Definition["icons"] is not JsonArray icons) return;
        foreach (var icon in icons.OfType<JsonObject>().ToArray())
        {
            var source = DesktopAgent.Text(icon["src"]).Trim();
            if (source.Length == 0) icons.Remove(icon); else icon["src"] = source;
        }
        if (icons.Count == 0) agent.Definition.Remove("icons");
    }
}
