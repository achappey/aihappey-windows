using System.Text.Json;

namespace AIHappey.Desktop.Core;

public sealed class DesktopCatalogClient(DesktopChatClient requests, HttpClient http) : IDesktopCatalogSource
{
    public const int MaxDownloadBytes = 64 * 1024 * 1024;
    private const int MaxCatalogBytes = 8 * 1024 * 1024;

    public async Task<IReadOnlyList<CatalogItem>> ListAsync(CatalogKind kind, CancellationToken ct)
    {
        if (kind == CatalogKind.Agent)
        {
            using var doc = await ReadAsync(ServiceKind.Agents, "v1/models", ct);
            return Data(doc.RootElement).EnumerateArray().Select(CatalogProjection.Agent).OfType<CatalogItem>()
                .DistinctBy(item => item.Key).OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        return await PagesAsync("v1/skills", CatalogProjection.Skill, item => item.Id, ct);
    }

    public Task<IReadOnlyList<CatalogVersion>> VersionsAsync(string skillId, CancellationToken ct) =>
        PagesAsync(CatalogRoutes.Skill(skillId) + "/versions", CatalogProjection.Version, item => item.Id, ct);

    private async Task<IReadOnlyList<T>> PagesAsync<T>(string path, Func<JsonElement, T?> project, Func<T, string> key, CancellationToken ct) where T : class
    {
        var items = new List<T>();
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        for (var page = 0; page < 200; page++)
        {
            using var doc = await ReadAsync(ServiceKind.Ai, path + "?limit=100&order=asc" + (cursor is null ? "" : "&after=" + Uri.EscapeDataString(cursor)), ct);
            var root = doc.RootElement;
            items.AddRange(Data(root).EnumerateArray().Select(project).OfType<T>());
            if (!root.TryGetProperty("has_more", out var more) || more.ValueKind == JsonValueKind.False) return items.DistinctBy(key).ToArray();
            if (more.ValueKind != JsonValueKind.True || CatalogProjection.Text(root, "last_id") is not { } next || !cursors.Add(next))
                throw new GatewayException("The service returned an invalid catalog continuation.");
            cursor = next;
        }
        throw new GatewayException("The catalog exceeded the supported page limit.");
    }

    public async Task<byte[]> DownloadSkillAsync(string skillId, string? version, CancellationToken ct)
    {
        var path = CatalogRoutes.Skill(skillId) + (version is null ? "/content" : "/versions/" + CatalogRoutes.Version(version) + "/content");
        using var request = await requests.RequestAsync(ServiceKind.Ai, HttpMethod.Get, path, ct);
        request.Headers.Accept.ParseAdd("application/zip");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        DesktopChatClient.CheckResponse(response);
        if (response.Content.Headers.ContentType?.MediaType != "application/zip") throw new GatewayException("The service did not return a skill ZIP archive.");
        var bytes = await ReadBoundedAsync(response.Content, MaxDownloadBytes, ct);
        if (bytes.Length < 4 || bytes[0] != 'P' || bytes[1] != 'K'
            || !((bytes[2] == 3 && bytes[3] == 4) || (bytes[2] == 5 && bytes[3] == 6)))
            throw new GatewayException("The service returned invalid skill archive content.");
        return bytes;
    }

    private async Task<JsonDocument> ReadAsync(ServiceKind service, string path, CancellationToken ct)
    {
        using var request = await requests.RequestAsync(service, HttpMethod.Get, path, ct);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        DesktopChatClient.CheckResponse(response);
        try { return JsonDocument.Parse(await ReadBoundedAsync(response.Content, MaxCatalogBytes, ct)); }
        catch (JsonException) { throw new GatewayException("The service returned an invalid catalog document."); }
    }

    private static JsonElement Data(JsonElement root) => root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array ? data
        : throw new GatewayException("The service returned an invalid catalog list.");

    internal static async Task<byte[]> ReadBoundedAsync(HttpContent content, int limit, CancellationToken ct)
    {
        if (content.Headers.ContentLength > limit) throw new GatewayException("The service response exceeded the supported size.");
        await using var stream = await content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) != 0)
        {
            if (buffer.Length + read > limit) throw new GatewayException("The service response exceeded the supported size.");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }
}
