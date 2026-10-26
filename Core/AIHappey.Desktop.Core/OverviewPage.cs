using FluentIcons.Common;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace AIHappey.Desktop.Core;

/// <summary>Shared layout for card-based overview pages. Data and actions remain outside the visual component.</summary>
internal sealed class OverviewPage : UserControl
{
    internal readonly TextBox SearchBox = new() { Name = "CatalogSearch", PlaceholderText = DesktopResources.Get("SearchPlaceholder"), MaxWidth = 360, HorizontalAlignment = HorizontalAlignment.Stretch, Height = 40, CornerRadius = new CornerRadius(8) };
    internal readonly OverviewCardsPanel Cards = new() { Name = "CatalogCards" };
    private readonly OverviewFilterDropdown tagFilter = new("AgentTagsFilter"), ownerFilter = new("AgentOwnerFilter");
    private readonly HashSet<string> selectedTags = new(StringComparer.Ordinal), selectedOwners = new(StringComparer.Ordinal);
    private string[] tagOptions = [], ownerOptions = [];
    private readonly StackPanel body = new() { Spacing = 16, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(24, 24, 24, 24) };
    private readonly NavigationView filters = new() { Name = "CatalogFilters", PaneDisplayMode = NavigationViewPaneDisplayMode.Top,
        IsSettingsVisible = false, IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed,
        IsPaneToggleButtonVisible = false, AlwaysShowHeader = false, Height = 56 };
    private readonly Dictionary<string, NavigationViewItem> filterItems = [];
    private readonly TextBlock status = new() { Name = "CatalogStatus", TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly Button retry = new() { Content = DesktopResources.Get("Retry"), HorizontalAlignment = HorizontalAlignment.Center };
    private readonly Button cancel = new() { Content = DesktopResources.Get("Cancel"), HorizontalAlignment = HorizontalAlignment.Center };
    private readonly Button more = new() { Content = DesktopResources.Get("ShowMore"), HorizontalAlignment = HorizontalAlignment.Center };
    private readonly ScrollViewer viewer;
    private IReadOnlyList<CatalogItem> items = [];
    private IReadOnlySet<string> favorites = new HashSet<string>();
    private string activeFilter = "all";
    private string source = "Backend";
    private int visible = 50;
    private bool working;
    private bool failed;
    private bool updatingFilters;
    private bool actionsEnabled = true;
    public CatalogKind Kind { get; }
    public Action? RetryRequested { get; set; }
    public Action? CancelRequested { get; set; }
    public Action<CatalogItem, Button>? DetailsRequested { get; set; }
    public Action<CatalogItem>? FavoriteRequested { get; set; }
    public Action<CatalogItem>? DownloadRequested { get; set; }
    public Action<CatalogItem>? ChatRequested { get; set; }
    public Action<CatalogItem, Button>? EditRequested { get; set; }
    public Action<CatalogItem>? DeleteRequested { get; set; }
    public Action<CatalogItem>? PluginRequested { get; set; }

    public OverviewPage(CatalogKind kind)
    {
        Kind = kind;
        var title = new TextBlock { Name = "OverviewTitle", Text = kind == CatalogKind.Agent ? DesktopResources.Get("Agents") : DesktopResources.Get("Skills"), FontSize = 36,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        AutomationProperties.SetHeadingLevel(title, Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level1);
        var description = new TextBlock { Name = "OverviewDescription", FontSize = 16, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
            Text = kind == CatalogKind.Agent
                ? DesktopResources.Get("AgentsDescription")
                : DesktopResources.Get("SkillsDescription") };
        body.Children.Add(new OverviewPageHeader(title, kind == CatalogKind.Skill ? "https://agentskills.io" : null,
            kind == CatalogKind.Skill ? "https://agentskills.io/specification" : null));
        body.Children.Add(description);
        SearchBox.HorizontalAlignment = HorizontalAlignment.Stretch;
        if (kind == CatalogKind.Agent) body.Children.Add(new OverviewSearchFiltersRow(SearchBox, tagFilter, ownerFilter));
        else
        {
            var searchRow = new Grid { MaxWidth = 360, HorizontalAlignment = HorizontalAlignment.Stretch };
            searchRow.Children.Add(SearchBox); body.Children.Add(searchRow);
        }
        // Stock top navigation supplies the animated indicator, keyboard behavior and overflow.
        body.Children.Add(filters);
        body.Children.Add(status); body.Children.Add(retry); body.Children.Add(cancel); body.Children.Add(Cards); body.Children.Add(more);
        viewer = new ScrollViewer { Content = body, HorizontalScrollMode = ScrollMode.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        Content = viewer;
        ControlAppearance.Native(SearchBox);
        ToolbarControls.Label(SearchBox, kind == CatalogKind.Agent ? DesktopResources.Get("SearchAgents") : DesktopResources.Get("SearchSkills"));
        foreach (var button in new[] { retry, cancel, more }) ControlAppearance.Native(button);
        ControlAppearance.Apply(description, (_, _) => { }, palette => description.Foreground = new SolidColorBrush(palette.Text));
        ControlAppearance.Apply(status, (_, _) => { }, palette => status.Foreground = new SolidColorBrush(palette.Text));
        AutomationProperties.SetLiveSetting(status, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        SearchBox.TextChanged += (_, _) => { visible = 50; Render(); };
        filters.SelectionChanged += (_, args) =>
        {
            if (updatingFilters || args.SelectedItem is not NavigationViewItem { Tag: string key } || activeFilter == key) return;
            activeFilter = key; visible = 50; Render();
        };
        retry.Click += (_, _) => RetryRequested?.Invoke();
        cancel.Click += (_, _) => CancelRequested?.Invoke();
        more.Click += (_, _) => { visible += 50; Render(); };
        viewer.SizeChanged += (_, _) => SizeBody();
        Loaded += (_, _) => SizeBody();
        Render();
    }

    private void SizeBody()
    {
        var width = viewer.ViewportWidth > 0 ? viewer.ViewportWidth : viewer.ActualWidth;
        if (width > 0) body.Width = Math.Max(0, Math.Min(760, width - 48));
    }

    public void SetItems(IReadOnlyList<CatalogItem> value, IReadOnlySet<string> savedFavorites, string sourceLabel)
    {
        items = Kind == CatalogKind.Agent ? value.Select(AgentOverviewProjection.Normalize).ToArray() : value;
        tagOptions = items.SelectMany(item => item.Tags).Distinct(StringComparer.Ordinal).Order(StringComparer.CurrentCultureIgnoreCase).ToArray();
        ownerOptions = items.Select(item => item.Owner).Where(owner => !string.IsNullOrWhiteSpace(owner)).Cast<string>()
            .Distinct(StringComparer.Ordinal).Order(StringComparer.CurrentCultureIgnoreCase).ToArray();
        selectedTags.IntersectWith(tagOptions); selectedOwners.IntersectWith(ownerOptions);
        favorites = savedFavorites; source = sourceLabel; working = false; visible = 50; Render();
    }

    public void SetFavorites(IReadOnlySet<string> value) { favorites = value; Render(); }

    public void Loading(string? message = null)
    {
        working = true; Cards.Children.Clear(); status.Text = message ?? DesktopResources.Get("Loading");
        status.Visibility = cancel.Visibility = Visibility.Visible; retry.Visibility = more.Visibility = Visibility.Collapsed;
        SearchBox.IsEnabled = filters.IsEnabled = false;
        tagFilter.IsEnabled = ownerFilter.IsEnabled = false;
    }

    public void Error(string message)
    {
        working = false; Cards.Children.Clear(); status.Text = message;
        failed = true;
        status.Visibility = retry.Visibility = Visibility.Visible; cancel.Visibility = more.Visibility = Visibility.Collapsed;
        SearchBox.IsEnabled = actionsEnabled; filters.IsEnabled = false;
        tagFilter.IsEnabled = ownerFilter.IsEnabled = false;
    }

    public void SetActionsEnabled(bool enabled)
    {
        actionsEnabled = enabled;
        foreach (var button in ControlAppearance.Descendants(Cards).OfType<Button>()) button.IsEnabled = enabled;
        retry.IsEnabled = more.IsEnabled = enabled;
        SearchBox.IsEnabled = enabled && !working;
        filters.IsEnabled = enabled && !working && !failed;
        tagFilter.IsEnabled = enabled && !working && !failed && tagOptions.Length > 0;
        ownerFilter.IsEnabled = enabled && !working && !failed && ownerOptions.Length > 0;
    }

    private void Render()
    {
        if (working) return;
        failed = false;
        var focused = XamlRoot is null ? null : FocusManager.GetFocusedElement(XamlRoot) as Button;
        var focusId = focused is null ? null : AutomationProperties.GetAutomationId(focused);
        SearchBox.IsEnabled = true; retry.Visibility = cancel.Visibility = Visibility.Collapsed;
        var searched = Kind == CatalogKind.Agent ? AgentOverviewProjection.Search(items, SearchBox.Text) : CatalogProjection.Search(items, SearchBox.Text);
        if (Kind == CatalogKind.Agent)
        {
            UpdateAgentFilters(searched);
            searched = searched.Where(item => AgentOverviewProjection.Matches(item, selectedTags, selectedOwners)).ToArray();
        }
        // Keep unchanged items alive so result/count refreshes do not reset focus or the indicator.
        updatingFilters = true;
        AddFilter("all", DesktopResources.Format("AllCount", searched.Count), Icon.List);
        AddFilter("favorites", DesktopResources.Format("FavoritesCount", searched.Count(item => favorites.Contains(item.Key))), Icon.Star);
        AddFilter("backend", $"{source} ({searched.Count(item => item.Origin == CatalogOrigin.Backend)})");
        AddFilter("local", DesktopResources.Format("LocalCount", searched.Count(item => item.Origin == CatalogOrigin.Local)));
        if (Kind == CatalogKind.Agent)
        {
            var metadataTabs = tagOptions.Select(value => (Key: AgentOverviewProjection.TabKey(true, value), Label: value))
                .Concat(ownerOptions.Select(value => (Key: AgentOverviewProjection.TabKey(false, value), Label: value))).ToArray();
            var keys = new[] { "all", "favorites", "backend", "local" }.Concat(metadataTabs.Select(tab => tab.Key)).ToArray();
            foreach (var stale in filterItems.Keys.Except(keys).ToArray()) RemoveFilter(stale);
            foreach (var tab in metadataTabs)
                AddFilter(tab.Key, $"{tab.Label} ({searched.Count(item => AgentOverviewProjection.MatchesTab(item, tab.Key))})");
            // Reorder only when metadata changes, retaining native item identities and selection.
            for (var index = 0; index < keys.Length; index++)
            {
                var tab = filterItems[keys[index]];
                if (ReferenceEquals(filters.MenuItems[index], tab)) continue;
                filters.MenuItems.Remove(tab); filters.MenuItems.Insert(index, tab);
            }
        }
        filters.SelectedItem = filterItems[activeFilter];
        filters.IsEnabled = true;
        updatingFilters = false;
        var selected = searched.Where(item => activeFilter switch
        {
            "favorites" => favorites.Contains(item.Key), "backend" => item.Origin == CatalogOrigin.Backend,
            "local" => item.Origin == CatalogOrigin.Local, _ => Kind != CatalogKind.Agent || AgentOverviewProjection.MatchesTab(item, activeFilter)
        }).ToArray();
        Cards.Children.Clear();
        foreach (var item in selected.Take(visible)) Cards.Children.Add(BuildCard(item));
        status.Text = items.Count == 0 ? DesktopResources.Get(Kind == CatalogKind.Agent ? "NoAgents" : "NoSkills")
            : selected.Length == 0 ? DesktopResources.Get("CatalogNoResults") : "";
        status.Visibility = selected.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        more.Visibility = selected.Length > visible ? Visibility.Visible : Visibility.Collapsed;
        SetActionsEnabled(actionsEnabled);
        if (!string.IsNullOrEmpty(focusId) && focused is not null && !focused.IsLoaded)
            DispatcherQueue.TryEnqueue(() => (ControlAppearance.Descendants(body).OfType<Button>().FirstOrDefault(button => AutomationProperties.GetAutomationId(button) == focusId) as Control ?? SearchBox).Focus(FocusState.Programmatic));
    }

    private void UpdateAgentFilters(IReadOnlyList<CatalogItem> searched)
    {
        void Toggle(HashSet<string> selected, string? value)
        { if (value is null) selected.Clear(); else if (!selected.Remove(value)) selected.Add(value); visible = 50; Render(); }
        var tagCandidates = searched.Where(item => selectedOwners.Count == 0 || selectedOwners.Contains(item.Owner ?? "")).ToArray();
        var ownerCandidates = searched.Where(item => selectedTags.Count == 0 || item.Tags.Any(selectedTags.Contains)).ToArray();
        tagFilter.Update(DesktopResources.Get("AgentTags"), tagOptions.Select(value => new OverviewFilterOption(value, value,
            tagCandidates.Count(item => item.Tags.Contains(value)))).ToArray(), selectedTags, tagCandidates.Length, value => Toggle(selectedTags, value));
        ownerFilter.Update(DesktopResources.Get("AgentOwnedBy"), ownerOptions.Select(value => new OverviewFilterOption(value, value,
            ownerCandidates.Count(item => item.Owner == value))).ToArray(), selectedOwners, ownerCandidates.Length, value => Toggle(selectedOwners, value));
    }

    private void RemoveFilter(string key)
    {
        if (!filterItems.Remove(key, out var item)) return;
        if (activeFilter == key) activeFilter = "all";
        filters.MenuItems.Remove(item);
    }

    private void AddFilter(string key, string label, Icon? icon = null)
    {
        if (!filterItems.TryGetValue(key, out var item))
        {
            item = new NavigationViewItem { Name = "CatalogFilter", Tag = key };
            if (icon is not null) item.Icon = DesktopIcons.Create(icon.Value);
            AutomationProperties.SetAutomationId(item, "CatalogFilter_" + key);
            filterItems.Add(key, item); filters.MenuItems.Add(item);
        }
        item.Content = label; ToolbarControls.Label(item, label);
    }

    private Border BuildCard(CatalogItem item)
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto }); grid.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) }); grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var header = new Grid { Margin = new Thickness(16, 16, 16, 0), ColumnSpacing = 12 };
        header.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); header.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        var icon = CardIcon(item);
        if (icon is not null) header.Children.Add(icon);
        var labels = new StackPanel { Spacing = 6 };
        labels.Children.Add(new TextBlock { Text = item.Name, FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis });
        var badgeText = item.Kind == CatalogKind.Agent ? item.Model : item.Version;
        if (!string.IsNullOrWhiteSpace(badgeText))
        {
            var badge = new Border { HorizontalAlignment = HorizontalAlignment.Left,
                Child = new TextBlock { Text = badgeText, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis } };
            ToolTipService.SetToolTip(badge, badgeText); NativeCardSurface.Badge(badge); labels.Children.Add(badge);
        }
        Grid.SetColumn(labels, 1); header.Children.Add(labels); grid.Children.Add(header);
        var description = new TextBlock { Text = item.Description, TextWrapping = TextWrapping.Wrap, MaxLines = 3, TextTrimming = TextTrimming.CharacterEllipsis,
            FontSize = 13, Margin = new Thickness(16, 18, 16, 16), MinHeight = 36 };
        NativeCardSurface.Secondary(description);
        Grid.SetRow(description, 1); grid.Children.Add(description);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        if (item.Kind == CatalogKind.Skill)
        { var view = ActionButton(item, "Details", Icon.Info); view.Click += (_, _) => DetailsRequested?.Invoke(item, view); actions.Children.Add(view); }
        if (item.Origin == CatalogOrigin.Local)
        {
            var edit = ActionButton(item, item.Kind == CatalogKind.Agent ? "AgentEdit" : "SkillEdit", Icon.Edit); edit.Click += (_, _) => EditRequested?.Invoke(item, edit); actions.Children.Add(edit);
            var delete = ActionButton(item, "Delete", Icon.Delete); delete.Click += (_, _) => DeleteRequested?.Invoke(item); actions.Children.Add(delete);
        }
        if (item.CanDownload)
        { var download = ActionButton(item, "Download", Icon.ArrowDownload); download.Click += (_, _) => DownloadRequested?.Invoke(item); actions.Children.Add(download); }
        var favorite = ActionButton(item, favorites.Contains(item.Key) ? "RemoveFavorite" : "AddFavorite", Icon.Star, favorites.Contains(item.Key) ? IconVariant.Filled : IconVariant.Regular);
        AutomationProperties.SetAutomationId(favorite, item.Key + ":Favorite"); favorite.Name = "CatalogFavorite";
        favorite.Click += (_, _) => FavoriteRequested?.Invoke(item); actions.Children.Add(favorite);
        if (item.Kind == CatalogKind.Agent)
        { var chat = ActionButton(item, "StartChat", Icon.Chat); chat.Name = "CatalogStartChat"; chat.Click += (_, _) => ChatRequested?.Invoke(item); actions.Children.Add(chat);
          var plugin = ActionButton(item, "PluginFromAgent", Icon.PuzzlePiece); plugin.Name = "CatalogCreatePlugin"; plugin.Click += (_, _) => PluginRequested?.Invoke(item); actions.Children.Add(plugin); }
        var footer = new Border { Child = actions, Padding = new Thickness(12, 8, 12, 8), BorderThickness = new Thickness(0, 1, 0, 0) };
        NativeCardSurface.Divider(footer); Grid.SetRow(footer, 2); grid.Children.Add(footer);
        var card = new Border { Name = "CatalogCard", Tag = item, Child = grid };
        NativeCardSurface.Card(card);
        AutomationProperties.SetName(card, item.Name); return card;
    }

    private static Button ActionButton(CatalogItem item, string action, Icon icon, IconVariant variant = IconVariant.Regular)
    {
        var button = new Button { Name = "Catalog" + action.Replace(" ", ""), Content = DesktopIcons.Create(icon, 18, variant), Width = 36, Height = 36, Padding = new Thickness(0) };
        NativeCardSurface.Action(button); ToolbarControls.Label(button, DesktopResources.Format("ActionForItem", DesktopResources.Get(action), item.Name)); AutomationProperties.SetAutomationId(button, item.Key + ":" + action); return button;
    }

    private UIElement? CardIcon(CatalogItem item)
    {
        if (item.Kind == CatalogKind.Skill)
        {
            var provider = ProviderCatalog.SkillProvider(item);
            return provider is null || provider.Icons.Count == 0 ? null
                : new ProviderLogo(provider with { Urls = new() }, 32, showPlaceholder: false) { Name = "SkillProviderLogo", VerticalAlignment = VerticalAlignment.Top };
        }
        return new ProviderLogo(DesktopAgentIcons.Artwork(item), 32, showPlaceholder: true, placeholderIcon: Icon.Bot)
            { Name = "AgentIcon", VerticalAlignment = VerticalAlignment.Top };
    }
}

