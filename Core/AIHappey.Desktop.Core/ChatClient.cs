using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AgentHappey.Common.Models;
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
        UIMessage? systemContext = null, McpTurnSnapshot? mcp = null, DesktopAgent? localAgent = null)
    {
        var snapshot = (preferences ?? session.Settings.Chat).Clone();
        providerKey = ChatPreferences.ResolveProvider(service, target, providerKey);
        using var request = await RequestAsync(service, HttpMethod.Post, "api/chat", ct);
        var requestMessages = DesktopSystemContext.RequestMessages(service, messages,
            service == ServiceKind.Ai ? systemContext ?? session.CaptureSystemContext() : null);
        request.Headers.Accept.ParseAdd("text/event-stream");
        request.Content = service == ServiceKind.Ai
            ? JsonContent.Create(snapshot.RequestBody(target, conversationId, requestMessages, providerKey, mcp ?? session.Mcp?.Capture()), options: PortableConversations.Json)
            : localAgent is not null ? JsonContent.Create(LocalAgentRequest(localAgent, conversationId, requestMessages), options: PortableConversations.Json)
            : JsonContent.Create(new AgentRequest { Id = conversationId, Model = target, Messages = requestMessages }, options: PortableConversations.Json);
        if (service == ServiceKind.Ai) snapshot.ApplyHeaders(request, providerKey);
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
    public static async IAsyncEnumerable<string> ReadAsync(Stream stream, [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        var data = new StringBuilder();
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.Length > 2_000_000) throw new GatewayException(DesktopResources.Get("StreamSizeLimit"));
            if (line.Length == 0)
            {
                if (data.Length > 0) { yield return data.ToString().TrimEnd('\n'); data.Clear(); }
                continue;
            }
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var value = line[5..];
            if (value.StartsWith(' ')) value = value[1..];
            data.Append(value).Append('\n');
            if (data.Length > 2_000_000) throw new GatewayException(DesktopResources.Get("StreamSizeLimit"));
        }
        if (data.Length > 0) yield return data.ToString().TrimEnd('\n');
    }
}

/// <summary>Retains the wire payload for forward compatibility; known events use existing typed contracts.</summary>
public sealed record StreamEvent(string Type, JsonElement Raw, UIMessagePart? Part)
{
    public static StreamEvent Parse(string payload)
    {
        using var doc = JsonDocument.Parse(payload);
        var raw = doc.RootElement.Clone();
        var type = PortableConversations.String(raw, "type") ?? throw new JsonException("Missing event type.");
        // Assembly uses the wire shape, not the server converter's narrower part union.
        // This also preserves arbitrary provider fields and newer SDK event kinds.
        return new(type, raw, new PortableUIPart(raw));
    }
}
