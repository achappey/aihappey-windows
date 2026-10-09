using System.Net.Http.Json;
using System.Text.Json;
using AIHappey.Vercel.Models;

namespace AIHappey.Desktop.Core;

public sealed record ImageGenerationBatch(ImageRequest Request, JsonElement Response)
{
    public IReadOnlyList<string> Images => Response.TryGetProperty("images", out var images) && images.ValueKind == JsonValueKind.Array
        ? images.EnumerateArray().Select(i => i.ValueKind == JsonValueKind.String ? i.GetString()! : throw new JsonException()).ToArray() : [];
    public IReadOnlyList<string> Warnings => Response.TryGetProperty("warnings", out var warnings) && warnings.ValueKind == JsonValueKind.Array
        ? warnings.EnumerateArray().Select(w => w.ValueKind == JsonValueKind.String ? w.GetString()! : w.GetRawText()).ToArray() : [];
    public double? Cost(int index)
    {
        if (!Response.TryGetProperty("providerMetadata", out var metadata) || !metadata.TryGetProperty("gateway", out var gateway)) return null;
        static double? Number(JsonElement node) => node.ValueKind == JsonValueKind.Number && node.TryGetDouble(out var n) && double.IsFinite(n) ? n : null;
        if (gateway.TryGetProperty("images", out var images) && images.ValueKind == JsonValueKind.Array && index < images.GetArrayLength()
            && images[index].TryGetProperty("cost", out var perImage)) return Number(perImage);
        return gateway.TryGetProperty("cost", out var cost) && Images.Count > 0 ? Number(cost) / Images.Count : null;
    }
}

/// <summary>Uses the same host authentication/runtime policy as chat. No inference retries.</summary>
public sealed class DesktopImageClient(DesktopChatClient gateway, HttpClient http)
{
    public async Task GenerateAsync(ChatTarget model, string prompt, ImagePreferences preferences,
        IReadOnlyList<ComposerAttachment> attachments, ComposerAttachment? mask,
        Func<ImageGenerationBatch, Task> completed, CancellationToken ct)
    {
        var snapshot = preferences.Clone(); snapshot.Validate();
        var files = attachments.ToArray();
        // Sequential batches bound memory and commit every successful response before the next
        // paid request. A later failure/cancel never hides an already completed batch.
        var limit = Math.Min(snapshot.MaxImagesPerCall ?? snapshot.N, snapshot.N);
        for (var remaining = snapshot.N; remaining > 0; remaining -= limit)
        {
            ct.ThrowIfCancellationRequested();
            var body = snapshot.Request(model, prompt, files, mask, Math.Min(limit, remaining));
            using var request = await gateway.RequestAsync(ServiceKind.Ai, HttpMethod.Post, "api/images", ct);
            request.Content = JsonContent.Create(body, options: JsonSerializerOptions.Web);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            DesktopChatClient.CheckResponse(response);
            // Bound untrusted responses; keep enough room for several original-resolution images.
            const long maximum = 160L * 1024 * 1024;
            if (response.Content.Headers.ContentLength > maximum) throw new GatewayException(DesktopResources.Get("ImageResponseInvalid"));
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var buffer = new MemoryStream(); var block = new byte[81920];
            while (await stream.ReadAsync(block, ct) is var read && read > 0)
            {
                if (buffer.Length + read > maximum) throw new GatewayException(DesktopResources.Get("ImageResponseInvalid"));
                await buffer.WriteAsync(block.AsMemory(0, read), ct);
            }
            buffer.Position = 0;
            using var document = await JsonDocument.ParseAsync(buffer, cancellationToken: ct);
            var batch = new ImageGenerationBatch(body, document.RootElement.Clone());
            if (batch.Images.Count == 0 || batch.Images.Count > 20) throw new GatewayException(DesktopResources.Get("ImageResponseInvalid"));
            // Once returned, a successful batch is committed even if Stop was just pressed.
            await completed(batch);
        }
    }
}
