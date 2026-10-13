using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace AIHappey.Desktop.Core;

public interface ISpeechProviderForm
{
    string Title { get; }
    FrameworkElement View { get; }
    void Commit(SpeechPreferences preferences);
}

public static class SpeechProviderForms
{
    private static readonly Dictionary<string, Func<JsonObject, ISpeechProviderForm>> Factories = new(StringComparer.OrdinalIgnoreCase)
    {
        ["openai"] = config => new OpenAISpeechSettingsForm(config)
    };

    public static void Register(string key, Func<JsonObject, ISpeechProviderForm> factory) => Factories[key] = factory;
    public static ISpeechProviderForm? Create(string key, SpeechPreferences preferences) => Factories.TryGetValue(key, out var factory)
        ? factory((JsonObject)(preferences.ProviderOptions.GetValueOrDefault(key) ?? new JsonObject()).DeepClone())
        : null;
}

public sealed class OpenAISpeechSettingsForm : ISpeechProviderForm
{
    private readonly JsonObject config;
    private readonly ComboBox voice = new() { Name = "OpenAISpeechVoice", Header = DesktopResources.Get("SpeechVoice"), HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox customVoice = new() { Name = "OpenAISpeechCustomVoice", Header = DesktopResources.Get("SpeechCustomVoice"), PlaceholderText = "voice_1234", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox format = new() { Name = "OpenAISpeechOutputFormat", Header = DesktopResources.Get("SpeechOutputFormat"), HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly NumberBox speed = new() { Name = "OpenAISpeechSpeed", Header = DesktopResources.Get("SpeechSpeed"), Minimum = .25, Maximum = 4, SmallChange = .25, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
    private bool speedChanged;

    public string Title => "OpenAI";
    public FrameworkElement View { get; }

    public OpenAISpeechSettingsForm(JsonObject source)
    {
        config = (JsonObject)source.DeepClone();
        var panel = new StackPanel { Name = "OpenAISpeechForm", Spacing = 16 };
        NativeSettingsSurface.Card(panel, "OpenAISpeechGeneralCard", DesktopResources.Get("General"), out var body);

        Add(voice, DesktopResources.Get("SpeechVoice"));
        voice.Items.Add(Item(DesktopResources.Get("ProviderDefault"), OpenAISpeechOptions.DefaultVoice));
        foreach (var value in OpenAISpeechOptions.Voices) voice.Items.Add(Item(Capitalize(value), value));
        voice.Items.Add(Item(DesktopResources.Get("Custom"), OpenAISpeechOptions.CustomVoice));
        var selectedVoice = OpenAISpeechOptions.VoiceSelection(config);
        voice.SelectedItem = voice.Items.Cast<ComboBoxItem>().First(item => (string)item.Tag == selectedVoice);

        customVoice.Text = OpenAISpeechOptions.CustomVoiceId(config);
        Add(customVoice, DesktopResources.Get("SpeechCustomVoice"));

        format.Items.Add(Item(DesktopResources.Get("ProviderDefault"), ""));
        var selectedFormat = Text(config["response_format"]) ?? "";
        foreach (var value in OpenAISpeechOptions.Formats.Append(selectedFormat).Where(value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
            format.Items.Add(Item(value.ToUpperInvariant(), value));
        format.SelectedItem = format.Items.Cast<ComboBoxItem>().First(item => string.Equals((string)item.Tag, selectedFormat, StringComparison.OrdinalIgnoreCase));
        Add(format, DesktopResources.Get("SpeechOutputFormat"));

        speed.Value = Number(config["speed"]) ?? 1;
        Add(speed, DesktopResources.Get("SpeechSpeed"));
        speed.ValueChanged += (_, _) => speedChanged = true;
        voice.SelectionChanged += (_, _) => RefreshCustomVoice();
        RefreshCustomVoice();

        body.Children.Add(voice);
        body.Children.Add(customVoice);
        body.Children.Add(format);
        body.Children.Add(speed);
        View = panel;
    }

    public void Commit(SpeechPreferences preferences)
    {
        var selection = (voice.SelectedItem as ComboBoxItem)?.Tag as string ?? OpenAISpeechOptions.DefaultVoice;
        if (selection == OpenAISpeechOptions.CustomVoice && string.IsNullOrWhiteSpace(customVoice.Text))
            throw new InvalidOperationException(DesktopResources.Get("SpeechCustomVoiceRequired"));
        if (!double.IsFinite(speed.Value) || speed.Value is < .25 or > 4)
            throw new InvalidOperationException(DesktopResources.Get("SpeechOpenAISpeedInvalid"));
        OpenAISpeechOptions.SetVoice(config, selection, customVoice.Text);
        OpenAISpeechOptions.SetFormat(config, (format.SelectedItem as ComboBoxItem)?.Tag as string);
        if (speedChanged) OpenAISpeechOptions.SetSpeed(config, speed.Value);
        preferences.ProviderOptions["openai"] = (JsonObject)config.DeepClone();
    }

    private void RefreshCustomVoice() => customVoice.IsEnabled = (voice.SelectedItem as ComboBoxItem)?.Tag as string == OpenAISpeechOptions.CustomVoice;
    private static ComboBoxItem Item(string content, string tag) => new() { Content = content, Tag = tag };
    private static string Capitalize(string value) => char.ToUpperInvariant(value[0]) + value[1..];
    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    private static double? Number(JsonNode? node) => node is JsonValue value && value.TryGetValue<double>(out var number) && double.IsFinite(number) ? number : null;
    private static void Add(Control control, string label)
    {
        ControlAppearance.Stock(control);
        AutomationProperties.SetName(control, label);
    }
}

internal sealed class GeneralSpeechSettingsForm
{
    private readonly TextBox voice = Field("SpeechVoice", "SpeechVoice", "SpeechVoiceHint");
    private readonly ComboBox format = new() { Name = "SpeechOutputFormat", Header = DesktopResources.Get("SpeechOutputFormat"), HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly NumberBox speed = new() { Name = "SpeechSpeed", Header = DesktopResources.Get("SpeechSpeed"), Minimum = .5, Maximum = 2, SmallChange = .1, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
    private readonly TextBox language = Field("SpeechLanguage", "SpeechLanguage", "SpeechLanguageHint");
    private readonly TextBox instructions = Field("SpeechInstructions", "SpeechInstructions", "SpeechInstructionsHint", true);
    public FrameworkElement View { get; }

    public GeneralSpeechSettingsForm(SpeechPreferences preferences)
    {
        var panel = new StackPanel { Name = "GeneralSpeechForm", Spacing = 16 };
        NativeSettingsSurface.Card(panel, "SpeechGeneralCard", DesktopResources.Get("General"), out var body);
        format.Items.Add(new ComboBoxItem { Content = DesktopResources.Get("ProviderDefault"), Tag = "" });
        foreach (var value in SpeechAudio.OutputFormats) format.Items.Add(new ComboBoxItem { Content = value.ToUpperInvariant(), Tag = value });
        format.SelectedItem = format.Items.Cast<ComboBoxItem>().First(item => string.Equals((string)item.Tag, preferences.OutputFormat ?? "", StringComparison.OrdinalIgnoreCase));
        voice.Text = preferences.Voice ?? "";
        language.Text = preferences.Language ?? "";
        instructions.Text = preferences.Instructions ?? "";
        speed.Value = preferences.Speed ?? 1;
        ControlAppearance.Stock(format);
        ControlAppearance.Stock(speed);
        foreach (var control in new FrameworkElement[] { voice, format, speed, language, instructions }) body.Children.Add(control);
        View = panel;
    }

    public void Commit(SpeechPreferences preferences)
    {
        preferences.Voice = Clean(voice.Text);
        preferences.OutputFormat = Clean((format.SelectedItem as ComboBoxItem)?.Tag as string);
        preferences.Speed = Math.Abs(speed.Value - 1) < .0001 ? null : speed.Value;
        preferences.Language = Clean(language.Text);
        preferences.Instructions = Clean(instructions.Text);
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static TextBox Field(string name, string label, string placeholder, bool multiline = false)
    {
        var box = new TextBox
        {
            Name = name, Header = DesktopResources.Get(label), PlaceholderText = DesktopResources.Get(placeholder),
            HorizontalAlignment = HorizontalAlignment.Stretch, AcceptsReturn = multiline,
            TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap, MinHeight = multiline ? 96 : 0
        };
        ControlAppearance.Stock(box);
        AutomationProperties.SetName(box, DesktopResources.Get(label));
        return box;
    }
}

public sealed class SpeechSettingsDialog : ContentDialog, IResponsiveDialog
{
    private SpeechPreferences draft;
    private readonly string? provider;
    private readonly Grid layout = new() { RowSpacing = 12 };
    private readonly StackPanel tabs = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private readonly ScrollViewer page = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Disabled, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock validation = new() { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    private GeneralSpeechSettingsForm general = null!;
    private ISpeechProviderForm? providerForm;
    public Func<SpeechPreferences, Task>? SaveAsync { get; set; }
    public bool DiscardOnShutdown { get; set; }

    public SpeechSettingsDialog(SpeechPreferences preferences, string? provider)
    {
        Name = "SpeechSettingsDialog";
        draft = preferences.Clone();
        this.provider = string.IsNullOrWhiteSpace(provider) ? null : provider.Trim().ToLowerInvariant();
        Title = DesktopResources.Get("SpeechSettings");
        CloseButtonText = DesktopResources.Get("Close");
        PrimaryButtonText = DesktopResources.Get("ChatRestoreDefaults");
        Resources["ContentDialogMaxWidth"] = 920d;
        Resources["ContentDialogMinWidth"] = 0d;
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        layout.Children.Add(tabs);
        Grid.SetRow(page, 1); layout.Children.Add(page);
        Grid.SetRow(validation, 2); layout.Children.Add(validation);
        Content = layout;
        AutomationProperties.SetLiveSetting(validation, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Assertive);
        PrimaryButtonClick += (_, args) => { args.Cancel = true; draft = new(); Build(); };
        Closing += Save;
        Opened += (_, _) => { SizeToRoot(); XamlRoot.Changed += RootChanged; };
        Closed += (_, _) => XamlRoot.Changed -= RootChanged;
        Build();
    }

    private void Build()
    {
        tabs.Children.Clear();
        validation.Visibility = Visibility.Collapsed;
        general = new(draft);
        providerForm = provider is null ? null : SpeechProviderForms.Create(provider, draft);
        AddTab(DesktopResources.Get("General"), general.View, true);
        if (providerForm is not null) AddTab(providerForm.Title, providerForm.View, false);
        page.Content = general.View;
    }

    private void AddTab(string title, FrameworkElement view, bool selected)
    {
        var tab = new ToggleButton { Content = title, IsChecked = selected };
        ControlAppearance.Stock(tab);
        tabs.Children.Add(tab);
        tab.Click += (_, _) =>
        {
            foreach (var other in tabs.Children.OfType<ToggleButton>()) other.IsChecked = other == tab;
            page.Content = view;
            page.ChangeView(null, 0, null);
        };
    }

    private async void Save(ContentDialog sender, ContentDialogClosingEventArgs args)
    {
        if (DiscardOnShutdown) return;
        var deferral = args.GetDeferral();
        try
        {
            general.Commit(draft);
            providerForm?.Commit(draft);
            draft.Validate();
            if (SaveAsync is not null) await SaveAsync(draft.Clone());
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            args.Cancel = true;
            validation.Text = DesktopResources.Format("SpeechSettingsSaveFailedDetail", error.Message);
            validation.Visibility = Visibility.Visible;
        }
        finally { deferral.Complete(); }
    }

    private void RootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => SizeToRoot();
    void IResponsiveDialog.SizeToRoot() => SizeToRoot();
    private void SizeToRoot()
    {
        layout.Width = Math.Max(0, Math.Min(820, XamlRoot.Size.Width - 96));
        layout.Height = Math.Max(0, Math.Min(680, XamlRoot.Size.Height - 240));
    }
}
