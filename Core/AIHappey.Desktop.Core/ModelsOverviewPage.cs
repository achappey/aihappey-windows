using FluentIcons.Common;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace AIHappey.Desktop.Core;

/// <summary>Native model cards and faceted browsing. Discovery, persistence and launching belong to ChatShell.</summary>
internal sealed class ModelsOverviewPage : UserControl
{
    internal readonly TextBox SearchBox = new() { Name = "ModelSearch", PlaceholderText = DesktopResources.Get("SearchPlaceholder"), Height = 40, CornerRadius = new CornerRadius(8) };
    internal readonly OverviewCardsPanel Cards = new() { Name = "ModelCards" };
    internal readonly ModelOverviewFilter Filter = new();
    private readonly StackPanel body = new() { Spacing = 16, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(24) };
    private readonly TextBlock description = new() { Name = "ModelsDescription", FontSize = 16, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center };
    private readonly TextBlock status = new() { Name = "ModelsStatus", TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center };
    private readonly ProgressRing loading = new() { Width = 32, Height = 32, HorizontalAlignment = HorizontalAlignment.Center, Visibility = Visibility.Collapsed };
    private readonly Button retry = new() { Name = "ModelsRetry", Content = DesktopResources.Get("Retry"), HorizontalAlignment = HorizontalAlignment.Center, Visibility = Visibility.Collapsed };
    private readonly Button cancel = new() { Name = "ModelsCancel", Content = DesktopResources.Get("Cancel"), HorizontalAlignment = HorizontalAlignment.Center, Visibility = Visibility.Collapsed };
    private readonly Button more = new() { Name = "ModelsShowMore", Content = DesktopResources.Get("ShowMore"), HorizontalAlignment = HorizontalAlignment.Center, Visibility = Visibility.Collapsed };
    private readonly Button providers = new() { Name = "ModelsProviders", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Height = 40 };
    private readonly StackPanel providerChoices = new() { Spacing = 4 };
    internal readonly NavigationView ModelTypes = new() { Name = "ModelsTypes", PaneDisplayMode = NavigationViewPaneDisplayMode.Top,
        IsSettingsVisible = false, IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed,
        IsPaneToggleButtonVisible = false, AlwaysShowHeader = false, Height = 56 };
    private readonly Dictionary<string, NavigationViewItem> typeItems = [];
    private readonly ToggleSwitch contextEnabled = new() { Name = "ModelsContextFilter", Header = DesktopResources.Get("ContextWindow") };
    private readonly ToggleSwitch priceEnabled = new() { Name = "ModelsPriceFilter", Header = DesktopResources.Get("ModelsPricePerMillion") };
    private readonly StackPanel tagChoices = new() { Spacing = 4 };
    private readonly ModelRangeControl contextRange = new("Context", DesktopResources.Get("ContextWindow"));
    private readonly ModelRangeControl inputRange = new("Input", DesktopResources.Get("Input"), 1_000_000);
    private readonly ModelRangeControl outputRange = new("Output", DesktopResources.Get("Output"), 1_000_000);
    private readonly Grid searchRow = new() { ColumnSpacing = 12, RowSpacing = 12, HorizontalAlignment = HorizontalAlignment.Stretch, MaxWidth = 700 };
    private readonly StackPanel providerField = new() { Spacing = 4 };
    private readonly SplitView split = new() { PanePlacement = SplitViewPanePlacement.Right, OpenPaneLength = 320, DisplayMode = SplitViewDisplayMode.Inline };
    private readonly ScrollViewer viewer;
    private ModelOverviewCatalog catalog = new([]);
    private IReadOnlySet<string> favorites = new HashSet<string>();
    private int visible = 50;
    private bool working;
    private bool actionsEnabled = true;
    private bool updating;

    public Action? RetryRequested { get; set; }
    public Action? CancelRequested { get; set; }
    public Action<ChatTarget>? CopyRequested { get; set; }
    public Action<ChatTarget>? FavoriteRequested { get; set; }
    public Action<ChatTarget>? LaunchRequested { get; set; }
    public Action<Uri>? WebsiteRequested { get; set; }
    public Action<bool>? FiltersOpenChanged { get; set; }
    public bool FiltersOpen { get => split.IsPaneOpen; set => split.IsPaneOpen = value; }

