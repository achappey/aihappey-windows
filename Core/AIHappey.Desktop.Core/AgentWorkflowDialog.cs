using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AIHappey.Desktop.Core;

public sealed class AgentWorkflowDialog : ContentDialog, IResponsiveDialog
{
    private readonly AgentWorkflowPreferences draft;
    private readonly List<ChatTarget> ordered;
    private readonly Grid layout = new() { Name = "AgentSettingsLayout", RowSpacing = 16 };
    private readonly StackPanel details = new() { Spacing = 12 };
    private readonly TextBlock error = new() { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    public ChatPreferences ToolPreferences { get; }
    public Func<AgentWorkflowPreferences, IReadOnlyList<string>, Task>? SaveAsync { get; set; }
    public AgentWorkflowDialog(AgentWorkflowPreferences preferences, IReadOnlyList<ChatTarget> selected, IReadOnlyList<ChatTarget> models,
        ChatPreferences? toolPreferences = null)
    {
        draft = preferences.Clone(); ordered = selected.ToList();
        ToolPreferences = (toolPreferences ?? new()).Clone();
        Name = "AgentWorkflowDialog"; Title = DesktopResources.Get("AgentSettings");
        PrimaryButtonText = DesktopResources.Get("Save"); CloseButtonText = DesktopResources.Get("Cancel");
        DefaultButton = ContentDialogButton.Primary;
        Resources["ContentDialogMaxWidth"] = 920d; Resources["ContentDialogMinWidth"] = 0d;
        var body = new StackPanel { Spacing = 16 };
        var type = new ComboBox { Name = "WorkflowType", Header = DesktopResources.Get("AgentWorkflow"), HorizontalAlignment = HorizontalAlignment.Stretch,
            DisplayMemberPath = nameof(WorkflowChoice.Label), ItemsSource = AgentWorkflowPreferences.Types.Select(key => new WorkflowChoice(key, DesktopResources.Get("Workflow_" + key))).ToArray(),
            IsEnabled = selected.Count > 1 };
        type.SelectedIndex = Math.Max(0, AgentWorkflowPreferences.Types.ToList().IndexOf(draft.Type));
        type.SelectionChanged += (_, _) => { if (type.SelectedItem is WorkflowChoice choice) { draft.Type = choice.Key; Render(models); } };
        ControlAppearance.Stock(type); body.Children.Add(type); body.Children.Add(details); body.Children.Add(error);
        var page = new ScrollViewer { Name = "AgentSettingsPage", Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Disabled, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        var tools = new LocalToolsSettingsView(ToolPreferences);
        var tabs = new NavigationView { Name = "AgentSettingsTabs", PaneDisplayMode = NavigationViewPaneDisplayMode.Top,
            IsSettingsVisible = false, IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed,
            IsPaneToggleButtonVisible = false, AlwaysShowHeader = false, Height = 56 };
        var generalTab = new NavigationViewItem { Name = "AgentGeneralTab", Content = DesktopResources.Get("General"), Tag = body };
        var toolsTab = new NavigationViewItem { Name = "AgentToolsTab", Content = DesktopResources.Get("Tools"), Tag = tools };
        foreach (var item in new[] { generalTab, toolsTab })
        {
            AutomationProperties.SetAutomationId(item, item.Name); AutomationProperties.SetName(item, (string)item.Content); tabs.MenuItems.Add(item);
        }
        tabs.SelectionChanged += (_, _) =>
        {
            if (tabs.SelectedItem is not NavigationViewItem { Tag: FrameworkElement view }) return;
            page.Content = view; page.ChangeView(null, 0, null, true);
        };
        tabs.SelectedItem = generalTab;
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto }); layout.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        layout.Children.Add(tabs); Grid.SetRow(page, 1); layout.Children.Add(page); Content = layout;
        AutomationProperties.SetLiveSetting(error, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Assertive);
        Opened += (_, _) => { SizeToRoot(); XamlRoot.Changed += RootChanged; };
        Closed += (_, _) => XamlRoot.Changed -= RootChanged;
        Render(models);
        PrimaryButtonClick += async (_, args) =>
        {
            var deferral = args.GetDeferral(); IsPrimaryButtonEnabled = false;
            try
            {
                if (ordered.Count > 1) draft.Validate(models);
                var validated = DesktopAgentRequests.Build(ordered.Select(t => t.Id), ordered,
                    ordered.Where(t => t.LocalAgentName is not null && t.ModelMetadata.HasValue)
                        .Select(t => new DesktopAgent(JsonNode.Parse(t.ModelMetadata!.Value.GetRawText())!.AsObject())).ToArray(), draft, requireRemoteDefinitions: false);
                if (SaveAsync is not null) await SaveAsync(draft.Clone(), ordered.Select(t => t.Id).ToArray());
            }
            catch (Exception exception)
            {
                args.Cancel = true; error.Text = exception.Message; error.Visibility = Visibility.Visible; tabs.SelectedItem = generalTab;
                layout.UpdateLayout(); error.StartBringIntoView();
            }
            finally { IsPrimaryButtonEnabled = true; deferral.Complete(); }
        };
    }
    private void Render(IReadOnlyList<ChatTarget> models)
    {
        details.Children.Clear(); error.Visibility = Visibility.Collapsed;
        if (draft.Type == "decisions")
        {
            details.Children.Add(new TextBlock { Text = DesktopResources.Get("WorkflowDecisionModel") });
            var model = new ChatTargetPicker { Name = "WorkflowDecisionModel", PlaceholderText = DesktopResources.Get("SelectModel"), HorizontalAlignment = HorizontalAlignment.Stretch };
            model.Configure(false, false, new HashSet<string>(), false, "");
            model.ItemsSource = models.Where(DecisionRouting.IsDecisionModel).ToArray(); model.Text = draft.DecisionModel ?? "";
            model.SelectionChanged += () => draft.DecisionModel = model.Text;
            details.Children.Add(model);
        }
        if (draft.Type == "sequential")
            for (var i = 0; i < ordered.Count; i++)
            {
                var index = i; var row = new Grid { ColumnSpacing = 8 };
                row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
                row.Children.Add(new TextBlock { Text = $"{i + 1}. {ordered[i].Label}", TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
                foreach (var direction in new[] { -1, 1 })
                {
                    var button = new Button { Content = direction == -1 ? "↑" : "↓", IsEnabled = i + direction >= 0 && i + direction < ordered.Count };
                    ControlAppearance.Stock(button); AutomationProperties.SetName(button, DesktopResources.Get(direction == -1 ? "WorkflowMoveUp" : "WorkflowMoveDown"));
                    button.Click += (_, _) => { (ordered[index], ordered[index + direction]) = (ordered[index + direction], ordered[index]); Render(models); };
                    Grid.SetColumn(button, direction == -1 ? 1 : 2); row.Children.Add(button);
                }
                details.Children.Add(row);
            }
        if (draft.Type == "groupchat") Number("WorkflowIterations", draft.MaximumIterationCount, value => draft.MaximumIterationCount = value ?? 5, 1, 20);
        if (draft.Type == "handoff") RenderHandoffs(models);
        if (draft.Type == "magentic")
        {
            var manager = AgentPicker("WorkflowManager"); manager.SelectedItem = ordered.FirstOrDefault(t => t.Id == draft.ManagerAgentKey) ?? ordered.FirstOrDefault();
            draft.ManagerAgentKey = (manager.SelectedItem as ChatTarget)?.Id;
            manager.SelectionChanged += (_, _) => draft.ManagerAgentKey = (manager.SelectedItem as ChatTarget)?.Id;
            details.Children.Add(manager);
            Number("WorkflowMaxRounds", draft.MaxRounds, value => draft.MaxRounds = value);
            Number("WorkflowMaxResets", draft.MaxResets, value => draft.MaxResets = value);
            Number("WorkflowMaxStalls", draft.MaxStalls, value => draft.MaxStalls = value);
            var language = new TextBox { Header = DesktopResources.Get("WorkflowResponseLanguage"), Text = draft.ResponseLanguage ?? "" };
            language.TextChanged += (_, _) => draft.ResponseLanguage = language.Text; ControlAppearance.Stock(language); details.Children.Add(language);
        }
    }
    private void Number(string key, int? value, Action<int?> changed, int minimum = 0, int maximum = int.MaxValue)
    {
        var number = new NumberBox { Header = DesktopResources.Get(key), Value = value ?? double.NaN, Minimum = minimum, Maximum = maximum,
            SmallChange = 1, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline };
        number.ValueChanged += (_, args) => changed(double.IsNaN(args.NewValue) ? null : checked((int)Math.Truncate(args.NewValue)));
        ControlAppearance.Stock(number); details.Children.Add(number);
    }
    private ComboBox AgentPicker(string key)
    {
        var picker = new ComboBox { Header = DesktopResources.Get(key), ItemsSource = ordered, DisplayMemberPath = nameof(ChatTarget.Label),
            HorizontalAlignment = HorizontalAlignment.Stretch };
        ControlAppearance.Stock(picker); return picker;
    }
    private static string BackendId(ChatTarget target) => target.LocalAgentName ?? target.RemoteAgentId ?? target.Id;
    private void RenderHandoffs(IReadOnlyList<ChatTarget> models)
    {
        foreach (var handoff in draft.Handoffs.ToArray())
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(new TextBlock { Text = handoff?.ToJsonString() ?? "", TextWrapping = TextWrapping.Wrap, MaxWidth = 380 });
            var remove = new Button { Content = DesktopResources.Get("Delete") }; ControlAppearance.Stock(remove);
            remove.Click += (_, _) => { draft.Handoffs.Remove(handoff); Render(models); }; row.Children.Add(remove); details.Children.Add(row);
        }
        var from = AgentPicker("WorkflowHandoffFrom"); var to = AgentPicker("WorkflowHandoffTo");
        details.Children.Add(from); details.Children.Add(to);
        var add = new Button { Content = DesktopResources.Get("Add"), IsEnabled = ordered.Count > 1 }; ControlAppearance.Stock(add);
        add.Click += (_, _) =>
        {
            if (from.SelectedItem is not ChatTarget source || to.SelectedItem is not ChatTarget destination || source.Id == destination.Id) return;
            draft.Handoffs.Add(new JsonArray(JsonValue.Create(BackendId(source)), new JsonArray(JsonValue.Create(BackendId(destination))))); Render(models);
        };
        details.Children.Add(add);
    }
    private sealed record WorkflowChoice(string Key, string Label);
    private void RootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => SizeToRoot();
    protected override void OnApplyTemplate()
    {
        if (XamlRoot is not null) SizeToRoot();
        base.OnApplyTemplate();
    }
    void IResponsiveDialog.SizeToRoot() => SizeToRoot();
    private void SizeToRoot()
    {
        layout.Width = Math.Max(0, Math.Min(820, XamlRoot.Size.Width - 96));
        layout.MaxHeight = Math.Max(0, Math.Min(680, XamlRoot.Size.Height - 240));
    }
}
