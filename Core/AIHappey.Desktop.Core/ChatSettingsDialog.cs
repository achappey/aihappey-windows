using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AIHappey.Desktop.Core;

public sealed class ChatSettingsDialog : ContentDialog, IResponsiveDialog
{
    private ChatPreferences draft;
    private readonly string? provider;
    private readonly Grid layout = new() { RowSpacing = 16 };
    private readonly NavigationView tabs = new() { Name = "ChatSettingsTabs", PaneDisplayMode = NavigationViewPaneDisplayMode.Top,
        IsSettingsVisible = false, IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed,
        IsPaneToggleButtonVisible = false, AlwaysShowHeader = false, Height = 56 };
    private readonly ScrollViewer page = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Disabled, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock validation = new() { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    private TextBox tokens = null!;
    private StackPanel general = null!;
    private ChatToolSettingsView? toolSettings;
    private readonly Func<ChatPreferences, IReadOnlyList<ChatToolOption>> availableTools;
    private readonly bool modelChat;
    private IChatProviderForm? providerForm;
    private readonly RealtimeSettingsForm? realtimeForm;
    private SkillsSettingsView? skillsView;
    private readonly Func<CancellationToken, Task<DesktopSkillSelection>>? loadSkills;
    private readonly Func<string, CancellationToken, Task>? prefetchSkill;
    private Action? selectSkillsTab;
    private Action? selectToolsTab;
    private Action? selectGeneralTab, selectProviderTab;
    private Action? selectPluginsTab;
    private readonly IReadOnlyList<DesktopAgentPlugin> plugins;
    public ChatPreferences? Result { get; private set; }
    public RealtimePreferences? RealtimeResult { get; private set; }
    public Func<ChatPreferences, Task>? SaveAsync { get; set; }
    public Func<ChatPreferences, RealtimePreferences, Task>? SaveWithRealtimeAsync { get; set; }
    public bool DiscardOnShutdown { get; set; }
    public Task RefreshSkillsAsync() => skillsView?.RefreshAsync() ?? Task.CompletedTask;
    public void CancelSkillLoading() => skillsView?.Dispose();
    public void ShowSkillsTab() => selectSkillsTab?.Invoke();
    public void ShowToolsTab() => selectToolsTab?.Invoke();
    public void ShowPluginsTab() => selectPluginsTab?.Invoke();
    public void RefreshAvailableTools() => toolSettings?.RefreshAvailableTools();

    public ChatSettingsDialog(ChatPreferences preferences, string? provider,
        Func<CancellationToken, Task<DesktopSkillSelection>>? loadSkills = null,
        Func<string, CancellationToken, Task>? prefetchSkill = null, IReadOnlyList<DesktopAgentPlugin>? plugins = null,
        Func<ChatPreferences, IReadOnlyList<ChatToolOption>>? availableTools = null, ServiceKind service = ServiceKind.Ai,
        RealtimePreferences? realtimePreferences = null, bool realtimeReadOnly = false,
        IReadOnlyList<ChatTarget>? liveModels = null, Func<CancellationToken, Task<IReadOnlyList<ChatTarget>>>? liveAgents = null)
    {
        this.loadSkills = loadSkills; this.prefetchSkill = prefetchSkill;
        this.plugins = plugins ?? [];
        modelChat = service == ServiceKind.Ai;
        this.availableTools = availableTools ?? (draft => McpTurnSnapshot.Empty.SettingsTools(draft,
            draft.EnabledSkillIds.Count > 0, this.plugins.Any(plugin => draft.EnabledAgentPluginIds.Contains(plugin.Id))));
        Name = "ChatSettingsDialog"; this.provider = provider; draft = preferences.Clone();
        if (realtimePreferences is not null && provider is "openai" or "google")
            realtimeForm = new(realtimePreferences, provider, realtimeReadOnly, liveModels, liveAgents);
        Title = DesktopResources.Get("ChatSettings"); CloseButtonText = DesktopResources.Get("Close");
        PrimaryButtonText = DesktopResources.Get("ChatRestoreDefaults");
        Resources["ContentDialogMaxWidth"] = 920d; Resources["ContentDialogMinWidth"] = 0d;
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        tabs.SelectionChanged += (_, _) =>
        {
            if (tabs.SelectedItem is not NavigationViewItem { Tag: FrameworkElement view }) return;
            if (ReferenceEquals(view, general)) RefreshAvailableTools();
            page.Content = view; page.ChangeView(null, 0, null, true);
        };
        layout.Children.Add(tabs); Grid.SetRow(page, 1); layout.Children.Add(page);
        Grid.SetRow(validation, 2); layout.Children.Add(validation); Content = layout;
        AutomationProperties.SetLiveSetting(validation, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Assertive);
        Build();
        PrimaryButtonClick += (_, args) =>
        {
            // Restore inference options, not the app-wide choices owned by user settings.
            args.Cancel = true; draft = draft.InferenceDefaults();
            realtimeForm?.RestoreDefaults();
            Result = null; validation.Visibility = Visibility.Collapsed; Build();
        };
        Closing += CommitOnClose;
        Opened += (_, _) => { SizeToRoot(); XamlRoot.Changed += RootChanged; tokens.Focus(FocusState.Programmatic); };
        Closed += (_, _) => { XamlRoot.Changed -= RootChanged; skillsView?.Dispose(); };
    }

    private void Build()
    {
        skillsView?.Dispose(); skillsView = new(draft, loadSkills, prefetchSkill);
        page.Content = null; tabs.MenuItems.Clear(); general = new StackPanel { Spacing = 16 };
        NativeSettingsSurface.Card(general, "GeneralChatCard", ChatSettingsFields.L("artificialIntelligence"), out var body);
        tokens = new TextBox { Name = "MaxOutputTokens", Header = DesktopResources.Get("ChatMaxOutputTokens"), PlaceholderText = DesktopResources.Get("ChatOptional"), Text = draft.MaxOutputTokens?.ToString() ?? "" };
        ControlAppearance.Stock(tokens); AutomationProperties.SetName(tokens, DesktopResources.Get("ChatMaxOutputTokens")); body.Children.Add(tokens);
        var tokenError = new TextBlock { Text = DesktopResources.Get("ChatTokensInvalid"), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed }; body.Children.Add(tokenError);
        tokens.TextChanged += (_, _) => tokenError.Visibility = ChatPreferences.TryTokenLimit(tokens.Text, out _) ? Visibility.Collapsed : Visibility.Visible;
        toolSettings = modelChat ? new ChatToolSettingsView(draft, availableTools) : null;
        if (toolSettings is not null) general.Children.Add(toolSettings);
        providerForm = realtimeForm is null ? ChatProviderForms.Create(provider, draft) : null;
        NavigationViewItem Tab(string name, string label, FrameworkElement view)
        {
            var tab = new NavigationViewItem { Name = name, Content = label, Tag = view };
            AutomationProperties.SetAutomationId(tab, name); AutomationProperties.SetName(tab, label);
            tabs.MenuItems.Add(tab); return tab;
        }
        var generalTab = Tab("ChatGeneralTab", DesktopResources.Get("General"), general);
        var toolsView = new LocalToolsSettingsView(draft);
        var toolsTab = Tab("ChatToolsTab", DesktopResources.Get("Tools"), toolsView);
        var skillsTab = Tab("ChatSkillsTab", DesktopResources.Get("Skills"), skillsView);
        var pluginsView = new PluginsSettingsView(plugins, draft.EnabledAgentPluginIds);
        var pluginsTab = Tab("ChatPluginsTab", DesktopResources.Get("PluginsTitle"), pluginsView);
        selectPluginsTab = () => tabs.SelectedItem = pluginsTab;
        selectProviderTab = null;
        if (providerForm is not null || realtimeForm is not null)
        {
            var providerTab = Tab("ChatProviderTab", provider?.ToLowerInvariant() switch { "openai" => "OpenAI", "google" => "Google", "mistral" => "Mistral", "anthropic" => "Anthropic", "deepseek" => "DeepSeek", _ => provider ?? "" },
                (FrameworkElement?)realtimeForm ?? providerForm!.View);
            selectProviderTab = () => tabs.SelectedItem = providerTab;
        }
        selectGeneralTab = () => tabs.SelectedItem = generalTab;
        selectSkillsTab = () => tabs.SelectedItem = skillsTab;
        selectToolsTab = () => tabs.SelectedItem = toolsTab;
        selectGeneralTab();
    }

    private async void CommitOnClose(ContentDialog sender, ContentDialogClosingEventArgs args)
    {
        if (DiscardOnShutdown) return;
        if (!ChatPreferences.TryTokenLimit(tokens.Text, out var limit) || toolSettings?.IsValid == false || providerForm?.IsValid == false || realtimeForm?.IsValid == false)
        {
            args.Cancel = true; validation.Text = DesktopResources.Get("ChatSettingsInvalid"); validation.Visibility = Visibility.Visible;
            if (!ChatPreferences.TryTokenLimit(tokens.Text, out _))
            {
                selectGeneralTab?.Invoke(); layout.UpdateLayout(); tokens.Focus(FocusState.Programmatic); tokens.StartBringIntoView();
            }
            else if (toolSettings?.IsValid == false) { selectGeneralTab?.Invoke(); layout.UpdateLayout(); toolSettings.FocusInvalid(); }
            else { selectProviderTab?.Invoke(); layout.UpdateLayout(); providerForm?.FocusInvalid(); realtimeForm?.FocusInvalid(); }
            return;
        }
        var deferral = args.GetDeferral();
        try
        {
            draft.MaxOutputTokens = limit; providerForm?.Commit(draft);
            if (modelChat) draft.NormalizeTools();
            var realtime = realtimeForm?.Result;
            if (realtime is not null && SaveWithRealtimeAsync is not null) await SaveWithRealtimeAsync(draft.Clone(), realtime.Clone());
            else if (SaveAsync is not null) await SaveAsync(draft.Clone());
            Result = draft.Clone();
            RealtimeResult = realtime;
        }
        catch (Exception)
        {
            args.Cancel = true; validation.Text = DesktopResources.Get("ChatSettingsSaveFailed"); validation.Visibility = Visibility.Visible;
        }
        finally { deferral.Complete(); }
    }
    private void RootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => SizeToRoot();
    protected override void OnApplyTemplate()
    {
        // Bound top-navigation overflow before its first measure, including callers that
        // show the dialog directly rather than through SystemAppearance.PrepareDialog.
        if (XamlRoot is not null) SizeToRoot();
        base.OnApplyTemplate();
    }
    void IResponsiveDialog.SizeToRoot() => SizeToRoot();
    private void SizeToRoot()
    {
        layout.Width = Math.Max(0, Math.Min(820, XamlRoot.Size.Width - 96));
        // Native ContentDialog chrome can leave less space than the root-size estimate
        // (including its own maximum height). Allow the grid to shrink with its presenter;
        // a fixed Height makes the inner scroll viewport extend underneath the footer.
        layout.MaxHeight = Math.Max(0, Math.Min(680, XamlRoot.Size.Height - 240));
    }
}
