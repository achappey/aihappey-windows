using FluentIcons.Common;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Markup;
using Windows.System;

namespace AIHappey.Desktop.Core;

/// <summary>A stock button/flyout/search/list composition. Focus alone never opens it.
/// The native ListView owns keyboard navigation, virtualization, selection indicators and UI Automation.</summary>
public sealed class ChatTargetPicker : Button
{
    private readonly Flyout popup = new() { Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft };
    private readonly TextBlock summary = new() { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox search = new() { Name = "TargetSearch", PlaceholderText = DesktopResources.Get("TargetSearch") };
    private readonly ListView list = new() { Name = "TargetChoices", MaxHeight = 400, MinHeight = 40, SelectionMode = ListViewSelectionMode.Multiple,
        IsMultiSelectCheckBoxEnabled = true, IsItemClickEnabled = true };
    private readonly TextBlock empty = new() { Text = DesktopResources.Get("TargetNoResults"), Margin = new Thickness(12), Visibility = Visibility.Collapsed };
    private readonly Button allFavorites = new() { Name = "SelectAllFavorites", Content = DesktopResources.Get("SelectAllFavorites"), HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Button router = new() { Name = "FavoriteAgentRouter", HorizontalAlignment = HorizontalAlignment.Stretch };
    private IReadOnlyList<ChatTarget> items = [];
    private IReadOnlySet<string> favorites = new HashSet<string>();
    private bool updating;
    private bool multiple;
    private bool agentMode;
    private bool routerAvailable;
    private string routerLabel = "";
    private string placeholder = "";
    public ChatSelection Selection { get; } = new();
    public event Action? SelectionChanged;
    public Action? Opening { get; set; }
    // Agent forms allow clearing every model and let Save validation flag the incomplete draft.
    public bool AllowEmptySelection { get; set; }
    public bool IsSuggestionListOpen { get => popup.IsOpen; set { if (!value) popup.Hide(); else if (IsLoaded && IsEnabled) popup.ShowAt(this); } }
    public string PlaceholderText { get => placeholder; set { placeholder = value; UpdateSummary(); } }
    // Compatibility with existing launch/history paths: Text is a canonical key, never editable display text.
    public string Text { get => Selection.Ids.FirstOrDefault() ?? ""; set { Selection.FavoriteRouter = false; Selection.Replace([value]); UpdateSummary(); SyncSelectedRows(); } }
    public object? ItemsSource { get => items; set { items = value is IEnumerable<ChatTarget> values ? values.ToArray() : []; Rebuild(); } }
    public ChatTargetPicker()
    {
        Name = "ChatTargetPicker"; HorizontalContentAlignment = HorizontalAlignment.Stretch;
        var label = new Grid { ColumnSpacing = 8 };
        label.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        label.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        label.Children.Add(summary); var chevron = DesktopIcons.Create(Icon.ChevronDown, 12); Grid.SetColumn(chevron, 1); label.Children.Add(chevron); Content = label;
        var body = new StackPanel { Name = "TargetPickerFlyout", Spacing = 8, Width = 360, MaxWidth = 480 };
        body.Children.Add(search); body.Children.Add(allFavorites); body.Children.Add(router); body.Children.Add(list); body.Children.Add(empty);
        popup.Content = body; Flyout = popup;
        list.ItemTemplate = (DataTemplate)XamlReader.Load("""
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"><TextBlock Text="{Binding Label}" TextTrimming="CharacterEllipsis" VerticalAlignment="Center" ToolTipService.ToolTip="{Binding Id}" /></DataTemplate>
            """);
        list.GroupStyle.Add(new GroupStyle { HeaderTemplate = (DataTemplate)XamlReader.Load("""
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"><TextBlock Text="{Binding Key}" FontWeight="SemiBold" Margin="12,12,0,4" /></DataTemplate>
            """) });
        foreach (var control in new Control[] { this, search, list, allFavorites, router }) ControlAppearance.Stock(control);
        AutomationProperties.SetName(search, DesktopResources.Get("TargetSearch"));
        AutomationProperties.SetName(list, DesktopResources.Get("SelectTarget"));
        popup.Opening += (_, _) =>
        {
            Opening?.Invoke();
            body.Width = Math.Max(220, Math.Min(420, XamlRoot?.Size.Width - 64 ?? 360));
            search.Text = ""; Rebuild();
        };
        popup.Opened += (_, _) => search.Focus(FocusState.Programmatic);
        search.TextChanged += (_, _) => Rebuild();
        search.KeyDown += (_, args) =>
        {
            if (args.Key == VirtualKey.Down) { list.Focus(FocusState.Keyboard); args.Handled = true; }
        };
        list.SelectionChanged += (_, args) =>
        {
            if (updating) return;
            // A filtered/grouped row is only a view. Apply changes by ID to the ordered selection.
            foreach (var item in args.AddedItems.OfType<ChatTarget>())
                if (!Selection.Ids.Contains(item.Id) || Selection.FavoriteRouter) Selection.Toggle(item.Id, multiple, agentMode || AllowEmptySelection);
            foreach (var item in args.RemovedItems.OfType<ChatTarget>())
                if (args.AddedItems.Count == 0 && Selection.Ids.Contains(item.Id)) Selection.Toggle(item.Id, multiple, agentMode || AllowEmptySelection);
            SyncSelectedRows(); UpdateSummary(); SelectionChanged?.Invoke();
            if (!multiple) popup.Hide();
        };
        list.ItemClick += (_, args) =>
        {
            // A router may have pinned a previously checked winner; clicking it still exits routing.
            if (Selection.FavoriteRouter && args.ClickedItem is ChatTarget item)
            { Selection.Toggle(item.Id, multiple, agentMode); SyncSelectedRows(); UpdateSummary(); SelectionChanged?.Invoke(); }
        };
        allFavorites.Click += (_, _) =>
        {
            Selection.FavoriteRouter = false; Selection.Replace(items.Where(i => favorites.Contains(i.Id)).Select(i => i.Id));
            SyncSelectedRows(); UpdateSummary(); SelectionChanged?.Invoke();
        };
        router.Click += (_, _) =>
        {
            if (!routerAvailable) return;
            Selection.FavoriteRouter = true; SyncSelectedRows(); UpdateSummary(); SelectionChanged?.Invoke(); popup.Hide();
        };
        IsEnabledChanged += (_, _) => { if (!IsEnabled) popup.Hide(); };
        Unloaded += (_, _) => popup.Hide();
    }
    public void Configure(bool agents, bool multiselect, IReadOnlySet<string> favoriteIds, bool canRoute, string routeLabel)
    {
        agentMode = agents; multiple = multiselect; favorites = favoriteIds; routerAvailable = canRoute; routerLabel = routeLabel;
        // Do not silently discard an existing multi-selection if routing becomes unavailable. Send validates it.
        list.SelectionMode = multiple ? ListViewSelectionMode.Multiple : ListViewSelectionMode.Single;
        list.IsMultiSelectCheckBoxEnabled = multiple; Rebuild();
    }
    public void SetSelection(IEnumerable<string> ids, bool favoriteRouter = false)
    { Selection.Replace(ids); Selection.FavoriteRouter = favoriteRouter; SyncSelectedRows(); UpdateSummary(); }
    private void UpdateSummary()
    {
        summary.Text = Selection.FavoriteRouter ? routerLabel : string.Join(", ", Selection.Ids.Select(id =>
            items.FirstOrDefault(i => i.Id == id)?.Label ?? DesktopAgentTargets.DisplayName(id)));
        if (summary.Text.Length == 0) summary.Text = placeholder;
        ToolTipService.SetToolTip(this, summary.Text);
        AutomationProperties.SetHelpText(this, summary.Text);
    }
    private IReadOnlyList<TargetGroup> groups = [];
    private void Rebuild()
    {
        updating = true;
        try
        {
            var query = search.Text.Trim();
            bool Matches(ChatTarget item) => query.Length == 0 || item.Label.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || item.Id.Contains(query, StringComparison.OrdinalIgnoreCase) || item.Tags.Any(tag => tag.Contains(query, StringComparison.CurrentCultureIgnoreCase))
                || (agentMode && item.OwnedBy?.Contains(query, StringComparison.CurrentCultureIgnoreCase) == true)
                || (item.ProviderKey is { } key && ProviderCatalog.Get(key).Name.Contains(query, StringComparison.CurrentCultureIgnoreCase));
            var visible = items.Where(Matches).ToArray(); var next = new List<TargetGroup>();
            void Add(string key, IEnumerable<ChatTarget> values)
            { var rows = values.ToArray(); if (rows.Length > 0) next.Add(new(key, rows)); }
            Add(DesktopResources.Get("Favorites"), visible.Where(i => favorites.Contains(i.Id)));
            if (!agentMode)
            {
                foreach (var group in visible.Where(i => !favorites.Contains(i.Id)).GroupBy(i => i.ProviderKey ?? ""))
                    Add(group.Key.Length == 0 ? DesktopResources.Get("Models") : ProviderCatalog.Get(group.Key).Name, group);
            }
            else
            {
                var grouped = visible.Select(i => (Target: i, Groups: DesktopAgentMetadata.Groups(i))).ToArray();
                foreach (var key in grouped.SelectMany(i => i.Groups).Distinct().Order(StringComparer.CurrentCultureIgnoreCase))
                    Add(key, grouped.Where(i => i.Groups.Contains(key)).Select(i => i.Target).OrderBy(i => i.Label, StringComparer.CurrentCultureIgnoreCase));
                foreach (var local in new[] { false, true })
                    Add(local ? DesktopResources.Get("Local") : DesktopResources.Get("RemoteAgents"), grouped.Where(i => i.Groups.Count == 0
                        && !favorites.Contains(i.Target.Id) && (i.Target.LocalAgentName is not null) == local).Select(i => i.Target).OrderBy(i => i.Label, StringComparer.CurrentCultureIgnoreCase));
            }
            groups = next;
            list.ItemsSource = new CollectionViewSource { IsSourceGrouped = true, Source = next }.View;
            empty.Visibility = visible.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            allFavorites.Visibility = !agentMode && multiple && routerAvailable && items.Any(i => favorites.Contains(i.Id)) ? Visibility.Visible : Visibility.Collapsed;
            router.Content = routerLabel; router.Visibility = agentMode && routerAvailable ? Visibility.Visible : Visibility.Collapsed;
            SyncSelectedRows(); UpdateSummary();
        }
        finally { updating = false; }
    }
    private void SyncSelectedRows()
    {
        var before = updating; updating = true;
        try
        {
            list.SelectedItems.Clear();
            foreach (var row in groups.SelectMany(g => g).Where(i => !Selection.FavoriteRouter && Selection.Ids.Contains(i.Id)))
            {
                if (!multiple && list.SelectedItems.Count > 0) break;
                list.SelectedItems.Add(row);
            }
        }
        finally { updating = before; }
    }
    public sealed class TargetGroup(string key, IEnumerable<ChatTarget> items) : List<ChatTarget>(items) { public string Key { get; } = key; }
}
