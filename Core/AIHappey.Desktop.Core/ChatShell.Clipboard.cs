using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Windows.System;
using Windows.UI.Core;

namespace AIHappey.Desktop.Core;

public sealed partial class ChatShell
{
    private void PrepareClipboardAttachments()
    {
        // Native TextBox paste may be unavailable when the clipboard has no text format.
        // Intercept the shortcut before TextBox handles it; leave ordinary text entirely native.
        input.PreviewKeyDown += async (_, args) =>
        {
            bool Down(VirtualKey key) => Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);
            if (!(args.Key == VirtualKey.V && Down(VirtualKey.Control))
                && !(args.Key == VirtualKey.Insert && Down(VirtualKey.Shift))) return;
            if (TryPasteContextAttachments() is not { } paste) return;
            args.Handled = true;
            await paste;
        };
        input.Paste += async (_, args) =>
        {
            if (TryPasteContextAttachments() is not { } paste) return;
            args.Handled = true;
            await paste;
        };
    }

    // A non-null task means this is an attachment paste and native path/text insertion is suppressed,
    // including when the composer is temporarily unable to admit files.
    private Task? TryPasteContextAttachments()
    {
        if (closing || activePage != DesktopPage.Chat) return null;
        try
        {
            var data = Clipboard.GetContent();
            if (!data.Contains(StandardDataFormats.StorageItems) && !data.Contains(StandardDataFormats.Bitmap)) return null;
            return CanAddContext && input.IsEnabled && !input.IsReadOnly ? PasteContextAttachmentsAsync(data) : Task.CompletedTask;
        }
        catch (Exception)
        {
            Show(DesktopResources.Get("ClipboardUnavailable"), InfoBarSeverity.Warning);
            return Task.CompletedTask;
        }
    }

    private async Task PasteContextAttachmentsAsync(DataPackageView data)
    {
        if (!CanAddContext || !input.IsEnabled || input.IsReadOnly) return;
        var version = contextVersion;
        var partition = session.HistoryPartition;
        // Reserve the operation before awaiting the clipboard so sending/navigation cannot race admission.
        await RunAsync(async ct =>
        {
            // Explorer can advertise both files and a preview bitmap. Admit the original files only.
            if (data.Contains(StandardDataFormats.StorageItems))
            {
                var items = await data.GetStorageItemsAsync().AsTask(ct);
                if (IsCurrentContext(version, partition)) await AdmitStorageItemsAsync(items, version, partition, ct);
            }
            else if (data.Contains(StandardDataFormats.Bitmap))
            {
                var attachment = await ReadClipboardBitmapAsync(data, ct);
                ct.ThrowIfCancellationRequested();
                if (IsCurrentContext(version, partition)) AddContextAttachment(attachment);
            }
        });
        if (IsCurrentContext(version, partition)) input.Focus(FocusState.Programmatic);
    }

    private static async Task<ComposerAttachment> ReadClipboardBitmapAsync(DataPackageView data, CancellationToken ct)
    {
        var reference = await data.GetBitmapAsync().AsTask(ct);
        using var source = await reference.OpenReadAsync().AsTask(ct);
        ComposerAttachments.ValidateSize((long)source.Size);
        var decoder = await BitmapDecoder.CreateAsync(source).AsTask(ct);
        if (decoder.PixelWidth == 0 || decoder.PixelHeight == 0 || (ulong)decoder.PixelWidth * decoder.PixelHeight > 32_000_000)
            throw new InvalidOperationException(DesktopResources.Get("AttachmentLimit"));
        using var bitmap = await decoder.GetSoftwareBitmapAsync().AsTask(ct);
        using var buffer = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, buffer).AsTask(ct);
        encoder.SetSoftwareBitmap(bitmap);
        await encoder.FlushAsync().AsTask(ct);
        ComposerAttachments.ValidateSize((long)buffer.Size);
        using var stream = buffer.AsStreamForRead();
        return await ComposerAttachments.ReadAsync("clipboard.png", "image/png", stream, ct);
    }
}
