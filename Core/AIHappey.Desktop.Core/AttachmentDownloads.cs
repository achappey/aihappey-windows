using System.Net;
using System.Text;

namespace AIHappey.Desktop.Core;

/// <summary>Explicit, anonymous downloads only. No gateway/provider headers, file paths,
/// browser blob URLs, redirects, or automatic MCP resource execution.</summary>
public static class AttachmentDownloads
{
    public const int MaximumBytes = 64 * 1024 * 1024;
    public static string Identity(MessageAttachment file)
    {
        var binary = file.Base64;
        var mime = file.MediaType;
        if (file.Url?.StartsWith("data:", StringComparison.OrdinalIgnoreCase) == true)
        {
            var comma = file.Url.IndexOf(',');
            if (comma > 0 && file.Url[..comma].EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
            { mime = file.Url[5..comma].Split(';')[0]; binary = Uri.UnescapeDataString(file.Url[(comma + 1)..]); }
        }
        var payload = binary is not null ? "binary:" + mime + ":" + binary
            : file.Text is not null ? "text:" + mime + ":" + file.Text : file.Url ?? "resource:" + file.ResourceUri;
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }
    public static Uri? RemoteUri(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not "http" and not "https"
            || !string.IsNullOrEmpty(uri.UserInfo) || uri.IsLoopback || !uri.Host.Contains('.')
            || uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)) return null;
        if (IPAddress.TryParse(uri.Host.Trim('[', ']'), out var address))
        {
            var bytes = address.GetAddressBytes();
            if (IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal
                || bytes.Length == 4 && (bytes[0] is 0 or 10 or 127 || bytes[0] == 169 && bytes[1] == 254
                    || bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31 || bytes[0] == 192 && bytes[1] == 168)) return null;
        }
        return uri;
    }

    public static string Extension(string mime) => mime.Split(';')[0].ToLowerInvariant() switch
    {
        "application/pdf" => ".pdf", "application/json" => ".json", "text/plain" => ".txt", "text/html" => ".html", "text/csv" => ".csv",
        "image/png" => ".png", "image/jpeg" => ".jpg", "image/webp" => ".webp", "image/gif" => ".gif", "image/svg+xml" => ".svg",
        "audio/mpeg" => ".mp3", "audio/wav" => ".wav", "audio/ogg" => ".ogg", "video/mp4" => ".mp4", "application/zip" => ".zip",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document" => ".docx",
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" => ".xlsx", _ => ".bin"
    };
    public static string SafeName(string name, string mime)
    {
        // Never use an MCP/remote-provided name as a filesystem path.
        name = name.Replace('\\', '/').Split('/').Last();
        var invalid = Path.GetInvalidFileNameChars();
        name = new string(name.Where(c => !invalid.Contains(c) && !char.IsControl(c)).Take(180).ToArray()).Trim(' ', '.');
        if (string.IsNullOrWhiteSpace(name)) name = "attachment";
        var stem = Path.GetFileNameWithoutExtension(name);
        if (new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }.Contains(stem, StringComparer.OrdinalIgnoreCase)) name = "_" + name;
        if (string.IsNullOrWhiteSpace(Path.GetExtension(name))) name += Extension(mime);
        return name;
    }
    public static string NameFromUri(string? value, string mime)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme != "data")
            return SafeName(Uri.UnescapeDataString(uri.AbsolutePath.Split('/').Last()), mime);
        return "attachment" + Extension(mime);
    }
    public static bool CanDownload(MessageAttachment file) => file.Base64 is not null || file.Text is not null
        || file.Url?.StartsWith("data:", StringComparison.OrdinalIgnoreCase) == true || RemoteUri(file.Url) is not null
        || !string.IsNullOrWhiteSpace(file.Url) && !file.Url.Contains(':') && !file.Url.StartsWith('/') && !file.Url.Contains('\\');

    public static byte[]? EmbeddedBytes(MessageAttachment file)
    {
        byte[]? bytes = null;
        if (file.Text is not null) bytes = Encoding.UTF8.GetBytes(file.Text);
        else if (file.Base64 is not null) bytes = DecodeBase64(file.Base64);
        else if (file.Url?.StartsWith("data:", StringComparison.OrdinalIgnoreCase) == true)
        {
            var comma = file.Url.IndexOf(',');
            if (comma < 0) throw new InvalidOperationException("The attachment contains an invalid data URL.");
            var header = file.Url[..comma]; var payload = file.Url[(comma + 1)..];
            if (payload.Length > MaximumBytes * 4L) throw new InvalidOperationException("This attachment exceeds the 64 MB download limit.");
            bytes = header.EndsWith(";base64", StringComparison.OrdinalIgnoreCase) ? DecodeBase64(Uri.UnescapeDataString(payload)) : DecodePercentBytes(payload);
        }
        else if (!string.IsNullOrWhiteSpace(file.Url) && !file.Url.Contains(':') && !file.Url.StartsWith('/') && !file.Url.Contains('\\')) bytes = DecodeBase64(file.Url);
        if (bytes?.Length > MaximumBytes) throw new InvalidOperationException("This attachment exceeds the 64 MB download limit.");
        return bytes;
    }
    private static byte[] DecodeBase64(string text)
    {
        if (text.Length > (MaximumBytes + 2L) / 3 * 4) throw new InvalidOperationException("This attachment exceeds the 64 MB download limit.");
        try { return Convert.FromBase64String(text); }
        catch (FormatException) { throw new InvalidOperationException("The attachment contains invalid binary data."); }
    }
    private static byte[] DecodePercentBytes(string payload)
    {
        using var output = new MemoryStream();
        for (var index = 0; index < payload.Length; index++)
        {
            if (payload[index] == '%' && index + 2 < payload.Length && byte.TryParse(payload.AsSpan(index + 1, 2), System.Globalization.NumberStyles.HexNumber, null, out var value))
            { output.WriteByte(value); index += 2; }
            else
            {
                var count = char.IsHighSurrogate(payload[index]) && index + 1 < payload.Length && char.IsLowSurrogate(payload[index + 1]) ? 2 : 1;
                output.Write(Encoding.UTF8.GetBytes(payload.Substring(index, count))); index += count - 1;
            }
            if (output.Length > MaximumBytes) throw new InvalidOperationException("This attachment exceeds the 64 MB download limit.");
        }
        return output.ToArray();
    }
    public static async Task WriteAsync(MessageAttachment file, Stream destination, HttpClient http, CancellationToken ct)
    {
        if (EmbeddedBytes(file) is { } bytes) { await destination.WriteAsync(bytes, ct); return; }
        var uri = RemoteUri(file.Url) ?? throw new InvalidOperationException("This resource requires browser or MCP access and cannot be downloaded by desktop yet.");
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"The attachment server returned HTTP {(int)response.StatusCode}. No credentials or automatic redirects were sent.");
        if (response.Content.Headers.ContentLength > MaximumBytes) throw new InvalidOperationException("This attachment exceeds the 64 MB download limit.");
        await using var source = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[81920]; long total = 0;
        while (await source.ReadAsync(buffer, ct) is var length && length > 0)
        {
            total += length;
            if (total > MaximumBytes) throw new InvalidOperationException("This attachment exceeds the 64 MB download limit.");
            await destination.WriteAsync(buffer.AsMemory(0, length), ct);
        }
    }
}
