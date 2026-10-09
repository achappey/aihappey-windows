using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AIHappey.Vercel.Models;

namespace AIHappey.Desktop.Core;

public sealed record McpConnectedServer(McpCatalogItem Server, McpDiscovery Discovery,
    Func<string, JsonElement, string, string, CancellationToken, Task<JsonElement>> Call,
    Func<string, string?, int, CancellationToken, Task<JsonElement>>? Read = null,
    Func<string, string, string, IReadOnlyDictionary<string, string>, CancellationToken, Task<IReadOnlyList<string>>>? Complete = null);

/// <summary>A catalog selection retains its originating connection epoch, not just a possibly ambiguous URI.</summary>
public sealed record McpResourceEntry(string ServerId, string ServerName, string ServerUrl, JsonElement Resource, bool IsTemplate,
    Func<string, string?, int, CancellationToken, Task<JsonElement>> Read,
    Func<string, string, string, IReadOnlyDictionary<string, string>, CancellationToken, Task<IReadOnlyList<string>>>? Complete)
{
    public string Name => CatalogProjection.Text(Resource, "title") ?? CatalogProjection.Text(Resource, "name") ?? Uri;
    public string Uri => CatalogProjection.Text(Resource, IsTemplate ? "uriTemplate" : "uri") ?? "";
    public string Description => CatalogProjection.Text(Resource, "description") ?? "";
    public string ResourceType => DesktopResources.Get(IsTemplate ? "McpResourceTemplate" : "McpResource");
    public string? MimeType => CatalogProjection.Text(Resource, "mimeType");
    public string Kind => ResourceType + " · " + ServerName + (MimeType is { } mime ? " · " + mime : "");
}

public sealed record McpSelectedResource(string ServerId, string Uri, string Name, JsonElement Result)
{
    public IReadOnlyList<UIMessagePart> Parts() => DesktopMcpResources.MessageParts(Result);
}

public static class McpCatalogPagination
{
    public static async Task<IReadOnlyList<JsonElement>> ReadAsync(
        Func<string?, CancellationToken, Task<(IReadOnlyList<JsonElement> Items, string? NextCursor)>> page, CancellationToken ct)
    {
        var items = new List<JsonElement>(); var seen = new HashSet<string>(StringComparer.Ordinal); string? cursor = null;
        for (var index = 0; ; index++)
        {
            ct.ThrowIfCancellationRequested();
            if (index >= 1000) throw new InvalidOperationException("MCP catalog page limit.");
            var result = await page(cursor, ct);
            ct.ThrowIfCancellationRequested();
            if (result.Items.Count > 10000 - items.Count) throw new InvalidOperationException("MCP catalog item limit.");
            items.AddRange(result.Items.Select(item => item.Clone()));
            cursor = result.NextCursor;
            if (string.IsNullOrEmpty(cursor)) return items.AsReadOnly();
            if (!seen.Add(cursor)) throw new InvalidOperationException("Repeated MCP catalog cursor.");
        }
    }
}

