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
    private int pending;
    private bool saving;
    public DesktopAgent? Result { get; private set; }
    public Func<DesktopAgent, CancellationToken, Task>? SaveAsync { get; set; }
    public DesktopAgent Draft => draft.Clone();

    public AgentEditDialog(DesktopAgent agent, bool isEditing, DesktopSession session, DesktopCatalogClient catalog,
        HttpClient registryHttp, IReadOnlyList<ChatTarget> models, McpTurnSnapshot? connected = null,
        IReadOnlyList<DesktopSkill>? localSkills = null, Func<DesktopSkill, CancellationToken, Task<byte[]>>? localSkillArchive = null)
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
    private void UpdateSave() => IsPrimaryButtonEnabled = draft.IsValid && fields.IsValid && pending == 0 && !saving;
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
        var modelOptions = models.Where(m => m.ModelType is null or "language").ToArray();
        var model = new AutoSuggestBox { Name = "AgentModel", Header = ChatSettingsFields.L("model"), Text = draft.ModelId,
            ItemsSource = modelOptions.Take(100).ToArray(), DisplayMemberPath = "Label", TextMemberPath = "Id", PlaceholderText = DesktopResources.Get("SelectModel"),
            QueryIcon = DesktopIcons.Create(Icon.ChevronDown, 12), HorizontalAlignment = HorizontalAlignment.Stretch };
        ControlAppearance.Stock(model); AutomationProperties.SetName(model, ChatSettingsFields.L("model")); body.Children.Add(model);
        void SetModel(string id)
        {
            if (id == draft.ModelId) return;
            CommitProvider(); var previous = DesktopAgent.Provider(draft.ModelId); draft.ChangeModel(id);
            if (previous != DesktopAgent.Provider(id)) BuildProvider(); fields.Refresh();
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
            { Suggestions(model.Text); model.IsSuggestionListOpen = true; }
        };
        model.SuggestionChosen += (_, args) => { if (args.SelectedItem is ChatTarget selected) { model.Text = selected.Id; SetModel(selected.Id); } };
        model.QuerySubmitted += (_, args) =>
        {
            if (args.ChosenSuggestion is ChatTarget selected) { model.Text = selected.Id; SetModel(selected.Id); }
            else { Suggestions(); model.IsSuggestionListOpen = true; }
        };
        model.LostFocus += (_, _) => { model.IsSuggestionListOpen = false; SetModel(model.Text.Trim()); };
        fields.Text(body, "agent.instructions", () => DesktopAgent.Text(draft.Definition["instructions"]), v => draft.Definition["instructions"] = v, multiline: true).Name = "AgentInstructions";
        fields.Text(body, "agent.argumentHint", () => DesktopAgent.Text(draft.Definition["argumentHint"]), v => draft.Definition["argumentHint"] = v).Name = "AgentArgumentHint";
        return panel;
    }
    private void BuildProvider()
    {
        var old = tabs.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => i.Tag as string == "providers");
        if (old is not null) tabs.MenuItems.Remove(old);
        var provider = DesktopAgent.Provider(draft.ModelId); var panel = Panel();
        openAI = null; providerPreferences = null; providerBaseline = null;
        if (provider.Equals("openai", StringComparison.OrdinalIgnoreCase))
        {
            providerPreferences = new();
            providerPreferences.ProviderMetadata["openai"] = draft.Definition["model"]?["providerMetadata"] as JsonObject ?? new();
            if (draft.Definition["model"]?["providerHeaders"] is JsonObject headers)
                providerPreferences.ProviderHeaders["openai"] = headers.Where(p => p.Value is JsonValue).ToDictionary(p => p.Key, p => DesktopAgent.Text(p.Value), StringComparer.OrdinalIgnoreCase);
            openAI = new(providerPreferences); openAI.Commit(providerPreferences);
            providerBaseline = (JsonObject)providerPreferences.ProviderMetadata["openai"].DeepClone();
            providerBaseline["__headers"] = JsonSerializer.SerializeToNode(providerPreferences.ProviderHeaders.GetValueOrDefault("openai") ?? []);
            panel.Children.Add(openAI.View);
        }
        else panel.Children.Add(new TextBlock { Text = DesktopResources.Get("AgentProviderDeferred"), TextWrapping = TextWrapping.Wrap });
        AddTab("providers", provider.Equals("openai", StringComparison.OrdinalIgnoreCase) ? "OpenAI" : provider.Length > 0 ? provider : DesktopResources.Get("AgentProvider"), panel);
    }
    private void CommitProvider()
    {
        if (openAI is null || providerPreferences is null || providerBaseline is null) return;
        openAI.Commit(providerPreferences);
        var after = (JsonObject)providerPreferences.ProviderMetadata["openai"].DeepClone();
        after["__headers"] = JsonSerializer.SerializeToNode(providerPreferences.ProviderHeaders.GetValueOrDefault("openai") ?? []);
        var model = DesktopAgent.Object(draft.Definition, "model");
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
        if (pending > 0 || saving || !fields.IsValid || openAI?.IsValid == false)
        { Message(DesktopResources.Get("ChatSettingsInvalid")); if (openAI?.IsValid == false) { tabs.SelectedItem = tabs.MenuItems.OfType<NavigationViewItem>().First(i => i.Tag as string == "providers"); openAI.FocusInvalid(); } else fields.FirstInvalid?.Focus(FocusState.Programmatic); return; }
        var deferral = args.GetDeferral(); saving = true; UpdateSave();
        try
        {
            CommitProvider(); draft.Validate(); lifetime.Token.ThrowIfCancellationRequested();
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