    public ModelsOverviewPage()
    {
        var title = new TextBlock { Name = "ModelsTitle", Text = DesktopResources.Get("ArtificialIntelligence"), FontSize = 36,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetHeadingLevel(title, Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level1);
        body.Children.Add(title); body.Children.Add(description);
        searchRow.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        searchRow.ColumnDefinitions.Add(new() { Width = new GridLength(260) });
        searchRow.RowDefinitions.Add(new() { Height = GridLength.Auto }); searchRow.RowDefinitions.Add(new() { Height = GridLength.Auto });
        SearchBox.VerticalAlignment = VerticalAlignment.Bottom;
        providerField.Children.Add(new TextBlock { Text = DesktopResources.Get("Providers") }); providerField.Children.Add(providers);
        Grid.SetColumn(providerField, 1); searchRow.Children.Add(SearchBox); searchRow.Children.Add(providerField); body.Children.Add(searchRow);
        var providerFlyout = new Flyout { Content = new ScrollViewer { Content = providerChoices, MaxHeight = 360, MinWidth = 220,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled } };
        providers.Flyout = providerFlyout;
        providerFlyout.Opening += (_, _) => BuildProviderChoices();
        // Use stock WinUI top navigation, including its selected-item indicator and native overflow menu.
        body.Children.Add(ModelTypes);
        body.Children.Add(loading); body.Children.Add(status); body.Children.Add(retry); body.Children.Add(cancel); body.Children.Add(Cards); body.Children.Add(more);
        viewer = new ScrollViewer { Content = body, HorizontalScrollMode = ScrollMode.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        split.Content = viewer;
        var filterBody = new StackPanel { Spacing = 16, Padding = new Thickness(16) };
        var filterHeader = new Grid();
        filterHeader.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); filterHeader.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        filterHeader.Children.Add(new TextBlock { Text = DesktopResources.Get("Filters"), FontSize = 20, VerticalAlignment = VerticalAlignment.Center });
        var close = new Button { Content = DesktopIcons.Create(Icon.Dismiss), Width = 32, Height = 32, Padding = new Thickness(0) };
        ToolbarControls.Subtle(close); ToolbarControls.Label(close, DesktopResources.Get("CloseFilters"));
        close.Click += (_, _) => FiltersOpen = false;
        Grid.SetColumn(close, 1); filterHeader.Children.Add(close); filterBody.Children.Add(filterHeader);
        var contextBody = new StackPanel { Spacing = 12 }; contextBody.Children.Add(contextEnabled); contextBody.Children.Add(contextRange);
        filterBody.Children.Add(FilterCard(contextBody));
        var priceBody = new StackPanel { Spacing = 12 }; priceBody.Children.Add(priceEnabled); priceBody.Children.Add(inputRange); priceBody.Children.Add(outputRange);
        filterBody.Children.Add(FilterCard(priceBody));
        var tagsBody = new StackPanel { Spacing = 8 }; tagsBody.Children.Add(new TextBlock { Text = DesktopResources.Get("Tags"), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold }); tagsBody.Children.Add(tagChoices);
        filterBody.Children.Add(FilterCard(tagsBody));
        split.Pane = new ScrollViewer { Content = filterBody, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Content = split;
        foreach (var control in new Control[] { SearchBox, providers, retry, cancel, more, contextEnabled, priceEnabled }) ControlAppearance.Native(control);
        ToolbarControls.Label(SearchBox, DesktopResources.Get("SearchModels")); ToolbarControls.Label(providers, DesktopResources.Get("ModelsProviderFilter"));
        ToolbarControls.Label(contextEnabled, DesktopResources.Get("ContextWindow")); ToolbarControls.Label(priceEnabled, DesktopResources.Get("ModelsPricePerMillion"));
        AutomationProperties.SetLiveSetting(status, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        SearchBox.TextChanged += (_, _) => { Filter.Search = SearchBox.Text; Changed(); };
        split.PaneOpened += (_, _) => FiltersOpenChanged?.Invoke(true);
        split.PaneClosed += (_, _) => FiltersOpenChanged?.Invoke(false);
        ModelTypes.SelectionChanged += (_, _) =>
        {
            if (updating || ModelTypes.SelectedItem is not NavigationViewItem { Tag: string type }) return;
            Filter.Type = type; KeepTags(); Changed();
        };
        contextEnabled.Toggled += (_, _) => { if (!updating) { Filter.ContextEnabled = contextEnabled.IsOn; Changed(); } };
        priceEnabled.Toggled += (_, _) => { if (!updating) { Filter.PriceEnabled = priceEnabled.IsOn; Changed(); } };
        contextRange.Changed = (a, b) => { Filter.ContextMin = a; Filter.ContextMax = b; Changed(); };
        inputRange.Changed = (a, b) => { Filter.InputMin = a; Filter.InputMax = b; Changed(); };
        outputRange.Changed = (a, b) => { Filter.OutputMin = a; Filter.OutputMax = b; Changed(); };
        retry.Click += (_, _) => RetryRequested?.Invoke(); cancel.Click += (_, _) => CancelRequested?.Invoke();
        more.Click += (_, _) => { visible += 50; Render(); };
        viewer.SizeChanged += (_, _) => SizeBody(); SizeChanged += (_, _) => SizeFilters();
        Loaded += (_, _) => { SizeFilters(); SizeBody(); };
        ActualThemeChanged += (_, _) => { providers.Flyout.Hide(); Render(); };
        Render();
    }

    private static Border FilterCard(UIElement child)
    { var card = new Border { Child = child, Padding = new Thickness(12) }; NativeCardSurface.Card(card); return card; }

    private void SizeFilters()
    {
        split.DisplayMode = ActualWidth >= 1080 ? SplitViewDisplayMode.Inline : SplitViewDisplayMode.Overlay;
        split.OpenPaneLength = Math.Max(0, Math.Min(320, ActualWidth - 24));
    }
    private void SizeBody()
    {
        var width = viewer.ViewportWidth > 0 ? viewer.ViewportWidth : viewer.ActualWidth;
        if (width <= 0) return;
        body.Width = Math.Max(0, Math.Min(980, width - 48));
        var narrow = body.Width < 600;
        searchRow.ColumnDefinitions[1].Width = new GridLength(narrow ? 0 : 260);
        searchRow.ColumnSpacing = narrow ? 0 : 12;
        Grid.SetColumn(providerField, narrow ? 0 : 1); Grid.SetRow(providerField, narrow ? 1 : 0);
    }

    public void SetItems(IReadOnlyList<ChatTarget> models, IReadOnlySet<string> savedFavorites)
    {
        catalog = new(models); favorites = savedFavorites; working = false; visible = 50;
        Filter.Providers.IntersectWith(catalog.Providers.Select(p => p.Key));
        BuildTypes(); KeepTags(); Render();
    }
    public void SetFavorites(IReadOnlySet<string> value) { favorites = value; Render(); }
    public void Reset()
    {
        working = true; SearchBox.Text = ""; Filter.Type = ""; Filter.Search = ""; Filter.Providers.Clear(); Filter.Tags.Clear();
        Filter.ContextEnabled = Filter.PriceEnabled = false; Filter.ContextMin = Filter.ContextMax = Filter.InputMin = Filter.InputMax = Filter.OutputMin = Filter.OutputMax = null;
        updating = true; contextEnabled.IsOn = priceEnabled.IsOn = false; updating = false;
        catalog = new([]); favorites = new HashSet<string>(); typeItems.Clear();
        updating = true; ModelTypes.MenuItems.Clear(); updating = false;
        providerChoices.Children.Clear(); tagChoices.Children.Clear(); FiltersOpen = false; Loading();
    }
    public new void Loading()
    {
        working = true; Cards.Children.Clear(); status.Text = DesktopResources.Get("Loading"); status.Visibility = cancel.Visibility = loading.Visibility = Visibility.Visible;
        loading.IsActive = true; retry.Visibility = more.Visibility = Visibility.Collapsed; SetControlsEnabled();
    }
    public void Error(string message)
    {
        working = true; Cards.Children.Clear(); status.Text = message; status.Visibility = retry.Visibility = Visibility.Visible;
        cancel.Visibility = more.Visibility = loading.Visibility = Visibility.Collapsed; loading.IsActive = false; retry.IsEnabled = actionsEnabled;
    }
    public void SetActionsEnabled(bool enabled)
    {
        actionsEnabled = enabled; SetControlsEnabled();
        foreach (var button in ControlAppearance.Descendants(Cards).OfType<Button>()) button.IsEnabled = enabled;
    }
    private void SetControlsEnabled()
    {
        var enabled = actionsEnabled && !working;
        SearchBox.IsEnabled = providers.IsEnabled = ModelTypes.IsEnabled = contextEnabled.IsEnabled = priceEnabled.IsEnabled = enabled;
        foreach (var box in ControlAppearance.Descendants(tagChoices).OfType<CheckBox>()) box.IsEnabled = enabled && ((int?)box.Tag is not 0 || box.IsChecked == true);
        contextRange.SetActionsEnabled(enabled && Filter.ContextEnabled);
        inputRange.SetActionsEnabled(enabled && Filter.PriceEnabled); outputRange.SetActionsEnabled(enabled && Filter.PriceEnabled);
        retry.IsEnabled = actionsEnabled; more.IsEnabled = enabled;
    }

    private void BuildTypes()
    {
        updating = true;
        ModelTypes.MenuItems.Clear(); typeItems.Clear();
        var types = catalog.Models.Select(m => m.ModelType ?? "language").Distinct().OrderBy(TypeLabel, StringComparer.CurrentCultureIgnoreCase).ToArray();
        if (!types.Contains(Filter.Type)) Filter.Type = types.FirstOrDefault() ?? "";
        foreach (var type in types)
        {
            var item = new NavigationViewItem { Name = "ModelsType_" + type, Tag = type, Content = TypeLabel(type), Icon = DesktopIcons.Create(TypeIcon(type)) };
            AutomationProperties.SetAutomationId(item, item.Name);
            typeItems.Add(type, item); ModelTypes.MenuItems.Add(item);
        }
        ModelTypes.SelectedItem = typeItems.GetValueOrDefault(Filter.Type);
        updating = false;
    }
    private static string TypeLabel(string type) => DesktopResources.Get("AiModelType_" + type);
    internal static Icon TypeIcon(string? type) => DesktopIcons.ModelType(type);
    private void KeepTags() => Filter.Tags.IntersectWith(catalog.Models.Where(m => m.ModelType == Filter.Type).SelectMany(m => m.Tags));
    private void Changed() { visible = 50; Render(); }

    private void BuildProviderChoices()
    {
        providerChoices.Children.Clear();
        var all = Choice("ModelsProviderAll", DesktopResources.Get("All"), Filter.Providers.Count == 0);
        all.Click += (_, _) => { Filter.Providers.Clear(); BuildProviderChoices(); Changed(); }; providerChoices.Children.Add(all);
        foreach (var provider in catalog.Providers)
        {
            var box = Choice("ModelsProvider_" + provider.Key, $"{provider.Name} ({provider.Count})", Filter.Providers.Contains(provider.Key));
            box.Click += (_, _) =>
            {
                if (!Filter.Providers.Remove(provider.Key)) Filter.Providers.Add(provider.Key);
                all.IsChecked = Filter.Providers.Count == 0; Changed();
            };
            providerChoices.Children.Add(box);
        }
    }
    private static CheckBox Choice(string name, string label, bool selected)
    {
        var box = new CheckBox { Name = name, Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap }, IsChecked = selected };
        ControlAppearance.Native(box); ToolbarControls.Label(box, label); AutomationProperties.SetAutomationId(box, name); return box;
    }

    private void Render()
    {
        if (working) return;
        var focused = XamlRoot is null ? null : FocusManager.GetFocusedElement(XamlRoot) as Control;
        var focusId = focused is null ? null : AutomationProperties.GetAutomationId(focused);
        var result = catalog.Filter(Filter);
        description.Text = DesktopResources.Format("ModelsDescription", catalog.Models.Count.ToString("N0", CultureInfo.CurrentCulture));
        foreach (var (type, item) in typeItems)
        {
            var label = $"{TypeLabel(type)} ({result.TypeCounts.GetValueOrDefault(type)})";
            item.Content = label; AutomationProperties.SetName(item, label);
        }
        providers.Content = new TextBlock { Text = Filter.Providers.Count == 0 ? DesktopResources.Get("All")
            : string.Join(", ", catalog.Providers.Where(p => Filter.Providers.Contains(p.Key)).Select(p => p.Name)), TextTrimming = TextTrimming.CharacterEllipsis };
        ToolTipService.SetToolTip(providers, ((TextBlock)providers.Content).Text);
        contextRange.Update(result.Context, result.Context.Selection(Filter.ContextMin, Filter.ContextMax), Filter.ContextEnabled);
        inputRange.Update(result.Input, result.Input.Selection(Filter.InputMin, Filter.InputMax), Filter.PriceEnabled);
        outputRange.Update(result.Output, result.Output.Selection(Filter.OutputMin, Filter.OutputMax), Filter.PriceEnabled);
        tagChoices.Children.Clear();
        var allTags = Choice("ModelsTagAll", $"{DesktopResources.Get("All")} ({result.AllTagCount})", Filter.Tags.Count == 0);
        allTags.Click += (_, _) => { Filter.Tags.Clear(); Changed(); }; tagChoices.Children.Add(allTags);
        foreach (var (tag, count) in result.TagCounts)
        {
            var box = Choice("ModelsTag_" + tag, $"{tag} ({count})", Filter.Tags.Contains(tag)); box.Tag = count;
            box.Click += (_, _) => { if (!Filter.Tags.Remove(tag)) Filter.Tags.Add(tag); Changed(); }; tagChoices.Children.Add(box);
        }
        Cards.Children.Clear();
        foreach (var model in result.Models.Take(visible)) Cards.Children.Add(BuildCard(model));
        status.Text = catalog.Models.Count == 0 ? DesktopResources.Get("NoModels") : result.Models.Count == 0 ? DesktopResources.Get("CatalogNoResults") : "";
        status.Visibility = result.Models.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        retry.Visibility = cancel.Visibility = loading.Visibility = Visibility.Collapsed; loading.IsActive = false;
        more.Visibility = result.Models.Count > visible ? Visibility.Visible : Visibility.Collapsed;
        SetControlsEnabled();
        if (!string.IsNullOrEmpty(focusId) && focused is not null && !focused.IsLoaded)
            DispatcherQueue.TryEnqueue(() => (ControlAppearance.Descendants(this).OfType<Control>()
                .FirstOrDefault(c => AutomationProperties.GetAutomationId(c) == focusId && c.IsEnabled) ?? SearchBox).Focus(FocusState.Programmatic));
    }

    private Border BuildCard(ChatTarget model)
    {
        var grid = new Grid(); grid.RowDefinitions.Add(new() { Height = GridLength.Auto }); grid.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) }); grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var header = new Grid { Margin = new Thickness(16, 16, 16, 0), ColumnSpacing = 12 };
        header.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); header.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        header.Children.Add(ProviderIcon(model));
        var labels = new StackPanel { Spacing = 6 };
        var name = new TextBlock { Name = "ModelName", Text = model.Label, FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis };
        ToolTipService.SetToolTip(name, model.Label); labels.Children.Add(name);
        // Wrap badges at narrow widths instead of introducing horizontal scrolling.
        var badges = new VariableSizedWrapGrid { Orientation = Orientation.Horizontal, MaximumRowsOrColumns = 3 };
        if (model.ContextWindow is > 0) badges.Children.Add(Badge(ModelOverviewCatalog.CompactNumber(model.ContextWindow.Value), Icon.Folder, DesktopResources.Format("ModelsContextBadge", model.ContextWindow.Value.ToString("N0"))));
        if (model.MaxTokens is > 0) badges.Children.Add(Badge(ModelOverviewCatalog.CompactNumber(model.MaxTokens.Value), Icon.ArrowDownload, DesktopResources.Format("ModelsOutputBadge", model.MaxTokens.Value.ToString("N0"))));
        if (model.Tags.Contains("real-time")) badges.Children.Add(Badge(DesktopResources.Get("AiModelType_audio"), Icon.Headphones, DesktopResources.Get("AiModelType_audio")));
        labels.Children.Add(badges); Grid.SetColumn(labels, 1); header.Children.Add(labels);
        if (ModelOverviewCatalog.IsNew(model, DateTimeOffset.UtcNow))
        { var badge = Badge(DesktopResources.Get("New"), null, DesktopResources.Get("New")); badge.Name = "ModelNew"; Grid.SetColumn(badge, 2); header.Children.Add(badge); }
        grid.Children.Add(header);
        var text = new TextBlock { Name = "ModelDescription", Text = model.Description ?? "", FontSize = 13, TextWrapping = TextWrapping.Wrap, MaxLines = 3, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(16, 18, 16, 16), MinHeight = 36 };
        ToolTipService.SetToolTip(text, model.Description); NativeCardSurface.Secondary(text); Grid.SetRow(text, 1); grid.Children.Add(text);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        var copy = ActionButton(model, "Copy", Icon.Copy); ToolTipService.SetToolTip(copy, ModelOverviewCatalog.DisplayId(model)); copy.Click += (_, _) => CopyRequested?.Invoke(model); actions.Children.Add(copy);
        if (ModelOverviewCatalog.CanLaunch(model))
        { var launch = ActionButton(model, "ModelsLaunch", TypeIcon(model.ModelType)); launch.Name = "ModelLaunch"; launch.Click += (_, _) => LaunchRequested?.Invoke(model); actions.Children.Add(launch); }
        var saved = favorites.Contains(ModelOverviewCatalog.FavoriteKey(model));
        var favorite = ActionButton(model, saved ? "RemoveFavorite" : "AddFavorite", Icon.Star, saved ? IconVariant.Filled : IconVariant.Regular); favorite.Name = "ModelFavorite";
        AutomationProperties.SetAutomationId(favorite, ModelOverviewCatalog.FavoriteKey(model) + ":Favorite"); favorite.Click += (_, _) => FavoriteRequested?.Invoke(model); actions.Children.Add(favorite);
        var footer = new Border { Child = actions, Padding = new Thickness(12, 8, 12, 8), BorderThickness = new Thickness(0, 1, 0, 0) }; NativeCardSurface.Divider(footer); Grid.SetRow(footer, 2); grid.Children.Add(footer);
        var card = new Border { Name = "ModelCard", Tag = model, Child = grid }; NativeCardSurface.Card(card); AutomationProperties.SetName(card, model.Label); return card;
    }
    private Button ActionButton(ChatTarget model, string action, Icon icon, IconVariant variant = IconVariant.Regular)
    {
        var button = new Button { Name = "Model" + action, Content = DesktopIcons.Create(icon, 18, variant), Width = 36, Height = 36, Padding = new Thickness(0), IsEnabled = actionsEnabled };
        NativeCardSurface.Action(button); ToolbarControls.Label(button, DesktopResources.Format("ActionForItem", DesktopResources.Get(action), model.Label));
        AutomationProperties.SetAutomationId(button, ModelOverviewCatalog.FavoriteKey(model) + ":" + action); return button;
    }
    private static Border Badge(string text, Icon? icon, string label)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        if (icon is not null) content.Children.Add(DesktopIcons.Create(icon.Value, 12));
        content.Children.Add(new TextBlock { Text = text, FontSize = 12 });
        var badge = new Border { Child = content, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 6, 4) };
        NativeCardSurface.Badge(badge); ToolbarControls.Label(badge, label); return badge;
    }
    private UIElement ProviderIcon(ChatTarget model)
    {
        var provider = ModelProviders.Get(ModelOverviewCatalog.ProviderKey(model));
        var icon = new ProviderLogo(ProviderCatalog.Get(ModelOverviewCatalog.ProviderKey(model)), 32) { VerticalAlignment = VerticalAlignment.Top };
        if (ModelProviders.SafeWebUri(provider.Homepage) is not { } homepage) { ToolTipService.SetToolTip(icon, provider.Name); return icon; }
        var button = new Button { Name = "ModelProviderWebsite", Content = icon, Width = 36, Height = 36, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Top, IsEnabled = actionsEnabled };
        NativeCardSurface.Action(button); ToolbarControls.Label(button, DesktopResources.Format("ModelsProviderWebsite", provider.Name));
        AutomationProperties.SetAutomationId(button, ModelOverviewCatalog.FavoriteKey(model) + ":Website"); button.Click += (_, _) => WebsiteRequested?.Invoke(homepage); return button;
    }
}

