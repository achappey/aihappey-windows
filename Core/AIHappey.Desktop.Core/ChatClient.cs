using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Vercel.Models;

namespace AIHappey.Desktop.Core;

public sealed record ChatTarget(string Id, string Label)
{
    public string? ProviderKey { get; init; }
    public string? ModelType { get; init; }
    public long? Created { get; init; }
    public string? DisplayId { get; init; }
    public string? ProviderModelId { get; init; }
    public string? Description { get; init; }
    public string? OwnedBy { get; init; }
    public string? LocalAgentName { get; init; }
    public string? RemoteAgentId { get; init; }
    public string? AgentModelId { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
    public double? ContextWindow { get; init; }
    public double? MaxTokens { get; init; }
    public double? InputPrice { get; init; }
    public double? OutputPrice { get; init; }
    public JsonElement? ModelMetadata { get; init; }
    public override string ToString() => Label;
}

public sealed class GatewayException(string message) : Exception(message);

public sealed class DesktopChatClient(DesktopSession session, HttpClient http)
{
    public async Task<IReadOnlyList<ChatTarget>> ListAsync(ServiceKind service, CancellationToken ct)
    {
        using var request = await RequestAsync(service, HttpMethod.Get, "v1/models", ct);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        await GatewayErrors.CheckResponseAsync(response, ct);
        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new GatewayException(DesktopResources.Get("InvalidModelCatalog"));
        var items = data.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object
                && x.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(id.GetString()))
            .Select(x => AiModelCatalog.Project(x, service)).ToArray();
        return service == ServiceKind.Ai ? AiModelCatalog.NewestFirst(items)
            : items.OrderBy(x => x.Label, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async IAsyncEnumerable<StreamEvent> StreamAsync(ServiceKind service, string target, string conversationId,
        List<UIMessage> messages, [EnumeratorCancellation] CancellationToken ct, ChatPreferences? preferences = null, string? providerKey = null,
        UIMessage? systemContext = null, McpTurnSnapshot? mcp = null, DesktopAgent? localAgent = null,
        System.Text.Json.Nodes.JsonObject? agentRequest = null)
    {
        var snapshot = (preferences ?? session.Settings.Chat).Clone();
        providerKey = ChatPreferences.ResolveProvider(service, target, providerKey);
        using var request = await RequestAsync(service, HttpMethod.Post, "api/chat", ct);
        var requestMessages = DesktopSystemContext.RequestMessages(service, messages,
            service == ServiceKind.Ai ? systemContext ?? session.CaptureSystemContext() : null);
        request.Headers.Accept.ParseAdd("text/event-stream");
        var body = service == ServiceKind.Ai
            ? snapshot.RequestBody(target, conversationId, requestMessages, providerKey, mcp ?? session.Mcp?.Capture())
            : JsonSerializer.SerializeToNode(localAgent is not null ? LocalAgentRequest(localAgent, conversationId, requestMessages)
                : new { id = conversationId, model = target, messages = requestMessages }, PortableConversations.Json)!.AsObject();
        if (service != ServiceKind.Ai && agentRequest is not null)
        {
            body = (System.Text.Json.Nodes.JsonObject)agentRequest.DeepClone();
            body["id"] = conversationId;
            body["messages"] = JsonSerializer.SerializeToNode(requestMessages, PortableConversations.Json);
        }
        if (service != ServiceKind.Ai)
        {
            if (mcp is { Tools.Count: > 0 })
            {
                // Agents use the ordinary top-level declaration contract, not
                // model-chat provider options or the agent's configured tools.
                body["tools"] = new System.Text.Json.Nodes.JsonArray(mcp.Tools.Select(tool =>
                {
                    var declaration = new System.Text.Json.Nodes.JsonObject();
                    foreach (var field in new[] { "name", "description", "inputSchema", "annotations" })
                        if (tool.TryGetProperty(field, out var value)) declaration[field] = System.Text.Json.Nodes.JsonNode.Parse(value.GetRawText());
                    return (System.Text.Json.Nodes.JsonNode)declaration;
                }).ToArray());
            }
            snapshot.ToolOutputContentSettings.ApplyToRequest(body);
        }
        request.Content = JsonContent.Create(body, options: PortableConversations.Json);
        if (service == ServiceKind.Ai) snapshot.ApplyHeaders(request, providerKey);
        // The Chat preference owns this header, including overriding an injected host value.
        request.Headers.Remove("X-AIHappey-Debug");
        if (snapshot.ApiDebugEnabled) request.Headers.Add("X-AIHappey-Debug", "true");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        await GatewayErrors.CheckResponseAsync(response, ct);
        if (response.Content.Headers.ContentType?.MediaType != "text/event-stream")
            throw new GatewayException(DesktopResources.Get("ChatStreamRequired"));
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        await foreach (var payload in SseReader.ReadAsync(stream, ct))
        {
            if (payload == "[DONE]") yield break;
            yield return StreamEvent.Parse(payload);
        }
    }

    public static object LocalAgentRequest(DesktopAgent agent, string conversationId, List<UIMessage> messages)
    {
        agent.Validate();
        // No SDK reserialization: preserve portable fields, including newer plugin/tool/schema options.
        return new { id = conversationId, agents = new[] { agent.Definition.DeepClone() }, messages };
    }

    internal async Task<HttpRequestMessage> RequestAsync(ServiceKind service, HttpMethod method, string path, CancellationToken ct, string? expectedAiPartition = null)
    {
        void CheckPartition()
        {
            if (expectedAiPartition is not null && expectedAiPartition != ImageLibraryStore.Partition(session))
                throw new OperationCanceledException("The video account or endpoint changed.", ct);
        }
        CheckPartition();
        session.Settings.Validate(session.Host.AllowLocal);
        var endpoint = await session.Runtime.ResolveAsync(service, session.Settings, ct);
        CheckPartition();
        var request = new HttpRequestMessage(method, new Uri(endpoint, path));
        try { await session.Host.AuthenticateAsync(request, service, ct); CheckPartition(); return request; }
        catch { request.Dispose(); throw; }
    }

    internal static void CheckResponse(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        throw new GatewayException(response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "Access denied. Check your account, API keys, and gateway permissions.",
            HttpStatusCode.TooManyRequests => "The service is rate limited. Wait before sending again.",
            _ => $"The service returned HTTP {(int)response.StatusCode}. No inference request was retried."
        });
    }
}

public static class SseReader
{
    // A 64 MiB downloadable file needs ~86 MiB of base64 text, plus JSON/tool metadata.
    // Bound allocation while framing, not after ReadLineAsync has allocated an unbounded line.
    public const int MaximumEventCharacters = 96 * 1024 * 1024;

