using System.Diagnostics;
using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace AIHappey.Desktop.Core;

/// <summary>One small extension point for local document formats. No downloading or OCR.</summary>
public interface IDocumentTextExtractor
{
    bool Supports(string filename, string mediaType);
    Task<string?> ExtractAsync(ReadOnlyMemory<byte> content, CancellationToken ct);
}

/// <summary>Shared format dispatch for composer conversion, attachment reads, and local file reads.
/// Register new format handlers here; callers do not need format-specific branches.</summary>
public sealed class DocumentTextExtraction(IEnumerable<IDocumentTextExtractor>? handlers = null)
{
    private readonly IDocumentTextExtractor[] handlers = (handlers ?? [new PdfDocumentTextExtractor(), new PlainTextDocumentExtractor()]).ToArray();
    public bool Supports(string filename, string mediaType) => handlers.Any(h => h.Supports(filename, mediaType));
    public async Task<string?> ExtractAsync(string filename, string mediaType, ReadOnlyMemory<byte> content, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); ComposerAttachments.ValidateSize(content.Length);
        var handler = handlers.FirstOrDefault(h => h.Supports(filename, mediaType));
        if (handler is null) return null;
        var text = await handler.ExtractAsync(content, ct);
        ct.ThrowIfCancellationRequested();
        if (text?.Length > PdfDocumentTextExtractor.MaximumCharacters)
            throw new InvalidOperationException(DesktopResources.Get("PdfTextLimit"));
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }
}

public sealed class PlainTextDocumentExtractor : IDocumentTextExtractor
{
    public bool Supports(string filename, string mediaType) => SkillFiles.IsText(mediaType.ToLowerInvariant())
        || SkillFiles.IsText(SkillFiles.MimeType(filename)) || filename.EndsWith(".log", StringComparison.OrdinalIgnoreCase);
    public Task<string?> ExtractAsync(ReadOnlyMemory<byte> content, CancellationToken ct) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested(); ComposerAttachments.ValidateSize(content.Length);
        // StreamReader recognizes UTF-8/UTF-16/UTF-32 BOMs; otherwise decode strict UTF-8.
        using var reader = new StreamReader(new MemoryStream(content.ToArray()), new UTF8Encoding(false, true), true);
        var text = new StringBuilder(); var buffer = new char[8192];
        while (reader.Read(buffer, 0, buffer.Length) is var read && read > 0)
        {
            ct.ThrowIfCancellationRequested();
            if (text.Length + read > PdfDocumentTextExtractor.MaximumCharacters)
                throw new InvalidOperationException(DesktopResources.Get("PdfTextLimit"));
            text.Append(buffer, 0, read);
        }
        return (string?)text.ToString();
    }, ct);
}

public sealed class PdfDocumentTextExtractor : IDocumentTextExtractor
{
    public const int MaximumPages = 2000;
    public const int MaximumCharacters = 2_000_000;

    public bool Supports(string filename, string mediaType) => mediaType == "application/pdf"
        || filename.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) && !mediaType.StartsWith("text/", StringComparison.Ordinal);

    public Task<string?> ExtractAsync(ReadOnlyMemory<byte> content, CancellationToken ct) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        if (content.Length > ComposerAttachments.MaximumFileBytes) throw new InvalidOperationException(DesktopResources.Get("PdfAttachmentLimit"));
        using var document = PdfDocument.Open(content.ToArray());
        if (document.NumberOfPages > MaximumPages) throw new InvalidOperationException(DesktopResources.Get("PdfPageLimit"));
        var text = new StringBuilder();
        var watch = Stopwatch.StartNew();
        foreach (var page in document.GetPages())
        {
            ct.ThrowIfCancellationRequested();
            var pageText = ContentOrderTextExtractor.GetText(page);
            if (text.Length + pageText.Length + 1 > MaximumCharacters || watch.Elapsed > TimeSpan.FromSeconds(15))
                throw new InvalidOperationException(DesktopResources.Get("PdfTextLimit"));
            text.AppendLine(pageText);
        }
        ct.ThrowIfCancellationRequested();
        var result = text.ToString().Trim();
        return result.Length == 0 ? null : result;
    }, ct);
}
