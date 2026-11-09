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

public sealed class DesktopChatClient(DesktopSession session, HttpClient http, HttpClient? directHttp = null)
{
    // Separate transport: no gateway defaults, cookies, redirects, or inference retries.
    private static readonly HttpClient directTransport = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
        { Timeout = Timeout.InfiniteTimeSpan };
    internal string AgentCatalogScope => session.HistoryPartition + "|" + JsonSerializer.Serialize(session.Settings.Agents);

    internal async Task<JsonElement> ReadAiModelsAsync(CancellationToken ct)
    {
        using var request = await RequestAsync(ServiceKind.Ai, HttpMethod.Get, "v1/models", ct);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        await GatewayErrors.CheckResponseAsync(response, ct);
        using var document = JsonDocument.Parse(await DesktopCatalogClient.ReadBoundedAsync(response.Content, DesktopModelCatalogStore.MaxBytes, ct));
        if (!DesktopModelCatalogStore.ValidResponse(document.RootElement)) throw new GatewayException(DesktopResources.Get("InvalidModelCatalog"));
        return document.RootElement.Clone();
    }

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
        System.Text.Json.Nodes.JsonObject? agentRequest = null, DesktopChatRoute? route = null)
    {
        var snapshot = (preferences ?? session.Settings.Chat).Clone();
        providerKey = ChatPreferences.ResolveProvider(service, target, providerKey);
        if (service == ServiceKind.Ai)
        {
            route ??= DesktopChatRoutes.Resolve(session.Host, snapshot, new(target, target) { ProviderKey = providerKey });
            if (route.IsNative)
            {
                await foreach (var item in StreamNativeAsync(route, messages, snapshot, systemContext, mcp ?? session.Mcp?.Capture(), ct)) yield return item;
                yield break;
            }
        }
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

    private async IAsyncEnumerable<StreamEvent> StreamNativeAsync(DesktopChatRoute route, List<UIMessage> messages,
        ChatPreferences preferences, UIMessage? systemContext, McpTurnSnapshot? mcp, [EnumeratorCancellation] CancellationToken ct)
    {
        var body = NativeChatRequests.Build(route, preferences, DesktopSystemContext.RequestMessages(ServiceKind.Ai, messages,
            systemContext ?? session.CaptureSystemContext()), mcp);
        using var request = route.IsDirect ? new HttpRequestMessage(HttpMethod.Post, route.DirectUrl)
            : await RequestAsync(ServiceKind.Ai, HttpMethod.Post, route.Endpoint.TrimStart('/'), ct);
        preferences.ApplyHeaders(request, route.ProviderKey);
        if (route.IsDirect)
        {
            if (!session.Host.SupportsDirectChat || route.ProviderKey is null)
                throw new InvalidOperationException(DesktopResources.Get("DirectChatUnavailable"));
            await session.Host.AuthenticateDirectChatAsync(request, route.ProviderKey, route.Endpoint, ct);
        }
        else
        {
            request.Headers.Remove("X-AIHappey-Debug");
            if (preferences.ApiDebugEnabled) request.Headers.Add("X-AIHappey-Debug", "true");
        }
        request.Headers.Accept.ParseAdd("text/event-stream");
        request.Content = JsonContent.Create(body, options: PortableConversations.Json);
        using var response = await (route.IsDirect ? directHttp ?? directTransport : http)
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        await GatewayErrors.CheckResponseAsync(response, ct);
        if (response.Content.Headers.ContentType?.MediaType != "text/event-stream")
            throw new GatewayException(DesktopResources.Get("ChatStreamRequired"));
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var mapper = new NativeChatStream(route, NativeShellTools.Enabled(preferences, route.ProviderKey));
        await foreach (var frame in SseReader.ReadEventsAsync(stream, ct))
        {
            if (frame.Data == "[DONE]") break;
            foreach (var item in mapper.Apply(frame.Data, frame.Event)) yield return item;
        }
        // A bare EOF or [DONE] is not evidence of a successful native response.
        mapper.CheckComplete();
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
    public sealed record Frame(string Data, string? Event);
    // A 64 MiB downloadable file needs ~86 MiB of base64 text, plus JSON/tool metadata.
    // Bound allocation while framing, not after ReadLineAsync has allocated an unbounded line.
    public const int MaximumEventCharacters = 96 * 1024 * 1024;

    public static async IAsyncEnumerable<string> ReadAsync(Stream stream, [EnumeratorCancellation] CancellationToken ct = default,
        int maximumEventCharacters = MaximumEventCharacters)
    {
        await foreach (var frame in ReadEventsAsync(stream, ct, maximumEventCharacters)) yield return frame.Data;
    }

    public static async IAsyncEnumerable<Frame> ReadEventsAsync(Stream stream, [EnumeratorCancellation] CancellationToken ct = default,
        int maximumEventCharacters = MaximumEventCharacters)
    {
        if (maximumEventCharacters <= 0 || maximumEventCharacters > MaximumEventCharacters)
            throw new ArgumentOutOfRangeException(nameof(maximumEventCharacters));
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        var data = new StringBuilder();
        var line = new StringBuilder();
        var buffer = new char[8192];
        var skipLf = false;
        string? eventName = null;
        Frame? ConsumeLine()
        {
            if (line.Length == 0)
            {
                if (data.Length == 0) { eventName = null; return null; }
                var payload = new Frame(data.ToString(0, data.Length - 1), eventName); // Remove only the framing newline.
                data.Clear();
                eventName = null;
                return payload;
            }
            if (line.Length >= 5 && line[0] == 'd' && line[1] == 'a' && line[2] == 't' && line[3] == 'a' && line[4] == ':')
            {
                var start = line.Length > 5 && line[5] == ' ' ? 6 : 5;
                var length = line.Length - start;
                if ((long)data.Length + length > maximumEventCharacters) throw SizeError(maximumEventCharacters);
                data.Append(line, start, length).Append('\n');
            }
            else if (line.ToString().StartsWith("event:", StringComparison.Ordinal))
                eventName = line.ToString(6, line.Length - 6).TrimStart(' ');
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
        if (data.Length > 0) yield return new Frame(data.ToString(0, data.Length - 1), eventName);
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
