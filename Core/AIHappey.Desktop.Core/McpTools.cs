using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AIHappey.Vercel.Models;

namespace AIHappey.Desktop.Core;

public sealed class McpTurnSnapshot
{
    private sealed record Route(string OriginalName, Func<string, JsonElement, string, string, CancellationToken, Task<JsonElement>> Call);
    private readonly Dictionary<string, Route> routes = new(StringComparer.Ordinal);
    private readonly List<JsonElement> tools = [];
    private readonly List<JsonElement> context = [];
    private readonly List<McpResourceEntry> resources = [];
    private readonly Dictionary<string, Func<string, string?, int, CancellationToken, Task<JsonElement>>> resourceRoutes = new(StringComparer.Ordinal);
    public static McpTurnSnapshot Empty => new();
    public IReadOnlyList<JsonElement> Tools => tools.AsReadOnly();
    public IReadOnlyList<JsonElement> Context => context.AsReadOnly();
    public IReadOnlyList<McpResourceEntry> Resources => resources.AsReadOnly();

    public static McpTurnSnapshot Create(IEnumerable<(McpCatalogItem Server, McpDiscovery Discovery,
        Func<string, JsonElement, string, string, CancellationToken, Task<JsonElement>> Call)> connected)
        => Create(connected.Select(s => new McpConnectedServer(s.Server, s.Discovery, s.Call)));

    public static McpTurnSnapshot Create(IEnumerable<McpConnectedServer> connected)
    {
        var result = new McpTurnSnapshot();
        var servers = connected.OrderBy(s => s.Server.Id, StringComparer.Ordinal).ToArray();
        var candidates = servers.SelectMany(s => s.Discovery.Tools.Where(t => CatalogProjection.Text(t, "name") is { Length: > 0 })
            .Select(t => (s.Server, s.Call, Tool: t, Name: CatalogProjection.Text(t, "name")!))).ToArray();
        var counts = candidates.GroupBy(c => c.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count());
        // Reserve real names before allocating aliases, including adversarial alias-like real names.
        var used = candidates.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
        var hasResources = servers.Any(s => s.Discovery.Resources.Count > 0 || s.Discovery.ResourceTemplates.Count > 0);
        used.Add(DesktopMcpResources.ToolName);
        foreach (var server in servers)
        {
            var serverTools = new JsonArray();
            foreach (var candidate in candidates.Where(c => c.Server.Id == server.Server.Id))
            {
                var name = candidate.Name;
                if (name == DesktopMcpResources.ToolName || counts[name] > 1 || !Regex.IsMatch(name, "^[a-zA-Z0-9_-]{1,64}$"))
                {
                    var stem = Regex.Replace(name, "[^a-zA-Z0-9_-]", "_");
                    stem = stem[..Math.Min(stem.Length, 40)];
                    var suffix = McpValidation.Hash(candidate.Server.Id + "|" + name)[..12].ToLowerInvariant();
                    name = "mcp_" + stem + "_" + suffix;
                    var index = 2; var baseName = name;
                    while (!used.Add(name)) name = baseName[..Math.Min(baseName.Length, 58)] + "_" + index++;
                }
                if (!candidate.Tool.TryGetProperty("inputSchema", out var schema) || schema.ValueKind != JsonValueKind.Object) continue;
                var tool = new JsonObject { ["name"] = name, ["inputSchema"] = JsonNode.Parse(schema.GetRawText()) };
                foreach (var field in new[] { "description", "title", "outputSchema", "annotations" })
                    if (candidate.Tool.TryGetProperty(field, out var value)) tool[field] = JsonNode.Parse(value.GetRawText());
                result.tools.Add(JsonSerializer.SerializeToElement(tool));
                result.routes.Add(name, new(candidate.Name, candidate.Call));
                serverTools.Add(tool.DeepClone());
            }
            var info = new JsonObject { ["name"] = CatalogProjection.Text(server.Discovery.ServerInfo, "name") ?? server.Server.Name,
                ["title"] = CatalogProjection.Text(server.Discovery.ServerInfo, "title") ?? server.Server.Name,
                ["version"] = CatalogProjection.Text(server.Discovery.ServerInfo, "version"), ["mcpServerUrl"] = server.Server.Url };
            var block = new JsonObject { ["modelContextProtocolServer"] = info, ["tools"] = serverTools };
            var catalog = DesktopMcpResources.AssistantCatalog(server.Discovery.Resources, false);
            var templates = DesktopMcpResources.AssistantCatalog(server.Discovery.ResourceTemplates, true);
            if (catalog.Count > 0) block["resources"] = catalog;
            if (templates.Count > 0) block["resourceTemplates"] = templates;
            if (server.Read is { } read)
            {
                result.resourceRoutes.TryAdd(server.Server.Url, read);
                foreach (var item in server.Discovery.Resources.Where(DesktopMcpResources.ForUser))
                    result.resources.Add(new(server.Server.Id, server.Server.Name, server.Server.Url, item.Clone(), false, read, null));
                foreach (var item in server.Discovery.ResourceTemplates.Where(DesktopMcpResources.ForUser))
                    result.resources.Add(new(server.Server.Id, server.Server.Name, server.Server.Url, item.Clone(), true, read,
                        server.Discovery.Capabilities.TryGetProperty("completions", out var capability) && capability.ValueKind == JsonValueKind.Object ? server.Complete : null));
            }
            if (!string.IsNullOrWhiteSpace(server.Discovery.Instructions)) block["instructions"] = server.Discovery.Instructions;
            result.context.Add(JsonSerializer.SerializeToElement(block));
        }
        if (hasResources)
        {
            result.tools.Add(DesktopMcpResources.Tool);
            result.routes.Add(DesktopMcpResources.ToolName, new(DesktopMcpResources.ToolName,
                (_, input, _, _, ct) => result.ReadResourceToolAsync(input, ct)));
        }
        return result;
    }

