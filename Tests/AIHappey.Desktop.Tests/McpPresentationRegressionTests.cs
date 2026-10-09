using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Desktop.Core;

internal static class McpPresentationRegressionTests
{
    public static async Task RunAsync(Action<bool, string> check, string root)
    {
        var icons = McpIcons.Read(Json("""
            {"icons":[null,{}, {"src":"file:///C:/secret.png"}, {"src":"https://user:secret@icons.example/icon.png"},
            {"src":"https://127.0.0.1/icon.png"}, {"src":"https://icons.example/dark.svg","theme":"dark","mimeType":"image/svg+xml"},
            {"src":"https://icons.example/light.png","theme":"light"}, {"src":"https://icons.example/default.png"}]}
            """));
        check(icons.Count == 3 && McpIcons.Select(icons, "dark")?.Source.EndsWith("dark.svg") == true
            && McpIcons.Select(icons, "light")?.Source.EndsWith("light.png") == true, "MCP icons reject unsafe entries and select current theme");
        check(McpIcons.Select(icons, "other")?.Source.EndsWith("default.png") == true && McpIcons.Select([], "light") is null,
            "MCP icons select theme-neutral fallback and handle absent metadata");
        check(McpIcons.Read(Json("{\"icons\":{}}")).Count == 0 && McpIcons.Read(Json("null")).Count == 0,
            "MCP icons tolerate malformed and missing lists");
        var svg = new McpIcon("data:image/svg+xml,%3Csvg%20xmlns%3D%22http%3A%2F%2Fwww.w3.org%2F2000%2Fsvg%22%2F%3E");
        check(McpIcons.IsSvg(svg) && Encoding.UTF8.GetString(McpIcons.EmbeddedBytes(svg)).StartsWith("<svg"), "MCP embedded SVG decoding");
        check(McpIcons.EmbeddedBytes(new("data:image/png;base64,AQID")).SequenceEqual(new byte[] { 1, 2, 3 })
            && !McpIcons.Supported(new("data:text/html;base64,AQID")), "MCP embedded raster decoding and non-image rejection");
        try
        {
            McpIcons.EmbeddedBytes(new("data:image/png;base64," + Convert.ToBase64String(new byte[McpIcons.MaximumEmbeddedBytes + 1])));
            throw new Exception("Expected icon size limit.");
        }
        catch (FormatException) { check(true, "MCP embedded icon size limit"); }

        using var http = new HttpClient(new RegistryHandler());
        var catalog = await new DesktopMcpCatalogClient(http).ListAsync(["https://registry.example/servers"], CancellationToken.None);
        var item = catalog.Items.Single();
        check(catalog.FailedSources.Count == 0 && item.Icons.Single().Source == "https://icons.example/server.svg", "MCP registry projects server icons");
        var server = new DesktopMcpServer { Id = item.Id, Name = item.Name, Url = item.Url, Icons = item.Icons };
        var clone = server.Clone();
        check(clone.Icons.SequenceEqual(server.Icons) && !ReferenceEquals(clone.Icons, server.Icons)
            && clone.CatalogItem.Icons.SequenceEqual(server.Icons), "MCP clones and installed catalog retain icon metadata");
        var store = new DesktopMcpStore(Path.Combine(root, "mcp-presentation"));
        await store.SaveAsync("icons", [server]);
        var loaded = await store.LoadAsync("icons");
        check(!loaded.HasInvalidEntries && loaded.Servers.Single().Icons.SequenceEqual(item.Icons), "MCP icon persistence round trip");
        var legacy = JsonSerializer.Deserialize<DesktopMcpServer>("""{"id":"legacy","name":"Legacy","url":"https://mcp.example/"}""", JsonSerializerOptions.Web)!;
        legacy.Validate();
        check(legacy.Icons.Count == 0, "MCP existing saved servers default to empty icon metadata");
        var connected = new McpConnectionView(server, McpConnectionState.Connected, new(Json("""{"icons":[{"src":"https://icons.example/live.png"}]}"""), Json("{}"), null, []), null);
        check(McpIcons.ForServer(connected).Single().Source.EndsWith("live.png")
            && McpIcons.ForServer(connected with { Discovery = null }).SequenceEqual(server.Icons), "MCP live discovery takes precedence with installed-icon fallback");

        var resource = new McpResourceEntry("server", "Server", "https://mcp.example/", Json("""
            {"name":"wire-name","title":"Display title","uri":"resource://item","description":"Description","mimeType":"text/plain"}
            """), false, (_, _, _, _) => Task.FromResult(Json("{\"contents\":[]}")), null);
        check(resource.Name == "Display title" && resource.Description == "Description" && resource.MimeType == "text/plain"
            && resource.ResourceType != resource.Kind && resource.Uri == "resource://item", "MCP resource metadata is separate without changing resource URI");
        check((resource with { Resource = Json("{\"name\":\"Minimal\",\"uri\":\"resource://minimal\"}") }).MimeType is null,
            "MCP resource MIME badge is optional");
        check(DesktopMcpResources.ExpandTemplate("resource://items/{name}", new Dictionary<string, string> { ["name"] = "hello world" })
            == "resource://items/hello world", "MCP template expansion remains unchanged by presentation");
    }

    private static JsonElement Json(string value) => JsonSerializer.Deserialize<JsonElement>(value);
    private sealed class RegistryHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Headers.Authorization is not null) throw new Exception("Unexpected registry credentials.");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""
                {"servers":[{"server":{"name":"server","description":"Example","icons":[{"src":"https://icons.example/server.svg","mimeType":"image/svg+xml"}],
                "remotes":[{"type":"streamable-http","url":"https://mcp.example/"}]}}]}
                """) });
        }
    }
}