    public static async IAsyncEnumerable<string> ReadAsync(Stream stream, [EnumeratorCancellation] CancellationToken ct = default,
        int maximumEventCharacters = MaximumEventCharacters)
    {
        if (maximumEventCharacters <= 0 || maximumEventCharacters > MaximumEventCharacters)
            throw new ArgumentOutOfRangeException(nameof(maximumEventCharacters));
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        var data = new StringBuilder();
        var line = new StringBuilder();
        var buffer = new char[8192];
        var skipLf = false;
        string? ConsumeLine()
        {
            if (line.Length == 0)
            {
                if (data.Length == 0) return null;
                var payload = data.ToString(0, data.Length - 1); // Remove only the framing newline.
                data.Clear();
                return payload;
            }
            if (line.Length >= 5 && line[0] == 'd' && line[1] == 'a' && line[2] == 't' && line[3] == 'a' && line[4] == ':')
            {
                var start = line.Length > 5 && line[5] == ' ' ? 6 : 5;
                var length = line.Length - start;
                if ((long)data.Length + length > maximumEventCharacters) throw SizeError(maximumEventCharacters);
                data.Append(line, start, length).Append('\n');
            }
            line.Clear();
            return null;
        }
        while (await reader.ReadAsync(buffer.AsMemory(), ct) is var read && read > 0)
        {
            var offset = 0;
            while (offset < read)
            {
                ct.ThrowIfCancellationRequested();
                if (skipLf)
                {
                    skipLf = false;
                    if (buffer[offset] == '\n') { offset++; continue; }
                }
                var end = buffer.AsSpan(offset, read - offset).IndexOfAny('\r', '\n');
                var length = end < 0 ? read - offset : end;
                // Include the SSE prefix/optional space without reducing the payload budget.
                if ((long)line.Length + length > (long)maximumEventCharacters + 6) throw SizeError(maximumEventCharacters);
                line.Append(buffer, offset, length);
                offset += length;
                if (end < 0) break;
                skipLf = buffer[offset++] == '\r';
                if (ConsumeLine() is { } payload) yield return payload;
            }
        }
        if (line.Length > 0) ConsumeLine();
        if (data.Length > 0) yield return data.ToString(0, data.Length - 1);
    }

    private static GatewayException SizeError(int limit) => new(DesktopResources.Format("StreamSizeLimit", limit));
}

/// <summary>Retains the wire payload for forward compatibility; known events use existing typed contracts.</summary>
public sealed record StreamEvent(string Type, JsonElement Raw, UIMessagePart? Part)
{
    public static StreamEvent Parse(string payload)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(payload); }
        catch (JsonException) { throw new GatewayException(DesktopResources.Get("InvalidStreamEvent")); }
        using var document = doc;
        var raw = doc.RootElement.Clone();
        var type = PortableConversations.String(raw, "type");
        if (string.IsNullOrWhiteSpace(type)) throw new GatewayException(DesktopResources.Get("InvalidStreamEvent"));
        // Assembly uses the wire shape, not the server converter's narrower part union.
        // This also preserves arbitrary provider fields and newer SDK event kinds.
        return new(type, raw, new PortableUIPart(raw));
    }
}
