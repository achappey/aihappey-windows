using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace AIHappey.Desktop.Core;

public enum ImagePreviewAction { None, Save, Delete, AddToPrompt }
public sealed class ImagePreviewDialog : ContentDialog, IResponsiveDialog
{
    private readonly Grid layout = new() { RowSpacing = 12 };
    public ImagePreviewAction Action { get; private set; }
    public ImagePreviewDialog(LibraryImage item)
    {
        Name = "ImagePreviewDialog"; Title = DesktopResources.Get("ImagePreview"); CloseButtonText = DesktopResources.Get("Close");
        Resources["ContentDialogMaxWidth"] = 1200d; Resources["ContentDialogMinWidth"] = 0d;
        layout.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) }); layout.RowDefinitions.Add(new() { Height = GridLength.Auto }); layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var image = new Image { Name = "FullGeneratedImage", Stretch = Stretch.Uniform, Source = new BitmapImage(new Uri(item.Path)) };
        layout.Children.Add(image);
        var description = new TextBlock { Text = item.Generation.Prompt + "\n" + item.Model + (item.Cost is { } cost ? $" · ${cost:F4}" : ""), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, MaxHeight = 100 };
        Grid.SetRow(description, 1); layout.Children.Add(description);
        var actions = new MessageFooterPanel();
        foreach (var (action, label) in new[] { (ImagePreviewAction.Save, "ImageSaveCopy"), (ImagePreviewAction.AddToPrompt, "ImageAddToPrompt"), (ImagePreviewAction.Delete, "Delete") })
        {
            var button = new Button { Name = "ImagePreview" + action, Content = DesktopResources.Get(label) }; ControlAppearance.Stock(button);
            button.Click += (_, _) => { Action = action; Hide(); }; actions.Children.Add(button);
        }
        Grid.SetRow(actions, 2); layout.Children.Add(actions); Content = layout;
        Opened += (_, _) => { SizeToRoot(); XamlRoot.Changed += RootChanged; }; Closed += (_, _) => XamlRoot.Changed -= RootChanged;
    }
    private void RootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => SizeToRoot();
    void IResponsiveDialog.SizeToRoot() => SizeToRoot();
    private void SizeToRoot() { layout.Width = Math.Max(0, Math.Min(1100, XamlRoot.Size.Width - 96)); layout.Height = Math.Max(0, Math.Min(800, XamlRoot.Size.Height - 240)); }
}
