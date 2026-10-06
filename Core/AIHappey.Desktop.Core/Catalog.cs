using System.Text.Json;

namespace AIHappey.Desktop.Core;

public enum CatalogKind { Agent, Skill }
// Origin is not RuntimeLocation: even a managed loopback service supplies backend items.
public enum CatalogOrigin { Backend, Local }

public sealed record CatalogItem(CatalogKind Kind, string Id, string Name, string Description)
{
    public CatalogOrigin Origin { get; init; } = CatalogOrigin.Backend;
    public ServiceKind Service => Kind == CatalogKind.Agent ? ServiceKind.Agents : ServiceKind.Ai;
    public string Key => $"{Kind}:{Origin}:{Id}";
    public string? Model { get; init; }
    public string? Version { get; init; }
    public string? LatestVersion { get; init; }
    public long? Created { get; init; }
    public string? Owner { get; init; }
    public JsonElement? Definition { get; init; }
    public IReadOnlyList<CatalogIcon> Icons { get; init; } = [];
    public bool CanDownload => Kind == CatalogKind.Agent ? Definition.HasValue : CatalogRoutes.SupportsSkill(Id);
}

public sealed record CatalogIcon(string Source, string? Theme);
public sealed record CatalogVersion(string Id, string Version, long? Created, string? Name, string? Description);

public static class CatalogProjection
{
    public static CatalogItem? Agent(JsonElement value)
    {
        var id = Text(value, "id");
        if (string.IsNullOrWhiteSpace(id)) return null;
        JsonElement? definition = value.TryGetProperty("agent", out var agent) && agent.ValueKind == JsonValueKind.Object ? agent.Clone() : null;
        var model = definition is { } d && d.TryGetProperty("model", out var m) ? Text(m, "id") : null;
        var icons = new List<CatalogIcon>();
        if (definition is { } a && a.TryGetProperty("icons", out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var icon in list.EnumerateArray())
                if (Text(icon, "src") is { } src) icons.Add(new(src, Text(icon, "theme")));
        return new(CatalogKind.Agent, id, Text(value, "name") ?? (definition is { } n ? Text(n, "name") : null) ?? id,
            Text(value, "description") ?? (definition is { } desc ? Text(desc, "description") : null) ?? "")
        {
            Model = model, Definition = definition, Owner = Text(value, "owned_by"), Created = Number(value, "created"), Icons = icons
        };
    }

    public static CatalogItem? Skill(JsonElement value)
    {
        var id = Text(value, "id");
        return string.IsNullOrWhiteSpace(id) ? null : new(CatalogKind.Skill, id, Text(value, "name") ?? id, Text(value, "description") ?? "")
        { Version = Text(value, "default_version"), LatestVersion = Text(value, "latest_version"), Created = Number(value, "created_at") };
    }

    public static CatalogVersion? Version(JsonElement value)
    {
        var id = Text(value, "id");
        var version = Text(value, "version");
        return string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(version) ? null
            : new(id, version, Number(value, "created_at"), Text(value, "name"), Text(value, "description"));
    }

    public static string? Text(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(property.GetString()) ? property.GetString() : null;
    private static long? Number(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var number) ? number : null;

    public static IReadOnlyList<CatalogItem> Search(IEnumerable<CatalogItem> items, string query) => items
        .Where(item => string.IsNullOrWhiteSpace(query) || string.Join(" ", item.Name, item.Description, item.Model, item.Version, item.Id)
            .Contains(query.Trim(), StringComparison.OrdinalIgnoreCase))
        .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ThenBy(item => item.Id, StringComparer.Ordinal).ToArray();
}

public static class CatalogRoutes
{
    public static bool SupportsSkill(string id) => id.Split('/') is { Length: 2 } parts && parts.All(ValidSegment);
    public static string Skill(string id)
    {
        if (!SupportsSkill(id)) throw new InvalidOperationException("This skill identifier does not support gateway content or version requests.");
        return "v1/skills/" + string.Join("/", id.Split('/').Select(Uri.EscapeDataString));
    }
    public static string Version(string version)
    {
        if (!ValidSegment(version)) throw new InvalidOperationException("The service returned an unsupported skill version.");
        return Uri.EscapeDataString(version);
    }
    private static bool ValidSegment(string value) => !string.IsNullOrWhiteSpace(value) && value is not "." and not ".."
        && !value.Any(character => char.IsControl(character) || character is '/' or '\\' or '%');
}

/// <summary>Future local stores can implement this read boundary without changing overview controls.
/// Creation/editing/import and local execution require their own capabilities; none are enabled here.</summary>
public interface IDesktopCatalogSource
{
    Task<IReadOnlyList<CatalogItem>> ListAsync(CatalogKind kind, CancellationToken ct);
}
