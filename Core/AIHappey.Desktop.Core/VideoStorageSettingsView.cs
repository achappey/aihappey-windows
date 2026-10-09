using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace AIHappey.Desktop.Core;

internal sealed class VideoStorageSettingsView : StackPanel
{
    private readonly VideoPreferences draft;
    private readonly string? originalRoot;
    private readonly CancellationToken lifetime;
    private readonly TextBlock folder;
    private readonly TextBlock validation = new() { Name = "VideoStorageValidation", TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    public bool PickerOpen { get; private set; }
    public VideoStorageSettingsView(VideoPreferences draft, CancellationToken lifetime)
    {
        this.draft = draft; originalRoot = draft.StorageRoot; this.lifetime = lifetime; Name = "VideoStorageSettings";
        NativeSettingsSurface.Card(this, "VideoStorageCard", DesktopResources.Get("VideoStorage"), out var storage);
        folder = new() { Name = "VideoStoragePath", Text = draft.EffectiveRoot, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true }; storage.Children.Add(folder);
        storage.Children.Add(new TextBlock { Text = DesktopResources.Get("VideoFolderHint"), TextWrapping = TextWrapping.Wrap });
        var actions = new VariableSizedWrapGrid { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left };
        var choose = new Button { Name = "VideoChooseFolder", Content = DesktopResources.Get("VideoChooseFolder"), Margin = new Thickness(0, 0, 8, 8) };
        var reset = new Button { Name = "VideoDefaultFolder", Content = DesktopResources.Get("VideoDefaultFolder"), Margin = new Thickness(0, 0, 0, 8) };
        foreach (var button in new[] { choose, reset }) { ControlAppearance.Stock(button); ToolbarControls.Label(button, button.Content.ToString()!); actions.Children.Add(button); }
        choose.Click += async (_, _) =>
        {
            if (PickerOpen || lifetime.IsCancellationRequested) return; PickerOpen = true; choose.IsEnabled = reset.IsEnabled = false; validation.Visibility = Visibility.Collapsed;
            try
            {
                var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.VideosLibrary }; picker.FileTypeFilter.Add("*");
                WinRT.Interop.InitializeWithWindow.Initialize(picker, Microsoft.UI.Win32Interop.GetWindowFromWindowId(XamlRoot.ContentIslandEnvironment.AppWindowId));
                var selected = await picker.PickSingleFolderAsync().AsTask(lifetime);
                if (!lifetime.IsCancellationRequested && selected is not null) { draft.StorageRoot = selected.Path; folder.Text = draft.EffectiveRoot; }
            }
            catch (OperationCanceledException) { }
            catch (Exception) { if (!lifetime.IsCancellationRequested) ShowError(DesktopResources.Get("VideoFolderFailed")); }
            finally { PickerOpen = false; choose.IsEnabled = reset.IsEnabled = true; }
        };
        reset.Click += (_, _) => { if (PickerOpen || lifetime.IsCancellationRequested) return; draft.StorageRoot = null; folder.Text = draft.EffectiveRoot; validation.Visibility = Visibility.Collapsed; };
        storage.Children.Add(actions); storage.Children.Add(validation);
        NativeSettingsSurface.Card(this, "VideoPollingCard", DesktopResources.Get("VideoPollingInterval"), out var polling);
        var interval = new Slider { Name = "VideoPollingInterval", Header = DesktopResources.Get("VideoPollingInterval"), Minimum = 5, Maximum = 60, StepFrequency = 1, Value = draft.PollingIntervalSeconds };
        var value = new TextBlock { Text = draft.PollingIntervalSeconds + " s" };
        interval.ValueChanged += (_, args) => { draft.PollingIntervalSeconds = (int)Math.Round(args.NewValue); value.Text = draft.PollingIntervalSeconds + " s"; };
        ControlAppearance.Stock(interval); polling.Children.Add(interval); polling.Children.Add(value);
        AutomationProperties.SetLiveSetting(validation, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Assertive);
    }
    public void ValidateAndPrepare()
    {
        draft.ValidateStorage();
        if (!string.Equals(originalRoot, draft.StorageRoot, StringComparison.OrdinalIgnoreCase)) VideoDisk.Prepare(draft.EffectiveRoot);
    }
    public void ShowError(string message) { validation.Text = message; validation.Visibility = Visibility.Visible; }
}
