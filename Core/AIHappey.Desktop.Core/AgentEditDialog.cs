using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AIHappey.Desktop.Core;

/// <summary>Native editor for the browser Agent contract. Unsupported sections are deliberately
/// not projected away. All controls work on an isolated draft until Save succeeds.</summary>
public sealed partial class AgentEditDialog : ContentDialog, IResponsiveDialog
{
    private readonly DesktopAgent draft;
    private readonly DesktopSession session;
    private readonly DesktopCatalogClient catalog;
    private readonly HttpClient registryHttp;
    private readonly ChatSettingsFields fields = new();
    private readonly Grid layout = new() { RowSpacing = 16 };
    private readonly NavigationView tabs = new() { PaneDisplayMode = NavigationViewPaneDisplayMode.Top, IsSettingsVisible = false,
        IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed, IsPaneToggleButtonVisible = false, AlwaysShowHeader = false, Height = 56 };
    private readonly ScrollViewer page = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        HorizontalScrollMode = ScrollMode.Disabled, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock feedback = new() { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    private readonly Dictionary<string, FrameworkElement> pages = [];
    private readonly CancellationTokenSource lifetime = new();
    private readonly string partition;
    private readonly IReadOnlyList<DesktopSkill> localSkills;
    private readonly IReadOnlyList<ChatTarget> availableAgents;
    private readonly IReadOnlyDictionary<string, string?> catalogModelTypes;
    private readonly Func<DesktopSkill, CancellationToken, Task<byte[]>>? localSkillArchive;
    private OpenAIChatSettingsForm? openAI;
    private ChatPreferences? providerPreferences;
    private JsonObject? providerBaseline;
    private string? settingsModelId;
    private JsonObject? settingsModel;
    private ChatSettingsFields providerFields = new();
    private int pending;
    private bool saving;
    public DesktopAgent? Result { get; private set; }
    public Func<DesktopAgent, CancellationToken, Task>? SaveAsync { get; set; }
    public DesktopAgent Draft => draft.Clone();

    public AgentEditDialog(DesktopAgent agent, bool isEditing, DesktopSession session, DesktopCatalogClient catalog,
        HttpClient registryHttp, IReadOnlyList<ChatTarget> models, McpTurnSnapshot? connected = null,
        IReadOnlyList<DesktopSkill>? localSkills = null, Func<DesktopSkill, CancellationToken, Task<byte[]>>? localSkillArchive = null, IReadOnlyList<DesktopAgentPlugin>? plugins = null,
        IReadOnlyList<ChatTarget>? availableAgents = null)
    {
        draft = agent.Clone(); this.session = session; this.catalog = catalog; this.registryHttp = registryHttp;
        // The catalog can expose the same ID for multiple types. Prefer language only
        // for untyped legacy configurations; picker commits always carry their own type.
        catalogModelTypes = models.GroupBy(model => model.Id).ToDictionary(group => group.Key,
            group => group.FirstOrDefault(model => model.ModelType == "language")?.ModelType ?? group.First().ModelType);
        if (AnswerModels.Count <= 1 && DecisionModels.Count > 0)
            draft.SelectModels(AnswerModels.Select(model => (DesktopAgent.Text(model["id"]), ModelType(DesktopAgent.Text(model["id"])))));
        this.localSkills = localSkills ?? []; this.localSkillArchive = localSkillArchive;
        this.availableAgents = availableAgents ?? [];
        partition = session.AgentPartition;
        Name = "AgentEditDialog";
        Resources["ContentDialogMaxWidth"] = 920d; Resources["ContentDialogMinWidth"] = 0d;
        Title = DesktopResources.Get(isEditing ? "AgentEdit" : "AgentCreate");
        PrimaryButtonText = DesktopResources.Get("Save"); CloseButtonText = DesktopResources.Get("Cancel"); DefaultButton = ContentDialogButton.Primary;
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto }); layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        layout.Children.Add(tabs); Grid.SetRow(feedback, 1); layout.Children.Add(feedback); Grid.SetRow(page, 2); layout.Children.Add(page); Content = layout;
        AutomationProperties.SetLiveSetting(feedback, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        AddTab("general", DesktopResources.Get("General"), General(isEditing, models));
        AddTab("details", DesktopResources.Get("Details"), Details());
        AddTab("modelContext", DesktopResources.Get("McpTitle"), ModelContext(connected ?? McpTurnSnapshot.Empty));
        AddTab("skills", DesktopResources.Get("Skills"), Skills());
        AddTab("plugins", DesktopResources.Get("PluginsTitle"), Plugins(plugins ?? []));
        AddTab("tools", DesktopResources.Get("Tools"), Tools());
        AddTab("checks", DesktopResources.Get("AgentChecks"), Checks(connected ?? McpTurnSnapshot.Empty));
        BuildProvider();
        tabs.SelectionChanged += (_, args) =>
        {
            if (args.SelectedItem is not NavigationViewItem { Tag: string key }) return;
            if (page.Content is DependencyObject previous)
                foreach (var control in ControlAppearance.Descendants(previous).OfType<Control>())
                {
                    if (control is AutoSuggestBox suggest) suggest.IsSuggestionListOpen = false;
                    if (control is ComboBox combo) combo.IsDropDownOpen = false;
                    if (control is ChatTargetPicker picker) picker.IsSuggestionListOpen = false;
                }
            page.Content = pages[key]; SizePage(); page.ChangeView(0, 0, null, true);
        };
        page.SizeChanged += (_, _) => SizePage();
        tabs.SelectedItem = tabs.MenuItems[0]; page.Content = pages["general"];
        PrimaryButtonClick += Save;
        Opened += (_, _) => { SizeToRoot(); XamlRoot.Changed += RootChanged;
            (pages["general"] as Panel)?.Children.OfType<CommunityToolkit.WinUI.Controls.SettingsCard>().FirstOrDefault()?.StartBringIntoView(); };
        Closed += (_, _) => { lifetime.Cancel(); if (XamlRoot is not null) XamlRoot.Changed -= RootChanged; };
        fields.Watch(UpdateSave); fields.Refresh();
    }
    private void AddTab(string key, string title, FrameworkElement view)
    {
        pages[key] = view; var tab = new NavigationViewItem { Content = title, Tag = key };
        AutomationProperties.SetAutomationId(tab, "AgentTab_" + key); tabs.MenuItems.Add(tab);
    }
    private static StackPanel Panel() => new() { Spacing = 16 };
    private StackPanel Card(StackPanel parent, string key)
    {
        NativeSettingsSurface.Card(parent, "Agent_" + key.Replace('.', '_'), ChatSettingsFields.L(key), out var body); return body;
    }
    private void Message(string text) { feedback.Text = text; feedback.Visibility = Visibility.Visible; }
    private string? ModelType(string id)
    {
        var savedType = DesktopAgent.Text(draft.Models.FirstOrDefault(model => DesktopAgent.Text(model["id"]) == id)?["type"]);
        return savedType.Length > 0 ? savedType : catalogModelTypes.GetValueOrDefault(id);
    }
    private IReadOnlyList<JsonObject> AnswerModels => draft.Models.Where(model => ModelType(DesktopAgent.Text(model["id"])) != "decision").ToArray();
    private IReadOnlyList<JsonObject> DecisionModels => draft.Models.Where(model => ModelType(DesktopAgent.Text(model["id"])) == "decision").ToArray();
    private bool HasValidEditorModels => AnswerModels.Count > 0 && DecisionModels.Count == (AnswerModels.Count > 1 ? 1 : 0);
    private void UpdateSave() => IsPrimaryButtonEnabled = draft.IsValid && HasValidEditorModels && fields.IsValid && providerFields.IsValid && pending == 0 && !saving;
    private async Task WorkAsync(Func<CancellationToken, Task> work)
    {
        pending++; UpdateSave();
        try
        {
            await work(lifetime.Token);
            lifetime.Token.ThrowIfCancellationRequested();
            if (session.AgentPartition != partition) throw new OperationCanceledException();
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { Message(error is GatewayException or InvalidOperationException or InvalidDataException ? error.Message : DesktopResources.Get("CatalogFailed")); }
        finally { pending--; UpdateSave(); }
    }
    private StackPanel General(bool editing, IReadOnlyList<ChatTarget> models)
    {
        var panel = Panel(); var body = Card(panel, "agent.general");
        var name = fields.Text(body, "agent.name", () => draft.Name, v => draft.Definition["name"] = v.Trim()); name.Name = "AgentName"; name.IsEnabled = !editing;
        fields.Text(body, "agent.description", () => draft.Description, v => draft.Definition["description"] = v).Name = "AgentDescription";
        var visibleModels = ProviderPreferences.Filter(models, session.Settings);
        var modelOptions = visibleModels.Where(model => model.ModelType is null or "" or "language")
            .Concat(AnswerModels.Select(configuration => new ChatTarget(DesktopAgent.Text(configuration["id"]), DesktopAgent.Text(configuration["id"]))
                { ModelType = ModelType(DesktopAgent.Text(configuration["id"])) }))
            .Where(model => model.ModelType is null or "" or "language").DistinctBy(model => model.Id).ToArray();
        // Match the workflow decision selector: use decision entries from the complete
        // catalog, not chat-visible rows, and filter by type before deduplicating IDs.
        var decisionOptions = models.Where(DecisionRouting.IsDecisionModel)
            .Concat(DecisionModels.Select(configuration => new ChatTarget(DesktopAgent.Text(configuration["id"]), DesktopAgent.Text(configuration["id"]))
                { ModelType = "decision" })).DistinctBy(model => model.Id).ToArray();
        var modelField = new StackPanel { Spacing = 8 };
        var modelLabel = new TextBlock { Text = DesktopResources.Get("AgentModels") };
        modelField.Children.Add(modelLabel);
        var model = new ChatTargetPicker { Name = "AgentModels", PlaceholderText = DesktopResources.Get("SelectModel"),
            AllowEmptySelection = true, HorizontalAlignment = HorizontalAlignment.Stretch, ItemsSource = modelOptions };
        model.Configure(agents: false, multiselect: true, favoriteIds: new HashSet<string>(), canRoute: false, routeLabel: "");
        model.SetSelection(AnswerModels.Select(configuration => DesktopAgent.Text(configuration["id"])));
        AutomationProperties.SetName(model, DesktopResources.Get("AgentModels"));
        AutomationProperties.SetLabeledBy(model, modelLabel);
        AutomationProperties.SetIsRequiredForForm(model, true);
        modelField.Children.Add(model); body.Children.Add(modelField);
        var decisionField = new StackPanel { Name = "AgentDecisionModelField", Spacing = 8 };
        var decisionLabel = new TextBlock { Text = DesktopResources.Get("AgentDecisionModel") + " *" };
        var decision = new ChatTargetPicker { Name = "AgentDecisionModel", PlaceholderText = DesktopResources.Get("SelectModel"),
            AllowEmptySelection = true, HorizontalAlignment = HorizontalAlignment.Stretch, ItemsSource = decisionOptions };
        decision.Configure(agents: false, multiselect: false, favoriteIds: new HashSet<string>(), canRoute: false, routeLabel: "");
        AutomationProperties.SetName(decision, DesktopResources.Get("AgentDecisionModel"));
        AutomationProperties.SetLabeledBy(decision, decisionLabel);
        AutomationProperties.SetIsRequiredForForm(decision, true);
        decisionField.Children.Add(decisionLabel); decisionField.Children.Add(decision); body.Children.Add(decisionField);
        void RefreshDecision()
        {
            var answerIds = AnswerModels.Select(configuration => DesktopAgent.Text(configuration["id"])).ToHashSet();
            decision.ItemsSource = decisionOptions.Where(option => !answerIds.Contains(option.Id)).ToArray();
            decision.SetSelection(DecisionModels.Select(configuration => DesktopAgent.Text(configuration["id"])));
            decisionField.Visibility = AnswerModels.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        }
        RefreshDecision();
        model.SelectionChanged += () =>
        {
            CommitProvider();
            var answers = model.Selection.Ids.ToArray();
            var decisions = answers.Length > 1 ? DecisionModels.Select(configuration => DesktopAgent.Text(configuration["id"]))
                .Where(id => !answers.Contains(id)).Select(id => (id, (string?)"decision")).ToArray() : [];
            draft.SelectModels(answers.Select(id => (id, modelOptions.FirstOrDefault(option => option.Id == id)?.ModelType ?? "language")).Concat(decisions));
            RefreshDecision(); BuildProvider(); fields.Refresh();
        };
        decision.SelectionChanged += () =>
        {
            CommitProvider();
            draft.SelectModels(AnswerModels.Select(configuration => DesktopAgent.Text(configuration["id"]))
                .Select(id => (id, ModelType(id))).Concat(decision.Selection.Ids.Select(id => (id, (string?)"decision"))));
            RefreshDecision(); BuildProvider(); fields.Refresh();
        };
        fields.Text(body, "agent.instructions", () => DesktopAgent.Text(draft.Definition["instructions"]), v => draft.Definition["instructions"] = v, multiline: true).Name = "AgentInstructions";
        fields.Text(body, "agent.argumentHint", () => DesktopAgent.Text(draft.Definition["argumentHint"]), v => draft.Definition["argumentHint"] = v).Name = "AgentArgumentHint";
        return panel;
    }
    private void BuildProvider()
    {
        var wasSelected = (tabs.SelectedItem as NavigationViewItem)?.Tag as string == "providers";
        var old = tabs.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => i.Tag as string == "providers");
        if (old is not null) tabs.MenuItems.Remove(old);
        settingsModel = draft.Models.FirstOrDefault(model => DesktopAgent.Text(model["id"]) == settingsModelId) ?? draft.Models.FirstOrDefault();
        settingsModelId = DesktopAgent.Text(settingsModel?["id"]);
        var provider = DesktopAgent.Provider(settingsModelId); var panel = Panel();
        providerFields = new();
        openAI = null; providerPreferences = null; providerBaseline = null;
        var picker = new ComboBox { Name = "AgentSettingsModel", Header = DesktopResources.Get("AgentModels"), HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var configuration in draft.Models) picker.Items.Add(DesktopAgent.Text(configuration["id"]));
        picker.SelectedItem = settingsModelId; ControlAppearance.Stock(picker); panel.Children.Add(picker);
        picker.SelectionChanged += (_, _) =>
        {
            if (picker.SelectedItem is not string id || id == settingsModelId) return;
            if (!providerFields.IsValid || openAI?.IsValid == false) { picker.SelectedItem = settingsModelId; return; }
            CommitProvider(); settingsModelId = id; BuildProvider();
        };
        if (provider.Equals("openai", StringComparison.OrdinalIgnoreCase) && DesktopAgent.Text(settingsModel?["type"]) != "decision")
        {
            providerPreferences = new();
            providerPreferences.ProviderMetadata["openai"] = settingsModel?["providerMetadata"] as JsonObject ?? new();
            if (settingsModel?["providerHeaders"] is JsonObject headers)
                providerPreferences.ProviderHeaders["openai"] = headers.Where(p => p.Value is JsonValue).ToDictionary(p => p.Key, p => DesktopAgent.Text(p.Value), StringComparer.OrdinalIgnoreCase);
            openAI = new(providerPreferences); openAI.Commit(providerPreferences);
            providerBaseline = (JsonObject)providerPreferences.ProviderMetadata["openai"].DeepClone();
            providerBaseline["__headers"] = JsonSerializer.SerializeToNode(providerPreferences.ProviderHeaders.GetValueOrDefault("openai") ?? []);
            panel.Children.Add(openAI.View);
        }
        else if (settingsModel is { } configuration)
        {
            providerFields.Json(panel, "providerMetadata", () => configuration["providerMetadata"], node => configuration["providerMetadata"] = node).Header = DesktopResources.Get("AgentProviderMetadata");
            providerFields.Json(panel, "providerHeaders", () => configuration["providerHeaders"], node => configuration["providerHeaders"] = node).Header = DesktopResources.Get("AgentProviderHeaders");
        }
        AddTab("providers", provider.Equals("openai", StringComparison.OrdinalIgnoreCase) ? "OpenAI" : provider.Length > 0 ? provider : DesktopResources.Get("AgentProvider"), panel);
        providerFields.Watch(UpdateSave);
        if (wasSelected) { tabs.SelectedItem = tabs.MenuItems.OfType<NavigationViewItem>().First(i => i.Tag as string == "providers"); page.Content = panel; }
    }
    private void CommitProvider()
    {
        if (openAI is null || providerPreferences is null || providerBaseline is null) return;
        openAI.Commit(providerPreferences);
        var after = (JsonObject)providerPreferences.ProviderMetadata["openai"].DeepClone();
        after["__headers"] = JsonSerializer.SerializeToNode(providerPreferences.ProviderHeaders.GetValueOrDefault("openai") ?? []);
        var model = settingsModel;
        if (model is null) return;
        var target = model["providerMetadata"] as JsonObject ?? new();
        var before = (JsonObject)providerBaseline.DeepClone(); var oldHeaders = before["__headers"]?.DeepClone(); var newHeaders = after["__headers"]?.DeepClone();
        before.Remove("__headers"); after.Remove("__headers");
        if (!JsonNode.DeepEquals(before, after)) { DesktopAgent.ApplyChanges(target, before, after); model["providerMetadata"] = target; }
        if (!JsonNode.DeepEquals(oldHeaders, newHeaders))
        {
            if (newHeaders is JsonObject { Count: > 0 }) model["providerHeaders"] = newHeaders; else model.Remove("providerHeaders");
        }
    }
    private async void Save(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true;
        if (!HasValidEditorModels)
        {
            Message(DesktopResources.Get(AnswerModels.Count > 1 ? "AgentDecisionModelRequired" : "AgentFieldsRequired"));
            tabs.SelectedItem = tabs.MenuItems.OfType<NavigationViewItem>().First(i => i.Tag as string == "general");
            return;
        }
        if (pending > 0 || saving || !fields.IsValid || !providerFields.IsValid || openAI?.IsValid == false)
        { Message(DesktopResources.Get("ChatSettingsInvalid")); if (openAI?.IsValid == false) { tabs.SelectedItem = tabs.MenuItems.OfType<NavigationViewItem>().First(i => i.Tag as string == "providers"); openAI.FocusInvalid(); } else fields.FirstInvalid?.Focus(FocusState.Programmatic); return; }
        var deferral = args.GetDeferral(); saving = true; UpdateSave();
        try
        {
            CommitProvider();
            foreach (var model in DecisionModels) model["type"] = "decision";
            draft.SyncLegacyModel(); draft.Validate(); lifetime.Token.ThrowIfCancellationRequested();
            if (partition != session.AgentPartition) throw new OperationCanceledException();
            if (SaveAsync is not null) await SaveAsync(draft.Clone(), lifetime.Token);
            Result = draft.Clone(); args.Cancel = false;
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { Message(error is InvalidOperationException or InvalidDataException ? error.Message : DesktopResources.Get("ChatSettingsSaveFailed")); }
        finally { saving = false; UpdateSave(); deferral.Complete(); }
    }
    private void RootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => SizeToRoot();
    void IResponsiveDialog.SizeToRoot() => SizeToRoot();
    private void SizeToRoot()
    {
        layout.Width = Math.Max(0, Math.Min(820, XamlRoot.Size.Width - 96));
        layout.Height = Math.Max(0, Math.Min(680, XamlRoot.Size.Height - 240));
        SizePage();
    }
    private void SizePage()
    {
        // ContentDialog chrome and the vertical scrollbar own their space. Never measure
        // settings rows against a requested width larger than their actual visible viewport.
        if (page.Content is FrameworkElement content && page.ActualWidth > 0)
        {
            content.Width = Math.Max(0, page.ActualWidth - 16);
            content.HorizontalAlignment = HorizontalAlignment.Left;
        }
    }
}
