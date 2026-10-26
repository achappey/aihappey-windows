namespace AIHappey.Desktop.Core;

/// <summary>Shared normalized metadata for every agent overview tab. Skills do not use this projection.</summary>
public static class AgentOverviewProjection
{
    public static CatalogItem Normalize(CatalogItem item) => item with
    {
        Tags = DesktopAgentMetadata.NormalizeTags(item.Tags.Concat(DesktopAgentMetadata.Tags(item.Definition))),
        Owner = string.IsNullOrWhiteSpace(item.Owner) ? DesktopAgentMetadata.Owner(item.Definition) : item.Owner.Trim()
    };

    public static bool Matches(CatalogItem item, IReadOnlySet<string> tags, IReadOnlySet<string> owners) =>
        (tags.Count == 0 || item.Tags.Any(tags.Contains)) && (owners.Count == 0 || owners.Contains(item.Owner ?? ""));

    public static IReadOnlyList<CatalogItem> Search(IEnumerable<CatalogItem> items, string query) => items
        .Where(item => string.IsNullOrWhiteSpace(query) || string.Join(" ", item.Name, item.Description, item.Model, item.Id,
            item.Owner, string.Join(" ", item.Tags)).Contains(query.Trim(), StringComparison.OrdinalIgnoreCase))
        .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ThenBy(item => item.Id, StringComparer.Ordinal).ToArray();

    public static string TabKey(bool byTags, string value) => (byTags ? "tag:" : "owner:") + value;

    public static bool MatchesTab(CatalogItem item, string key) => key.StartsWith("tag:", StringComparison.Ordinal)
        ? item.Tags.Contains(key[4..]) : !key.StartsWith("owner:", StringComparison.Ordinal) || item.Owner == key[6..];
}
