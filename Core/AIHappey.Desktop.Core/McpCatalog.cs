using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AIHappey.Desktop.Core;

public sealed record McpCatalogItem(string Id, string Name, string Description, string Url,
    string? Version = null, string? RegistryUrl = null);
public sealed record McpCatalogResult(IReadOnlyList<McpCatalogItem> Items, IReadOnlyList<string> FailedSources);

public static class McpValidation
{
    public static Uri Endpoint(string value)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
            || !(uri.Scheme == "https" || uri.Scheme == "http" && uri.IsLoopback)
            || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment)
            || !string.IsNullOrEmpty(uri.Query))
            throw new InvalidOperationException(DesktopResources.Get("McpInvalidUrl"));
        return uri;
    }

    public static Dictionary<string, string> Headers(IEnumerable<KeyValuePair<string, string>> values)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in values)
        {
            // Credentials are allowed only here; transport/session headers remain SDK-owned.
            if (string.IsNullOrWhiteSpace(name) || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_')
                || value.Any(char.IsControl) || name.StartsWith("Mcp-", StringComparison.OrdinalIgnoreCase)
                || new[] { "Host", "Content-Length", "Content-Type", "Accept", "Connection", "Transfer-Encoding", "Cookie" }
                    .Contains(name, StringComparer.OrdinalIgnoreCase) || value.Length > 16384 || headers.Count >= 32)
                throw new InvalidOperationException(DesktopResources.Get("McpInvalidHeaders"));
            if (!headers.TryAdd(name, value.Trim())) throw new InvalidOperationException(DesktopResources.Get("McpInvalidHeaders"));
        }
        return headers;
    }

    public static string Id(string name) => "registry:" + name.Trim().ToLowerInvariant();
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

/// <summary>Unauthenticated registry reads. No gateway credentials or per-server headers enter this client.</summary>
public sealed class DesktopMcpCatalogClient(HttpClient http)
{
    public async Task<McpCatalogResult> ListAsync(IEnumerable<string> sources, CancellationToken ct)
    {
        var items = new Dictionary<string, McpCatalogItem>(StringComparer.Ordinal);
        var failed = new List<string>();
        foreach (var source in sources.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).Distinct())
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            string? cursor = null;
            try
            {
                var endpoint = McpValidation.Endpoint(source);
                for (var page = 0; ; page++)
                {
                    ct.ThrowIfCancellationRequested();
                    if (page >= 1000 || items.Count >= 20000) throw new InvalidOperationException("Registry limit.");
                    var uri = cursor is null ? endpoint : new Uri(endpoint.AbsoluteUri + "?cursor=" + Uri.EscapeDataString(cursor));
                    using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                    using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                    response.EnsureSuccessStatusCode();
                    var bytes = await DesktopCatalogClient.ReadBoundedAsync(response.Content, 8 * 1024 * 1024, ct);
                    using var doc = JsonDocument.Parse(bytes);
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("servers", out var servers) || servers.ValueKind != JsonValueKind.Array)
                        throw new JsonException("Invalid registry response.");
                    foreach (var entry in servers.EnumerateArray())
                    {
                        if (!entry.TryGetProperty("server", out var server) || server.ValueKind != JsonValueKind.Object) continue;
                        var name = CatalogProjection.Text(server, "name");
                        if (string.IsNullOrWhiteSpace(name) || !server.TryGetProperty("remotes", out var remotes) || remotes.ValueKind != JsonValueKind.Array) continue;
                        foreach (var remote in remotes.EnumerateArray())
                        {
                            if (CatalogProjection.Text(remote, "type") != "streamable-http") continue;
                            try
                            {
                                var url = McpValidation.Endpoint(CatalogProjection.Text(remote, "url") ?? "").AbsoluteUri;
                                var id = McpValidation.Id(name);
                                items.TryAdd(source + "|" + id, new(id, name, CatalogProjection.Text(server, "description") ?? "", url,
                                    CatalogProjection.Text(server, "version"), source));
                                break;
                            }
                            catch (InvalidOperationException) { /* Unusable remote: try another endpoint. */ }
                        }
                    }
                    cursor = root.TryGetProperty("metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object
                        ? CatalogProjection.Text(metadata, "next_cursor") : null;
                    if (string.IsNullOrEmpty(cursor)) break;
                    if (!seen.Add(cursor)) throw new JsonException("Repeated registry cursor.");
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception e) when (e is HttpRequestException or JsonException or InvalidOperationException or IOException or OperationCanceledException)
            { failed.Add(source); }
        }
        return new(items.Values.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToArray(), failed);
    }
}
