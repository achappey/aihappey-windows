using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage;

namespace AIHappey.Desktop.Core;

public sealed partial class ChatShell
{
    private sealed record ExplorerBatch(string[] Paths, string? Partition, int? Version, bool ConfirmAccount,
        TaskCompletionSource<string> Completion);
    private readonly Queue<ExplorerBatch> explorerBatches = new();
    private readonly InfoBar explorerNotice = new() { Name = "ExplorerPending", IsClosable = false, Visibility = Visibility.Collapsed };
    private bool explorerDraining, explorerReady, accountTransition;
    private ContentDialog? explorerConfirmation;
    private const long ExplorerBatchBytes = 128L * 1024 * 1024;

    /// <summary>UI-thread entry point. Attachment delivery has no reference to the submission path.</summary>
    public Task<string> EnqueueExplorerFilesAsync(IReadOnlyList<string> paths)
    {
        if (!DispatcherQueue.HasThreadAccess) throw new InvalidOperationException("Explorer delivery requires the UI dispatcher.");
        if (closing) return Task.FromResult("canceled");
        if (paths.Count == 0 || explorerBatches.Count >= 16
            || paths.Count + explorerBatches.Sum(batch => batch.Paths.Length) > ExplorerRequest.MaximumPaths)
        { ExplorerDeliveryFailed(); return Task.FromResult("rejected"); }
        foreach (var path in paths) ExplorerRequest.ValidatePath(path);
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        explorerBatches.Enqueue(new(paths.ToArray(), explorerReady ? session.HistoryPartition : null,
            explorerReady ? contextVersion : null, accountTransition, completion));
        UpdateExplorerNotice(); ScheduleExplorerDrain();
        return completion.Task;
    }
    public void ExplorerDeliveryFailed() => Show(DesktopResources.Get("ExplorerDeliveryFailed"), InfoBarSeverity.Warning);
    private void PrepareExplorerNotice(StackPanel notices)
    {
        var cancel = new Button { Content = DesktopResources.Get("Cancel"), Name = "CancelExplorerPending" };
        ControlAppearance.Stock(cancel); ControlAppearance.Stock(explorerNotice);
        cancel.Click += (_, _) => CancelExplorerBatches(); explorerNotice.ActionButton = cancel;
        notices.Children.Add(explorerNotice);
    }
    private void UpdateExplorerNotice()
    {
        explorerNotice.Message = DesktopResources.Format("ExplorerPendingFiles", explorerBatches.Sum(batch => batch.Paths.Length));
        explorerNotice.IsOpen = explorerBatches.Count > 0;
        explorerNotice.Visibility = explorerNotice.IsOpen ? Visibility.Visible : Visibility.Collapsed;
    }
    private void CancelExplorerBatches()
    {
        while (explorerBatches.TryDequeue(out var batch)) batch.Completion.TrySetResult("canceled");
        UpdateExplorerNotice();
    }
    private void ScheduleExplorerDrain()
    {
        if (explorerBatches.Count > 0 && !closing) DispatcherQueue.TryEnqueue(async () => await DrainExplorerAsync());
    }
    private async Task DrainExplorerAsync()
    {
        if (explorerDraining || !explorerReady || busy || closing || downloading || historyDialogOpen || catalogDialog is not null || accountTransition) return;
        explorerDraining = true;
        try
        {
            while (explorerBatches.Count > 0 && !busy && !closing && !historyDialogOpen && !downloading && catalogDialog is null)
            {
                var batch = explorerBatches.Dequeue(); UpdateExplorerNotice();
                try
                {
                    if (batch.ConfirmAccount || batch.Partition is { } receivedPartition && receivedPartition != session.HistoryPartition)
                    {
                        explorerConfirmation = new ContentDialog { XamlRoot = XamlRoot, Title = DesktopResources.Get("ExplorerAccountTitle"),
                            Content = DesktopResources.Get("ExplorerAccountConfirm"), PrimaryButtonText = DesktopResources.Get("Attachments"),
                            CloseButtonText = DesktopResources.Get("Cancel"), DefaultButton = ContentDialogButton.Close };
                        SystemAppearance.PrepareDialog(explorerConfirmation); historyDialogOpen = true;
                        ContentDialogResult choice;
                        try { choice = await explorerConfirmation.ShowAsync(); }
                        finally { explorerConfirmation = null; historyDialogOpen = false; }
                        if (choice != ContentDialogResult.Primary || closing) { batch.Completion.TrySetResult("canceled"); continue; }
                    }
                    else if (batch.Version is { } receivedVersion && receivedVersion != contextVersion)
                    {
                        Show(DesktopResources.Get("ExplorerContextChanged"), InfoBarSeverity.Warning);
                        batch.Completion.TrySetResult("canceled"); continue;
                    }
                    ShowPage(DesktopPage.Chat); // Does not reset the conversation, prompt, or attachments.
                    var version = contextVersion; var partition = session.HistoryPartition;
                    var outcome = "canceled";
                    await RunAsync(async ct =>
                    {
                        var rejected = new List<string>(); long bytes = 0; var admitted = 0;
                        rejected.AddRange(await ComposerAttachments.AdmitAsync(batch.Paths, Path.GetFileName,
                            async (path, token) =>
                            {
                                var attributes = File.GetAttributes(path);
                                if (attributes.HasFlag(System.IO.FileAttributes.Directory)) throw new InvalidOperationException("Not a file.");
                                var file = await StorageFile.GetFileFromPathAsync(path).AsTask(token);
                                var properties = await file.GetBasicPropertiesAsync().AsTask(token);
                                ComposerAttachments.ValidateSize(checked((long)properties.Size));
                                if ((long)properties.Size > ExplorerBatchBytes - bytes) throw new InvalidOperationException("Batch limit reached.");
                                await using var stream = await file.OpenStreamForReadAsync();
                                var attachment = await ComposerAttachments.ReadAsync(file.Name, file.ContentType, stream, token);
                                if (attachment.Content.Length > ExplorerBatchBytes - bytes) throw new InvalidOperationException("Batch limit reached.");
                                bytes += attachment.Content.Length; return attachment;
                            }, attachment => { AddContextAttachment(attachment); admitted++; }, () => IsCurrentContext(version, partition), ct));
                        if (!IsCurrentContext(version, partition)) return;
                        outcome = rejected.Count == 0 ? "attached" : admitted == 0 ? "rejected" : "partial";
                        if (rejected.Count > 0) Show(DesktopResources.Format("RejectedFiles", string.Join(", ", rejected)), InfoBarSeverity.Warning);
                    });
                    batch.Completion.TrySetResult(outcome);
                    if (!closing) input.Focus(FocusState.Programmatic);
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                { batch.Completion.TrySetResult("rejected"); if (!closing) ExplorerDeliveryFailed(); }
            }
        }
        finally { explorerDraining = false; UpdateExplorerNotice(); }
    }
}
