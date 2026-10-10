using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace AIHappey.Desktop.Core;

public sealed partial class ChatShell
{
    private readonly SharedFileStore sharedFileStore;
    private readonly FilesOverviewPage filesOverview = new();
    private readonly Button addFiles = new() { Name = "AddFiles", Content = new SymbolIcon(Symbol.Add), Width = 40, Height = 40,
        Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
    private ContentDialog? removeSharedFileDialog;
    private LocalSharedFileTools? activeSharedFileTools;
    private bool CanUseFiles => initialized && !busy && !closing && !downloading && !historyDialogOpen && catalogDialog is null && activePage == DesktopPage.Files;
    private void PrepareFiles()
    {
        ControlAppearance.Stock(addFiles); ToolbarControls.Label(addFiles, DesktopResources.Get("Add"));
        var menu = new MenuFlyout();
        var files = new MenuFlyoutItem { Name = "ShareFiles", Text = DesktopResources.Get("FilesAddFiles"), Icon = new SymbolIcon(Symbol.OpenFile) };
        var folder = new MenuFlyoutItem { Name = "ShareFolder", Text = DesktopResources.Get("FilesAddFolder"), Icon = new SymbolIcon(Symbol.Folder) };
        files.Click += async (_, _) => await PickSharedItemsAsync(false); folder.Click += async (_, _) => await PickSharedItemsAsync(true);
        menu.Items.Add(files); menu.Items.Add(folder); addFiles.Flyout = menu;
        filesOverview.RetryRequested = async () => await RunAsync(LoadFilesAsync);
        filesOverview.OpenRequested = async item => await OpenSharedItemAsync(item);
        filesOverview.RemoveRequested = async item => await RemoveSharedItemAsync(item);
        filesOverview.CanDrop = () => CanUseFiles;
        filesOverview.DropRequested = async data =>
        {
            if (!CanUseFiles) return false;
            var admitted = false;
            await RunAsync(async ct => { admitted = await AdmitSharedItemsAsync(await data.GetStorageItemsAsync().AsTask(ct), session.FilesPartition, ct); });
            return admitted;
        };
    }
    private async Task LoadFilesAsync(CancellationToken ct)
    {
        var partition = session.FilesPartition; filesOverview.Loading();
        try
        {
            var items = await sharedFileStore.ListAsync(partition, ct);
            var views = await Task.Run(() => items.Select(item => { ct.ThrowIfCancellationRequested(); return SharedFileStore.Inspect(item); }).ToArray(), ct);
            ct.ThrowIfCancellationRequested(); if (closing || session.FilesPartition != partition) return;
            filesOverview.SetItems(views);
        }
        catch (OperationCanceledException) { if (!closing && session.FilesPartition == partition) filesOverview.Error(DesktopResources.Get("OperationCanceled")); throw; }
        catch { if (!closing && session.FilesPartition == partition) filesOverview.Error(DesktopResources.Get("FilesLoadFailed")); throw; }
    }
    private async Task PickSharedItemsAsync(bool folder)
    {
        if (!CanUseFiles) return;
        await RunAsync(async ct =>
        {
            var partition = session.FilesPartition; var window = Microsoft.UI.Win32Interop.GetWindowFromWindowId(XamlRoot.ContentIslandEnvironment.AppWindowId);
            IReadOnlyList<IStorageItem> items;
            if (folder)
            {
                var picker = new FolderPicker(); picker.FileTypeFilter.Add("*"); WinRT.Interop.InitializeWithWindow.Initialize(picker, window);
                var selected = await picker.PickSingleFolderAsync(); items = selected is null ? [] : [selected];
            }
            else
            {
                var picker = new FileOpenPicker(); picker.FileTypeFilter.Add("*"); WinRT.Interop.InitializeWithWindow.Initialize(picker, window);
                items = (await picker.PickMultipleFilesAsync()).Cast<IStorageItem>().ToArray();
            }
            ct.ThrowIfCancellationRequested(); if (closing || session.FilesPartition != partition) return;
            await AdmitSharedItemsAsync(items, partition, ct);
        });
        if (!closing && addFiles.IsLoaded) addFiles.Focus(FocusState.Programmatic);
    }
    private async Task<bool> AdmitSharedItemsAsync(IReadOnlyList<IStorageItem> items, string partition, CancellationToken ct)
    {
        var errors = new List<string>(); var added = 0;
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested(); if (closing || session.FilesPartition != partition) throw new OperationCanceledException(ct);
            if (item is not (StorageFile or StorageFolder)) continue;
            try { await sharedFileStore.AddAsync(partition, item.Path, item is StorageFolder, ct); added++; }
            catch (Exception error) when (error is not OperationCanceledException)
            { errors.Add(item.Name + ": " + (error is LocalToolException ? error.Message : DesktopResources.Get("FilesShareFailed"))); }
        }
        await LoadFilesAsync(ct);
        if (items.Count > 0) Show(DesktopResources.Format("FilesSharedCount", added) + (errors.Count == 0 ? "" : "\n" + string.Join("\n", errors)), errors.Count == 0 ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
        return added > 0;
    }
    private async Task OpenSharedItemAsync(SharedFileReference item)
    {
        if (!CanUseFiles) return;
        await RunAsync(async ct =>
        {
            var partition = session.FilesPartition;
            var current = (await sharedFileStore.ListAsync(partition, ct)).FirstOrDefault(i => i.Id == item.Id);
            if (current is null || !SharedFileStore.Inspect(current).Available) throw new LocalToolException(DesktopResources.Get("FilesNotShared"));
            ct.ThrowIfCancellationRequested(); if (closing || partition != session.FilesPartition) return;
            var opened = current.IsFolder ? await Windows.System.Launcher.LaunchFolderAsync(await StorageFolder.GetFolderFromPathAsync(current.Path))
                : await Windows.System.Launcher.LaunchFileAsync(await StorageFile.GetFileFromPathAsync(current.Path));
            if (!opened) Show(DesktopResources.Get("FilesOpenFailed"), InfoBarSeverity.Warning);
        });
    }
    private async Task RemoveSharedItemAsync(SharedFileReference item)
    {
        if (!CanUseFiles) return;
        historyDialogOpen = true; var partition = session.FilesPartition;
        try
        {
            removeSharedFileDialog = new ContentDialog { XamlRoot = XamlRoot, Title = DesktopResources.Get("FilesRemove"),
                Content = DesktopResources.Format("FilesRemoveConfirm", item.Name), PrimaryButtonText = DesktopResources.Get("FilesRemove"),
                CloseButtonText = DesktopResources.Get("Cancel"), DefaultButton = ContentDialogButton.Close };
            SystemAppearance.PrepareDialog(removeSharedFileDialog);
            if (await removeSharedFileDialog.ShowAsync() != ContentDialogResult.Primary || closing || partition != session.FilesPartition) return;
            await sharedFileStore.RemoveAsync(partition, item.Id, downloadLifetime.Token);
            if (!closing && partition == session.FilesPartition) await LoadFilesAsync(downloadLifetime.Token);
        }
        catch (OperationCanceledException) { }
        catch { if (!closing) Show(DesktopResources.Get("FilesShareFailed"), InfoBarSeverity.Error); }
        finally { removeSharedFileDialog = null; historyDialogOpen = false; }
    }
    private void InvalidateFiles() { filesOverview.Loading(); removeSharedFileDialog?.Hide(); activeSharedFileTools?.Dispose(); activeSharedFileTools = null; }
}