/// <summary>Two native sliders and numeric entries form an accessible range; token prices are shown per million.</summary>
internal sealed class ModelRangeControl : StackPanel
{
    private readonly NumberBox min = new() { SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, Minimum = 0 };
    private readonly NumberBox max = new() { SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, Minimum = 0 };
    private readonly Slider lower = new();
    private readonly Slider upper = new();
    private readonly TextBlock unavailable = new() { Text = DesktopResources.Get("ModelsNoRange"), TextWrapping = TextWrapping.Wrap };
    private readonly double multiplier;
    private bool updating;
    private bool hasValues;
    private bool hasVariation;
    public Action<double, double>? Changed { get; set; }
    public ModelRangeControl(string name, string label, double multiplier = 1)
    {
        this.multiplier = multiplier; Spacing = 6;
        Children.Add(new TextBlock { Text = label, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        min.Name = "Models" + name + "Min"; max.Name = "Models" + name + "Max";
        min.Header = DesktopResources.Get("Min"); max.Header = DesktopResources.Get("Max");
        var boxes = new Grid { ColumnSpacing = 8 }; boxes.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); boxes.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        boxes.Children.Add(min); Grid.SetColumn(max, 1); boxes.Children.Add(max); Children.Add(boxes); Children.Add(lower); Children.Add(upper); Children.Add(unavailable);
        foreach (var control in new Control[] { min, max, lower, upper }) ControlAppearance.Native(control);
        ToolbarControls.Label(min, label + " " + DesktopResources.Get("Min")); ToolbarControls.Label(max, label + " " + DesktopResources.Get("Max"));
        ToolbarControls.Label(lower, label + " " + DesktopResources.Get("Min")); ToolbarControls.Label(upper, label + " " + DesktopResources.Get("Max"));
        min.ValueChanged += (_, _) => Notify(min.Value, max.Value); max.ValueChanged += (_, _) => Notify(min.Value, max.Value);
        lower.ValueChanged += (_, _) => Notify(lower.Value, upper.Value); upper.ValueChanged += (_, _) => Notify(lower.Value, upper.Value);
    }
    private void Notify(double a, double b)
    {
        if (updating || !double.IsFinite(a) || !double.IsFinite(b)) return;
        Changed?.Invoke(a / multiplier, b / multiplier);
    }
    public void SetActionsEnabled(bool enabled)
    {
        min.IsEnabled = max.IsEnabled = enabled && hasValues;
        lower.IsEnabled = upper.IsEnabled = enabled && hasValues && hasVariation;
    }
    public void Update(ModelNumericRange range, ModelNumericRange selected, bool enabled)
    {
        updating = true;
        try
        {
            // Reset bounds before changing types, including a lower catalog maximum.
            min.Minimum = max.Minimum = lower.Minimum = upper.Minimum = 0;
            min.Maximum = max.Maximum = lower.Maximum = upper.Maximum = Math.Max(range.Max * multiplier, 0);
            min.Minimum = max.Minimum = lower.Minimum = upper.Minimum = Math.Max(range.Min * multiplier, 0);
            lower.StepFrequency = upper.StepFrequency = multiplier == 1 ? Math.Max(1, Math.Round((range.Max - range.Min) / 100)) : Math.Max((range.Max - range.Min) * multiplier / 100, 0.0001);
            min.SmallChange = max.SmallChange = lower.StepFrequency;
            min.Value = lower.Value = selected.Min * multiplier; max.Value = upper.Value = selected.Max * multiplier;
            hasValues = range.HasValues; hasVariation = range.Max > range.Min; SetActionsEnabled(enabled);
            unavailable.Visibility = range.HasValues ? Visibility.Collapsed : Visibility.Visible;
        }
        finally { updating = false; }
    }
}
