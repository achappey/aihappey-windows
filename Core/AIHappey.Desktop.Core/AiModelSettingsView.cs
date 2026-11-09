using FluentIcons.Common;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AIHappey.Desktop.Core;

/// <summary>Edits a dialog-owned draft, never the live session or current chat selection.</summary>
public sealed class AiModelSettingsView : NavigationView
{
    private readonly AiModelPreferences draft;
    private readonly Dictionary<string, AutoSuggestBox> pickers = [];
    private readonly Dictionary<string, ChatTargetPicker> multiPickers = [];
    private readonly Dictionary<string, TextBlock> states = [];
    private readonly Dictionary<string, StackPanel> pages = [];
    private IReadOnlyList<ChatTarget>? catalog;
    private readonly DesktopSettings? providerSettings;
    private IReadOnlyList<ChatTarget> VisibleModels => providerSettings is null ? catalog ?? [] : ProviderPreferences.Filter(catalog ?? [], providerSettings);
    private IReadOnlyList<ChatTarget> ChoiceModels => AiModelCatalog.PickerChoices(VisibleModels, draft.ShowVoiceModels);

    public AiModelSettingsView(AiModelPreferences draft, IReadOnlyList<ChatTarget>? catalog = null, UIElement? imageSettings = null, UIElement? videoSettings = null,
        DesktopSettings? providerSettings = null)
    {
        this.draft = draft;
        this.catalog = catalog;
        this.providerSettings = providerSettings?.Clone();
        Name = "AiModelSettingsView";
        PaneDisplayMode = NavigationViewPaneDisplayMode.Top;
        IsSettingsVisible = false; IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed;
        IsPaneToggleButtonVisible = false; AlwaysShowHeader = false;
        var content = new Grid { Padding = new Thickness(4, 12, 4, 8) };
        Content = content;
        foreach (var type in AiModelCatalog.Types)
        {
            var label = DesktopResources.Get("AiModelType_" + type);
            MenuItems.Add(new NavigationViewItem { Content = label, Tag = type });
            var page = new StackPanel { Spacing = 16, Visibility = Visibility.Collapsed };
            pages.Add(type, page); content.Children.Add(page);
            var state = new TextBlock { TextWrapping = TextWrapping.Wrap };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetLiveSetting(state, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
            states.Add(type, state);
            if (AiModelPreferences.MultiModelTypes.Contains(type))
            {
                var picker = new ChatTargetPicker { Name = "AiDefaultModel_" + type,
                    AllowEmptySelection = true, PlaceholderText = DesktopResources.Get("SelectModel"),
                    HorizontalAlignment = HorizontalAlignment.Stretch };
                picker.Configure(false, true, new HashSet<string>(), false, "", draft.ShowVoiceModels);
                picker.SetSelection(draft.DefaultsFor(type) ?? []);
                ToolbarControls.Label(picker, label + ": " + DesktopResources.Get("AiDefaultModels"));
                picker.SelectionChanged += () => { draft.SetDefaults(type, picker.Selection.Ids); UpdateState(type); };
                multiPickers.Add(type, picker);
                page.Children.Add(new TextBlock { Text = DesktopResources.Get("AiDefaultModels") });
                page.Children.Add(picker);
            }
            else
            {
                var picker = new AutoSuggestBox
                {
                    Name = "AiDefaultModel_" + type, Header = DesktopResources.Get("AiDefaultModel"),
                    PlaceholderText = DesktopResources.Get("SelectModel"), Text = DisplayDefault(type),
                    QueryIcon = DesktopIcons.Create(Icon.Search), HorizontalAlignment = HorizontalAlignment.Stretch
                };
                ToolbarControls.Label(picker, label + ": " + DesktopResources.Get("AiDefaultModel"));
                pickers.Add(type, picker); page.Children.Add(picker);
                void Search(string query) => picker.ItemsSource = AiModelCatalog.OfType(ChoiceModels, type)
                    .Where(x => string.IsNullOrWhiteSpace(query) || x.Label.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase)
                        || x.Id.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase)).Take(100).ToArray();
                void Choose(ChatTarget selected)
                {
                    draft.SetDefault(type, selected.Id); picker.Text = selected.Id; UpdateState(type);
                }
                picker.TextChanged += (_, args) =>
                {
                    if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
                    Search(picker.Text);
                    // Search text is not a setting until the user commits an available model.
                    if (string.IsNullOrWhiteSpace(picker.Text)) { draft.SetDefault(type, null); UpdateState(type); }
                };
                picker.SuggestionChosen += (_, args) => Choose((ChatTarget)args.SelectedItem);
                picker.QuerySubmitted += (_, args) =>
                {
                    var selected = args.ChosenSuggestion as ChatTarget
                        ?? AiModelCatalog.OfType(ChoiceModels, type).FirstOrDefault(x => x.Id == picker.Text.Trim());
                    if (selected is not null) Choose(selected);
                    else { Search(picker.Text); picker.IsSuggestionListOpen = true; }
                };
                picker.GotFocus += (_, _) => { Search(""); picker.IsSuggestionListOpen = true; };
            }
            page.Children.Add(state);
            var clear = new Button { Name = "AiClearDefault_" + type, Content = DesktopResources.Get("AiClearDefault") };
            clear.Click += (_, _) =>
            {
                draft.SetDefault(type, null);
                if (multiPickers.TryGetValue(type, out var multi)) multi.SetSelection([]);
                else pickers[type].Text = DisplayDefault(type);
                UpdateState(type);
            };
            page.Children.Add(clear);
            if (type is "image" or "video" or "speech" or "transcription")
            {
                var toggle = new ToggleSwitch
                {
                    Name = "AiChatWith_" + type, Header = DesktopResources.Get("AiChatWith_" + type), IsOn = draft.AllowsChat(type)
                };
                ToolbarControls.Label(toggle, DesktopResources.Get("AiChatWith_" + type));
                toggle.Toggled += (_, _) =>
                {
                    switch (type)
                    {
                        case "image": draft.ChatWithImageModels = toggle.IsOn; break;
                        case "video": draft.ChatWithVideoModels = toggle.IsOn; break;
                        case "speech": draft.ChatWithSpeechModels = toggle.IsOn; break;
                        case "transcription": draft.ChatWithTranscriptionModels = toggle.IsOn; break;
                    }
                };
                page.Children.Add(toggle);
            }
            if (type == "image" && imageSettings is not null) page.Children.Add(imageSettings);
            if (type == "speech")
            {
                var voices = new ToggleSwitch { Name = "AiShowVoiceModels", Header = DesktopResources.Get("AiVoiceModels"), IsOn = draft.ShowVoiceModels };
                ToolbarControls.Label(voices, DesktopResources.Get("AiVoiceModels"));
                voices.Toggled += (_, _) =>
                {
                    draft.ShowVoiceModels = voices.IsOn;
                    if (this.catalog is not null) SetCatalog(this.catalog);
                };
                page.Children.Add(voices);
            }
            if (type == "video" && videoSettings is not null) page.Children.Add(videoSettings);
        }
        SelectionChanged += (_, _) =>
        {
            var type = (SelectedItem as NavigationViewItem)?.Tag as string;
            foreach (var page in pages) page.Value.Visibility = page.Key == type ? Visibility.Visible : Visibility.Collapsed;
        };
        SelectedItem = MenuItems[0];
        if (catalog is not null) SetCatalog(catalog);
        else SetCatalogStatus(DesktopResources.Get("AiModelsLoading"));
    }

    public void SetCatalog(IReadOnlyList<ChatTarget> models)
    {
        catalog = models;
        foreach (var type in AiModelCatalog.Types)
        {
            if (multiPickers.TryGetValue(type, out var multi))
            {
                multi.IsEnabled = true;
                multi.Configure(false, true, new HashSet<string>(), false, "", draft.ShowVoiceModels);
                multi.ItemsSource = AiModelCatalog.OfType(VisibleModels, type);
                multi.SetSelection(draft.DefaultsFor(type) ?? (type == "language"
                    ? AiModelCatalog.InitialModels(VisibleModels, draft, type, false) : []));
                UpdateState(type);
                continue;
            }
            pickers[type].IsEnabled = true;
            pickers[type].ItemsSource = AiModelCatalog.OfType(ChoiceModels, type).Take(100).ToArray();
            pickers[type].Text = DisplayDefault(type);
            UpdateState(type);
        }
    }

    public void SetCatalogStatus(string message)
    {
        foreach (var type in AiModelCatalog.Types)
        {
            if (multiPickers.TryGetValue(type, out var multi)) multi.IsEnabled = false;
            else pickers[type].IsEnabled = false;
            states[type].Text = message; states[type].Visibility = Visibility.Visible;
        }
    }

    // Display the effective language fallback without turning a catalog arrival into a saved preference.
    private string DisplayDefault(string type) => draft.DefaultFor(type)
        ?? (type == "language" && catalog is not null ? AiModelCatalog.NewChatModel(VisibleModels, draft) : "");

    private void UpdateState(string type)
    {
        if (catalog is null) return;
        var models = AiModelCatalog.OfType(catalog, type);
        var ids = draft.DefaultsFor(type) ?? [];
        states[type].Text = ids.Any(id => !models.Any(x => x.Id == id))
            ? DesktopResources.Get("AiDefaultUnavailable")
            : ids.Any(id => !VisibleModels.Any(m => m.Id == id && m.ModelType == type)) ? DesktopResources.Get("AiDefaultProviderDisabled")
            : !VisibleModels.Any(m => m.ModelType == type) ? DesktopResources.Get("AiNoModels") : "";
        states[type].Visibility = string.IsNullOrEmpty(states[type].Text) ? Visibility.Collapsed : Visibility.Visible;
    }
}
