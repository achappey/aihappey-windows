using AIHappey.Vercel.Models;

namespace AIHappey.Desktop.Core;

/// <summary>An admitted, immutable draft snapshot. Local paths are never sent or retained in history.</summary>
public sealed class ComposerAttachment
{
    private readonly byte[]? bytes;
    public string Name { get; }
    public string MediaType { get; }
    public string? RemoteUrl { get; }
    public bool IsLink => RemoteUrl is not null;
    internal ReadOnlyMemory<byte> Content => bytes ?? ReadOnlyMemory<byte>.Empty;

    private ComposerAttachment(string name, string mediaType, byte[]? bytes, string? url)
    { Name = name; MediaType = mediaType; this.bytes = bytes; RemoteUrl = url; }

    public static ComposerAttachment Local(string filename, string? mediaType, ReadOnlyMemory<byte> content)
    {
        ComposerAttachments.ValidateSize(content.Length);
        var name = filename.Replace('\\', '/').Split('/').Last();
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("An attachment needs a filename.");
        var type = UrlAttachments.ValidMediaType(mediaType) ?? UrlAttachments.TypeFromPath(name) ?? "application/octet-stream";
        return new(name, type, content.ToArray(), null);
    }

    public static ComposerAttachment Link(string value, string mediaType)
    {
        var url = value.Trim();
        var type = UrlAttachments.ValidMediaType(mediaType);
        if (!UrlAttachments.IsHttpUrl(url) || type is null) throw new ArgumentException("Invalid URL or media type.");
        return new(UrlAttachments.Filename(url), type, null, url);
    }

    internal FileUIPart FilePart() => new()
    {
        Filename = Name, MediaType = MediaType,
        Url = RemoteUrl ?? $"data:{MediaType};base64,{Convert.ToBase64String(bytes!)}"
    };
}

public sealed record PreparedComposerMessage(UIMessage Message, IReadOnlyList<string> Warnings);

public static class ComposerAttachments
{
    public const int MaximumFileBytes = 25 * 1024 * 1024;
    public static void ValidateSize(long size)
    {
        if (size < 0 || size > MaximumFileBytes) throw new InvalidOperationException("The attachment exceeds the 25 MB per-file limit.");
    }

    /// <summary>Shared picker/drop admission. Keep successful files, but never admit into a stale draft.</summary>
    public static async Task<IReadOnlyList<string>> AdmitAsync<T>(IEnumerable<T> files, Func<T, string> name,
        Func<T, CancellationToken, Task<ComposerAttachment>> read, Action<ComposerAttachment> admit,
        Func<bool> isCurrent, CancellationToken ct)
    {
        var rejected = new List<string>();
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            if (!isCurrent()) break;
            try
            {
                var attachment = await read(file, ct);
                ct.ThrowIfCancellationRequested();
                if (!isCurrent()) break;
                admit(attachment);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException
                or System.Runtime.InteropServices.COMException)
            { ct.ThrowIfCancellationRequested(); rejected.Add(name(file)); }
        }
        return rejected;
    }

    public static async Task<ComposerAttachment> ReadAsync(string filename, string? mediaType, Stream source, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var block = new byte[81920];
        while (await source.ReadAsync(block, ct) is var read && read > 0)
        {
            ValidateSize(buffer.Length + read);
            await buffer.WriteAsync(block.AsMemory(0, read), ct);
        }
        return await Task.Run(() => ComposerAttachment.Local(filename, mediaType, buffer.ToArray()), ct);
    }

    public static async Task<PreparedComposerMessage> PrepareAsync(string prompt, IReadOnlyList<ComposerAttachment> attachments,
        ServiceKind service, bool extractDocuments, IDocumentTextExtractor extractor, CancellationToken ct)
    {
        // Snapshot both settings and attachments before any asynchronous extraction.
        var snapshot = attachments.ToArray();
        var parts = new List<UIMessagePart>();
        var warnings = new List<string>();
        if (extractDocuments && service == ServiceKind.Ai)
            foreach (var file in snapshot.Where(file => !file.IsLink && extractor.Supports(file.Name, file.MediaType)))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var text = await extractor.ExtractAsync(file.Content, ct);
                    if (!string.IsNullOrWhiteSpace(text))
                        // Same PDF/default-MIME wrapping as the web's toMarkdownLinkSmart.
                        parts.Add(new TextUIPart { Text = $"<details><summary>{file.Name}</summary>\n\n\n{text}\n\n\n</details>" });
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception e) when (e is not OutOfMemoryException)
                { warnings.Add($"Text could not be extracted from {file.Name}. The original file is still included."); }
            }
        parts.AddRange(await Task.Run(() => snapshot.OrderBy(file => file.IsLink).Select(file =>
        { ct.ThrowIfCancellationRequested(); return (UIMessagePart)file.FilePart(); }).ToArray(), ct));
        if (!string.IsNullOrWhiteSpace(prompt)) parts.Add(new TextUIPart { Text = prompt.Trim() });
        ct.ThrowIfCancellationRequested();
        if (parts.Count == 0) throw new InvalidOperationException("Add a message, attachment, or link before sending.");
        return new(new UIMessage
        {
            Id = Guid.NewGuid().ToString("N"), Role = Role.user, Parts = parts,
            Metadata = new Dictionary<string, object> { ["timestamp"] = DateTimeOffset.UtcNow.ToString("O") }
        }, warnings);
    }
}
