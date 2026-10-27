using System.Text.Json;
using System.Text.Json.Nodes;

namespace AIHappey.Desktop.Core;

/// <summary>Non-streaming tool invocations. Uses host-owned auth; never retries inference or follows arbitrary URLs.</summary>
public sealed class LocalResponsesClient(DesktopChatClient gateway, HttpClient http)
{
    public async Task<JsonElement> CreateAsync(ServiceKind service, JsonObject body, Func<bool> isCurrent, CancellationToken ct,
        ChatPreferences? preferences = null, string? model = null)
    {
        void Check() { ct.ThrowIfCancellationRequested(); if (!isCurrent()) throw new OperationCanceledException(ct); }
        Check(); using var request = await gateway.RequestAsync(service, HttpMethod.Post, "v1/responses", ct); Check();
        if (service == ServiceKind.Ai) preferences?.ApplyHeaders(request, ChatPreferences.ResolveProvider(service, model ?? ""));
        request.Content = JsonContent.Create(body);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        Check(); await GatewayErrors.CheckResponseAsync(response, ct);
        var bytes = await DesktopCatalogClient.ReadBoundedAsync(response.Content, 100 * 1024 * 1024, ct);
        using var document = JsonDocument.Parse(bytes); Check();
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new LocalToolException("Invalid Responses API result.");
        return document.RootElement.Clone();
    }
}
