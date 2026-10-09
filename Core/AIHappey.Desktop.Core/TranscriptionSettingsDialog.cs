using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace AIHappey.Desktop.Core;

public interface ITranscriptionProviderForm
{
    string Title { get; }
    FrameworkElement View { get; }
    void Commit(TranscriptionPreferences preferences);
}
public static class TranscriptionProviderForms
{
    private static readonly Dictionary<string, Func<JsonObject, ITranscriptionProviderForm>> Factories = new(StringComparer.OrdinalIgnoreCase)
    { ["openai"] = config => new OpenAITranscriptionSettingsForm(config) };
    public static void Register(string key, Func<JsonObject, ITranscriptionProviderForm> factory) => Factories[key] = factory;
    public static ITranscriptionProviderForm? Create(string key, TranscriptionPreferences preferences) => Factories.TryGetValue(key, out var factory)
        ? factory((JsonObject)(preferences.ProviderOptions.GetValueOrDefault(key) ?? new()).DeepClone()) : null;
}
public sealed class OpenAITranscriptionSettingsForm : ITranscriptionProviderForm
{
    private readonly JsonObject config;
    private readonly TextBox language, languages, keywords, prompt;
    private readonly ComboBox format;
    public string Title => "OpenAI";
    public FrameworkElement View { get; }
    public OpenAITranscriptionSettingsForm(JsonObject source)
    {
        config = TranscriptionPreferences.CleanOpenAI(source);
        var panel = new StackPanel { Name = "OpenAITranscriptionForm", Spacing = 16 };
        NativeSettingsSurface.Card(panel, "TranscriptionGeneralCard", DesktopResources.Get("General"), out var general);
        TextBox Field(string name, string label, string placeholder, string value, bool multiline = false)
        {
            var field = new TextBox { Name = name, Header = DesktopResources.Get(label), PlaceholderText = DesktopResources.Get(placeholder), Text = value,
                AcceptsReturn = multiline, TextWrapping = TextWrapping.Wrap, MinHeight = multiline ? 88 : 0, MaxHeight = multiline ? 200 : double.PositiveInfinity, HorizontalAlignment = HorizontalAlignment.Stretch };
            ControlAppearance.Stock(field); ToolbarControls.Label(field, DesktopResources.Get(label)); general.Children.Add(field); return field;
        }
        language = Field("TranscriptionLanguage", "TranscriptionLanguage", "TranscriptionLanguageHint", OpenAIChatConfig.Text(config["language"]) ?? "");
        languages = Field("TranscriptionLanguages", "TranscriptionLanguages", "TranscriptionLanguagesHint", Join(config["languages"], ", "));
        keywords = Field("TranscriptionKeywords", "TranscriptionKeywords", "TranscriptionKeywordsHint", Join(config["keywords"], "\n"), true);
        prompt = Field("TranscriptionPrompt", "TranscriptionPrompt", "TranscriptionPromptHint", OpenAIChatConfig.Text(config["prompt"]) ?? "", true);
        format = new ComboBox { Name = "TranscriptionResponseFormat", Header = DesktopResources.Get("TranscriptionResponseFormat"), HorizontalAlignment = HorizontalAlignment.Stretch };
        var selected = OpenAIChatConfig.Text(config["response_format"]) ?? "json";
        foreach (var choice in new[] { "json", "text", "srt", "verbose_json", "vtt", "diarized_json" }.Append(selected).Distinct()) format.Items.Add(new ComboBoxItem { Content = choice, Tag = choice });
        format.SelectedItem = format.Items.Cast<ComboBoxItem>().First(i => (string)i.Tag == selected);
        ControlAppearance.Stock(format); ToolbarControls.Label(format, DesktopResources.Get("TranscriptionResponseFormat")); general.Children.Add(format);
        general.Children.Add(new TextBlock { Text = DesktopResources.Get("TranscriptionFormatHint"), TextWrapping = TextWrapping.Wrap });
        View = panel;
    }
    private static string Join(JsonNode? node, string separator) => node is JsonArray array ? string.Join(separator, array.Select(OpenAIChatConfig.Text).Where(s => !string.IsNullOrWhiteSpace(s))) : "";
    public void Commit(TranscriptionPreferences preferences)
    {
        void Text(string key, string value) { if (string.IsNullOrWhiteSpace(value)) config.Remove(key); else config[key] = value; }
        void List(string key, string value, char delimiter)
        {
            var entries = value.Split(delimiter, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (entries.Length == 0) config.Remove(key); else config[key] = new JsonArray(entries.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());
        }
        Text("language", language.Text.Trim()); Text("prompt", prompt.Text);
        List("languages", languages.Text, ','); List("keywords", keywords.Text.Replace("\r", ""), '\n');
        config["response_format"] = (string)((ComboBoxItem)format.SelectedItem).Tag;
        preferences.ProviderOptions["openai"] = (JsonObject)config.DeepClone();
    }
}

public sealed class TranscriptionSettingsDialog : ContentDialog, IResponsiveDialog
{
    private TranscriptionPreferences draft;
    private readonly string[] providers;
    private readonly Grid layout = new() { RowSpacing = 12 };
    private readonly StackPanel tabs = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private readonly ScrollViewer page = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Disabled, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock validation = new() { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    private readonly List<ITranscriptionProviderForm> forms = [];
    public Func<TranscriptionPreferences, Task>? SaveAsync { get; set; }
    public bool DiscardOnShutdown { get; set; }
    public TranscriptionSettingsDialog(TranscriptionPreferences preferences, IEnumerable<string> providers)
    {
        Name = "TranscriptionSettingsDialog"; draft = preferences.Clone(); this.providers = providers.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        Title = DesktopResources.Get("TranscriptionSettings"); CloseButtonText = DesktopResources.Get("Close"); PrimaryButtonText = DesktopResources.Get("ChatRestoreDefaults");
        Resources["ContentDialogMaxWidth"] = 920d; Resources["ContentDialogMinWidth"] = 0d;
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto }); layout.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) }); layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        layout.Children.Add(tabs); Grid.SetRow(page, 1); layout.Children.Add(page); Grid.SetRow(validation, 2); layout.Children.Add(validation); Content = layout;
        AutomationProperties.SetLiveSetting(validation, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Assertive);
        PrimaryButtonClick += (_, args) => { args.Cancel = true; draft = new(); Build(); };
        Closing += async (_, args) =>
        {
            if (DiscardOnShutdown) return;
            var deferral = args.GetDeferral();
            try { foreach (var form in forms) form.Commit(draft); if (SaveAsync is not null) await SaveAsync(draft.Clone()); }
            catch (Exception) { args.Cancel = true; validation.Text = DesktopResources.Get("TranscriptionSettingsSaveFailed"); validation.Visibility = Visibility.Visible; }
            finally { deferral.Complete(); }
        };
        Opened += (_, _) => { SizeToRoot(); XamlRoot.Changed += RootChanged; }; Closed += (_, _) => XamlRoot.Changed -= RootChanged;
        Build();
    }
    private void Build()
    {
        forms.Clear(); tabs.Children.Clear(); validation.Visibility = Visibility.Collapsed;
        foreach (var provider in providers)
        {
            var form = TranscriptionProviderForms.Create(provider, draft); if (form is null) continue; forms.Add(form);
            var tab = new ToggleButton { Content = form.Title, IsChecked = forms.Count == 1 }; ControlAppearance.Stock(tab); tabs.Children.Add(tab);
            tab.Click += (_, _) => { foreach (var other in tabs.Children.OfType<ToggleButton>()) other.IsChecked = other == tab; page.Content = form.View; page.ChangeView(null, 0, null); };
        }
        page.Content = forms.FirstOrDefault()?.View ?? (object)new TextBlock { Text = DesktopResources.Get("TranscriptionNoProviderSettings"), TextWrapping = TextWrapping.Wrap };
    }
    private void RootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => SizeToRoot();
    void IResponsiveDialog.SizeToRoot() => SizeToRoot();
    private void SizeToRoot() { layout.Width = Math.Max(0, Math.Min(820, XamlRoot.Size.Width - 96)); layout.Height = Math.Max(0, Math.Min(680, XamlRoot.Size.Height - 240)); }
}
