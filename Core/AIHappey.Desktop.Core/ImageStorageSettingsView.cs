using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace AIHappey.Desktop.Core;

/// <summary>Storage controls edit only the user-settings dialog's image draft.</summary>
internal sealed class ImageStorageSettingsView : StackPanel
{
    private readonly ImagePreferences draft;
    private readonly string? originalRoot;
    private readonly CancellationToken lifetime;
    private readonly TextBlock folder;
    private readonly TextBlock validation = new() { Name = "ImageStorageValidation", TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    public bool PickerOpen { get; private set; }

    public ImageStorageSettingsView(ImagePreferences draft, CancellationToken lifetime)
    {
        this.draft = draft; originalRoot = draft.StorageRoot; this.lifetime = lifetime;
        Name = "ImageStorageSettings";
        NativeSettingsSurface.Card(this, "ImageStorageCard", DesktopResources.Get("ImageStorage"), out var storage);
        folder = new() { Name = "ImageStoragePath", Text = draft.EffectiveRoot, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        storage.Children.Add(folder);
        storage.Children.Add(new TextBlock { Text = DesktopResources.Get("ImageFolderHint"), TextWrapping = TextWrapping.Wrap });
        // A wrapping native panel also keeps both actions reachable at narrow widths/high text scaling.
        var actions = new VariableSizedWrapGrid { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left };
        var choose = new Button { Name = "ImageChooseFolder", Content = DesktopResources.Get("ImageChooseFolder"), Margin = new Thickness(0, 0, 8, 8) };
        var reset = new Button { Name = "ImageDefaultFolder", Content = DesktopResources.Get("ImageDefaultFolder"), Margin = new Thickness(0, 0, 0, 8) };
        foreach (var button in new[] { choose, reset })
        {
            ControlAppearance.Stock(button); ToolbarControls.Label(button, button.Content.ToString()!); actions.Children.Add(button);
        }
        choose.Click += async (_, _) =>
        {
            if (PickerOpen || lifetime.IsCancellationRequested) return;
            PickerOpen = true; choose.IsEnabled = reset.IsEnabled = false; validation.Visibility = Visibility.Collapsed;
            try
            {
                var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
                picker.FileTypeFilter.Add("*");
                WinRT.Interop.InitializeWithWindow.Initialize(picker, Microsoft.UI.Win32Interop.GetWindowFromWindowId(XamlRoot.ContentIslandEnvironment.AppWindowId));
                var selected = await picker.PickSingleFolderAsync().AsTask(lifetime);
                if (!lifetime.IsCancellationRequested && selected is not null) { draft.StorageRoot = selected.Path; folder.Text = draft.EffectiveRoot; }
            }
            catch (OperationCanceledException) { }
            catch (Exception) { if (!lifetime.IsCancellationRequested) ShowError(DesktopResources.Get("ImageFolderFailed")); }
            finally { PickerOpen = false; choose.IsEnabled = reset.IsEnabled = true; }
        };
        reset.Click += (_, _) =>
        {
            if (PickerOpen || lifetime.IsCancellationRequested) return;
            draft.StorageRoot = null; folder.Text = draft.EffectiveRoot; validation.Visibility = Visibility.Collapsed;
        };
        storage.Children.Add(actions); storage.Children.Add(validation);
        AutomationProperties.SetLiveSetting(validation, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Assertive);
    }

    public void ValidateAndPrepare()
    {
        // Validate storage only: unrelated inference preferences are not being edited here.
        new ImagePreferences { StorageRoot = draft.StorageRoot }.Validate();
        if (!string.Equals(originalRoot, draft.StorageRoot, StringComparison.OrdinalIgnoreCase)) Directory.CreateDirectory(draft.EffectiveRoot);
    }

    public void ShowError(string message) { validation.Text = message; validation.Visibility = Visibility.Visible; }
}
