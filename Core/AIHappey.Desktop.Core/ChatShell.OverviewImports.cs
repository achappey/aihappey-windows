using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace AIHappey.Desktop.Core;

public sealed partial class ChatShell
{
    private string OverviewImportPartition(DesktopPage page) => page switch
    {
        DesktopPage.Agents => session.AgentPartition,
        DesktopPage.Plugins => session.PluginPartition,
        _ => session.HistoryPartition,
    };

    private async Task ImportOverviewFilesAsync(DataPackageView data, DesktopPage page, string extension, string invalidFormat,
        Func<IReadOnlyList<StorageFile>, CancellationToken, Task> import)
    {
        if (busy || closing || historyDialogOpen || catalogDialog is not null || activePage != page) return;
        var partition = OverviewImportPartition(page);
        // Reserve before resolving the OS data package, not after the first asynchronous read.
        await RunAsync(async ct =>
        {
            var items = await data.GetStorageItemsAsync().AsTask(ct);
            ct.ThrowIfCancellationRequested();
            if (closing || activePage != page || partition != OverviewImportPartition(page)) throw new OperationCanceledException(ct);
            if (items.Count == 0 || items.Any(item => item is not StorageFile || !item.Name.EndsWith(extension, StringComparison.OrdinalIgnoreCase)))
            { Show(DesktopResources.Get(invalidFormat), InfoBarSeverity.Warning); return; }
            await import(items.OfType<StorageFile>().ToArray(), ct);
        });
    }
}
