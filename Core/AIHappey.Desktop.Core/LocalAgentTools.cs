using System.Text.Json;
using System.Text.Json.Nodes;

namespace AIHappey.Desktop.Core;

/// <summary>Agent editor and runtime share fresh persistent reads, including within one tool-call turn.</summary>
public sealed class LocalAgentTools(DesktopAgentStore store, string partition, IReadOnlyList<DesktopAgent> defaults,
    Func<bool> isCurrent, Func<CancellationToken, Task> changed,
    Func<CancellationToken, Task<IReadOnlyList<ChatTarget>>> remoteAgents, LocalToolFiles files,
    Func<JsonObject, CancellationToken, Task<JsonElement>> invoke)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private void Check(CancellationToken ct) { ct.ThrowIfCancellationRequested(); if (!isCurrent()) throw new OperationCanceledException(ct); }
    public Task<JsonElement> CallAsync(string name, JsonElement input, CancellationToken ct) => DesktopLocalTools.SafeAsync(async () =>
    {
        await gate.WaitAsync(ct);
        try
        {
            Check(ct); var agents = await store.ListAsync(partition, defaults, ct); Check(ct);
            if (name == "local_agents_list") return DesktopLocalTools.Result(new { agents = agents.Select(a => new { name = a.Name, description = a.Description }) });
            if (name == "local_agents_run") return await RunAsync(agents, input, ct);
            var agentName = DesktopLocalTools.Required(input, "agentName");
            if (name == "local_agents_get")
                return DesktopLocalTools.Result(new { agent = agents.FirstOrDefault(a => a.Name == agentName)?.Definition
                    ?? throw new LocalToolException("Agent with that exact name was not found.") });
            if (name == "local_agents_delete")
            {
                Check(ct); await store.DeleteAsync(partition, agentName, ct); await NotifyAsync(ct);
                return DesktopLocalTools.Result(null, DesktopLocalTools.Text("Deleted local agent: " + agentName));
            }
            if (name != "local_agents_create") throw new LocalToolException("Unsupported agent tool.");
            if (agents.Any(a => a.Name == agentName)) throw new LocalToolException("Agent with that name already exists.");
            var servers = new JsonObject();
            if (input.TryGetProperty("mcpServerUrls", out var urls))
            {
                if (urls.ValueKind != JsonValueKind.Array) throw new LocalToolException("mcpServerUrls must be an array.");
                foreach (var url in urls.EnumerateArray())
                {
                    if (url.ValueKind != JsonValueKind.String || !Uri.TryCreate(url.GetString(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                        throw new LocalToolException("MCP server URLs must be HTTP(S) URLs.");
                    servers[url.GetString()!] = new JsonObject { ["type"] = "streamable-http", ["url"] = url.GetString() };
                }
            }
            string Text(string key) => input.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()! : throw new LocalToolException(key + " must be a string.");
            bool Flag(string key) => input.TryGetProperty(key, out var value) ? value.ValueKind switch
                { JsonValueKind.True => true, JsonValueKind.False => false, _ => throw new LocalToolException(key + " must be boolean.") } : false;
            if (!input.TryGetProperty("modelTemperature", out var temperature) || temperature.ValueKind != JsonValueKind.Number || !temperature.TryGetDouble(out var t) || !double.IsFinite(t))
                throw new LocalToolException("modelTemperature must be a finite number.");
            var agent = new DesktopAgent(new JsonObject { ["name"] = agentName, ["description"] = Text("agentDescription"),
                ["instructions"] = Text("agentInstructions"), ["model"] = new JsonObject { ["id"] = DesktopLocalTools.Required(input, "modelId"),
                    ["options"] = new JsonObject { ["temperature"] = t }, ["providerMetadata"] = new JsonObject() },
                ["mcpClient"] = new JsonObject { ["policy"] = new JsonObject { ["readOnlyHint"] = Flag("policyReadOnly"),
                    ["idempotentHint"] = Flag("policyIdempotent"), ["openWorldHint"] = Flag("policyOpenWorld"), ["destructiveHint"] = Flag("policyDestructive") } },
                ["mcpServers"] = servers });
            try { Check(ct); await store.SaveAsync(partition, agent, null, ct, portableTool: true); }
            catch (InvalidDataException e) { throw new LocalToolException(e.Message); }
            catch (InvalidOperationException) { throw new LocalToolException("Agent could not be created; check its name and required fields."); }
            await NotifyAsync(ct);
            return DesktopLocalTools.Result(null, DesktopLocalTools.Text("Local agent created: " + agentName));
        }
        finally { gate.Release(); }
    }, ct);
    private async Task NotifyAsync(CancellationToken ct)
    {
        Check(ct);
        try { await changed(ct); }
        catch (Exception e) when (e is not OperationCanceledException and not OutOfMemoryException) { }
    }
    private async Task<JsonElement> RunAsync(IReadOnlyList<DesktopAgent> agents, JsonElement input, CancellationToken ct)
    {
        var id = DesktopLocalTools.Required(input, "agentId").Trim(); var prompt = DesktopLocalTools.Required(input, "prompt").Trim();
        if (id.Length == 0 || prompt.Length == 0) throw new LocalToolException("agentId and prompt are required.");
        var local = agents.FirstOrDefault(a => a.Name == id);
        if (local is null && !(await remoteAgents(ct)).Any(a => (a.RemoteAgentId ?? a.Id) == id)) throw new LocalToolException("Agent was not found.");
        Check(ct);
        var content = new JsonArray(new JsonObject { ["type"] = "input_text", ["text"] = prompt });
        if (CatalogProjection.Text(input, "filename")?.Trim() is { Length: > 0 } filename)
        {
            var file = await files.ReadAsync(filename, true, ct);
            content.Add(new JsonObject { ["type"] = "input_file", ["filename"] = file.Name,
                ["file_data"] = "data:" + file.MediaType + ";base64," + Convert.ToBase64String(file.Content.Span) });
        }
        var background = input.TryGetProperty("background", out var value) ? value.ValueKind switch
            { JsonValueKind.True => true, JsonValueKind.False => false, _ => throw new LocalToolException("background must be boolean.") } : false;
        var body = new JsonObject { ["input"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = content }),
            ["background"] = background, ["store"] = background, ["stream"] = false };
        if (local is not null) body["metadata"] = new JsonObject { ["agents"] = new JsonArray(local.Definition.DeepClone()) };
        else body["model"] = id;
        Check(ct); var response = await invoke(body, ct); Check(ct);
        return DesktopLocalTools.Result(response);
    }
}
