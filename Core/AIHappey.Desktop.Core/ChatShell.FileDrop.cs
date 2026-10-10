using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.ApplicationModel.DataTransfer;

namespace AIHappey.Desktop.Core;

public sealed partial class ChatShell
{
    private readonly Rectangle fileDropOutline = new()
    {
        Name = "FileDropOutline", IsHitTestVisible = false, Visibility = Visibility.Collapsed,
        Margin = new Thickness(8), StrokeThickness = 2, StrokeDashArray = new DoubleCollection { 2, 3 },
        RadiusX = 8, RadiusY = 8
    };

    private bool CanAddContext => initialized && !busy && !closing && !downloading && !historyDialogOpen
        && catalogDialog is null && activePage == DesktopPage.Chat;

    private void PrepareFileDrop()
    {
        // Transparent background makes the empty chat area a hit target too. The outline is
        // an overlay, not a border on the layout, so dragging never moves the composer.
        workspace.AllowDrop = true;
        workspace.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        input.AllowDrop = false; // Files are context, never path text inserted by the native TextBox.
        Grid.SetRow(fileDropOutline, 1); Grid.SetRowSpan(fileDropOutline, 2);
        workspace.Children.Add(fileDropOutline);
        ControlAppearance.Apply(fileDropOutline, (_, _) => { }, palette => fileDropOutline.Stroke = new SolidColorBrush(palette.Text));

        // Listen even when a child (e.g. TextBox/ScrollViewer) handles the routed event.
        // Only this ancestor admits files, so one drop cannot add the same batch twice.
        workspace.AddHandler(UIElement.DragEnterEvent, new DragEventHandler(ChatFileDragOver), true);
        workspace.AddHandler(UIElement.DragOverEvent, new DragEventHandler(ChatFileDragOver), true);
        workspace.AddHandler(UIElement.DropEvent, new DragEventHandler(ChatFileDrop), true);
        workspace.AddHandler(UIElement.DragLeaveEvent, new DragEventHandler(ChatFileDragLeave), true);
        Unloaded += (_, _) => ResetFileDrop();
    }

    private void ResetFileDrop() => fileDropOutline.Visibility = Visibility.Collapsed;

    private void ChatFileDragOver(object sender, DragEventArgs args)
    {
        if (activePage is DesktopPage.Images or DesktopPage.Videos or DesktopPage.Transcriptions or DesktopPage.Files) return; // Pages own their routed drop events.
        args.Handled = true;
        var accepts = CanAddContext && args.DataView.Contains(StandardDataFormats.StorageItems)
            && args.AllowedOperations.HasFlag(DataPackageOperation.Copy);
        args.AcceptedOperation = accepts ? DataPackageOperation.Copy : DataPackageOperation.None;
        args.DragUIOverride.Caption = accepts ? "Attach files" : "";
        args.DragUIOverride.IsCaptionVisible = accepts;
        fileDropOutline.Visibility = accepts ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ChatFileDragLeave(object sender, DragEventArgs args)
    {
        // DragLeave also bubbles when crossing descendants. Only clear outside the workspace.
        var point = args.GetPosition(workspace);
        if (point.X <= 0 || point.Y <= 0 || point.X >= workspace.ActualWidth || point.Y >= workspace.ActualHeight)
            ResetFileDrop();
    }

    private async void ChatFileDrop(object sender, DragEventArgs args)
    {
        if (activePage is DesktopPage.Images or DesktopPage.Videos or DesktopPage.Transcriptions or DesktopPage.Files) return;
        args.Handled = true;
        ResetFileDrop();
        args.AcceptedOperation = DataPackageOperation.None;
        if (!CanAddContext || !args.DataView.Contains(StandardDataFormats.StorageItems)
            || !args.AllowedOperations.HasFlag(DataPackageOperation.Copy)) return;

        var version = contextVersion;
        var partition = session.HistoryPartition;
        var data = args.DataView;
        var deferral = args.GetDeferral();
        try
        {
            // RunAsync reserves the operation before awaiting the OS data package, preventing
            // sends, account changes, navigation or a second drop from racing file admission.
            await RunAsync(async ct =>
            {
                var items = await data.GetStorageItemsAsync().AsTask(ct);
                if (!IsCurrentContext(version, partition)) return;
                args.AcceptedOperation = items.Any(item => item is Windows.Storage.StorageFile)
                    ? DataPackageOperation.Copy : DataPackageOperation.None;
                await AdmitStorageItemsAsync(items, version, partition, ct);
            });
        }
        finally
        {
            deferral.Complete();
            if (!closing) input.Focus(FocusState.Programmatic);
        }
    }
}
