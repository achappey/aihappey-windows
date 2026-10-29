using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AIHappey.Vercel.Models;

namespace AIHappey.Desktop.Core;

public sealed record SpeechResult(SpeechRequest Request, JsonObject Response, byte[] Audio, string MediaType, string Format)
{
    public IReadOnlyList<string> Warnings => Response["warnings"] is JsonArray warnings
        ? warnings.Select(w => w is JsonValue v && v.TryGetValue<string>(out var text) ? text : w?.ToJsonString() ?? "").Where(x => x.Length > 0).ToArray() : [];
}

/// <summary>The same Vercel AI SDK JSON endpoint and request contract as the browser.</summary>
public sealed class DesktopSpeechClient(DesktopChatClient gateway, HttpClient http)
{
    private static readonly JsonSerializerOptions RequestJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public Task<SpeechResult> GenerateAsync(ChatTarget model, string text, SpeechPreferences preferences, CancellationToken ct,
        ChatPreferences? requestPreferences = null)
        => GenerateAsync(preferences.Clone().Request(model, text), ct, requestPreferences);

    public async Task<SpeechResult> GenerateAsync(SpeechRequest body, CancellationToken ct, ChatPreferences? requestPreferences = null)
    {
        using var request = await gateway.RequestAsync(ServiceKind.Ai, HttpMethod.Post, "api/speech", ct);
        requestPreferences?.ApplyHeaders(request, DesktopAgent.Provider(body.Model));
        request.Content = JsonContent.Create(body, options: RequestJson);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        await GatewayErrors.CheckResponseAsync(response, ct);
        const int maximum = 140 * 1024 * 1024;
        if (response.Content.Headers.ContentLength > maximum) throw new GatewayException(DesktopResources.Get("SpeechResponseInvalid"));
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream(); var block = new byte[81920];
        while (await stream.ReadAsync(block, ct) is var read && read > 0)
        {
            if (buffer.Length + read > maximum) throw new GatewayException(DesktopResources.Get("SpeechResponseInvalid"));
            await buffer.WriteAsync(block.AsMemory(0, read), ct);
        }
        JsonObject result;
        try { result = JsonNode.Parse(buffer.ToArray())?.AsObject() ?? throw new JsonException(); }
        catch (Exception error) when (error is JsonException or InvalidOperationException)
        { throw new GatewayException(DesktopResources.Get("SpeechResponseInvalid")); }
        var decoded = SpeechAudio.Decode(result["audio"]);
        return new(body, result, decoded.Bytes, decoded.MediaType, decoded.Format);
    }
}