    private async Task<JsonElement> ReadResourceToolAsync(JsonElement input, CancellationToken ct)
    {
        var arguments = DesktopMcpResources.Arguments(input);
        if (!resourceRoutes.TryGetValue(arguments.ServerUrl, out var read))
            throw new InvalidOperationException(DesktopResources.Get("McpDisconnected"));
        return DesktopMcpResources.ToolResult(await read(arguments.Uri, arguments.Cursor, arguments.Limit, ct));
    }

    public bool Contains(string name) => routes.ContainsKey(name);
    public Task<JsonElement> CallAsync(string name, JsonElement input, string callId, string locale, CancellationToken ct) =>
        routes.TryGetValue(name, out var route) ? route.Call(route.OriginalName, input, callId, locale, ct)
            : throw new InvalidOperationException(DesktopResources.Get("McpUnknownTool"));
}

/// <summary>Runs only parts assembled for this active turn. History is context, never an execution queue.</summary>
public static class DesktopMcpToolExecution
{
    public const int MaxRounds = 8;
    public const int MaxCalls = 32;

    public static async Task<int> ExecutePendingAsync(ConversationMessage output, McpTurnSnapshot snapshot,
        HashSet<string> executed, string locale, CancellationToken ct)
    {
        var count = 0;
        for (var index = 0; index < output.Message.Parts.Count; index++)
        {
            var part = output.Message.Parts[index];
            if (!PortableConversations.IsTool(part)) continue;
            var raw = PortableConversations.Element(part);
            if (PortableConversations.String(raw, "state") != "input-available"
                || raw.TryGetProperty("providerExecuted", out var provider) && provider.ValueKind == JsonValueKind.True) continue;
            ct.ThrowIfCancellationRequested();
            var id = PortableConversations.String(raw, "toolCallId") ?? throw new JsonException("Missing tool call ID.");
            if (executed.Count >= MaxCalls || !executed.Add(id)) throw new GatewayException(DesktopResources.Get("McpToolLimit"));
            var node = PortableConversations.Node(part);
            try
            {
                if (!raw.TryGetProperty("input", out var input) || input.ValueKind != JsonValueKind.Object)
                    throw new InvalidOperationException(DesktopResources.Get("McpInvalidArguments"));
                var result = await snapshot.CallAsync(PortableConversations.ToolName(part), input, id, locale, ct);
                if (result.GetRawText().Length > 2_000_000) throw new InvalidOperationException(DesktopResources.Get("McpResultTooLarge"));
                var modelResult = JsonNode.Parse(result.GetRawText()) as JsonObject ?? throw new JsonException("Invalid MCP result.");
                modelResult.Remove("_meta");
                // MCP isError is an application result, not a transport failure: preserve the complete safe result.
                node["output"] = modelResult; node["state"] = "output-available";
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                node["state"] = "output-error"; node["errorText"] = DesktopResources.Get("OperationCanceled");
                output.Message.Parts[index] = PortableConversations.Part(node); throw;
            }
            catch (Exception e)
            {
                node["state"] = "output-error";
                // Do not persist arbitrary server/SDK exception messages (headers/URLs may be embedded).
                node["errorText"] = snapshot.Contains(PortableConversations.ToolName(part))
                    ? DesktopMcpManager.SafeError(e) : DesktopResources.Get("McpUnknownTool");
            }
            output.Message.Parts[index] = PortableConversations.Part(node); count++;
        }
        return count;
    }
}
