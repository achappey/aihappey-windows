using System.Diagnostics;
using System.IO.Compression;
using System.Text;

namespace AIHappey.Desktop.Core;

internal sealed class DocumentExtractionLimitException() : InvalidOperationException(DesktopResources.Get("DocumentLimit"));

/// <summary>A fresh budget for every top-level call, shared by all ZIP/email descendants.
/// Parser calls are synchronous third-party code: the deadline is cooperative, not a hard process timeout.</summary>
internal sealed class DocumentExtractionContext(CancellationToken cancellationToken)
{
    internal const long MaximumExpandedBytes = 100L * 1024 * 1024;
    internal const int MaximumEntries = 1000;
    internal const int MaximumDepth = 3;
    private readonly Stopwatch watch = Stopwatch.StartNew();
    private long expandedBytes;
    private int entries;
    public CancellationToken CancellationToken { get; } = cancellationToken;

    public void Check(int depth = 0)
    {
        CancellationToken.ThrowIfCancellationRequested();
        if (depth > MaximumDepth || watch.Elapsed > TimeSpan.FromSeconds(15)) throw new DocumentExtractionLimitException();
    }
    public void AddEntries(int count)
    {
        Check();
        if (count < 0 || count > MaximumEntries - entries) throw new DocumentExtractionLimitException();
        entries += count;
    }
    public void AddBytes(long count)
    {
        Check();
        if (count < 0 || count > MaximumExpandedBytes - expandedBytes) throw new DocumentExtractionLimitException();
        expandedBytes += count;
    }
    public byte[] Read(Stream stream, long maximum = ComposerAttachments.MaximumFileBytes)
    {
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        int count;
        while ((count = stream.Read(buffer)) > 0)
        {
            AddBytes(count);
            if (count > maximum - output.Length) throw new DocumentExtractionLimitException();
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }
    // Validate every compressed package before a library can eagerly expand it. Check both
    // declared sizes and actual decompressed bytes; no entry is ever written to the filesystem.
    public void ValidatePackage(ReadOnlyMemory<byte> content)
    {
        using var input = new MemoryStream(content.ToArray(), false);
        using var archive = new ZipArchive(input, ZipArchiveMode.Read);
        CheckArchive(archive);
        var buffer = new byte[81920];
        foreach (var entry in archive.Entries)
        {
            using var stream = entry.Open();
            int count;
            while ((count = stream.Read(buffer)) > 0) AddBytes(count);
        }
    }
    public void CheckArchive(ZipArchive archive)
    {
        AddEntries(archive.Entries.Count);
        var names = new HashSet<string>(StringComparer.Ordinal);
        long declared = 0;
        foreach (var entry in archive.Entries)
        {
            Check();
            var path = SafePath(entry.FullName);
            if (!names.Add(path)) throw new InvalidDataException("Duplicate archive path.");
            if (entry.Length < 0 || entry.Length > MaximumExpandedBytes - declared) throw new DocumentExtractionLimitException();
            declared += entry.Length;
        }
        if (declared > MaximumExpandedBytes - expandedBytes) throw new DocumentExtractionLimitException();
    }
    internal static string SafePath(string path)
    {
        var normalized = path.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(normalized) || normalized.StartsWith('/') || normalized.Contains(':')
            || normalized.Any(char.IsControl) || normalized.Split('/').Any(p => p is ".." or "."))
            throw new InvalidDataException("Unsafe archive path.");
        return normalized;
    }
    internal static string Label(string value) => new(value.Select(c => char.IsControl(c) ? ' ' : c).ToArray());
}

internal sealed class DocumentTextBuilder(DocumentExtractionContext context)
{
    private readonly StringBuilder text = new();
    public int Length => text.Length;
    public void Append(string? value)
    {
        context.Check();
        if (value is null) return;
        if (value.Length > PdfDocumentTextExtractor.MaximumCharacters - text.Length) throw new DocumentExtractionLimitException();
        text.Append(value);
    }
    public void Line(string? value = null) { Append(value); Append("\n"); }
    public override string ToString() => text.ToString();
}

internal abstract class DocumentFormatTextExtractor(string[] extensions, string[] mediaTypes) : IDocumentTextExtractor
{
    public bool Supports(string filename, string mediaType) => extensions.Contains(Path.GetExtension(filename), StringComparer.OrdinalIgnoreCase)
        || mediaTypes.Contains(DocumentTextExtraction.NormalizeMediaType(mediaType), StringComparer.Ordinal);
    public Task<string?> ExtractAsync(ReadOnlyMemory<byte> content, CancellationToken ct)
        => Task.Run(() => ExtractAsync(content, new DocumentExtractionContext(ct), 0), ct);
    internal abstract Task<string?> ExtractAsync(ReadOnlyMemory<byte> content, DocumentExtractionContext context, int depth);
}
