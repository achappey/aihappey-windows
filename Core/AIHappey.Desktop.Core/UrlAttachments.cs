using System.Text.RegularExpressions;

namespace AIHappey.Desktop.Core;

/// <summary>Web-compatible URL file parts and MIME rules. Lookups are anonymous HEAD requests only.</summary>
public static partial class UrlAttachments
{
    public static readonly string[] CommonMediaTypes = ["text/html", "text/plain", "application/pdf", "application/json",
        "image/png", "image/jpeg", "image/webp", "audio/mpeg", "video/mp4"];

    public static bool IsHttpUrl(string? value) => Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https" && !string.IsNullOrEmpty(uri.Host) && string.IsNullOrEmpty(uri.UserInfo);

    [GeneratedRegex(@"^[a-z0-9][a-z0-9!#$&^_.+\-]*/[a-z0-9][a-z0-9!#$&^_.+\-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex MediaTypePattern();

    public static string? ValidMediaType(string? value)
    {
        var type = value?.Split(';')[0].Trim().ToLowerInvariant();
        return type is not null && type != "application/octet-stream" && MediaTypePattern().IsMatch(type) ? type : null;
    }

    public static string Filename(string value)
    {
        var uri = new Uri(value.Trim());
        var segment = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        return segment is null ? uri.Host : Uri.UnescapeDataString(segment);
    }

    public static string? TypeFromPath(string path) => Path.GetExtension(Uri.UnescapeDataString(path)).ToLowerInvariant() switch
    {
        ".pdf" => "application/pdf", ".json" or ".map" => "application/json", ".yaml" or ".yml" => "application/yaml",
        ".xml" => "application/xml", ".zip" => "application/zip", ".gz" => "application/gzip", ".7z" => "application/x-7z-compressed",
        ".txt" or ".log" => "text/plain", ".md" or ".markdown" => "text/markdown", ".csv" => "text/csv",
        ".html" or ".htm" => "text/html", ".css" => "text/css", ".js" or ".mjs" => "text/javascript",
        ".png" => "image/png", ".jpg" or ".jpeg" or ".jpe" => "image/jpeg", ".webp" => "image/webp", ".gif" => "image/gif",
        ".svg" => "image/svg+xml", ".bmp" => "image/bmp", ".tif" or ".tiff" => "image/tiff", ".avif" => "image/avif", ".ico" => "image/vnd.microsoft.icon",
        ".mp3" => "audio/mpeg", ".wav" => "audio/wav", ".ogg" or ".oga" => "audio/ogg", ".flac" => "audio/flac", ".m4a" => "audio/mp4", ".aac" => "audio/aac",
        ".mp4" or ".m4v" => "video/mp4", ".webm" => "video/webm", ".mov" => "video/quicktime", ".avi" => "video/x-msvideo", ".mpeg" or ".mpg" => "video/mpeg",
        ".doc" => "application/msword", ".xls" => "application/vnd.ms-excel", ".ppt" => "application/vnd.ms-powerpoint",
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        ".epub" => "application/epub+zip", ".rtf" => "application/rtf", ".eml" => "message/rfc822", _ => null
    };

    public static async Task<string?> ResolveMediaTypeAsync(string value, HttpClient anonymousHttp, CancellationToken ct = default)
    {
        if (!IsHttpUrl(value)) return null;
        ct.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(2500));
        try
        {
            // Use desktop's public-URL policy for probes. Manual MIME selection still permits an HTTP URL reference.
            var uri = AttachmentDownloads.RemoteUri(value);
            for (var redirects = 0; uri is not null && redirects < 6; redirects++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Head, uri);
                using var response = await anonymousHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if (response.IsSuccessStatusCode)
                {
                    var type = ValidMediaType(response.Content.Headers.ContentType?.ToString());
                    if (type is not null) return type;
                    break;
                }
                if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
                    uri = AttachmentDownloads.RemoteUri(new Uri(uri, location).AbsoluteUri);
                else break;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        catch (HttpRequestException) { }
        ct.ThrowIfCancellationRequested();
        return ValidMediaType(TypeFromPath(new Uri(value).AbsolutePath));
    }
}
