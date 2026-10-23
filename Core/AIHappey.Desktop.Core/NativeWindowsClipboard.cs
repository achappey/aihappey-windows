using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Windows.ApplicationModel.DataTransfer;

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
        if (!Clipboard.SetContentWithOptions(content, new ClipboardContentOptions { IsAllowedInHistory = false, IsRoamable = false }))
            throw new LocalToolException(DesktopResources.Get("LocalClipboardUnavailable"));
        Clipboard.Flush();
        return Task.FromResult(true);
    }, ct);

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
