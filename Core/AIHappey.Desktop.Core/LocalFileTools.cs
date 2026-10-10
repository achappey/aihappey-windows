using System.Text;
using System.Text.Json;

namespace AIHappey.Desktop.Core;

public sealed record LocalFileTextPage(int TotalLines, int StartLine, int? EndLine, int LineCount, int MaxLines,
    bool Truncated, int? NextLine, string Text);

/// <summary>Read-only local documents. Paging is over extracted text, never PDF page coordinates.</summary>
public sealed class LocalFileTools(DocumentTextExtraction extraction, Func<bool>? isCurrent = null)
{
    public const string ToolName = "local_files_read";
    public const int MaximumPageCharacters = 50_000;
    public const int MaximumLines = 2000;
    private void Check(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (isCurrent?.Invoke() == false) throw new OperationCanceledException(ct);
    }

    public Task<JsonElement> CallAsync(string name, JsonElement input, CancellationToken ct) => DesktopLocalTools.SafeAsync(async () =>
    {
        Check(ct);
        if (name != ToolName) throw new LocalToolException("Unsupported file tool.");
        var path = LocalFilePaths.Normalize(DesktopLocalTools.Required(input, "path"));
        var startLine = DesktopLocalTools.Integer(input, "startLine", 1);
        var maxLines = DesktopLocalTools.Integer(input, "maxLines", 200, MaximumLines);
        var filename = Path.GetFileName(path);
        // Escape before the URL-oriented MIME helper decodes; a literal %2e in a local
        // filename must not be interpreted as an extension separator.
        var mediaType = UrlAttachments.TypeFromPath(Uri.EscapeDataString(filename)) ?? SkillFiles.MimeType(filename);
        if (!extraction.Supports(filename, mediaType)) throw new LocalToolException(DesktopResources.Get("LocalFileUnsupported"));
        string text; long size; DateTime modified;
        try
        {
            LocalFilePaths.CheckNoLinks(path); Check(ct);
            if (Directory.Exists(path)) throw new LocalToolException(DesktopResources.Get("LocalFileNotFile"));
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            LocalFilePaths.CheckOpenedFile(stream.SafeFileHandle, path); Check(ct);
            size = stream.Length;
            if (size > ComposerAttachments.MaximumFileBytes) throw new LocalToolException(DesktopResources.Get("LocalFileTooLarge"));
            modified = File.GetLastWriteTimeUtc(path);
            var bytes = new byte[(int)size];
            await stream.ReadExactlyAsync(bytes, ct);
            if (await stream.ReadAsync(new byte[1], ct) != 0) throw new LocalToolException(DesktopResources.Get("LocalFileChanged"));
            Check(ct);
            text = await extraction.ExtractAsync(filename, mediaType, bytes, ct)
                ?? (size == 0 && SkillFiles.IsText(mediaType) ? "" : throw new LocalToolException(DesktopResources.Get("LocalFileNoText")));
            Check(ct);
        }
        catch (FileNotFoundException) { throw new LocalToolException(DesktopResources.Get("LocalFileNotFound")); }
        catch (DirectoryNotFoundException) { throw new LocalToolException(DesktopResources.Get("LocalFileNotFound")); }
        catch (UnauthorizedAccessException) { throw new LocalToolException(DesktopResources.Get("LocalFileAccessDenied")); }
        catch (DecoderFallbackException) { throw new LocalToolException(DesktopResources.Get("LocalFileEncodingUnsupported")); }
        var page = Page(text, startLine, maxLines, ct); Check(ct);
        return DesktopLocalTools.Result(new { path, filename, mediaType, size, modifiedAt = modified.ToString("O"),
            page.TotalLines, page.StartLine, page.EndLine, page.LineCount, page.MaxLines, page.Truncated, page.NextLine, page.Text });
    }, ct);

    public static LocalFileTextPage Page(string text, int startLine = 1, int maxLines = 200, CancellationToken ct = default)
    {
        if (startLine < 1 || maxLines < 1) throw new LocalToolException("Line numbers and maxLines must be positive integers.");
        maxLines = Math.Min(maxLines, MaximumLines);
        using var reader = new StringReader(text);
        var result = new StringBuilder(); var total = 0; var count = 0; var stopped = false;
        while (reader.ReadLine() is { } line)
        {
            ct.ThrowIfCancellationRequested(); total++;
            if (total < startLine || stopped) continue;
            if (count == maxLines || result.Length + line.Length + (count > 0 ? 1 : 0) > MaximumPageCharacters)
            {
                if (count == 0) throw new LocalToolException(DesktopResources.Get("LocalFileLineTooLong"));
                stopped = true; continue;
            }
            if (count > 0) result.Append('\n');
            result.Append(line); count++;
        }
        var end = count == 0 ? (int?)null : startLine + count - 1;
        var next = end is { } last && last < total ? last + 1 : (int?)null;
        return new(total, startLine, end, count, maxLines, next is not null, next, result.ToString());
    }
}
