namespace AIHappey.Desktop.Core;

/// <summary>Reopenable transcription media, deliberately independent of chat attachment admission.</summary>
public sealed class TranscriptionSource
{
    public string Name { get; }
    public string MediaType { get; }
    public long Length { get; }
    private readonly Func<CancellationToken, Task<Stream>> open;

    public TranscriptionSource(string name, string mediaType, long length, Func<CancellationToken, Task<Stream>> open)
    {
        Name = name.Replace('\\', '/').Split('/').Last();
        if (string.IsNullOrWhiteSpace(Name) || length <= 0 || TranscriptionFiles.MediaType(Name, mediaType) is null)
            throw new InvalidOperationException(DesktopResources.Get("TranscriptionUnsupported"));
        MediaType = mediaType; Length = length; this.open = open;
    }

    public static TranscriptionSource FromPath(string name, string mediaType, string path) =>
        new(name, mediaType, new FileInfo(path).Length, ct =>
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<Stream>(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan));
        });

    public static TranscriptionSource FromAttachment(ComposerAttachment file)
    {
        if (file.IsLink) throw new InvalidOperationException(DesktopResources.Get("TranscriptionUnsupported"));
        return new(file.Name, file.MediaType, file.Content.Length, ct =>
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<Stream>(new MemoryStream(file.Content.ToArray(), false));
        });
    }

    public async Task CopyToAsync(Stream destination, CancellationToken ct)
    {
        await using var input = await open(ct);
        var block = new byte[81920]; long total = 0;
        while (await input.ReadAsync(block, ct) is var read && read > 0)
        {
            total = checked(total + read);
            if (total > Length) throw new IOException(DesktopResources.Get("TranscriptionSourceChanged"));
            await destination.WriteAsync(block.AsMemory(0, read), ct);
        }
        if (total != Length) throw new IOException(DesktopResources.Get("TranscriptionSourceChanged"));
    }
}
