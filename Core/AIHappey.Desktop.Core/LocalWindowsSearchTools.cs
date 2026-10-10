using System.Text.Json;

namespace AIHappey.Desktop.Core;

public sealed record LocalWindowsSearchRequest(string Query, string? Folder, int Limit);
public sealed record LocalWindowsSearchItem(string Path, string Name, long? Size, DateTime? ModifiedAt, string? ItemType, int? Rank);
public sealed record LocalWindowsSearchResults(IReadOnlyList<LocalWindowsSearchItem> Results, bool HasMore);

public interface ILocalWindowsSearch
{
    Task<LocalWindowsSearchResults> SearchAsync(LocalWindowsSearchRequest request, CancellationToken ct);
}

/// <summary>One AQS interface; filename/property/content searches share the same native index.</summary>
public sealed class LocalWindowsSearchTools(ILocalWindowsSearch? search = null, Func<bool>? isCurrent = null)
{
    public const string ToolName = "local_windows_search";
    private readonly ILocalWindowsSearch search = search ?? new NativeWindowsSearch();
    private void Check(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (isCurrent?.Invoke() == false) throw new OperationCanceledException(ct);
    }

    public Task<JsonElement> CallAsync(string name, JsonElement input, CancellationToken ct) => DesktopLocalTools.SafeAsync(async () =>
    {
        Check(ct);
        if (name != ToolName) throw new LocalToolException("Unsupported Windows Search tool.");
        var rawQuery = DesktopLocalTools.Required(input, "query");
        var query = rawQuery.Trim();
        if (rawQuery.Length > 4096 || query.Length == 0 || rawQuery.Any(char.IsControl)) throw new LocalToolException(DesktopResources.Get("LocalSearchInvalidQuery"));
        var folder = input.TryGetProperty("folder", out _) ? LocalFilePaths.Normalize(DesktopLocalTools.Required(input, "folder")) : null;
        if (folder is not null)
        {
            LocalFilePaths.CheckNoLinks(folder);
            if (!Directory.Exists(folder)) throw new LocalToolException(DesktopResources.Get("LocalSearchFolderNotFound"));
        }
        var limit = DesktopLocalTools.Integer(input, "limit", 20, 100);
        var result = await search.SearchAsync(new(query, folder, limit), ct); Check(ct);
        // Bound even injected adapters. Index totals are not claimed: more indexed rows
        // may be excluded by the local-only policy, and the index can change during a call.
        var items = new List<LocalWindowsSearchItem>(); var characters = 0; var responseLimited = false;
        foreach (var item in result.Results)
        {
            Check(ct);
            if (items.Count == limit) break;
            try
            {
                var path = LocalFilePaths.Normalize(item.Path);
                if (folder is not null && !LocalFilePaths.IsWithin(path, folder)) continue;
                var filename = Path.GetFileName(path); var type = item.ItemType is { Length: <= 256 } value ? value : null;
                if (characters + path.Length + filename.Length + (type?.Length ?? 0) > 80_000) { responseLimited = true; break; }
                characters += path.Length + filename.Length + (type?.Length ?? 0);
                items.Add(item with { Path = path, Name = filename, ItemType = type });
            }
            catch (LocalToolException) { }
        }
        return DesktopLocalTools.Result(new { query, folder, limit, count = items.Count,
            hasMore = result.HasMore || result.Results.Count > limit || responseLimited, indexedOnly = true, results = items });
    }, ct);
}
