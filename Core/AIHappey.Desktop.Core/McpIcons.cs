using System.Text;
using System.Text.Json;

namespace AIHappey.Desktop.Core;

public sealed record McpIcon(string Source, string? Theme = null, string? MimeType = null);

/// <summary>Presentation-only metadata; never uses MCP credentials or dereferences resource URIs.</summary>
public static class McpIcons
{
    public const int MaximumEmbeddedBytes = 256 * 1024;
    public static IReadOnlyList<McpIcon> Read(JsonElement server)
    {
        if (server.ValueKind != JsonValueKind.Object || !server.TryGetProperty("icons", out var icons) || icons.ValueKind != JsonValueKind.Array) return [];
        return icons.EnumerateArray().Take(32).Select(icon => CatalogProjection.Text(icon, "src") is { } source
            ? new McpIcon(source, CatalogProjection.Text(icon, "theme"), CatalogProjection.Text(icon, "mimeType")) : null)
            .Where(icon => icon is not null && Supported(icon)).Cast<McpIcon>().ToArray();
    }

    public static IReadOnlyList<McpIcon> ForServer(McpConnectionView server, IReadOnlyList<McpIcon>? fallback = null)
    {
        var discovered = server.Discovery is { } discovery ? Read(discovery.ServerInfo) : [];
        return discovered.Count > 0 ? discovered : fallback is { Count: > 0 } ? fallback : server.Server.Icons;
    }

    public static McpIcon? Select(IEnumerable<McpIcon> icons, string theme)
    {
        var usable = icons.Where(Supported).ToArray();
        return usable.FirstOrDefault(icon => string.Equals(icon.Theme, theme, StringComparison.OrdinalIgnoreCase))
            ?? usable.FirstOrDefault(icon => string.IsNullOrEmpty(icon.Theme)) ?? usable.FirstOrDefault();
    }

    public static bool Supported(McpIcon icon)
    {
        if (string.IsNullOrWhiteSpace(icon.Source) || icon.Source.Length > MaximumEmbeddedBytes * 4) return false;
        if (icon.Source.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = icon.Source.IndexOf(',');
            return comma > 5 && ImageMime(icon.Source[5..comma].Split(';')[0]);
        }
        return AttachmentDownloads.RemoteUri(icon.Source) is not null
            && (icon.MimeType is null || ImageMime(icon.MimeType));
    }

    public static bool IsSvg(McpIcon icon) => string.Equals(icon.MimeType, "image/svg+xml", StringComparison.OrdinalIgnoreCase)
        || icon.Source.StartsWith("data:image/svg+xml", StringComparison.OrdinalIgnoreCase)
        || Uri.TryCreate(icon.Source, UriKind.Absolute, out var uri) && uri.AbsolutePath.EndsWith(".svg", StringComparison.OrdinalIgnoreCase);

    public static byte[] EmbeddedBytes(McpIcon icon)
    {
        if (!Supported(icon) || !icon.Source.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) throw new FormatException("Invalid icon data.");
        var comma = icon.Source.IndexOf(',');
        var payload = Uri.UnescapeDataString(icon.Source[(comma + 1)..]);
        var bytes = icon.Source[..comma].EndsWith(";base64", StringComparison.OrdinalIgnoreCase)
            ? Convert.FromBase64String(payload) : Encoding.UTF8.GetBytes(payload);
        return bytes.Length <= MaximumEmbeddedBytes ? bytes : throw new FormatException("Icon data limit.");
    }

    private static bool ImageMime(string mime) => mime.ToLowerInvariant() is "image/png" or "image/jpeg" or "image/gif"
        or "image/webp" or "image/bmp" or "image/x-icon" or "image/vnd.microsoft.icon" or "image/svg+xml";
}