/// <summary>Browser resource contracts, shared by the model tool and native composer. Never dereferences resource URIs locally.</summary>
public static class DesktopMcpResources
{
    public const string ToolName = "read_resource";
    public const int DefaultLimit = 100;
    public const int MaximumResultCharacters = 2_000_000;
    private static readonly Regex Parameters = new("{([^{}]+)}", RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions PrettyJson = new() { WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static JsonElement Tool => JsonSerializer.Deserialize<JsonElement>("""
        {"name":"read_resource","title":"Read an MCP resource",
         "description":"Reads a resource by URI from a MCP server. Use this to read MCP resources. The serverUrl and the resource uri can be from completely different domains. If the resource has a markdown (text/markdown) mime type, do not include its full content in your response. The markdown files are visible in the tool result and attachment views.",
         "inputSchema":{"type":"object","properties":{
           "serverUrl":{"type":"string","description":"URL of the MCP server. Make sure this is always the url of a connected MCP server. NOT the uri of the resource."},
           "uri":{"type":"string","description":"URI of the resource to read. Make sure this is always the uri of the requested resource."},
           "cursor":{"type":"string","description":"Optional opaque pagination cursor returned by a previous read_resource call."},
           "limit":{"type":"integer","minimum":1,"maximum":1000,"description":"Optional maximum number of lines to return for text-based resources."}},
           "required":["uri","serverUrl"]},
         "annotations":{"readOnlyHint":true,"destructiveHint":false,"idempotentHint":true,"openWorldHint":true}}
        """);

    public static bool ForAssistant(JsonElement value) => !Audience(value, out var audience)
        || audience.GetArrayLength() == 0 || audience.EnumerateArray().Any(a => a.ValueKind == JsonValueKind.String && a.GetString() == "assistant");

    // Match useResourceSelect, including its distinction between absent annotations and annotations without user audience.
    public static bool ForUser(JsonElement value) => !value.TryGetProperty("annotations", out var annotations)
        || annotations.ValueKind == JsonValueKind.Null
        || Audience(value, out var audience) && audience.EnumerateArray().Any(a => a.ValueKind == JsonValueKind.String && a.GetString() == "user");

    private static bool Audience(JsonElement value, out JsonElement audience)
    {
        audience = default;
        return value.TryGetProperty("annotations", out var annotations) && annotations.ValueKind == JsonValueKind.Object
            && annotations.TryGetProperty("audience", out audience) && audience.ValueKind == JsonValueKind.Array;
    }

    public static JsonArray AssistantCatalog(IEnumerable<JsonElement> items, bool template)
    {
        var result = new JsonArray();
        foreach (var item in items.Where(ForAssistant))
        {
            var projected = new JsonObject();
            foreach (var key in new[] { "name", template ? "uriTemplate" : "uri", "description", "mimeType" })
                if (item.TryGetProperty(key, out var value)) projected[key] = JsonNode.Parse(value.GetRawText());
            if (!template && item.TryGetProperty("size", out var size) && size.ValueKind == JsonValueKind.Number && size.TryGetDouble(out var n) && n != 0)
                projected["size"] = JsonNode.Parse(size.GetRawText());
            if (item.TryGetProperty("annotations", out var annotations) && annotations.ValueKind == JsonValueKind.Object)
            {
                var filtered = new JsonObject();
                foreach (var key in template ? new[] { "priority" } : new[] { "priority", "lastModified" })
                    if (annotations.TryGetProperty(key, out var value)) filtered[key] = JsonNode.Parse(value.GetRawText());
                if (filtered.Count > 0) projected["annotations"] = filtered;
            }
            result.Add(projected);
        }
        return result;
    }

    public static void ValidateUri(string uri)
    {
        // Windows paths are not absolute MCP URIs. Do not restrict legitimate custom resource schemes.
        if (string.IsNullOrWhiteSpace(uri) || !Regex.IsMatch(uri, "^[a-zA-Z][a-zA-Z0-9+.-]*:")
            || uri.Contains('\\') || !System.Uri.TryCreate(uri, UriKind.Absolute, out _))
            throw new InvalidOperationException(DesktopResources.Get("McpInvalidResourceUri"));
    }

    public static (string ServerUrl, string Uri, string? Cursor, int Limit) Arguments(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object || CatalogProjection.Text(input, "serverUrl") is not { Length: > 0 } serverUrl
            || CatalogProjection.Text(input, "uri") is not { Length: > 0 } uri)
            throw new InvalidOperationException(DesktopResources.Get("McpInvalidArguments"));
        ValidateUri(uri);
        string? cursor = null; var limit = DefaultLimit;
        if (input.TryGetProperty("cursor", out var c))
        {
            if (c.ValueKind != JsonValueKind.String) throw new InvalidOperationException(DesktopResources.Get("McpInvalidArguments"));
            cursor = c.GetString();
        }
        if (input.TryGetProperty("limit", out var l) && (l.ValueKind != JsonValueKind.Number || !l.TryGetInt32(out limit) || limit is < 1 or > 1000))
            throw new InvalidOperationException(DesktopResources.Get("McpInvalidArguments"));
        return (serverUrl, uri, cursor, limit);
    }

    public static JsonElement ValidateResult(JsonElement result)
    {
        if (result.GetRawText().Length > MaximumResultCharacters) throw new InvalidOperationException(DesktopResources.Get("McpResultTooLarge"));
        if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("contents", out var contents) || contents.ValueKind != JsonValueKind.Array)
            throw new JsonException("Invalid MCP resource result.");
        return result;
    }

