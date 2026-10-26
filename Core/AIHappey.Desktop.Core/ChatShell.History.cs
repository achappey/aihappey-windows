using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.System;

namespace AIHappey.Desktop.Core;

public sealed partial class ChatShell
{
    // Reuse the existing atomic, partition-scoped key-set format, in a separate preference directory.
    private readonly CatalogFavoritesStore conversationPinsStore;
    private HashSet<string> conversationPins = new(StringComparer.Ordinal);
    private string? conversationPinsPartition;
    private bool CanManageConversationHistory => initialized && !busy && !closing && !downloading && !accountTransition
        && !historyDialogOpen && catalogDialog is null;

    private bool IsConversationPinned(Conversation chat) => conversationPinsPartition == session.HistoryPartition && conversationPins.Contains(chat.Id);

    private async Task LoadConversationHistoryAsync(string partition, CancellationToken ct)
    {
        if (conversationPinsPartition != partition)
        {
            conversationPins = new(StringComparer.Ordinal); conversationPinsPartition = null;
        }
        var saved = await Task.Run(() => history.ListAsync(partition, ct), ct);
        var pins = await conversationPinsStore.LoadAsync(partition, ct);
        ct.ThrowIfCancellationRequested();
        if (closing || partition != session.HistoryPartition) return;
        conversations = saved; conversationPins = pins; conversationPinsPartition = partition;
        FilterHistory();
    }

    private async Task ToggleConversationPinAsync(Conversation selected)
    {
        if (!CanManageConversationHistory || conversationPinsPartition != session.HistoryPartition
            || !conversations.Any(chat => chat.Id == selected.Id)) return;
        var partition = session.HistoryPartition;
        await RunAsync(async ct =>
        {
            var next = new HashSet<string>(conversationPins, StringComparer.Ordinal);
            if (!next.Remove(selected.Id)) next.Add(selected.Id);
            await conversationPinsStore.SaveAsync(partition, next, ct);
            ct.ThrowIfCancellationRequested();
            if (closing || partition != session.HistoryPartition) return;
            conversationPins = next; FilterHistory();
            chats.ScrollIntoView(conversations.First(chat => chat.Id == selected.Id));
        });
    }

    private async Task RemoveConversationPinAsync(string partition, string id, CancellationToken ct)
    {
        if (conversationPinsPartition != partition || !conversationPins.Contains(id)) return;
        var next = new HashSet<string>(conversationPins, StringComparer.Ordinal); next.Remove(id);
        await conversationPinsStore.SaveAsync(partition, next, ct);
        if (!closing && partition == session.HistoryPartition) conversationPins = next;
    }

