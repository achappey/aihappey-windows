using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
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
    private IImageProviderForm? providerForm;
    private TextBox size = null!, aspect = null!, batch = null!, seed = null!;
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
    private static StackPanel Card(StackPanel parent, string title)
    {
        NativeSettingsSurface.Card(parent, title + "Card", DesktopResources.Get(title), out var panel);
        return panel;
    }
    private static TextBox Dimensions(StackPanel panel, string name, string label, string? value, string[] presets)
    {
        var select = new ComboBox { Name = name + "Preset", Header = DesktopResources.Get(label), HorizontalAlignment = HorizontalAlignment.Stretch };
        select.Items.Add(new ComboBoxItem { Content = DesktopResources.Get("ImageProviderDefault"), Tag = "" });
        foreach (var preset in presets) select.Items.Add(new ComboBoxItem { Content = preset, Tag = preset });
        select.Items.Add(new ComboBoxItem { Content = DesktopResources.Get("Custom"), Tag = "custom" });
        select.SelectedIndex = string.IsNullOrEmpty(value) ? 0 : presets.Contains(value) ? Array.IndexOf(presets, value) + 1 : select.Items.Count - 1;
        var custom = Field(name, label, value); custom.PlaceholderText = label == "ImageSize" ? "1024x1024" : "16:9";
        custom.Visibility = select.SelectedIndex == select.Items.Count - 1 ? Visibility.Visible : Visibility.Collapsed;
        select.SelectionChanged += (_, _) =>
        {
            var choice = (string)((ComboBoxItem)select.SelectedItem).Tag;
            custom.Visibility = choice == "custom" ? Visibility.Visible : Visibility.Collapsed;
            if (choice != "custom") custom.Text = choice;
        };
        ControlAppearance.Stock(select); ControlAppearance.Stock(custom); panel.Children.Add(select); panel.Children.Add(custom); return custom;
    }
    private void Build()
    {
        validation.Visibility = Visibility.Collapsed; tabs.Children.Clear();
        var general = new StackPanel { Spacing = 16 };
        size = Dimensions(Card(general, "ImageSize"), "ImageSize", "ImageSize", draft.Size, ImagePreferences.SizePresets);
        aspect = Dimensions(Card(general, "ImageAspectRatio"), "ImageAspectRatio", "ImageAspectRatio", draft.AspectRatio, ImagePreferences.AspectPresets);
        var output = Card(general, "ImageOutput");
        var countLabel = new TextBlock { Text = DesktopResources.Format("ImageCount", draft.N) }; output.Children.Add(countLabel);
        count = new Slider { Name = "ImageCount", Minimum = 1, Maximum = 20, StepFrequency = 1, Value = draft.N };
        AutomationProperties.SetName(count, DesktopResources.Get("ImageOutput")); count.ValueChanged += (_, _) => countLabel.Text = DesktopResources.Format("ImageCount", (int)count.Value);
        output.Children.Add(count);
        batch = Field("ImageBatchLimit", "ImageBatchLimit", draft.MaxImagesPerCall?.ToString(CultureInfo.InvariantCulture)); output.Children.Add(batch);
        var other = Card(general, "ImageOther"); seed = Field("ImageSeed", "ImageSeed", draft.Seed?.ToString(CultureInfo.InvariantCulture)); other.Children.Add(seed);
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
        var storage = Card(general, "ImageStorage");
        var folder = new TextBlock { Name = "ImageStoragePath", Text = draft.EffectiveRoot, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true }; storage.Children.Add(folder);
        storage.Children.Add(new TextBlock { Text = DesktopResources.Get("ImageFolderHint"), TextWrapping = TextWrapping.Wrap });
        var folderRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var choose = new Button { Name = "ImageChooseFolder", Content = DesktopResources.Get("ImageChooseFolder") };
        var reset = new Button { Name = "ImageDefaultFolder", Content = DesktopResources.Get("ImageDefaultFolder") };
        choose.Click += async (_, _) =>
        {
            if (pickerOpen) return; pickerOpen = true;
            try
            {
                var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary }; picker.FileTypeFilter.Add("*"); InitializePicker(picker);
                var selected = await picker.PickSingleFolderAsync().AsTask(lifetime.Token); if (selected is not null) { draft.StorageRoot = selected.Path; folder.Text = draft.EffectiveRoot; }
            }
            catch (OperationCanceledException) { }
            catch (Exception) { Error(DesktopResources.Get("ImageFolderFailed")); }
            finally { pickerOpen = false; }
        };
        reset.Click += (_, _) => { if (!pickerOpen) { draft.StorageRoot = null; folder.Text = draft.EffectiveRoot; } };
        folderRow.Children.Add(choose); folderRow.Children.Add(reset); storage.Children.Add(folderRow);
        foreach (var control in new Control[] { batch, seed, count, pickMask, clearMask, choose, reset }) ControlAppearance.Stock(control);
        providerForm = ImageProviderForms.Create(provider, draft);
        var generalTab = new ToggleButton { Name = "ImageGeneralTab", Content = DesktopResources.Get("General"), IsChecked = true }; tabs.Children.Add(generalTab); ControlAppearance.Stock(generalTab);
        ToggleButton? providerTab = null;
        if (providerForm is not null)
        {
            providerTab = new() { Name = "ImageProviderTab", Content = providerForm.Title }; tabs.Children.Add(providerTab); ControlAppearance.Stock(providerTab);
            providerTab.Click += (_, _) => { generalTab.IsChecked = false; providerTab.IsChecked = true; page.Content = providerForm.View; page.ChangeView(null, 0, null); };
        }
        generalTab.Click += (_, _) => { generalTab.IsChecked = true; if (providerTab is not null) providerTab.IsChecked = false; page.Content = general; page.ChangeView(null, 0, null); };
        page.Content = general;
    }
    private bool IsOwnedMask(string path) => Path.GetFullPath(path).StartsWith(Path.GetFullPath(maskDirectory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private void InitializePicker(object picker) => WinRT.Interop.InitializeWithWindow.Initialize(picker, Microsoft.UI.Win32Interop.GetWindowFromWindowId(XamlRoot.ContentIslandEnvironment.AppWindowId));
    private void Error(string text) { validation.Text = text; validation.Visibility = Visibility.Visible; }
    private async void CommitOnClose(ContentDialog sender, ContentDialogClosingEventArgs args)
    {
        if (DiscardOnShutdown) { lifetime.Cancel(); return; }
        if (pickerOpen) { args.Cancel = true; return; }
        var deferral = args.GetDeferral(); string? createdMask = null;
        try
        {
            draft.Size = string.IsNullOrWhiteSpace(size.Text) ? null : size.Text.Trim(); draft.AspectRatio = string.IsNullOrWhiteSpace(aspect.Text) ? null : aspect.Text.Trim(); draft.N = (int)count.Value;
            if (!string.IsNullOrWhiteSpace(batch.Text) && (!int.TryParse(batch.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var limit) || limit < 1)
                || !string.IsNullOrWhiteSpace(seed.Text) && !int.TryParse(seed.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                throw new InvalidOperationException(DesktopResources.Get("ImageSettingsInvalid"));
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
