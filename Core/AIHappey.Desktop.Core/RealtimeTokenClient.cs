using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace AIHappey.Desktop.Core;

public sealed class DesktopRealtimeTokenClient(DesktopChatClient client, HttpClient http)
{
    public async Task<string> CreateSessionAsync(string model, string sdp, JsonObject config, ChatPreferences preferences, CancellationToken ct)
    {
        using var request = await client.RequestAsync(ServiceKind.Ai, HttpMethod.Post,
            "api/realtime/session/" + string.Join('/', model.Split('/').Select(Uri.EscapeDataString)), ct);
        preferences.ApplyHeaders(request, "openai");
        request.Headers.Remove("X-AIHappey-Debug");
        request.Content = JsonContent.Create(new JsonObject { ["sdp"] = sdp, ["sessionConfig"] = config.DeepClone() });
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        DesktopChatClient.CheckResponse(response);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var limited = new MemoryStream(); var bytes = new byte[4096]; int count;
        while ((count = await stream.ReadAsync(bytes, ct)) > 0)
        {
            if (limited.Length + count > 512 * 1024) throw new GatewayException("Invalid realtime session response.");
            limited.Write(bytes, 0, count);
        }
        var result = RealtimeSession.Parse(limited.ToArray());
        if (string.IsNullOrWhiteSpace(result["sessionId"]?.ToString()) || string.IsNullOrWhiteSpace(result["sdp"]?.ToString()))
            throw new GatewayException("Invalid realtime session response.");
        return result["sdp"]!.GetValue<string>();
    }
    public async Task<string> GetAsync(JsonObject payload, ChatPreferences preferences, string provider, CancellationToken ct)
    {
        using var request = await client.RequestAsync(ServiceKind.Ai, HttpMethod.Post, "v1/realtime/client_secrets", ct);
        preferences.ApplyHeaders(request, provider);
        request.Headers.Remove("X-AIHappey-Debug"); // Token responses must never enter API-debug storage.
        request.Content = JsonContent.Create(payload);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        // Do not include upstream response bodies: they can contain credentials.
        DesktopChatClient.CheckResponse(response);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var limited = new MemoryStream();
        var bytes = new byte[4096]; int read;
        while ((read = await stream.ReadAsync(bytes, ct)) != 0)
        {
            if (limited.Length + read > 256 * 1024) throw new GatewayException("Invalid realtime credential response.");
            limited.Write(bytes, 0, read);
        }
        var token = RealtimeSession.Parse(limited.ToArray());
        var value = token["value"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(value) || token["expires_at"]?.GetValue<long>() <= DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            throw new GatewayException("The realtime credential is missing or expired.");
        return value;
    }
}
