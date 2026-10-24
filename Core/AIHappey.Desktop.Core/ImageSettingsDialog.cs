using System.Globalization;
using System.Text.Json.Nodes;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Pickers;

namespace AIHappey.Desktop.Core;

public interface IImageProviderForm
{
    string Title { get; }
    FrameworkElement View { get; }
    void Commit(ImagePreferences preferences);
}

/// <summary>Add a factory for each new provider; the dialog and request client need no provider branches.</summary>
public static class ImageProviderForms
{
    private static readonly Dictionary<string, Func<JsonObject, IImageProviderForm>> Factories = new(StringComparer.OrdinalIgnoreCase)
    { ["openai"] = config => new OpenAIImageSettingsForm(config) };
    public static void Register(string key, Func<JsonObject, IImageProviderForm> factory) => Factories[key] = factory;
    public static IImageProviderForm? Create(string? key, ImagePreferences preferences) => key is not null && Factories.TryGetValue(key, out var factory)
        ? factory((JsonObject)(preferences.ProviderOptions.GetValueOrDefault(key) ?? new JsonObject()).DeepClone()) : null;
}

public sealed class OpenAIImageSettingsForm : IImageProviderForm
{
    private readonly JsonObject config;
    public string Title => "OpenAI";
    public FrameworkElement View { get; }
    public OpenAIImageSettingsForm(JsonObject config)
    {
        this.config = config;
        var panel = new StackPanel { Name = "OpenAIImageForm", Spacing = 16 };
        foreach (var (field, label, choices) in new[]
        {
            ("quality", "ImageQuality", new[] { "auto", "low", "medium", "high" }),
            ("background", "ImageBackground", new[] { "auto", "transparent", "opaque" }),
            ("moderation", "ImageModeration", new[] { "auto", "low" })
        })
        {
            var select = new ComboBox { Name = "ImageOpenAI" + field, Header = DesktopResources.Get(label), HorizontalAlignment = HorizontalAlignment.Stretch };
            foreach (var choice in choices) select.Items.Add(new ComboBoxItem { Content = DesktopResources.Get("ImageOption_" + choice), Tag = choice });
            // Never erase future provider values merely by opening the form.
            var selected = OpenAIChatConfig.Text(config[field]) ?? "auto";
            if (!choices.Contains(selected)) select.Items.Add(new ComboBoxItem { Content = selected, Tag = selected });
            select.SelectedItem = select.Items.Cast<ComboBoxItem>().First(i => (string)i.Tag == selected);
            select.SelectionChanged += (_, _) => this.config[field] = (string)((ComboBoxItem)select.SelectedItem).Tag;
            ControlAppearance.Stock(select); AutomationProperties.SetName(select, DesktopResources.Get(label)); panel.Children.Add(select);
        }
        View = panel;
    }
    public void Commit(ImagePreferences preferences) => preferences.ProviderOptions["openai"] = (JsonObject)config.DeepClone();
}

