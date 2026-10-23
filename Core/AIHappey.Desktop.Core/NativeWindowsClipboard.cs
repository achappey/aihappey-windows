using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace AIHappey.Desktop.Core;

/// <summary>WinRT clipboard access stays on the shell's UI thread. Never subscribes to clipboard events.</summary>
internal sealed class NativeWindowsClipboard(DispatcherQueue dispatcher, Func<bool> isCurrent)
{
    public Task<string?> ReadAsync(CancellationToken ct) => OnUiAsync(async token =>
    {
        var content = Clipboard.GetContent();
        if (!content.Contains(StandardDataFormats.Text)) return null;
        return await content.GetTextAsync().AsTask(token);
    }, ct);

    public async Task WriteAsync(string text, CancellationToken ct) => await OnUiAsync(token =>
    {
        token.ThrowIfCancellationRequested();
        var content = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        content.SetText(text);
        SetContent(content, token);
        return Task.FromResult(true);
    }, ct);

    public Task<LocalClipboardImage?> ReadImageAsync(CancellationToken ct) => OnUiAsync<LocalClipboardImage?>(async token =>
    {
        var content = Clipboard.GetContent();
        if (!content.Contains(StandardDataFormats.Bitmap)) return null;
        var bitmap = await content.GetBitmapAsync().AsTask(token); Check(token);
        using var stream = await bitmap.OpenReadAsync().AsTask(token); Check(token);
        return await DecodeImageAsync(stream, token);
    }, ct);

    public async Task WriteImageAsync(string path, CancellationToken ct) => await OnUiAsync(async token =>
    {
        path = LocalClipboardTools.ValidateFilePath(path);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        LocalFilePaths.CheckOpenedFile(file.SafeFileHandle, path);
        using var input = file.AsRandomAccessStream();
        var image = await DecodeImageAsync(input, token); Check(token);
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(image.Png); await writer.StoreAsync().AsTask(token);
        }
        stream.Seek(0); Check(token);
        var content = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        content.SetBitmap(RandomAccessStreamReference.CreateFromStream(stream));
        SetContent(content, token); // Flush while the bitmap stream is still alive.
        return true;
    }, ct);

    public Task<LocalClipboardFiles> ReadFilesAsync(CancellationToken ct) => OnUiAsync(async token =>
    {
        var content = Clipboard.GetContent();
        if (!content.Contains(StandardDataFormats.StorageItems)) return new LocalClipboardFiles([]);
        var items = await content.GetStorageItemsAsync().AsTask(token); Check(token);
        var paths = new List<string>(); var unsupported = 0;
        foreach (var item in items.Take(LocalClipboardTools.MaximumFiles))
        {
            Check(token);
            // Virtual items/folders/network paths never become references or trigger content reads.
            if (!item.IsOfType(StorageItemTypes.File) || string.IsNullOrEmpty(item.Path)) { unsupported++; continue; }
            try { paths.Add(LocalClipboardTools.ValidateFilePath(item.Path)); }
            catch (Exception error) when (error is LocalToolException or IOException or UnauthorizedAccessException) { unsupported++; }
        }
        return new LocalClipboardFiles(paths, items.Count > LocalClipboardTools.MaximumFiles, unsupported);
    }, ct);

    public async Task WriteFilesAsync(IReadOnlyList<string> paths, CancellationToken ct) => await OnUiAsync(async token =>
    {
        if (paths.Count is < 1 or > LocalClipboardTools.MaximumFiles) throw new LocalToolException(DesktopResources.Get("LocalClipboardFilesInvalid"));
        var items = new List<IStorageItem>();
        foreach (var path in paths)
        {
            Check(token); var local = LocalClipboardTools.ValidateFilePath(path);
            var item = await StorageFile.GetFileFromPathAsync(local).AsTask(token); Check(token);
            if (!string.Equals(LocalClipboardTools.ValidateFilePath(item.Path), local, StringComparison.OrdinalIgnoreCase))
                throw new LocalToolException(DesktopResources.Get("LocalClipboardFilesInvalid"));
            items.Add(item);
        }
        // Recheck after asynchronous resolution and immediately before replacing the clipboard.
        foreach (var path in paths) { Check(token); LocalClipboardTools.ValidateFilePath(path); }
        var content = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        content.SetStorageItems(items, true /* readOnly: copy, never cut/move */);
        SetContent(content, token); return true;
    }, ct);

    private async Task<LocalClipboardImage> DecodeImageAsync(IRandomAccessStream stream, CancellationToken token)
    {
        Check(token);
        if (stream.Size is < 1 or > LocalClipboardTools.MaximumImageBytes) throw InvalidImage();
        var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(token); Check(token);
        if (decoder.PixelWidth == 0 || decoder.PixelHeight == 0
            || (ulong)decoder.PixelWidth * decoder.PixelHeight > LocalClipboardTools.MaximumImagePixels) throw InvalidImage();
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied).AsTask(token); Check(token);
        using var output = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output).AsTask(token); Check(token);
        encoder.SetSoftwareBitmap(bitmap); await encoder.FlushAsync().AsTask(token); Check(token);
        if (output.Size is < 24 or > LocalClipboardTools.MaximumImageBytes) throw InvalidImage();
        var bytes = new byte[(int)output.Size];
        using (var reader = new DataReader(output.GetInputStreamAt(0)))
        {
            if (await reader.LoadAsync((uint)bytes.Length).AsTask(token) != bytes.Length) throw InvalidImage();
            reader.ReadBytes(bytes);
        }
        Check(token);
        var image = new LocalClipboardImage((int)decoder.PixelWidth, (int)decoder.PixelHeight, bytes);
        LocalClipboardTools.ValidateImage(image); return image;
    }

    private void Check(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!isCurrent()) throw new OperationCanceledException(ct);
    }
    private void SetContent(DataPackage content, CancellationToken token)
    {
        Check(token);
        if (!Clipboard.SetContentWithOptions(content, new ClipboardContentOptions { IsAllowedInHistory = false, IsRoamable = false }))
            throw new LocalToolException(DesktopResources.Get("LocalClipboardUnavailable"));
        Clipboard.Flush();
    }
    private static LocalToolException InvalidImage() => new(DesktopResources.Get("LocalClipboardImageInvalid"));

    private async Task<T> OnUiAsync<T>(Func<CancellationToken, Task<T>> call, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var token = timeout.Token;
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        // Observe late native failures if the caller has stopped waiting.
        _ = completion.Task.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        token.ThrowIfCancellationRequested();
        if (!dispatcher.TryEnqueue(async () =>
        {
            try
            {
                token.ThrowIfCancellationRequested();
                if (!isCurrent()) throw new OperationCanceledException(token);
                var result = await call(token);
                token.ThrowIfCancellationRequested();
                if (!isCurrent()) throw new OperationCanceledException(token);
                completion.TrySetResult(result);
            }
            catch (OperationCanceledException) { completion.TrySetCanceled(); }
            catch (Exception error) { completion.TrySetException(error); }
        })) throw new LocalToolException(DesktopResources.Get("LocalClipboardUnavailable"));
        try { return await completion.Task.WaitAsync(token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && timeout.IsCancellationRequested)
        { throw new LocalToolException(DesktopResources.Get("LocalClipboardUnavailable")); }
        catch (Exception error) when (error is COMException or UnauthorizedAccessException or InvalidOperationException)
        { throw new LocalToolException(DesktopResources.Get("LocalClipboardUnavailable")); }
    }
}