    private async Task ExportConversationAsync(Conversation selected)
    {
        if (!CanManageConversationHistory || !conversations.Any(chat => chat.Id == selected.Id)) return;
        var partition = session.HistoryPartition;
        await RunAsync(async ct =>
        {
            try
            {
                var file = await StorageFile.GetFileFromPathAsync(history.DocumentPath(partition, selected.Id)).AsTask(ct);
                var folder = await file.GetParentAsync().AsTask(ct);
                ct.ThrowIfCancellationRequested();
                if (closing || partition != session.HistoryPartition) return;
                var options = new FolderLauncherOptions(); options.ItemsToSelect.Add(file);
                if (folder is null || !await Launcher.LaunchFolderAsync(folder, options).AsTask(ct))
                    Show(DesktopResources.Get("ChatExportFailed"), InfoBarSeverity.Error);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { if (!closing && partition == session.HistoryPartition) Show(DesktopResources.Get("ChatExportFailed"), InfoBarSeverity.Error); }
        });
    }

    private void PrepareConversationDrop()
    {
        // This target is the sidebar ListView (including its empty area), not the composer workspace.
        chats.AllowDrop = true;
        chats.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        chats.AddHandler(UIElement.DragEnterEvent, new DragEventHandler(ConversationDragOver), true);
        chats.AddHandler(UIElement.DragOverEvent, new DragEventHandler(ConversationDragOver), true);
        chats.AddHandler(UIElement.DropEvent, new DragEventHandler(ConversationDrop), true);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(chats, DesktopResources.Get("ChatImportDrop"));
    }

    private async void ConversationDragOver(object sender, DragEventArgs args)
    {
        args.Handled = true; args.AcceptedOperation = DataPackageOperation.None;
        if (!CanManageConversationHistory || !args.DataView.Contains(StandardDataFormats.StorageItems)
            || !args.AllowedOperations.HasFlag(DataPackageOperation.Copy)) return;
        var partition = session.HistoryPartition;
        var deferral = args.GetDeferral();
        try
        {
            var items = await args.DataView.GetStorageItemsAsync();
            var accepts = CanManageConversationHistory && partition == session.HistoryPartition && items.Count > 0
                && items.All(item => item is StorageFile file && file.FileType.Equals(".json", StringComparison.OrdinalIgnoreCase));
            args.AcceptedOperation = accepts ? DataPackageOperation.Copy : DataPackageOperation.None;
            args.DragUIOverride.Caption = accepts ? DesktopResources.Get("ChatImportDrop") : "";
            args.DragUIOverride.IsCaptionVisible = accepts;
        }
        catch (Exception) { args.AcceptedOperation = DataPackageOperation.None; }
        finally { deferral.Complete(); }
    }

    private async void ConversationDrop(object sender, DragEventArgs args)
    {
        args.Handled = true; args.AcceptedOperation = DataPackageOperation.None;
        if (!CanManageConversationHistory || !args.DataView.Contains(StandardDataFormats.StorageItems)
            || !args.AllowedOperations.HasFlag(DataPackageOperation.Copy)) return;
        var partition = session.HistoryPartition;
        var deferral = args.GetDeferral();
        Conversation? last = null;
        try
        {
            // Reserve the operation before awaiting the OS: account changes, sends and other drops cannot race admission.
            await RunAsync(async ct =>
            {
                var items = await args.DataView.GetStorageItemsAsync().AsTask(ct);
                if (closing || partition != session.HistoryPartition || items.Count == 0
                    || items.Any(item => item is not StorageFile file || !file.FileType.Equals(".json", StringComparison.OrdinalIgnoreCase))) return;
                var files = items.Cast<StorageFile>().ToArray();
                var result = await ImportConversationFilesAsync(files, partition, ct);
                last = result.Last;
                if (result.Imported > 0) args.AcceptedOperation = DataPackageOperation.Copy;
            });
            if (last is not null && !closing && partition == session.HistoryPartition) await OpenConversationAsync(last);
        }
        finally { deferral.Complete(); }
    }

    private async Task<(Conversation? Last, int Imported)> ImportConversationFilesAsync(IEnumerable<StorageFile> files, string partition, CancellationToken ct)
    {
        Conversation? last = null; var imported = 0; var duplicates = 0; var failed = 0;
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            if (closing || partition != session.HistoryPartition) return (null, imported);
            try
            {
                if (!file.FileType.Equals(".json", StringComparison.OrdinalIgnoreCase)) { failed++; continue; }
                var json = await FileIO.ReadTextAsync(file).AsTask(ct);
                ct.ThrowIfCancellationRequested();
                if (closing || partition != session.HistoryPartition) return (null, imported);
                var chat = await Task.Run(() => history.ImportAsync(partition, json, ct), ct);
                if (chat is null) duplicates++; else { imported++; last = chat; }
            }
            catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
            { failed++; }
        }
        if (!closing && partition == session.HistoryPartition)
        {
            await LoadConversationHistoryAsync(partition, ct);
            Show(DesktopResources.Format("ChatImportResult", imported, duplicates, failed), failed > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success);
        }
        return (last, imported);
    }
}
