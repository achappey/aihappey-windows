using System.Globalization;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
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
    private readonly List<(SettingsExpander Expander, StackPanel Body)> sections = [];
    private DimensionFields resolution = null!, aspect = null!;
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
    private StackPanel Section(StackPanel parent, string name, string key)
    {
        var expander = NativeSettingsSurface.Expander(parent, name, DesktopResources.Get(key), null, out var body);
        sections.Add((expander, body)); return body;
    }
    private TextBox Field(StackPanel parent, string name, string key, string? text)
    {
        var field = new TextBox { Name = name, Header = DesktopResources.Get(key), Text = text ?? "" }; ControlAppearance.Stock(field); ToolbarControls.Label(field, DesktopResources.Get(key)); parent.Children.Add(field); return field;
    }
    // Kept local to video settings: match the image selectors without changing their implementation.
    private sealed class DimensionFields
    {
        private readonly ComboBox select;
        private readonly TextBox width, height;
        private readonly char separator;
        private bool updating;
        public string? Value => select.SelectedIndex == 0 ? null : width.Text.Trim() + separator + height.Text.Trim();
        public Control? InvalidField => ImagePreferences.ValidDimensions(Value, separator) ? null
            : ImagePreferences.ValidDimensions(width.Text.Trim() + separator + "1", separator) ? height : width;

        public DimensionFields(StackPanel panel, string name, string key, string? value, string[] presets, char separator)
        {
            this.separator = separator;
            select = new() { Name = name + "Preset", Header = DesktopResources.Get(key), HorizontalAlignment = HorizontalAlignment.Stretch };
            select.Items.Add(new ComboBoxItem { Content = DesktopResources.Get("ImageProviderDefault"), Tag = "" });
            foreach (var preset in presets) select.Items.Add(new ComboBoxItem { Content = preset, Tag = preset });
            select.Items.Add(new ComboBoxItem { Content = DesktopResources.Get("Custom"), Tag = "custom" });
            select.SelectedIndex = string.IsNullOrWhiteSpace(value) ? 0 : presets.Contains(value) ? Array.IndexOf(presets, value) + 1 : select.Items.Count - 1;
            width = new() { Name = name + "Width", Header = DesktopResources.Get("ImageWidth"), HorizontalAlignment = HorizontalAlignment.Stretch };
            height = new() { Name = name + "Height", Header = DesktopResources.Get("ImageHeight"), HorizontalAlignment = HorizontalAlignment.Stretch };
            foreach (var field in new[] { width, height })
            {
                field.InputScope = new InputScope { Names = { new InputScopeName { NameValue = InputScopeNameValue.Number } } };
                ToolbarControls.Label(field, DesktopResources.Get(key) + ": " + field.Header); ControlAppearance.Stock(field);
            }
            ToolbarControls.Label(select, DesktopResources.Get(key)); ControlAppearance.Stock(select);
            SetParts(select.SelectedIndex == 0 ? null : value);
            select.SelectionChanged += (_, _) =>
            {
                if (updating) return;
                var choice = (string)((ComboBoxItem)select.SelectedItem).Tag;
                SetParts(choice == "custom" ? string.IsNullOrEmpty(width.Text) || string.IsNullOrEmpty(height.Text) ? presets[0] : Value : choice);
            };
            width.TextChanged += (_, _) => SelectFromParts(); height.TextChanged += (_, _) => SelectFromParts();
            var row = new Grid { Name = name + "Fields", ColumnSpacing = 12, RowSpacing = 12 };
            for (var column = 0; column < 2; column++) row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            row.RowDefinitions.Add(new() { Height = GridLength.Auto }); row.RowDefinitions.Add(new() { Height = GridLength.Auto });
            row.Children.Add(select); row.Children.Add(width); row.Children.Add(height);
            Grid.SetColumnSpan(select, 2); Grid.SetRow(width, 1); Grid.SetRow(height, 1); Grid.SetColumn(height, 1);
            panel.Children.Add(row);
        }
        private void SetParts(string? value)
        {
            updating = true;
            var parts = value?.Split(separator, 2);
            width.Text = parts?[0] ?? ""; height.Text = parts is { Length: 2 } ? parts[1] : "";
            width.IsEnabled = height.IsEnabled = select.SelectedIndex != 0;
            updating = false;
        }
        private void SelectFromParts()
        {
            if (updating) return;
            updating = true;
            var value = width.Text.Trim() + separator + height.Text.Trim();
            select.SelectedItem = select.Items.Cast<ComboBoxItem>().FirstOrDefault(item => (string)item.Tag == value) ?? select.Items[^1];
            updating = false;
        }
    }
    private void Build()
    {
        numbers.Clear(); sections.Clear(); validation.Visibility = Visibility.Collapsed;
        var general = new StackPanel { Spacing = 16, Padding = new Thickness(4, 0, 4, 8) }; scroll.Content = general;
        resolution = new(Section(general, "VideoSizeCard", "VideoSize"), "VideoResolution", "VideoResolution", draft.Resolution, VideoPreferences.ResolutionPresets, 'x');
        aspect = new(Section(general, "VideoAspectCard", "ImageAspectRatio"), "VideoAspectRatio", "ImageAspectRatio", draft.AspectRatio, VideoPreferences.AspectPresets, ':');
        var output = Section(general, "VideoOutputCard", "ImageOutput");
        var countPanel = new StackPanel { Name = "VideoCountPanel", Spacing = 8 };
        var countLabel = new TextBlock { Name = "VideoCountLabel", Text = DesktopResources.Format("VideoCountValue", draft.N), TextWrapping = TextWrapping.Wrap }; countPanel.Children.Add(countLabel);
        count = new Slider { Name = "VideoCount", Minimum = 1, Maximum = 10, StepFrequency = 1, Value = draft.N };
        ToolbarControls.Label(count, DesktopResources.Get("VideoCount")); ControlAppearance.Stock(count);
        count.ValueChanged += (_, _) => countLabel.Text = DesktopResources.Format("VideoCountValue", (int)count.Value);
        countPanel.Children.Add(count); output.Children.Add(countPanel);
        numbers["batch"] = Field(output, "VideoBatchLimit", "VideoBatchLimit", draft.MaxVideosPerCall?.ToString(CultureInfo.InvariantCulture));
        var properties = Section(general, "VideoPropertiesCard", "VideoProperties");
        numbers["duration"] = Field(properties, "VideoDuration", "VideoDuration", draft.Duration?.ToString(CultureInfo.InvariantCulture));
        numbers["fps"] = Field(properties, "VideoFps", "VideoFps", draft.Fps?.ToString(CultureInfo.InvariantCulture));
        // Keep the label outside ToggleSwitch's header template so it cannot overlap On/Off.
        var audioPanel = new StackPanel { Name = "VideoAudioFields", Spacing = 8 };
        audioPanel.Children.Add(new TextBlock { Name = "VideoGenerateAudioLabel", Text = DesktopResources.Get("VideoGenerateAudio"), TextWrapping = TextWrapping.Wrap });
        audio = new ToggleSwitch { Name = "VideoGenerateAudio", IsOn = draft.GenerateAudio, HorizontalAlignment = HorizontalAlignment.Left };
        ToolbarControls.Label(audio, DesktopResources.Get("VideoGenerateAudio")); ControlAppearance.Stock(audio); audioPanel.Children.Add(audio); properties.Children.Add(audioPanel);
        references = Section(general, "VideoReferencesCard", "VideoInputReferences");
        var frames = Section(general, "VideoFramesCard", "VideoFrames");
        first = new StackPanel { Name = "VideoFirstFramePanel", Spacing = 12 }; last = new StackPanel { Name = "VideoLastFramePanel", Spacing = 12 };
        frames.Children.Add(first); frames.Children.Add(last); RenderInputs();
        numbers["seed"] = Field(Section(general, "VideoOtherCard", "ImageOther"), "VideoSeed", "ImageSeed", draft.Seed?.ToString(CultureInfo.InvariantCulture));
        scroll.ChangeView(null, 0, null);
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
        if (kind != "references") panel.Children.Add(new TextBlock { Text = DesktopResources.Get(key), TextWrapping = TextWrapping.Wrap, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        var add = new Button { Name = "VideoAdd_" + kind, Content = kind == "references" ? DesktopResources.Get(key) : DesktopResources.Get("VideoChooseFrame") };
        ToolbarControls.Label(add, DesktopResources.Get(key)); ControlAppearance.Stock(add); add.Click += async (_, _) => await PickAsync(kind); panel.Children.Add(add);
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
            var invalid = resolution.InvalidField ?? aspect.InvalidField;
            foreach (var (key, field) in numbers)
                if (invalid is null && !string.IsNullOrWhiteSpace(field.Text)
                    && (!int.TryParse(field.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || key != "seed" && value < 1)) invalid = field;
            if (invalid is not null)
            {
                RevealInvalidField(invalid);
                throw new InvalidOperationException(DesktopResources.Get("VideoSettingsInvalid"));
            }
            int? Number(string key) => string.IsNullOrWhiteSpace(numbers[key].Text) ? null : int.Parse(numbers[key].Text, CultureInfo.InvariantCulture);
            draft.Resolution = resolution.Value; draft.AspectRatio = aspect.Value;
            draft.N = (int)count.Value; draft.MaxVideosPerCall = Number("batch"); draft.Duration = Number("duration"); draft.Fps = Number("fps"); draft.Seed = Number("seed"); draft.GenerateAudio = audio.IsOn;
            draft.Validate(); if (SaveAsync is not null) await SaveAsync(draft.Clone()); committed = true;
        }
        catch (Exception error) { args.Cancel = true; ShowError(error is InvalidOperationException ? error.Message : DesktopResources.Get("VideoSettingsInvalid")); }
        finally { working = false; deferral.Complete(); }
    }
    private void RevealInvalidField(Control field)
    {
        foreach (var (expander, body) in sections) if (NativeSettingsSurface.Contains(body, field)) expander.IsExpanded = true;
        DispatcherQueue.TryEnqueue(() => { layout.UpdateLayout(); field.Focus(FocusState.Programmatic); field.StartBringIntoView(); });
    }
    private void ShowError(string message) { validation.Text = message; validation.Visibility = Visibility.Visible; }
    private void RootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => SizeToRoot();
    void IResponsiveDialog.SizeToRoot() => SizeToRoot();
    private void SizeToRoot() { layout.Width = Math.Max(0, Math.Min(820, XamlRoot.Size.Width - 96)); layout.Height = Math.Max(0, Math.Min(620, XamlRoot.Size.Height - 240)); }
}
