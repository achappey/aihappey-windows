using Flags.Icons;
using Flags.Icons.WinUi;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace AIHappey.Desktop.Core;

/// <summary>Offline native provider cards and facets. Shell owns persistence, discovery, dialogs and URI launching.</summary>
internal sealed class ProvidersOverviewPage : UserControl
{
    internal readonly TextBox SearchBox = new() { Name = "ProviderSearch", PlaceholderText = DesktopResources.Get("SearchPlaceholder"), Height = 40, CornerRadius = new CornerRadius(8) };
    internal readonly OverviewCardsPanel Cards = new() { Name = "ProviderCards" };
    internal readonly ProviderOverviewFilter Filter = new();
    internal readonly NavigationView Tabs = new() { Name = "ProviderTabs", PaneDisplayMode = NavigationViewPaneDisplayMode.Top, IsSettingsVisible = false,
        IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed, IsPaneToggleButtonVisible = false, AlwaysShowHeader = false, Height = 56 };
    private readonly NavigationViewItem all = new() { Name = "ProvidersAll", Tag = false, Content = DesktopResources.Get("All") };
    private readonly NavigationViewItem saved = new() { Name = "ProvidersFavorites", Tag = true, Content = DesktopResources.Get("Favorites"), Icon = new FontIcon { Glyph = "\uE734" } };
    private readonly StackPanel body = new() { Spacing = 16, Margin = new Thickness(24), HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock description = new() { Name = "ProvidersDescription", FontSize = 16, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center };
    private readonly TextBlock status = new() { Name = "ProvidersStatus", TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center };
    private readonly InfoBar discoveryNotice = new() { Name = "ProvidersDiscoveryNotice", IsOpen = false, Severity = InfoBarSeverity.Informational };
    private readonly Button more = new() { Name = "ProvidersShowMore", Content = DesktopResources.Get("ShowMore"), HorizontalAlignment = HorizontalAlignment.Center };
    private readonly StackPanel facets = new() { Spacing = 16 };
    private readonly SplitView split = new() { PanePlacement = SplitViewPanePlacement.Right, OpenPaneLength = 320, DisplayMode = SplitViewDisplayMode.Inline };
    private readonly ScrollViewer viewer;
    private ProviderOverviewCatalog catalog = new([]);
    private IReadOnlySet<string> favorites = new HashSet<string>();
    private int visible = 50;
    private bool actionsEnabled = true;
    public Action<CatalogProvider>? FavoriteRequested { get; set; }
    public Action<CatalogProvider, Button>? DetailsRequested { get; set; }
    public Action<Uri>? LinkRequested { get; set; }
    public Action<bool>? FiltersOpenChanged { get; set; }
    public bool FiltersOpen { get => split.IsPaneOpen; set => split.IsPaneOpen = value; }
    public ProvidersOverviewPage()
    {
        var title = new TextBlock { Name = "ProvidersTitle", Text = DesktopResources.Get("ArtificialIntelligence"), FontSize = 36,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetHeadingLevel(title, Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level1);
        body.Children.Add(title); body.Children.Add(description); body.Children.Add(SearchBox); body.Children.Add(discoveryNotice);
        Tabs.MenuItems.Add(all); Tabs.MenuItems.Add(saved); Tabs.SelectedItem = all; body.Children.Add(Tabs);
        body.Children.Add(status); body.Children.Add(Cards); body.Children.Add(more);
        viewer = new ScrollViewer { Content = body, HorizontalScrollMode = ScrollMode.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        split.Content = viewer;
        var pane = new StackPanel { Spacing = 16, Padding = new Thickness(16) };
        var header = new Grid(); header.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        header.Children.Add(new TextBlock { Text = DesktopResources.Get("Filters"), FontSize = 20, VerticalAlignment = VerticalAlignment.Center });
        var close = new Button { Content = new SymbolIcon(Symbol.Cancel), Width = 32, Height = 32, Padding = new Thickness(0) };
        ToolbarControls.Subtle(close); ToolbarControls.Label(close, DesktopResources.Get("CloseFilters")); close.Click += (_, _) => FiltersOpen = false;
        Grid.SetColumn(close, 1); header.Children.Add(close); pane.Children.Add(header); pane.Children.Add(facets);
        split.Pane = new ScrollViewer { Content = pane, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Content = split;
        ControlAppearance.Native(SearchBox); ControlAppearance.Native(more); ToolbarControls.Label(SearchBox, DesktopResources.Get("SearchProviders"));
        AutomationProperties.SetLiveSetting(status, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        // Observe the dependency property directly: TextChanged may be deferred
        // while a page is being attached/retargeted inside the shell.
        SearchBox.RegisterPropertyChangedCallback(TextBox.TextProperty, (_, _) => { Filter.Search = SearchBox.Text; Changed(); });
        Tabs.SelectionChanged += (_, _) => { Filter.FavoritesOnly = ReferenceEquals(Tabs.SelectedItem, saved); Changed(); };
        more.Click += (_, _) => { visible += 50; Render(); };
        split.PaneOpened += (_, _) => FiltersOpenChanged?.Invoke(true); split.PaneClosed += (_, _) => FiltersOpenChanged?.Invoke(false);
        viewer.SizeChanged += (_, _) => SizeBody(); SizeChanged += (_, _) => SizeFilters(); Loaded += (_, _) => { SizeBody(); SizeFilters(); };
        Render();
    }
    private void SizeBody()
    {
        var width = viewer.ViewportWidth > 0 ? viewer.ViewportWidth : viewer.ActualWidth;
        if (width > 0) body.Width = Math.Max(0, Math.Min(980, width - 48));
    }
    private void SizeFilters() { split.DisplayMode = ActualWidth >= 1080 ? SplitViewDisplayMode.Inline : SplitViewDisplayMode.Overlay; split.OpenPaneLength = Math.Max(0, Math.Min(320, ActualWidth - 24)); }
    public void SetItems(IEnumerable<CatalogProvider> items, IReadOnlySet<string> savedFavorites, IEnumerable<ChatTarget>? models = null)
    {
        catalog = new(items, models); favorites = savedFavorites;
        discoveryNotice.IsOpen = false;
        Filter.ModelTypes.IntersectWith(catalog.ModelTypes.Values.SelectMany(t => t)); Changed();
    }
    public void SetFavorites(IReadOnlySet<string> value) { favorites = value; Render(); }
    public void Notice(string message) { discoveryNotice.Message = message; discoveryNotice.IsOpen = true; }
    public void Reset()
    {
        Filter.Categories.Clear(); Filter.Countries.Clear(); Filter.Regions.Clear(); Filter.ModelTypes.Clear();
        SearchBox.Text = ""; Tabs.SelectedItem = all; Filter.FavoritesOnly = false; FiltersOpen = false;
        favorites = new HashSet<string>(); catalog = new([]); discoveryNotice.IsOpen = false; Changed();
    }
    public void SetActionsEnabled(bool enabled)
    {
        actionsEnabled = enabled; SearchBox.IsEnabled = Tabs.IsEnabled = more.IsEnabled = enabled;
        foreach (var control in ControlAppearance.Descendants(Cards).OfType<Button>()) control.IsEnabled = enabled;
        foreach (var box in ControlAppearance.Descendants(facets).OfType<CheckBox>()) box.IsEnabled = enabled && (box.IsChecked == true || (int?)box.Tag != 0);
    }
    private void Changed() { visible = 50; Render(); }
    private void Render()
    {
        var focused = XamlRoot is null ? null : FocusManager.GetFocusedElement(XamlRoot) as Control;
        var focusId = focused is null ? null : AutomationProperties.GetAutomationId(focused);
        var result = catalog.Filter(Filter, favorites);
        description.Text = DesktopResources.Format("ProvidersDescription", catalog.Providers.Count.ToString("N0"));
        all.Content = $"{DesktopResources.Get("All")} ({catalog.Providers.Count})";
        saved.Content = $"{DesktopResources.Get("Favorites")} ({catalog.Providers.Count(p => favorites.Contains(p.Id))})";
        facets.Children.Clear();
        Facet("category", "ProviderCategory", Filter.Categories, p => p.Category is { } value ? [value] : []);
        Facet("modelType", "Models", Filter.ModelTypes, p => catalog.Types(p.Id));
        Facet("region", "ProviderInferenceRegions", Filter.Regions, p => p.InferenceRegions);
        Facet("country", "ProviderCountry", Filter.Countries, p => p.ProviderCountry is { } value ? [value] : []);
        Cards.Children.Clear(); foreach (var provider in result.Take(visible)) Cards.Children.Add(BuildCard(provider));
        status.Text = result.Count == 0 ? DesktopResources.Get(Filter.FavoritesOnly && favorites.Count == 0 ? "ProvidersNoFavorites" : "CatalogNoResults") : "";
        status.Visibility = result.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        more.Visibility = result.Count > visible ? Visibility.Visible : Visibility.Collapsed;
        SetActionsEnabled(actionsEnabled);
        if (focused is not null && !focused.IsLoaded && !string.IsNullOrWhiteSpace(focusId))
            DispatcherQueue.TryEnqueue(() => (ControlAppearance.Descendants(this).OfType<Control>().FirstOrDefault(c => c.IsEnabled && AutomationProperties.GetAutomationId(c) == focusId) ?? SearchBox).Focus(FocusState.Programmatic));
    }
    private void Facet(string key, string label, HashSet<string> selected, Func<CatalogProvider, IEnumerable<string>> values)
    {
        var body = new StackPanel { Spacing = 6 }; body.Children.Add(new TextBlock { Text = DesktopResources.Get(label), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        var available = catalog.Filter(Filter, favorites, key);
        var all = Choice("Providers_" + key + "_all", $"{DesktopResources.Get("All")} ({available.Count})", selected.Count == 0, available.Count);
        all.Click += (_, _) => { selected.Clear(); Changed(); }; body.Children.Add(all);
        foreach (var value in catalog.Providers.SelectMany(values).Distinct().OrderBy(v => FacetLabel(key, v), StringComparer.CurrentCultureIgnoreCase))
        {
            var count = available.Count(p => values(p).Contains(value) && (key != "modelType" || selected.All(t => catalog.Types(p.Id).Contains(t))));
            var box = Choice("Providers_" + key + "_" + value, $"{FacetLabel(key, value)} ({count})", selected.Contains(value), count);
            box.Click += (_, _) => { if (!selected.Remove(value)) selected.Add(value); Changed(); }; body.Children.Add(box);
        }
        var card = new Border { Child = body, Padding = new Thickness(12) }; NativeCardSurface.Card(card); facets.Children.Add(card);
    }
    private static CheckBox Choice(string id, string label, bool selected, int count)
    {
        var box = new CheckBox { Name = id.Replace("-", "_"), Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap }, IsChecked = selected, Tag = count };
        ControlAppearance.Native(box); ToolbarControls.Label(box, label); AutomationProperties.SetAutomationId(box, id); return box;
    }
    internal static string FacetLabel(string key, string value)
    {
        var resource = key switch { "category" => "ProviderCategory_", "country" => "ProviderCountry_", "region" => "ProviderRegion_", "modelType" => "AiModelType_", _ => "" };
        if (resource.Length == 0) return value;
        // New catalog values must remain browsable before their next translation update.
        try { var label = DesktopResources.Get(resource + value); return string.IsNullOrWhiteSpace(label) ? value : label; }
        catch { return value; }
    }
    private Border BuildCard(CatalogProvider provider)
    {
        var grid = new Grid(); grid.RowDefinitions.Add(new() { Height = GridLength.Auto }); grid.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) }); grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var header = new Grid { Margin = new Thickness(16, 16, 16, 0), ColumnSpacing = 12 };
        header.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); header.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); header.Children.Add(new ProviderLogo(provider));
        var labels = new StackPanel { Spacing = 6 };
        labels.Children.Add(new TextBlock { Name = "ProviderName", Text = provider.Name, FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        var metadata = new NativeWrapPanel { Spacing = 6 };
        if (provider.Category is { } category) metadata.Children.Add(Badge(FacetLabel("category", category)));
        if (provider.Experimental) metadata.Children.Add(Badge(DesktopResources.Get("ProviderExperimental")));
        foreach (var type in catalog.Types(provider.Id)) metadata.Children.Add(Badge(FacetLabel("modelType", type)));
        labels.Children.Add(metadata);
        var country = provider.ProviderCountry?.Trim().ToUpperInvariant();
        if (country is { Length: 2 } && country.All(c => c is >= 'A' and <= 'Z')
            && Enum.TryParse<LipisFlag>(country, out var flag) && Enum.IsDefined(flag) && flag != LipisFlag.None)
        {
            header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            var origin = new FlagIcon { Name = "ProviderCountryFlag", Lipis = flag, Width = 24, Height = 18,
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
            var label = $"{DesktopResources.Get("ProviderCountry")}: {FacetLabel("country", country)}";
            ToolTipService.SetToolTip(origin, label); AutomationProperties.SetName(origin, label);
            Grid.SetColumn(origin, 2); header.Children.Add(origin);
        }
        Grid.SetColumn(labels, 1); header.Children.Add(labels); grid.Children.Add(header);
        var text = new TextBlock { Name = "ProviderDescription", Text = provider.Description ?? provider.Id, FontSize = 13, TextWrapping = TextWrapping.Wrap, MaxLines = 3,
            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(16, 18, 16, 16), MinHeight = 40 };
        ToolTipService.SetToolTip(text, provider.Description); NativeCardSurface.Secondary(text); Grid.SetRow(text, 1); grid.Children.Add(text);
        var actions = new NativeWrapPanel { Spacing = 4 };
        var details = ActionButton(provider, "Details", "\uE890", "View"); details.Click += (_, _) => DetailsRequested?.Invoke(provider, details); actions.Children.Add(details);
        foreach (var link in ProviderCatalog.Links(provider)) { var button = ActionButton(provider, link.ResourceKey, link.Glyph, link.Key); button.Click += (_, _) => LinkRequested?.Invoke(link.Uri); actions.Children.Add(button); }
        var isFavorite = favorites.Contains(provider.Id);
        var favorite = ActionButton(provider, isFavorite ? "RemoveFavorite" : "AddFavorite", isFavorite ? "\uE735" : "\uE734", "Favorite");
        favorite.Name = "ProviderFavorite"; favorite.Click += (_, _) => FavoriteRequested?.Invoke(provider); actions.Children.Add(favorite);
        var footer = new Border { Child = actions, Padding = new Thickness(12, 8, 12, 8), BorderThickness = new Thickness(0, 1, 0, 0) }; NativeCardSurface.Divider(footer); Grid.SetRow(footer, 2); grid.Children.Add(footer);
        var card = new Border { Name = "ProviderCard", Tag = provider, Child = grid }; NativeCardSurface.Card(card); AutomationProperties.SetName(card, provider.Name); return card;
    }
    internal static Border Badge(string text)
    {
        var badge = new Border { Child = new TextBlock { Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap }, VerticalAlignment = VerticalAlignment.Top };
        NativeCardSurface.Badge(badge); return badge;
    }
    private static Button ActionButton(CatalogProvider provider, string resource, string glyph, string action)
    {
        var button = new Button { Name = "ProviderAction", Content = new FontIcon { Glyph = glyph, FontSize = 18 }, Width = 36, Height = 36, Padding = new Thickness(0) };
        NativeCardSurface.Action(button); ToolbarControls.Label(button, DesktopResources.Format("ActionForItem", DesktopResources.Get(resource), provider.Name));
        AutomationProperties.SetAutomationId(button, provider.Id + ":" + action); return button;
    }
}

/// <summary>Content-sized native wrapping, used by badges and actions so narrow cards never scroll horizontally.</summary>
internal sealed class NativeWrapPanel : Panel
{
    public double Spacing { get; set; } = 4;
    protected override Windows.Foundation.Size MeasureOverride(Windows.Foundation.Size available)
    {
        var width = double.IsFinite(available.Width) ? available.Width : 980; double x = 0, y = 0, row = 0;
        foreach (var child in Children) { child.Measure(new(width, double.PositiveInfinity)); var size = child.DesiredSize;
            if (x > 0 && x + size.Width > width) { y += row + Spacing; x = row = 0; } x += size.Width + Spacing; row = Math.Max(row, size.Height); }
        return new(width, y + row);
    }
    protected override Windows.Foundation.Size ArrangeOverride(Windows.Foundation.Size final)
    {
        double x = 0, y = 0, row = 0;
        foreach (var child in Children) { var size = child.DesiredSize; if (x > 0 && x + size.Width > final.Width) { y += row + Spacing; x = row = 0; }
            child.Arrange(new(x, y, Math.Min(size.Width, final.Width), size.Height)); x += size.Width + Spacing; row = Math.Max(row, size.Height); }
        return final;
    }
}