    public static JsonElement ToolResult(JsonElement result)
    {
        ValidateResult(result);
        var content = new JsonArray(); var json = new List<JsonNode?>();
        foreach (var item in result.GetProperty("contents").EnumerateArray())
        {
            if (CatalogProjection.Text(item, "mimeType") == "application/json" && item.TryGetProperty("text", out var text))
                json.Add(JsonNode.Parse(text.GetString() ?? throw new JsonException("Invalid JSON resource.")));
            else content.Add(new JsonObject { ["type"] = "resource", ["resource"] = JsonNode.Parse(item.GetRawText()) });
        }
        var output = new JsonObject { ["isError"] = false, ["content"] = content };
        if (json.Count == 1) output["structuredContent"] = json[0];
        else if (json.Count > 1) output["structuredContent"] = new JsonObject { ["items"] = new JsonArray(json.ToArray()) };
        return JsonSerializer.SerializeToElement(output);
    }

    // Deliberately mirror the browser's literal {argument} substitution, not a different RFC6570 expansion.
    public static IReadOnlyList<string> TemplateArguments(string template) => Parameters.Matches(template)
        .Select(m => m.Groups[1].Value.Trim()).Where(s => s.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
    public static string ExpandTemplate(string template, IReadOnlyDictionary<string, string> values) => Parameters.Replace(template,
        match => values.TryGetValue(match.Groups[1].Value.Trim(), out var value) ? value : "");

    public static IReadOnlyList<UIMessagePart> MessageParts(JsonElement result)
    {
        ValidateResult(result);
        var parts = new List<UIMessagePart>();
        foreach (var item in result.GetProperty("contents").EnumerateArray())
        {
            var uri = CatalogProjection.Text(item, "uri") ?? "";
            var mime = CatalogProjection.Text(item, "mimeType") ?? "text/plain";
            if (CatalogProjection.Text(item, "text") is { Length: > 0 } text)
                parts.Add(new TextUIPart { Text = Markdown(uri, text, mime) });
            else if (CatalogProjection.Text(item, "blob") is { } blob)
            {
                if (UrlAttachments.ValidMediaType(mime) is null) throw new JsonException("Invalid MCP resource media type.");
                ComposerAttachments.ValidateSize(Convert.FromBase64String(blob).Length);
                parts.Add(new FileUIPart { MediaType = mime, Url = $"data:{mime};base64,{blob}" });
            }
            else if (item.TryGetProperty("text", out _))
                parts.Add(new TextUIPart { Text = Markdown(uri, "", mime) });
            else throw new JsonException("Invalid MCP resource content.");
        }
        return parts;
    }

    public static string Markdown(string uri, string text, string mime = "text/plain")
    {
        var back = uri.StartsWith("http", StringComparison.Ordinal) ? $"\n\n[{uri}]({uri})" : "";
        string Details(string body) => $"<details><summary>{uri}</summary>\n\n{body}\n\n</details>{back}";
        var collapse = text.Length > 500 || text.Split('\n').Length > 15;
        if (mime == "application/json")
        {
            try { return Details("```json\n" + JsonSerializer.Serialize(JsonSerializer.Deserialize<JsonElement>(text), PrettyJson) + "\n```"); }
            catch (JsonException) { return Details("```xml\n" + text + "\n```"); }
        }
        if (mime is "application/xml" or "text/xml") return Details("```xml\n" + text + "\n```");
        if (mime == "text/markdown") return collapse ? Details(text) : uri + "\n\n" + text;
        if (mime.StartsWith("text/", StringComparison.Ordinal)) return collapse ? Details("\n" + text + "\n") : uri + "\n\n" + text;
        return Details("\n" + text + "\n");
    }
}
