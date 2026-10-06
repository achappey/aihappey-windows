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

public sealed class PdfDocumentTextExtractor : IDocumentTextExtractor
{
    public const int MaximumPages = 2000;
    public const int MaximumCharacters = 2_000_000;

    public bool Supports(string filename, string mediaType) => mediaType == "application/pdf"
        || filename.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) && !mediaType.StartsWith("text/", StringComparison.Ordinal);

    public Task<string?> ExtractAsync(ReadOnlyMemory<byte> content, CancellationToken ct) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        if (content.Length > ComposerAttachments.MaximumFileBytes) throw new InvalidOperationException("The PDF exceeds the attachment limit.");
        using var document = PdfDocument.Open(content.ToArray());
        if (document.NumberOfPages > MaximumPages) throw new InvalidOperationException("The PDF exceeds the text extraction page limit.");
        var text = new StringBuilder();
        var watch = Stopwatch.StartNew();
        foreach (var page in document.GetPages())
        {
            ct.ThrowIfCancellationRequested();
            var pageText = ContentOrderTextExtractor.GetText(page);
            if (text.Length + pageText.Length + 1 > MaximumCharacters || watch.Elapsed > TimeSpan.FromSeconds(15))
                throw new InvalidOperationException("The PDF exceeds the text extraction limit.");
            text.AppendLine(pageText);
        }
        ct.ThrowIfCancellationRequested();
        var result = text.ToString().Trim();
        return result.Length == 0 ? null : result;
    }, ct);
}
