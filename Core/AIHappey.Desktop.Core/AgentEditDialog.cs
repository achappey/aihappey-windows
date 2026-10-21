using FluentIcons.Common;
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
        IReadOnlyList<DesktopSkill>? localSkills = null, Func<DesktopSkill, CancellationToken, Task<byte[]>>? localSkillArchive = null, IReadOnlyList<DesktopAgentPlugin>? plugins = null)
    {
        draft = agent.Clone(); this.session = session; this.catalog = catalog; this.registryHttp = registryHttp;
        this.localSkills = localSkills ?? []; this.localSkillArchive = localSkillArchive;
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
    private void UpdateSave() => IsPrimaryButtonEnabled = draft.IsValid && fields.IsValid && providerFields.IsValid && pending == 0 && !saving;
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
        var modelOptions = models.Where(m => m.ModelType is null or "language" or "decision")
            .Concat(draft.Models.Where(model => !models.Any(m => m.Id == DesktopAgent.Text(model["id"])))
                .Select(model => new ChatTarget(DesktopAgent.Text(model["id"]), DesktopAgent.Text(model["id"])) { ModelType = DesktopAgent.Text(model["type"]) }))
            .DistinctBy(model => model.Id).Select(model => model.ModelType == "decision"
                ? model with { Label = model.Label + " (" + DesktopResources.Get("AgentDecisionRouter") + ")" } : model).ToArray();
        var choices = new ListView { Name = "AgentModels", SelectionMode = ListViewSelectionMode.Multiple,
            IsMultiSelectCheckBoxEnabled = true, Height = 200, ItemsSource = modelOptions,
            HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(choices, DesktopResources.Get("AgentModels"));
        var updatingModels = false;
        void RefreshChoices(string query = "")
        {
            updatingModels = true;
            choices.ItemsSource = modelOptions.Where(m => draft.ModelIds.Contains(m.Id)
                || (m.Id + " " + m.Label).Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
            foreach (var option in choices.Items.OfType<ChatTarget>())
                if (draft.ModelIds.Contains(option.Id)) choices.SelectedItems.Add(option);
            updatingModels = false;
        }
        RefreshChoices();
        void SelectModels(IEnumerable<ChatTarget> next)
        {
            CommitProvider();
            draft.SelectModels(next.Select(item => (item.Id, item.ModelType)));
            BuildProvider(); fields.Refresh();
        }
        choices.SelectionChanged += (_, args) =>
        {
            if (updatingModels) return;
            var removed = args.RemovedItems.OfType<ChatTarget>().Select(item => item.Id).ToHashSet();
            var next = draft.Models.Where(item => !removed.Contains(DesktopAgent.Text(item["id"])))
                .Select(item => modelOptions.First(m => m.Id == DesktopAgent.Text(item["id"])))
                .Concat(args.AddedItems.OfType<ChatTarget>()).DistinctBy(item => item.Id);
            SelectModels(next.ToArray());
        };
        var model = new AutoSuggestBox { Name = "AgentModel", Header = ChatSettingsFields.L("model"), Text = draft.ModelId,
            ItemsSource = modelOptions.Take(100).ToArray(), DisplayMemberPath = "Label", TextMemberPath = "Id", PlaceholderText = DesktopResources.Get("SelectModel"),
            QueryIcon = DesktopIcons.Create(Icon.ChevronDown, 12), HorizontalAlignment = HorizontalAlignment.Stretch };
        ControlAppearance.Stock(model); AutomationProperties.SetName(model, ChatSettingsFields.L("model")); body.Children.Add(model);
        void SetModel(string id)
        {
            if (draft.ModelIds.Contains(id) || modelOptions.FirstOrDefault(item => item.Id == id) is not { } selected) return;
            SelectModels(draft.Models.Select(item => modelOptions.First(m => m.Id == DesktopAgent.Text(item["id"]))).Append(selected).ToArray());
            RefreshChoices();
        }
        void Suggestions(string query = "") => model.ItemsSource = modelOptions
            .Where(m => (m.Id + " " + m.Label).Contains(query, StringComparison.OrdinalIgnoreCase)).Take(100).ToArray();
        model.GotFocus += (_, _) => { Suggestions(); model.IsSuggestionListOpen = true; };
        model.TextChanged += (_, args) =>
        {
            // A selected suggestion must commit immediately, not depend on a later focus
            // transition inside AutoSuggestBox's textbox/query-button template.
            if (modelOptions.Any(m => m.Id == model.Text.Trim())) SetModel(model.Text.Trim());
            if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
            { Suggestions(model.Text); RefreshChoices(model.Text); model.IsSuggestionListOpen = true; }
        };
        model.SuggestionChosen += (_, args) => { if (args.SelectedItem is ChatTarget selected) { model.Text = selected.Id; SetModel(selected.Id); } };
        model.QuerySubmitted += (_, args) =>
        {
            if (args.ChosenSuggestion is ChatTarget selected) { model.Text = selected.Id; SetModel(selected.Id); }
            else { Suggestions(); model.IsSuggestionListOpen = true; }
        };
        model.LostFocus += (_, _) => { model.IsSuggestionListOpen = false; SetModel(model.Text.Trim()); };
        body.Children.Add(choices);
        body.Children.Add(new TextBlock { Text = DesktopResources.Get("AgentModelRoutingHint"), TextWrapping = TextWrapping.Wrap });
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
        if (pending > 0 || saving || !fields.IsValid || !providerFields.IsValid || openAI?.IsValid == false)
        { Message(DesktopResources.Get("ChatSettingsInvalid")); if (openAI?.IsValid == false) { tabs.SelectedItem = tabs.MenuItems.OfType<NavigationViewItem>().First(i => i.Tag as string == "providers"); openAI.FocusInvalid(); } else fields.FirstInvalid?.Focus(FocusState.Programmatic); return; }
        var deferral = args.GetDeferral(); saving = true; UpdateSave();
        try
        {
            CommitProvider(); draft.SyncLegacyModel(); draft.Validate(); lifetime.Token.ThrowIfCancellationRequested();
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
