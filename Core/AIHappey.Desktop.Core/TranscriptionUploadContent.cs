using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AIHappey.Desktop.Core;

/// <summary>The existing gateway JSON contract, streamed with base64 instead of buffering the recording.</summary>
public sealed class TranscriptionUploadContent : HttpContent
{
    private readonly TranscriptionSource source;
    private readonly byte[] prefix;
    private readonly CancellationToken cancellationToken;
    public TranscriptionUploadContent(string model, TranscriptionSource source, JsonObject options, CancellationToken ct)
    {
        this.source = source; cancellationToken = ct;
        var metadata = JsonSerializer.Serialize(new { model, mediaType = source.MediaType, providerOptions = options }, JsonSerializerOptions.Web);
        prefix = Encoding.UTF8.GetBytes(metadata[..^1] + ",\"audio\":\"");
        Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
    }
    protected override bool TryComputeLength(out long length)
    {
        length = checked(prefix.LongLength + (source.Length + 2) / 3 * 4 + 2); return true;
    }
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => WriteAsync(stream, cancellationToken);
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken ct) => WriteAsync(stream, ct);
    private async Task WriteAsync(Stream stream, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, cancellationToken); ct = linked.Token;
        await stream.WriteAsync(prefix, ct);
        using var transform = new ToBase64Transform();
        await using (var base64 = new CryptoStream(stream, transform, CryptoStreamMode.Write, leaveOpen: true))
        {
            await source.CopyToAsync(base64, ct);
            await base64.FlushFinalBlockAsync(ct);
        }
        await stream.WriteAsync("\"}"u8.ToArray(), ct);
    }
}
