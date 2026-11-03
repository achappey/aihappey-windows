using System.Text.Json;

namespace AIHappey.Desktop.Core;

public sealed class DesktopCatalogClient(DesktopChatClient requests, HttpClient http) : IDesktopCatalogSource
{
    public const int MaxDownloadBytes = 64 * 1024 * 1024;
    private const int MaxCatalogBytes = 8 * 1024 * 1024;
    private const int MaxAgentCacheBytes = 32 * 1024 * 1024;
    private readonly object agentDetailsGate = new();
    private readonly Dictionary<string, AgentDetailEntry> agentDetails = new(StringComparer.Ordinal);
    private string agentDetailScope = "";
    private long agentDetailGeneration;
    private sealed class AgentDetailEntry
    {
        public required Lazy<Task<JsonElement>> Value { get; init; }
        public DateTimeOffset Expires { get; } = DateTimeOffset.UtcNow.AddMinutes(5);
        public int Bytes { get; set; }
    }

    public async Task<IReadOnlyList<CatalogItem>> ListAsync(CatalogKind kind, CancellationToken ct)
    {
        if (kind == CatalogKind.Agent)
        {
            lock (agentDetailsGate) { agentDetails.Clear(); agentDetailGeneration++; }
            using var doc = await ReadAsync(ServiceKind.Agents, "v1/models", ct);
            return Data(doc.RootElement).EnumerateArray().Select(CatalogProjection.Agent).OfType<CatalogItem>()
                .DistinctBy(item => item.Key).OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        return await PagesAsync("v1/skills", CatalogProjection.Skill, item => item.Id, ct);
    }

    public Task<IReadOnlyList<CatalogVersion>> VersionsAsync(string skillId, CancellationToken ct) =>
        PagesAsync(CatalogRoutes.Skill(skillId) + "/versions", CatalogProjection.Version, item => item.Id, ct);

    public async Task<JsonElement> AgentDefinitionAsync(string id, CancellationToken ct, bool refresh = false)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new GatewayException(DesktopResources.Get("AgentUnavailable"));
        var partition = requests.AgentCatalogScope;
        var request = await requests.RequestAsync(ServiceKind.Agents, HttpMethod.Get, "v1/models/" + Uri.EscapeDataString(id), ct);
        if (partition != requests.AgentCatalogScope) { request.Dispose(); throw new OperationCanceledException(ct); }
        var endpointPath = request.RequestUri!.AbsolutePath;
        var modelsStart = endpointPath.IndexOf("/v1/models/", StringComparison.Ordinal);
        var scope = McpValidation.Hash(partition + "|" + request.RequestUri.GetLeftPart(UriPartial.Authority)
            + "|" + (modelsStart < 0 ? endpointPath : endpointPath[..modelsStart]) + "|" + request.Headers);
        AgentDetailEntry entry;
        long generation;
        lock (agentDetailsGate)
        {
            if (agentDetailScope != scope)
            {
                agentDetails.Clear(); agentDetailScope = scope; agentDetailGeneration++;
            }
            generation = agentDetailGeneration;
            if (agentDetails.TryGetValue(id, out var cached) && cached.Expires > DateTimeOffset.UtcNow && (!refresh || cached.Bytes == 0))
            { request.Dispose(); entry = cached; }
            else
            {
                AgentDetailEntry? created = null;
                created = new() { Value = new(() => FetchAgentDefinitionAsync(request, id, created!, generation)) };
                entry = created; agentDetails[id] = entry;
            }
        }
        var value = await entry.Value.Value.WaitAsync(ct);
        ct.ThrowIfCancellationRequested();
        if (partition != requests.AgentCatalogScope) throw new OperationCanceledException(ct);
        lock (agentDetailsGate) if (generation != agentDetailGeneration) throw new OperationCanceledException(ct);
        return value.Clone();
    }