/// <summary>Two equally sized columns when they fit, otherwise one. No horizontal scrolling or fixed card widths.</summary>
internal sealed class OverviewCardsPanel : Panel
{
    private const double Gap = 16;
    private int Columns(double width) => width >= 640 ? 2 : 1;
    protected override Windows.Foundation.Size MeasureOverride(Windows.Foundation.Size available)
    {
        var width = double.IsInfinity(available.Width) ? 760 : available.Width;
        var columns = Columns(width); var cardWidth = Math.Max(0, (width - Gap * (columns - 1)) / columns);
        double height = 0;
        for (var index = 0; index < Children.Count; index += columns)
        {
            double rowHeight = 0;
            for (var column = 0; column < columns && index + column < Children.Count; column++)
            { Children[index + column].Measure(new(cardWidth, double.PositiveInfinity)); rowHeight = Math.Max(rowHeight, Children[index + column].DesiredSize.Height); }
            height += rowHeight + (index == 0 ? 0 : Gap);
        }
        return new(width, height);
    }
    protected override Windows.Foundation.Size ArrangeOverride(Windows.Foundation.Size final)
    {
        var columns = Columns(final.Width); var width = Math.Max(0, (final.Width - Gap * (columns - 1)) / columns); double y = 0;
        for (var index = 0; index < Children.Count; index += columns)
        {
            var height = Children.Skip(index).Take(columns).Max(child => child.DesiredSize.Height);
            for (var column = 0; column < columns && index + column < Children.Count; column++) Children[index + column].Arrange(new(column * (width + Gap), y, width, height));
            y += height + Gap;
        }
        return final;
    }
}
