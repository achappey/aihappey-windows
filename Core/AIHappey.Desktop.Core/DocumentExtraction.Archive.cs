using System.IO.Compression;

namespace AIHappey.Desktop.Core;

internal sealed class ZipDocumentTextExtractor(DocumentTextExtraction extraction) : DocumentFormatTextExtractor([".zip"], ["application/zip", "application/x-zip-compressed"])
{
    internal override async Task<string?> ExtractAsync(ReadOnlyMemory<byte> content, DocumentExtractionContext context, int depth)
    {
        using var input = new MemoryStream(content.ToArray(), false);
        using var archive = new ZipArchive(input, ZipArchiveMode.Read);
        context.CheckArchive(archive);
        var text = new DocumentTextBuilder(context);
        foreach (var entry in archive.Entries.OrderBy(e => e.FullName, StringComparer.Ordinal))
        {
            context.Check(depth);
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) continue;
            var filename = DocumentExtractionContext.SafePath(entry.FullName);
            var mime = UrlAttachments.TypeFromPath(Uri.EscapeDataString(filename)) ?? SkillFiles.MimeType(filename);
            if (!extraction.Supports(filename, mime))
            { DocumentContainerText.Notice(text, filename, "DocumentEntryUnsupported"); continue; }
            // Nested budget failures and cancellation are fatal, never converted into a partial success.
            if (entry.Length > ComposerAttachments.MaximumFileBytes) throw new DocumentExtractionLimitException();
            byte[] bytes;
            using (var stream = entry.Open()) bytes = context.Read(stream);
            await DocumentContainerText.AppendAsync(text, extraction, filename, mime, bytes, context, depth + 1);
        }
        return text.ToString().Trim();
    }
}

internal static class DocumentContainerText
{
    internal static void Notice(DocumentTextBuilder text, string filename, string resource)
        => text.Line(DesktopResources.Format(resource, DocumentExtractionContext.Label(filename)));

    internal static async Task AppendAsync(DocumentTextBuilder text, DocumentTextExtraction extraction, string filename,
        string mime, ReadOnlyMemory<byte> bytes, DocumentExtractionContext context, int depth)
    {
        context.Check(depth);
        if (!extraction.Supports(filename, mime)) { Notice(text, filename, "DocumentEntryUnsupported"); return; }
        try
        {
            var body = await extraction.ExtractCoreAsync(filename, mime, bytes, context, depth);
            if (string.IsNullOrWhiteSpace(body)) { Notice(text, filename, "DocumentEntryNoText"); return; }
            text.Line($"[File: {DocumentExtractionContext.Label(filename)}]"); text.Line(body); text.Line();
        }
        catch (OperationCanceledException) { throw; }
        catch (DocumentExtractionLimitException) { throw; }
        catch (Exception e) when (e is not OutOfMemoryException)
        { Notice(text, filename, "DocumentEntryFailed"); }
    }
}