public sealed class ImageSettingsDialog : ContentDialog, IResponsiveDialog
{
    private ImagePreferences draft;
    private readonly string? provider;
    private readonly Grid layout = new() { RowSpacing = 16 };
    private readonly StackPanel tabs = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private readonly ScrollViewer page = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Disabled, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock validation = new() { Name = "ImageSettingsValidation", TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    private readonly string maskDirectory;
    private readonly CancellationTokenSource lifetime = new();
    private readonly List<(SettingsExpander Expander, StackPanel Body)> sections = [];
    private Action showGeneral = null!;
    private IImageProviderForm? providerForm;
    private DimensionFields size = null!, aspect = null!;
    private TextBox batch = null!, seed = null!;
    private Slider count = null!;
    private ComposerAttachment? newMask;
    private bool maskChanged;
    private bool pickerOpen;
    public Func<ImagePreferences, Task>? SaveAsync { get; set; }
    public bool DiscardOnShutdown { get; set; }
    public ImageSettingsDialog(ImagePreferences preferences, string? provider, string maskDirectory)
    {
        Name = "ImageSettingsDialog"; draft = preferences.Clone(); this.provider = provider; this.maskDirectory = maskDirectory;
        Title = DesktopResources.Get("ImageSettings"); CloseButtonText = DesktopResources.Get("Close"); PrimaryButtonText = DesktopResources.Get("ChatRestoreDefaults");
        Resources["ContentDialogMaxWidth"] = 920d; Resources["ContentDialogMinWidth"] = 0d;
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        layout.Children.Add(tabs); Grid.SetRow(page, 1); layout.Children.Add(page); Grid.SetRow(validation, 2); layout.Children.Add(validation); Content = layout;
        AutomationProperties.SetLiveSetting(validation, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Assertive);
        PrimaryButtonClick += (_, args) =>
        {
            args.Cancel = true;
            if (pickerOpen) return;
            // Restore inference defaults, not the user's disk location or existing libraries.
            draft = new() { StorageRoot = draft.StorageRoot }; newMask = null; maskChanged = true; Build();
        };
        Closing += CommitOnClose;
        Opened += (_, _) => { SizeToRoot(); XamlRoot.Changed += RootChanged; };
        Closed += (_, _) => { lifetime.Cancel(); XamlRoot.Changed -= RootChanged; lifetime.Dispose(); };
        Build();
    }
    private static TextBox Field(string name, string label, string? value) => new()
    { Name = name, Header = DesktopResources.Get(label), Text = value ?? "", HorizontalAlignment = HorizontalAlignment.Stretch };
    private StackPanel Section(StackPanel parent, string title)
    {
        var expander = NativeSettingsSurface.Expander(parent, title + "Card", DesktopResources.Get(title), null, out var panel);
        sections.Add((expander, panel));
        return panel;
    }
    private sealed class DimensionFields
    {
        private readonly ComboBox select;
        private readonly TextBox width, height;
        private readonly char separator;
        private bool updating;
        public string? Value => select.SelectedIndex == 0 ? null : width.Text.Trim() + separator + height.Text.Trim();
        public Control? InvalidField => ImagePreferences.ValidDimensions(Value, separator) ? null
            : ImagePreferences.ValidDimensions(width.Text.Trim() + separator + "1", separator) ? height : width;

        public DimensionFields(StackPanel panel, string name, string label, string? value, string[] presets, char separator)
        {
            this.separator = separator;
            select = new() { Name = name + "Preset", Header = DesktopResources.Get(label), HorizontalAlignment = HorizontalAlignment.Stretch };
            select.Items.Add(new ComboBoxItem { Content = DesktopResources.Get("ImageProviderDefault"), Tag = "" });
            foreach (var preset in presets) select.Items.Add(new ComboBoxItem { Content = preset, Tag = preset });
            select.Items.Add(new ComboBoxItem { Content = DesktopResources.Get("Custom"), Tag = "custom" });
            select.SelectedIndex = string.IsNullOrEmpty(value) ? 0 : presets.Contains(value) ? Array.IndexOf(presets, value) + 1 : select.Items.Count - 1;
            width = Field(name + "Width", "ImageWidth", null); height = Field(name + "Height", "ImageHeight", null);
            foreach (var field in new[] { width, height })
            {
                field.InputScope = new InputScope { Names = { new InputScopeName { NameValue = InputScopeNameValue.Number } } };
                AutomationProperties.SetName(field, DesktopResources.Get(label) + ": " + field.Header);
                ControlAppearance.Stock(field);
            }
            AutomationProperties.SetName(select, DesktopResources.Get(label)); ControlAppearance.Stock(select);
            SetParts(value);
            select.SelectionChanged += (_, _) =>
            {
                if (updating) return;
                var choice = (string)((ComboBoxItem)select.SelectedItem).Tag;
                // Entering Custom from provider default starts with a complete, editable pair.
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
        validation.Visibility = Visibility.Collapsed; tabs.Children.Clear(); sections.Clear();
        var general = new StackPanel { Spacing = 16 };
        size = new(Section(general, "ImageSize"), "ImageSize", "ImageSize", draft.Size, ImagePreferences.SizePresets, 'x');
        aspect = new(Section(general, "ImageAspectRatio"), "ImageAspectRatio", "ImageAspectRatio", draft.AspectRatio, ImagePreferences.AspectPresets, ':');
        var output = Section(general, "ImageOutput");
        var outputFields = new StackPanel { Name = "ImageOutputFields", Spacing = 12 };
        var countPanel = new StackPanel { Spacing = 8 };
        var countLabel = new TextBlock { Text = DesktopResources.Format("ImageCount", draft.N), TextWrapping = TextWrapping.Wrap }; countPanel.Children.Add(countLabel);
        count = new Slider { Name = "ImageCount", Minimum = 1, Maximum = 20, StepFrequency = 1, Value = draft.N };
        AutomationProperties.SetName(count, DesktopResources.Get("ImageOutput")); count.ValueChanged += (_, _) => countLabel.Text = DesktopResources.Format("ImageCount", (int)count.Value);
        countPanel.Children.Add(count); outputFields.Children.Add(countPanel);
        batch = Field("ImageBatchLimit", "ImageBatchLimit", draft.MaxImagesPerCall?.ToString(CultureInfo.InvariantCulture)); outputFields.Children.Add(batch); output.Children.Add(outputFields);
        var other = Section(general, "ImageOther"); seed = Field("ImageSeed", "ImageSeed", draft.Seed?.ToString(CultureInfo.InvariantCulture)); other.Children.Add(seed);
        var maskRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var pickMask = new Button { Name = "ImagePickMask", Content = DesktopResources.Get("ImageMask") };
        var clearMask = new Button { Name = "ImageClearMask", Content = DesktopResources.Get("Delete") };
        var maskName = new TextBlock { Text = newMask?.Name ?? (draft.MaskPath is null ? "" : Path.GetFileName(draft.MaskPath)), TextWrapping = TextWrapping.Wrap };
        var preview = new Image { Name = "ImageMaskPreview", Width = 96, Height = 96, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left };
        if (!maskChanged && draft.MaskPath is { } path && IsOwnedMask(path) && File.Exists(path)) preview.Source = new BitmapImage(new Uri(path));
        clearMask.IsEnabled = newMask is not null || draft.MaskPath is not null;
        pickMask.Click += async (_, _) =>
        {
            if (pickerOpen) return; pickerOpen = true;
            try
            {
                var picker = new FileOpenPicker(); foreach (var extension in ImageAttachments.Extensions) picker.FileTypeFilter.Add(extension);
                InitializePicker(picker); var file = await picker.PickSingleFileAsync().AsTask(lifetime.Token); if (file is null) return;
                await using var stream = await file.OpenStreamForReadAsync();
                newMask = await ComposerAttachments.ReadAsync(file.Name, file.ContentType, stream, lifetime.Token); ImageAttachments.Validate(newMask);
                maskChanged = true; draft.MaskPath = null; maskName.Text = newMask.Name; clearMask.IsEnabled = true;
                preview.Source = new BitmapImage(new Uri(file.Path));
            }
            catch (OperationCanceledException) { }
            catch (Exception) { Error(DesktopResources.Get("ImageMaskFailed")); }
            finally { pickerOpen = false; }
        };
        clearMask.Click += (_, _) => { if (pickerOpen) return; newMask = null; draft.MaskPath = null; maskChanged = true; preview.Source = null; maskName.Text = ""; clearMask.IsEnabled = false; };
        maskRow.Children.Add(pickMask); maskRow.Children.Add(clearMask); other.Children.Add(maskRow); other.Children.Add(maskName); other.Children.Add(preview);
        foreach (var control in new Control[] { batch, seed, count, pickMask, clearMask }) ControlAppearance.Stock(control);
        providerForm = ImageProviderForms.Create(provider, draft);
        var generalTab = new ToggleButton { Name = "ImageGeneralTab", Content = DesktopResources.Get("General"), IsChecked = true }; tabs.Children.Add(generalTab); ControlAppearance.Stock(generalTab);
        ToggleButton? providerTab = null;
        if (providerForm is not null)
        {
            providerTab = new() { Name = "ImageProviderTab", Content = providerForm.Title }; tabs.Children.Add(providerTab); ControlAppearance.Stock(providerTab);
            providerTab.Click += (_, _) => { generalTab.IsChecked = false; providerTab.IsChecked = true; page.Content = providerForm.View; page.ChangeView(null, 0, null); };
        }
        showGeneral = () => { generalTab.IsChecked = true; if (providerTab is not null) providerTab.IsChecked = false; page.Content = general; };
        generalTab.Click += (_, _) => { showGeneral(); page.ChangeView(null, 0, null); };
        page.Content = general;
    }
    private bool IsOwnedMask(string path) => Path.GetFullPath(path).StartsWith(Path.GetFullPath(maskDirectory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private void InitializePicker(object picker) => WinRT.Interop.InitializeWithWindow.Initialize(picker, Microsoft.UI.Win32Interop.GetWindowFromWindowId(XamlRoot.ContentIslandEnvironment.AppWindowId));
    private void Error(string text) { validation.Text = text; validation.Visibility = Visibility.Visible; }
    private void RevealInvalidField(Control field)
    {
        showGeneral();
        foreach (var (expander, body) in sections) if (NativeSettingsSurface.Contains(body, field)) expander.IsExpanded = true;
        DispatcherQueue.TryEnqueue(() => { layout.UpdateLayout(); field.Focus(FocusState.Programmatic); field.StartBringIntoView(); });
    }
    private async void CommitOnClose(ContentDialog sender, ContentDialogClosingEventArgs args)
    {
        if (DiscardOnShutdown) { lifetime.Cancel(); return; }
        if (pickerOpen) { args.Cancel = true; return; }
        var deferral = args.GetDeferral(); string? createdMask = null;
        try
        {
            draft.Size = size.Value; draft.AspectRatio = aspect.Value; draft.N = (int)count.Value;
            var invalid = size.InvalidField ?? aspect.InvalidField;
            if (invalid is null && !string.IsNullOrWhiteSpace(batch.Text) && (!int.TryParse(batch.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var limit) || limit < 1)) invalid = batch;
            if (invalid is null && !string.IsNullOrWhiteSpace(seed.Text) && !int.TryParse(seed.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)) invalid = seed;
            if (invalid is not null)
            {
                RevealInvalidField(invalid);
                throw new InvalidOperationException(DesktopResources.Get("ImageSettingsInvalid"));
            }
            draft.MaxImagesPerCall = string.IsNullOrWhiteSpace(batch.Text) ? null : int.Parse(batch.Text, CultureInfo.InvariantCulture);
            draft.Seed = string.IsNullOrWhiteSpace(seed.Text) ? null : int.Parse(seed.Text, CultureInfo.InvariantCulture); draft.Validate(); providerForm?.Commit(draft);
            Directory.CreateDirectory(draft.EffectiveRoot);
            if (maskChanged && newMask is not null)
            {
                Directory.CreateDirectory(maskDirectory); createdMask = Path.Combine(maskDirectory, Guid.NewGuid().ToString("N") + ImageAttachments.Extension(newMask.MediaType));
                await File.WriteAllBytesAsync(createdMask, newMask.Content.ToArray()); draft.MaskPath = createdMask;
            }
            if (SaveAsync is not null) await SaveAsync(draft.Clone());
        }
        catch (Exception error)
        {
            if (createdMask is not null) { File.Delete(createdMask); draft.MaskPath = null; }
            args.Cancel = true; Error(error is InvalidOperationException ? error.Message : DesktopResources.Get("ImageSettingsSaveFailed"));
        }
        finally { deferral.Complete(); }
    }
    private void RootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => SizeToRoot();
    void IResponsiveDialog.SizeToRoot() => SizeToRoot();
    private void SizeToRoot() { layout.Width = Math.Max(0, Math.Min(820, XamlRoot.Size.Width - 96)); layout.Height = Math.Max(0, Math.Min(680, XamlRoot.Size.Height - 240)); }
}
