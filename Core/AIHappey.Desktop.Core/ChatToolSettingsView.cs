using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AIHappey.Desktop.Core;

/// <summary>Model-chat-only controls. The owning dialog supplies an isolated preferences draft.</summary>
public sealed class ChatToolSettingsView : StackPanel
{
    private readonly ChatPreferences draft;
    private readonly Func<ChatPreferences, IReadOnlyList<ChatToolOption>> availableTools;
    private readonly ComboBox choice = new() { Name = "ChatToolChoice", MinWidth = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox limit = new() { Name = "ChatMaxToolCalls", MinWidth = 0 };
    private readonly ListView stops = new() { Name = "ChatStopTools", SelectionMode = ListViewSelectionMode.Multiple,
        MaxHeight = 220, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock empty = new() { TextWrapping = TextWrapping.Wrap };
    private readonly SettingsExpander toolsSection;
    private bool syncing;
    public bool IsValid => ChatPreferences.TryTokenLimit(limit.Text, out _);
    public void FocusInvalid()
    {
        toolsSection.IsExpanded = true; UpdateLayout();
        limit.Focus(FocusState.Programmatic); limit.StartBringIntoView();
        DispatcherQueue.TryEnqueue(() => { if (limit.IsLoaded) { limit.Focus(FocusState.Programmatic); limit.StartBringIntoView(); } });
    }

    public ChatToolSettingsView(ChatPreferences draft, Func<ChatPreferences, IReadOnlyList<ChatToolOption>> availableTools)
    {
        this.draft = draft; this.availableTools = availableTools;
        Name = "ChatToolSettingsView"; Spacing = 16; draft.NormalizeTools();
        toolsSection = NativeSettingsSurface.Expander(this, "GeneralToolsCard", DesktopResources.Get("Tools"), null, out var tools);
        var options = new Grid { Name = "ChatToolOptions", ColumnSpacing = 16 };
        options.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        options.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        tools.Children.Add(options);
        choice.Header = ChatSettingsFields.L("toolChoice"); ControlAppearance.Stock(choice);
        AutomationProperties.SetName(choice, ChatSettingsFields.L("toolChoice")); options.Children.Add(choice);
        limit.Header = ChatSettingsFields.L("maxToolCalls"); limit.PlaceholderText = DesktopResources.Get("ChatOptional");
        limit.Text = draft.MaxToolCalls?.ToString() ?? ""; ControlAppearance.Stock(limit);
        AutomationProperties.SetName(limit, ChatSettingsFields.L("maxToolCalls")); Grid.SetColumn(limit, 1); options.Children.Add(limit);
        var error = new TextBlock { Text = DesktopResources.Get("ChatTokensInvalid"), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        AutomationProperties.SetLiveSetting(error, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite); tools.Children.Add(error);
        limit.TextChanged += (_, _) =>
        {
            var valid = ChatPreferences.TryTokenLimit(limit.Text, out var value);
            error.Visibility = valid ? Visibility.Collapsed : Visibility.Visible;
            if (valid) { draft.MaxToolCalls = value; draft.NormalizeTools(); RefreshChoice(); }
        };
        choice.SelectionChanged += (_, _) =>
        {
            if (!syncing && choice.SelectedItem is ComboBoxItem item)
            { draft.ToolChoice = (string)item.Tag == "auto" ? null : (string)item.Tag; draft.NormalizeTools(); }
        };
        tools.Children.Add(new TextBlock { Text = ChatSettingsFields.L("stopTools"), TextWrapping = TextWrapping.Wrap });
        AutomationProperties.SetName(stops, ChatSettingsFields.L("stopTools"));
        ScrollViewer.SetHorizontalScrollBarVisibility(stops, ScrollBarVisibility.Disabled);
        ScrollViewer.SetHorizontalScrollMode(stops, ScrollMode.Disabled);
        tools.Children.Add(stops); empty.Text = DesktopResources.Get("ChatNoAvailableTools"); tools.Children.Add(empty);
        stops.SelectionChanged += (_, _) =>
        {
            if (syncing) return;
            draft.StopTools = stops.SelectedItems.OfType<ListViewItem>().Select(item => (string)item.Tag).ToList();
            draft.NormalizeTools(); RefreshChoice();
        };
        NativeSettingsSurface.Expander(this, "GeneralMcpPolicyCard", ChatSettingsFields.L("mcpPage.policy"), null, out var policy);
        var fields = new ChatSettingsFields();
        fields.Switch(policy, "annotations.openWorld", () => draft.ToolAnnotations.OpenWorldHint,
            value => { draft.ToolAnnotations.OpenWorldHint = value; RefreshAvailableTools(); }).Name = "ChatPolicyOpenWorld";
        fields.Switch(policy, "annotations.destructive", () => draft.ToolAnnotations.DestructiveHint,
            value => { draft.ToolAnnotations.DestructiveHint = value; RefreshAvailableTools(); }).Name = "ChatPolicyDestructive";
        fields.Switch(policy, "annotations.readOnly", () => draft.ToolAnnotations.ReadOnlyHint,
            value => { draft.ToolAnnotations.ReadOnlyHint = value; RefreshAvailableTools(); }).Name = "ChatPolicyReadOnly";
        fields.Switch(policy, "annotations.idempotent", () => draft.ToolAnnotations.IdempotentHint,
            value => { draft.ToolAnnotations.IdempotentHint = value; RefreshAvailableTools(); }).Name = "ChatPolicyIdempotent";
        RefreshChoice(); RefreshAvailableTools();
    }

    private void RefreshChoice()
    {
        syncing = true;
        try
        {
            choice.Items.Clear();
            foreach (var value in draft.CanRequireTools ? new[] { "none", "auto", "required" } : new[] { "none", "auto" })
                choice.Items.Add(new ComboBoxItem { Tag = value, Content = ChatSettingsFields.L(value) });
            choice.SelectedItem = choice.Items.OfType<ComboBoxItem>().First(item => (string)item.Tag == (draft.NormalizedToolChoice ?? "auto"));
        }
        finally { syncing = false; }
    }

    public void RefreshAvailableTools()
    {
        var options = availableTools(draft).DistinctBy(option => option.Name).ToArray();
        var names = options.Select(option => option.Name).ToHashSet(StringComparer.Ordinal);
        var items = options.Concat(draft.StopTools.Where(name => !names.Contains(name))
            .Select(name => new ChatToolOption(name, name + " — " + DesktopResources.Get("ChatToolUnavailable")))).ToArray();
        syncing = true;
        try
        {
            stops.Items.Clear();
            foreach (var option in items)
            {
                var item = new ListViewItem { Tag = option.Name, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Content = new TextBlock { Text = option.Label, TextWrapping = TextWrapping.Wrap } };
                AutomationProperties.SetName(item, option.Label); stops.Items.Add(item);
                if (draft.StopTools.Contains(option.Name, StringComparer.Ordinal)) stops.SelectedItems.Add(item);
            }
            stops.Visibility = items.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            empty.Visibility = items.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        finally { syncing = false; }
    }
}
