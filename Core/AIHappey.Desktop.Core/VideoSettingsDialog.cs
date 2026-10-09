using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace AIHappey.Desktop.Core;

public sealed class VideoSettingsDialog : ContentDialog, IResponsiveDialog
{
    private VideoPreferences draft;
    private readonly VideoInputStore inputs;
    private readonly CancellationTokenSource lifetime = new();
    private readonly List<VideoInputFile> created = [];
    private readonly Grid layout = new() { RowSpacing = 12 };
    private readonly NavigationView tabs = new() { Name = "VideoSettingsTabs", PaneDisplayMode = NavigationViewPaneDisplayMode.Top,
        IsSettingsVisible = false, IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed, IsPaneToggleButtonVisible = false, AlwaysShowHeader = false };
    private readonly ScrollViewer scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        HorizontalScrollMode = ScrollMode.Disabled, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock validation = new() { Name = "VideoSettingsValidation", TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    private readonly Dictionary<string, TextBox> numbers = [];
    private TextBox resolution = null!, aspect = null!, providerOptions = null!;
    private Slider count = null!;
    private ToggleSwitch audio = null!;
    private StackPanel references = null!, first = null!, last = null!;
    private bool working, committed;
    public Func<VideoPreferences, Task>? SaveAsync { get; set; }
    public bool DiscardOnShutdown { get; set; }
    public VideoSettingsDialog(VideoPreferences preferences, VideoInputStore inputs)
    {
        this.inputs = inputs; draft = preferences.Clone(); Name = "VideoSettingsDialog";
        Title = DesktopResources.Get("VideoSettings"); PrimaryButtonText = DesktopResources.Get("ChatRestoreDefaults"); CloseButtonText = DesktopResources.Get("Close");
        Resources["ContentDialogMaxWidth"] = 920d; Resources["ContentDialogMinWidth"] = 0d;
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto }); layout.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) }); layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        tabs.MenuItems.Add(new NavigationViewItem { Content = DesktopResources.Get("General"), Tag = "general" }); tabs.SelectedItem = tabs.MenuItems[0];
        layout.Children.Add(tabs); Grid.SetRow(scroll, 1); layout.Children.Add(scroll); Grid.SetRow(validation, 2); layout.Children.Add(validation); Content = layout;
        AutomationProperties.SetLiveSetting(validation, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Assertive);
        Build();
        PrimaryButtonClick += (_, args) =>
        {
            args.Cancel = true; if (working) return;
            draft = new() { StorageRoot = draft.StorageRoot, PollingIntervalSeconds = draft.PollingIntervalSeconds }; Build(); validation.Visibility = Visibility.Collapsed;
        };
        Closing += SaveOnClose;
        Opened += (_, _) => { SizeToRoot(); XamlRoot.Changed += RootChanged; };
        Closed += (_, _) =>
        {
            lifetime.Cancel(); XamlRoot.Changed -= RootChanged;
            var keep = committed ? draft.InputReferences.Concat(new[] { draft.FirstFrame, draft.LastFrame }.OfType<VideoInputFile>()).Select(f => f.File).ToHashSet() : [];
            foreach (var file in created.Where(f => !keep.Contains(f.File)))
                try { File.Delete(inputs.PathFor(file)); } catch (Exception) { }
        };
    }
    private static StackPanel Card(StackPanel parent, string name, string key)
    { NativeSettingsSurface.Card(parent, name, DesktopResources.Get(key), out var content); return content; }
    private TextBox Field(StackPanel parent, string name, string key, string? text)
    {
        var field = new TextBox { Name = name, Header = DesktopResources.Get(key), Text = text ?? "" }; ControlAppearance.Stock(field); ToolbarControls.Label(field, DesktopResources.Get(key)); parent.Children.Add(field); return field;
    }
    private TextBox Dimension(StackPanel parent, string name, string key, string? current, string[] presets)
    {
        var select = new ComboBox { Name = name + "Preset", Header = DesktopResources.Get(key), HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var value in new[] { "", "custom" }.Concat(presets)) select.Items.Add(new ComboBoxItem { Tag = value, Content = value == "" ? DesktopResources.Get("ImageProviderDefault") : value == "custom" ? DesktopResources.Get("Custom") : value });
        var mode = string.IsNullOrWhiteSpace(current) ? "" : presets.Contains(current) ? current : "custom";
        select.SelectedItem = select.Items.Cast<ComboBoxItem>().Single(i => (string)i.Tag == mode); ControlAppearance.Stock(select); parent.Children.Add(select);
        var field = Field(parent, name, key, current); field.IsEnabled = mode == "custom";
        select.SelectionChanged += (_, _) => { var value = (string)((ComboBoxItem)select.SelectedItem).Tag; field.IsEnabled = value == "custom"; if (value != "custom") field.Text = value; };
        return field;
    }
    private void Build()
    {
        numbers.Clear(); var general = new StackPanel { Spacing = 16, Padding = new Thickness(4, 0, 4, 8) }; scroll.Content = general;
        resolution = Dimension(Card(general, "VideoSizeCard", "VideoSize"), "VideoResolution", "VideoResolution", draft.Resolution, VideoPreferences.ResolutionPresets);
        aspect = Dimension(Card(general, "VideoAspectCard", "ImageAspectRatio"), "VideoAspectRatio", "ImageAspectRatio", draft.AspectRatio, VideoPreferences.AspectPresets);
        var output = Card(general, "VideoOutputCard", "ImageOutput");
        count = new Slider { Name = "VideoCount", Header = DesktopResources.Get("VideoCount"), Minimum = 1, Maximum = 10, StepFrequency = 1, Value = draft.N }; ControlAppearance.Stock(count); output.Children.Add(count);
        numbers["batch"] = Field(output, "VideoBatchLimit", "VideoBatchLimit", draft.MaxVideosPerCall?.ToString(CultureInfo.InvariantCulture));
        var properties = Card(general, "VideoPropertiesCard", "VideoProperties");
        numbers["duration"] = Field(properties, "VideoDuration", "VideoDuration", draft.Duration?.ToString(CultureInfo.InvariantCulture));
        numbers["fps"] = Field(properties, "VideoFps", "VideoFps", draft.Fps?.ToString(CultureInfo.InvariantCulture));
        audio = new ToggleSwitch { Name = "VideoGenerateAudio", Header = DesktopResources.Get("VideoGenerateAudio"), IsOn = draft.GenerateAudio }; ControlAppearance.Stock(audio); properties.Children.Add(audio);
        references = Card(general, "VideoReferencesCard", "VideoInputReferences");
        first = Card(general, "VideoFirstFrameCard", "VideoFirstFrame"); last = Card(general, "VideoLastFrameCard", "VideoLastFrame"); RenderInputs();
        numbers["seed"] = Field(Card(general, "VideoOtherCard", "ImageOther"), "VideoSeed", "ImageSeed", draft.Seed?.ToString(CultureInfo.InvariantCulture));
        providerOptions = Field(Card(general, "VideoProviderOptionsCard", "VideoProviderOptions"), "VideoProviderOptions", "VideoProviderOptions", new JsonObject(draft.ProviderOptions.Select(p => KeyValuePair.Create<string, JsonNode?>(p.Key, p.Value.DeepClone()))).ToJsonString(VideoDisk.Json));
        providerOptions.AcceptsReturn = true; providerOptions.TextWrapping = TextWrapping.Wrap; providerOptions.MinHeight = 96; providerOptions.MaxHeight = 240;
    }
    private void RenderInputs()
    {
        InputPanel(references, "references", draft.InputReferences, "VideoAddReferences");
        InputPanel(first, "first_frame", draft.FirstFrame is { } a ? [a] : [], "VideoFirstFrame");
        InputPanel(last, "last_frame", draft.LastFrame is { } b ? [b] : [], "VideoLastFrame");
    }
    private void InputPanel(StackPanel panel, string kind, IReadOnlyList<VideoInputFile> files, string key)
    {
        panel.Children.Clear(); panel.AllowDrop = true;
        var add = new Button { Name = "VideoAdd_" + kind, Content = DesktopResources.Get(key) }; ControlAppearance.Stock(add); add.Click += async (_, _) => await PickAsync(kind); panel.Children.Add(add);
        panel.Children.Add(new TextBlock { Text = DesktopResources.Get("VideoImageDropHint"), TextWrapping = TextWrapping.Wrap });
        panel.DragOver -= DragOverImage; panel.DragOver += DragOverImage;
        panel.Tag = kind; panel.Drop -= DropImages; panel.Drop += DropImages;
        foreach (var file in files)
        {
            var row = new StackPanel { Spacing = 8 };
            try { var path = inputs.PathFor(file); if (File.Exists(path)) row.Children.Add(new Image { Source = new BitmapImage { UriSource = new Uri(path), DecodePixelWidth = 240 }, Height = 120, HorizontalAlignment = HorizontalAlignment.Left, Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform }); } catch (Exception) { }
            row.Children.Add(new TextBlock { Text = file.Name, TextWrapping = TextWrapping.Wrap });
            var remove = new Button { Name = "VideoRemove_" + kind, Content = DesktopResources.Get("Delete") }; ToolbarControls.Label(remove, DesktopResources.Format("RemoveContext", file.Name)); ControlAppearance.Stock(remove);
            remove.Click += (_, _) => { if (working) return; if (kind == "references") draft.InputReferences.Remove(file); else if (kind == "first_frame") draft.FirstFrame = null; else draft.LastFrame = null; RenderInputs(); };
            row.Children.Add(remove); panel.Children.Add(row);
        }
    }
    private void DragOverImage(object sender, DragEventArgs args)
    { args.Handled = true; args.AcceptedOperation = !working && args.DataView.Contains(StandardDataFormats.StorageItems) && args.AllowedOperations.HasFlag(DataPackageOperation.Copy) ? DataPackageOperation.Copy : DataPackageOperation.None; }
    private async void DropImages(object sender, DragEventArgs args)
    {
        args.Handled = true;
        if (working || !args.DataView.Contains(StandardDataFormats.StorageItems) || !args.AllowedOperations.HasFlag(DataPackageOperation.Copy)) { args.AcceptedOperation = DataPackageOperation.None; return; }
        var deferral = args.GetDeferral();
        try { args.AcceptedOperation = DataPackageOperation.Copy; await AdmitAsync((string)((StackPanel)sender).Tag, await args.DataView.GetStorageItemsAsync()); }
        finally { deferral.Complete(); }
    }
    private async Task PickAsync(string kind)
    {
        if (working) return; working = true;
        try
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
            foreach (var extension in ImageAttachments.Extensions) picker.FileTypeFilter.Add(extension);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, Microsoft.UI.Win32Interop.GetWindowFromWindowId(XamlRoot.ContentIslandEnvironment.AppWindowId));
            var files = await picker.PickMultipleFilesAsync().AsTask(lifetime.Token); working = false;
            await AdmitAsync(kind, files);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { ShowError(error is InvalidOperationException ? error.Message : DesktopResources.Get("VideoAttachmentRequired")); }
        finally { working = false; }
    }
    private async Task AdmitAsync(string kind, IEnumerable<IStorageItem> items)
    {
        if (working || lifetime.IsCancellationRequested) return; working = true;
        try
        {
            foreach (var file in items.OfType<StorageFile>())
            {
                if (kind == "references" && draft.InputReferences.Count >= 20) throw new InvalidOperationException(DesktopResources.Get("ImageAttachmentTotalLimit"));
                var size = await file.GetBasicPropertiesAsync(); ComposerAttachments.ValidateSize((long)size.Size);
                await using var stream = await file.OpenStreamForReadAsync(); var input = await ComposerAttachments.ReadAsync(file.Name, file.ContentType, stream, lifetime.Token);
                var saved = await inputs.AddAsync(input, lifetime.Token); created.Add(saved);
                if (kind == "references") draft.InputReferences.Add(saved); else if (kind == "first_frame") draft.FirstFrame = saved; else draft.LastFrame = saved;
                if (kind != "references") break;
            }
            RenderInputs();
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { ShowError(error is InvalidOperationException ? error.Message : DesktopResources.Get("VideoAttachmentRequired")); }
        finally { working = false; }
    }
    private async void SaveOnClose(ContentDialog sender, ContentDialogClosingEventArgs args)
    {
        if (DiscardOnShutdown) return;
        if (working) { args.Cancel = true; return; }
        var deferral = args.GetDeferral(); working = true;
        try
        {
            int? Number(string key) => string.IsNullOrWhiteSpace(numbers[key].Text) ? null : int.Parse(numbers[key].Text, CultureInfo.InvariantCulture);
            draft.Resolution = string.IsNullOrWhiteSpace(resolution.Text) ? null : resolution.Text.Trim(); draft.AspectRatio = string.IsNullOrWhiteSpace(aspect.Text) ? null : aspect.Text.Trim();
            draft.N = (int)count.Value; draft.MaxVideosPerCall = Number("batch"); draft.Duration = Number("duration"); draft.Fps = Number("fps"); draft.Seed = Number("seed"); draft.GenerateAudio = audio.IsOn;
            var options = JsonNode.Parse(providerOptions.Text) as JsonObject ?? throw new InvalidOperationException(DesktopResources.Get("VideoSettingsInvalid"));
            draft.ProviderOptions = options.ToDictionary(p => p.Key, p => p.Value as JsonObject ?? throw new InvalidOperationException(DesktopResources.Get("VideoSettingsInvalid")));
            draft.Validate(); if (SaveAsync is not null) await SaveAsync(draft.Clone()); committed = true;
        }
        catch (Exception error) { args.Cancel = true; ShowError(error is InvalidOperationException ? error.Message : DesktopResources.Get("VideoSettingsInvalid")); }
        finally { working = false; deferral.Complete(); }
    }
    private void ShowError(string message) { validation.Text = message; validation.Visibility = Visibility.Visible; }
    private void RootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => SizeToRoot();
    void IResponsiveDialog.SizeToRoot() => SizeToRoot();
    private void SizeToRoot() { layout.Width = Math.Max(0, Math.Min(820, XamlRoot.Size.Width - 96)); layout.Height = Math.Max(0, Math.Min(620, XamlRoot.Size.Height - 240)); }
}
