using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AIHappey.Desktop.Core;

public sealed record TranscriptionResult(string Filename, string MediaType, string Model, JsonObject Options, JsonObject Response);

/// <summary>The same Vercel JSON API as the browser. No provider-specific client transports.</summary>
public sealed class DesktopTranscriptionClient(DesktopChatClient gateway, HttpClient http)
{
    public async Task<TranscriptionResult> GenerateAsync(ChatTarget model, ComposerAttachment file, TranscriptionPreferences preferences, CancellationToken ct)
    {
        var body = preferences.Clone().Request(model, file);
        using var request = await gateway.RequestAsync(ServiceKind.Ai, HttpMethod.Post, "api/transcriptions", ct);
        request.Content = JsonContent.Create(body, options: JsonSerializerOptions.Web);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        DesktopChatClient.CheckResponse(response);
        const int maximum = 16 * 1024 * 1024;
        if (response.Content.Headers.ContentLength > maximum) throw new GatewayException(DesktopResources.Get("TranscriptionInvalidResponse"));
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream(); var block = new byte[81920];
        while (await stream.ReadAsync(block, ct) is var read && read > 0)
        {
            if (buffer.Length + read > maximum) throw new GatewayException(DesktopResources.Get("TranscriptionInvalidResponse"));
            await buffer.WriteAsync(block.AsMemory(0, read), ct);
        }
        buffer.Position = 0;
        var result = (await JsonNode.ParseAsync(buffer, cancellationToken: ct)) as JsonObject;
        if (result is null || result["text"] is not JsonValue text || !text.TryGetValue<string>(out _))
            throw new GatewayException(DesktopResources.Get("TranscriptionInvalidResponse"));
        return new(file.Name, file.MediaType, model.Id, JsonSerializer.SerializeToNode(body.ProviderOptions, JsonSerializerOptions.Web)!.AsObject(), result);
    }
}
