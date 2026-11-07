using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AIHappey.Desktop.Core;

/// <summary>Native, request-only editor. Catalog refreshes never construct executable tool handlers.</summary>
public sealed class ChatToolConfigurationView : StackPanel
{
    private sealed record Row(ChatToolOption? Tool, DropDownButton Callers, ToggleSwitch Search,
        IReadOnlyList<ToggleMenuFlyoutItem> Items, FrameworkElement View);
    private readonly ChatPreferences draft;
    private readonly Func<ChatPreferences, IReadOnlyList<ChatToolOption>> availableTools;
    private readonly ToggleSwitch namespaces = new() { Name = "ChatUseToolNamespaces", OnContent = "", OffContent = "" };
    private readonly StackPanel rows = new() { Spacing = 8 };
    private readonly List<Row> controls = [];
    private readonly TextBlock empty = new() { TextWrapping = TextWrapping.Wrap };
    private IReadOnlyList<ChatToolOption> tools = [];
    private bool syncing;

    private static string L(string key) => ChatSettingsFields.L("toolConfiguration." + key);
    private static string CallerLabel(string caller) => ChatSettingsFields.L("openai.programmaticToolCalling.allowedCallersOptions." + caller);

    public ChatToolConfigurationView(ChatPreferences draft, Func<ChatPreferences, IReadOnlyList<ChatToolOption>> availableTools)
    {
        this.draft = draft; this.availableTools = availableTools;
        Name = "ChatToolConfigurationView"; Spacing = 16;
        namespaces.IsOn = draft.UseToolNamespaces;
        ControlAppearance.Stock(namespaces); Identify(namespaces, namespaces.Name, L("useNamespaces"));
        NativeSettingsSurface.ToggleCard(this, "ChatToolNamespacesCard", L("useNamespaces"), namespaces);
        namespaces.Toggled += (_, _) =>
        {
            if (syncing) return;
            draft.UseToolNamespaces = namespaces.IsOn; ArrangeRows();
        };
        empty.Text = L("empty"); Children.Add(empty); Children.Add(rows);
        RefreshAvailableTools();
    }

    public void RefreshAvailableTools()
    {
        var next = availableTools(draft);
        // Reuse the controls when only state changed: preserve keyboard focus and open flyouts.
        var same = next.Count == tools.Count && next.Zip(tools).All(pair => SameTool(pair.Item1, pair.Item2));
        tools = next;
        if (!same || controls.Count == 0)
        {
            controls.Clear(); rows.Children.Clear();
            if (tools.Count > 0)
            {
                controls.Add(CreateRow(null));
                foreach (var tool in tools) controls.Add(CreateRow(tool));
            }
            ArrangeRows();
        }
        empty.Visibility = tools.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        namespaces.IsEnabled = tools.Count > 0;
        RefreshControls();
    }

    private static bool SameTool(ChatToolOption a, ChatToolOption b)
    {
        var left = a.Source ?? ChatToolSource.Local; var right = b.Source ?? ChatToolSource.Local;
        return a.Name == b.Name && a.Label == b.Label && left.Kind == right.Kind && left.Id == right.Id
            && left.Name == right.Name && left.Description == right.Description && left.Namespace == right.Namespace
            && left.RequestOptions?.DeferLoading == right.RequestOptions?.DeferLoading
            && (left.RequestOptions?.AllowedCallers ?? []).SequenceEqual(right.RequestOptions?.AllowedCallers ?? []);
    }