    private async Task<JsonElement> FetchAgentDefinitionAsync(HttpRequestMessage request, string id, AgentDetailEntry entry, long generation)
    {
        using (request)
        using (var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2)))
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            await GatewayErrors.CheckResponseAsync(response, timeout.Token);
            var bytes = await ReadBoundedAsync(response.Content, DesktopAgent.MaxDocumentBytes, timeout.Token);
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            if (!string.Equals(CatalogProjection.Text(root, "id"), id, StringComparison.OrdinalIgnoreCase)
                || !root.TryGetProperty("agent", out var agent) || agent.ValueKind != JsonValueKind.Object
                || CatalogProjection.Text(agent, "name") is null || !agent.TryGetProperty("instructions", out var instructions)
                || instructions.ValueKind != JsonValueKind.String)
                throw new GatewayException(DesktopResources.Get("InvalidCatalogDocument"));
            lock (agentDetailsGate)
            {
                entry.Bytes = bytes.Length;
                if (generation == agentDetailGeneration)
                {
                    if (bytes.Length > MaxAgentCacheBytes) agentDetails.Remove(id);
                    var size = agentDetails.Values.Sum(item => (long)item.Bytes);
                    foreach (var key in agentDetails.Keys.ToArray())
                    {
                        if (size <= MaxAgentCacheBytes && agentDetails.Count <= 32) break;
                        if (key != id && agentDetails[key].Bytes > 0) { size -= agentDetails[key].Bytes; agentDetails.Remove(key); }
                    }
                }
            }
            return agent.Clone();
        }
        catch (Exception error)
        {
            lock (agentDetailsGate)
                if (agentDetails.TryGetValue(id, out var current) && ReferenceEquals(current, entry)) agentDetails.Remove(id);
            if (error is JsonException) throw new GatewayException(DesktopResources.Get("InvalidCatalogDocument"));
            throw;
        }
    }

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
                throw new GatewayException(DesktopResources.Get("InvalidCatalogContinuation"));
            cursor = next;
        }
        throw new GatewayException(DesktopResources.Get("CatalogPageLimit"));
    }

    public async Task<byte[]> DownloadSkillAsync(string skillId, string? version, CancellationToken ct)
    {
        var path = CatalogRoutes.Skill(skillId) + (version is null ? "/content" : "/versions/" + CatalogRoutes.Version(version) + "/content");
        using var request = await requests.RequestAsync(ServiceKind.Ai, HttpMethod.Get, path, ct);
        request.Headers.Accept.ParseAdd("application/zip");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        DesktopChatClient.CheckResponse(response);
        if (response.Content.Headers.ContentType?.MediaType != "application/zip") throw new GatewayException(DesktopResources.Get("SkillZipRequired"));
        var bytes = await ReadBoundedAsync(response.Content, MaxDownloadBytes, ct);
        if (bytes.Length < 4 || bytes[0] != 'P' || bytes[1] != 'K'
            || !((bytes[2] == 3 && bytes[3] == 4) || (bytes[2] == 5 && bytes[3] == 6)))
            throw new GatewayException(DesktopResources.Get("InvalidSkillArchive"));
        return bytes;
    }

    private async Task<JsonDocument> ReadAsync(ServiceKind service, string path, CancellationToken ct)
    {
        using var request = await requests.RequestAsync(service, HttpMethod.Get, path, ct);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        DesktopChatClient.CheckResponse(response);
        try { return JsonDocument.Parse(await ReadBoundedAsync(response.Content, MaxCatalogBytes, ct)); }
        catch (JsonException) { throw new GatewayException(DesktopResources.Get("InvalidCatalogDocument")); }
    }

    private static JsonElement Data(JsonElement root) => root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array ? data
        : throw new GatewayException(DesktopResources.Get("InvalidCatalogList"));

    internal static async Task<byte[]> ReadBoundedAsync(HttpContent content, int limit, CancellationToken ct)
    {
        if (content.Headers.ContentLength > limit) throw new GatewayException(DesktopResources.Get("ResponseSizeLimit"));
        await using var stream = await content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) != 0)
        {
            if (buffer.Length + read > limit) throw new GatewayException(DesktopResources.Get("ResponseSizeLimit"));
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }
}