    private Row CreateRow(ChatToolOption? tool)
    {
        var id = tool?.Name ?? "all";
        var title = tool?.Label ?? L("allTools");
        var row = new Grid { Name = "ChatToolConfigRow", Tag = id, ColumnSpacing = 16, RowSpacing = 10, Padding = new Thickness(0, 8, 0, 8) };
        row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new() { Width = new GridLength(190) });
        row.ColumnDefinitions.Add(new() { Width = new GridLength(90) });
        row.RowDefinitions.Add(new() { Height = GridLength.Auto }); row.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var identity = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        identity.Children.Add(new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        if (tool is not null) identity.Children.Add(new TextBlock { Text = (tool.Source ?? ChatToolSource.Local).Name, TextWrapping = TextWrapping.Wrap, Opacity = .65, FontSize = 12 });
        row.Children.Add(identity);
        var callerPanel = new StackPanel { Spacing = 6 };
        callerPanel.Children.Add(new TextBlock { Text = L("allowedCallers"), TextWrapping = TextWrapping.Wrap, FontSize = 12 });
        var callers = new DropDownButton { Name = "ChatToolCallers", Tag = id, HorizontalAlignment = HorizontalAlignment.Stretch, MinWidth = 0 };
        ControlAppearance.Stock(callers); Identify(callers, "ChatToolCallers_" + id, title + ": " + L("allowedCallers"));
        callerPanel.Children.Add(callers); row.Children.Add(callerPanel); Grid.SetColumn(callerPanel, 1);
        var searchPanel = new StackPanel { Spacing = 6 };
        searchPanel.Children.Add(new TextBlock { Text = L("deferLoading"), TextWrapping = TextWrapping.Wrap, FontSize = 12 });
        var search = new ToggleSwitch { Name = "ChatToolSearch", Tag = id, OnContent = "", OffContent = "", MinWidth = 0 };
        ControlAppearance.Stock(search); Identify(search, "ChatToolSearch_" + id, title + ": " + L("deferLoading"));
        searchPanel.Children.Add(search); row.Children.Add(searchPanel); Grid.SetColumn(searchPanel, 2);
        var flyout = new MenuFlyout(); var items = new List<ToggleMenuFlyoutItem>(); callers.Flyout = flyout;
        foreach (var caller in ChatToolConfiguration.Callers)
        {
            var item = new ToggleMenuFlyoutItem { Text = CallerLabel(caller), Tag = caller };
            Identify(item, "ChatToolCaller_" + id + "_" + caller, title + ": " + CallerLabel(caller));
            item.Click += (_, _) =>
            {
                var selected = CallerValues(tool).ToList();
                if (selected.Contains(caller)) selected.RemoveAll(c => c == caller); else selected.Add(caller);
                foreach (var target in tool is null ? tools : new[] { tool }) ChatToolConfiguration.SetCallers(draft, target, selected, bulk: tool is null);
                RefreshControls();
            };
            flyout.Items.Add(item); items.Add(item);
        }
        search.Toggled += (_, _) =>
        {
            if (syncing) return;
            foreach (var target in tool is null ? tools : new[] { tool }) ChatToolConfiguration.SetDeferred(draft, target, search.IsOn, bulk: tool is null);
            RefreshControls();
        };
        // Keep every control native, but reflow the identity above the actions on narrow Windows dialogs.
        row.SizeChanged += (_, _) =>
        {
            var narrow = row.ActualWidth < 560;
            Grid.SetColumnSpan(identity, narrow ? 3 : 1);
            Grid.SetRow(callerPanel, narrow ? 1 : 0); Grid.SetColumn(callerPanel, narrow ? 0 : 1);
            Grid.SetColumnSpan(callerPanel, narrow ? 2 : 1); Grid.SetRow(searchPanel, narrow ? 1 : 0);
            row.ColumnDefinitions[1].Width = new GridLength(narrow ? 0 : 190);
        };
        var host = new StackPanel();
        NativeSettingsSurface.Card(host, "ChatToolConfigCard", title, out var body).Header = null;
        body.Children.Add(row);
        return new(tool, callers, search, items, host);
    }

    private IEnumerable<string> CallerValues(ChatToolOption? tool) => tool is null
        ? ChatToolConfiguration.Callers.Where(c => tools.Count > 0 && tools.All(t => ChatToolConfiguration.Effective(draft, t).AllowedCallers?.Contains(c) == true))
        : ChatToolConfiguration.Effective(draft, tool).AllowedCallers ?? [];

    private void RefreshControls()
    {
        syncing = true;
        try
        {
            namespaces.IsOn = draft.UseToolNamespaces;
            foreach (var row in controls)
            {
                var values = CallerValues(row.Tool).ToArray();
                var title = values.Length > 0 ? string.Join(", ", values.Select(CallerLabel)) : ChatSettingsFields.L("toolConfiguration.select");
                row.Callers.Content = new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap };
                ToolTipService.SetToolTip(row.Callers, title);
                foreach (var item in row.Items) item.IsChecked = values.Contains((string)item.Tag);
                row.Search.IsOn = row.Tool is { } tool ? ChatToolConfiguration.Effective(draft, tool).DeferLoading == true
                    : tools.Count > 0 && tools.All(t => ChatToolConfiguration.Effective(draft, t).DeferLoading == true);
            }
        }
        finally { syncing = false; }
    }

    private void ArrangeRows()
    {
        rows.Children.Clear();
        if (controls.Count == 0) return;
        rows.Children.Add(controls[0].View);
        var sorted = controls.Skip(1).OrderBy(r => draft.UseToolNamespaces || r.Tool!.Source?.Namespace == true
                ? (r.Tool!.Source ?? ChatToolSource.Local).NamespaceName : "", StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(r => r.Tool!.Name, StringComparer.CurrentCultureIgnoreCase);
        string? previous = null;
        foreach (var row in sorted)
        {
            var source = row.Tool!.Source ?? ChatToolSource.Local;
            var name = draft.UseToolNamespaces || source.Namespace ? source.NamespaceName : null;
            if (name is not null && name != previous) rows.Children.Add(new TextBlock { Name = "ChatToolNamespaceHeading",
                Text = L("namespace") + ": " + name, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            rows.Children.Add(row.View); previous = name;
        }
    }

    private static void Identify(DependencyObject element, string id, string name)
    { AutomationProperties.SetAutomationId(element, id); AutomationProperties.SetName(element, name); }
}
