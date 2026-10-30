using FluentIcons.Common;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace AIHappey.Desktop.Core;

internal sealed class ProviderDetailsDialog : ContentDialog, IResponsiveDialog
{
    private readonly InfoBar notice = new() { Name = "ProviderDetailsNotice", Severity = InfoBarSeverity.Warning, IsOpen = false };
    private readonly Grid layout = new() { RowSpacing = 12 };
    private readonly NavigationView tabs = new() { Name = "ProviderDetailsTabs", PaneDisplayMode = NavigationViewPaneDisplayMode.Top,
        IsSettingsVisible = false, IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed,
        IsPaneToggleButtonVisible = false, AlwaysShowHeader = false, Height = 56 };
    private readonly ScrollViewer page = new() { Name = "ProviderDetailsScroll", VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Disabled, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly StackPanel general = new() { Spacing = 16 };
    private readonly StackPanel modelsBody = new() { Spacing = 12 };
    private readonly OverviewCardsPanel cards = new() { Name = "ProviderModelCards" };
    private readonly Button more = new() { Name = "ProviderModelsShowMore", Content = DesktopResources.Get("ShowMore"), HorizontalAlignment = HorizontalAlignment.Center };
    private readonly IReadOnlyList<ChatTarget> models;
    private IReadOnlySet<string> favorites;
    private int visible = 50;
    private bool actionsEnabled = true;
    internal bool IsPresented { get; private set; }
    public Action<Uri>? LinkRequested { get; set; }
    public Action<ChatTarget>? CopyRequested { get; set; }
    public Action<ChatTarget>? FavoriteRequested { get; set; }
    public Action<ChatTarget>? LaunchRequested { get; set; }
    public void Notice(string message) { notice.Message = message; notice.IsOpen = true; }

    public ProviderDetailsDialog(CatalogProvider provider, IReadOnlyList<ChatTarget> availableModels, IReadOnlySet<string> savedFavorites)
    {
        models = AiModelCatalog.NewestFirst(availableModels.Where(model => ModelOverviewCatalog.ProviderKey(model) == provider.Id));
        favorites = savedFavorites;
        Title = provider.Name; Name = "ProviderDetails"; CloseButtonText = DesktopResources.Get("Close"); DefaultButton = ContentDialogButton.Close;
        Resources["ContentDialogMaxWidth"] = 920d; Resources["ContentDialogMinWidth"] = 0d;
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        layout.Children.Add(tabs); Grid.SetRow(notice, 1); layout.Children.Add(notice); Grid.SetRow(page, 2); layout.Children.Add(page); Content = layout;

        var header = new Grid { Name = "ProviderDetailsHeader", ColumnSpacing = 16 };
        header.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); header.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        header.Children.Add(new ProviderLogo(provider, 64) { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top });
        var badges = new NativeWrapPanel { Name = "ProviderDetailsMetadata", Spacing = 6 };
        void Badge(string name, string text, string label)
        {
            var badge = ProvidersOverviewPage.Badge(text); badge.Name = name;
            ToolbarControls.Label(badge, label); badges.Children.Add(badge);
        }
        if (provider.Category is { } category) Badge("ProviderDetailCategory", ProvidersOverviewPage.FacetLabel("category", category),
            DesktopResources.Get("ProviderCategory") + ": " + ProvidersOverviewPage.FacetLabel("category", category));
        if (provider.Experimental) Badge("ProviderDetailExperimental", DesktopResources.Get("ProviderExperimental"), DesktopResources.Get("ProviderExperimental"));
        if (provider.ProviderCountry is { } country) Badge("ProviderDetailCountry", ProvidersOverviewPage.FacetLabel("country", country),
            DesktopResources.Get("ProviderCountry") + ": " + ProvidersOverviewPage.FacetLabel("country", country));
        foreach (var region in provider.InferenceRegions) Badge("ProviderDetailRegion", ProvidersOverviewPage.FacetLabel("region", region),
            DesktopResources.Get("ProviderInferenceRegions") + ": " + ProvidersOverviewPage.FacetLabel("region", region));
        Grid.SetColumn(badges, 1); header.Children.Add(badges); general.Children.Add(header);
        general.Children.Add(new TextBlock { Name = "ProviderFullDescription", Text = provider.Description ?? provider.Id, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
        var links = new NativeWrapPanel { Spacing = 8 };
        foreach (var link in ProviderCatalog.Links(provider))
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            content.Children.Add(DesktopIcons.Create(DesktopIcons.ProviderLink(link.Key), 16)); content.Children.Add(new TextBlock { Text = DesktopResources.Get(link.ResourceKey) });
            var button = new Button { Name = "ProviderDetailLink", Content = content, Tag = link };
            ControlAppearance.Native(button); ToolbarControls.Label(button, DesktopResources.Get(link.ResourceKey));
            AutomationProperties.SetAutomationId(button, "ProviderDetails:" + link.Key);
            ToolTipService.SetToolTip(button, link.Uri.AbsoluteUri); button.Click += (_, _) => LinkRequested?.Invoke(link.Uri); links.Children.Add(button);
        }
        general.Children.Add(links);
        NavigationViewItem Tab(string name, string key, string label, Icon? icon = null)
        {
            var item = new NavigationViewItem { Name = name, Tag = key, Content = label };
            if (icon is { } value) item.Icon = DesktopIcons.Create(value);
            AutomationProperties.SetAutomationId(item, name); AutomationProperties.SetName(item, label); tabs.MenuItems.Add(item); return item;
        }
        var generalTab = Tab("ProviderDetailsGeneral", "general", DesktopResources.Get("General"));
        foreach (var group in models.GroupBy(model => model.ModelType ?? "language").OrderBy(group => ProvidersOverviewPage.FacetLabel("modelType", group.Key), StringComparer.CurrentCultureIgnoreCase))
            Tab("ProviderDetailsType_" + group.Key, group.Key, $"{ProvidersOverviewPage.FacetLabel("modelType", group.Key)} ({group.Count()})", DesktopIcons.ModelType(group.Key));
        if (models.Count == 0) Tab("ProviderDetailsModels", "models", DesktopResources.Get("Models"));
        modelsBody.Children.Add(cards); modelsBody.Children.Add(more); ControlAppearance.Native(more);
        more.Click += (_, _) => { visible += 50; RenderModels(); };
        tabs.SelectionChanged += (_, _) =>
        {
            visible = 50;
            if ((tabs.SelectedItem as NavigationViewItem)?.Tag as string == "general") page.Content = general;
            else { page.Content = modelsBody; RenderModels(); }
            page.ChangeView(null, 0, null, true);
        };
        tabs.SelectedItem = generalTab;
        Opened += (_, _) => { IsPresented = true; SizeToRoot(); XamlRoot.Changed += RootChanged; };
        Closed += (_, _) => { IsPresented = false; XamlRoot.Changed -= RootChanged; };
    }

    public void SetFavorites(IReadOnlySet<string> value)
    {
        favorites = value;
        // Update in place: a favorite click must not lose keyboard focus or scroll position.
        foreach (var card in cards.Children.OfType<Border>().Where(card => card.Tag is ChatTarget))
        {
            var model = (ChatTarget)card.Tag;
            var button = ControlAppearance.Descendants(card).OfType<Button>().Single(button => button.Name == "ModelFavorite");
            var saved = favorites.Contains(ModelOverviewCatalog.FavoriteKey(model));
            button.Content = DesktopIcons.Create(Icon.Star, 18, saved ? IconVariant.Filled : IconVariant.Regular);
            ToolbarControls.Label(button, DesktopResources.Format("ActionForItem", DesktopResources.Get(saved ? "RemoveFavorite" : "AddFavorite"), model.Label));
        }
    }

    public void SetActionsEnabled(bool enabled)
    {
        actionsEnabled = enabled; tabs.IsEnabled = more.IsEnabled = enabled;
        foreach (var button in ControlAppearance.Descendants(general).OfType<Button>().Concat(ControlAppearance.Descendants(cards).OfType<Button>())) button.IsEnabled = enabled;
    }

    private void RenderModels()
    {
        var type = (tabs.SelectedItem as NavigationViewItem)?.Tag as string;
        var selected = models.Where(model => (model.ModelType ?? "language") == type).ToArray();
        var focused = XamlRoot is null ? null : FocusManager.GetFocusedElement(XamlRoot) as Control;
        var focusId = focused is null ? null : AutomationProperties.GetAutomationId(focused);
        cards.Children.Clear();
        var factory = new ModelCardFactory { Favorites = favorites, ActionsEnabled = actionsEnabled,
            CopyRequested = model => CopyRequested?.Invoke(model), FavoriteRequested = model => FavoriteRequested?.Invoke(model),
            LaunchRequested = model => LaunchRequested?.Invoke(model), WebsiteRequested = uri => LinkRequested?.Invoke(uri) };
        foreach (var model in selected.Take(visible)) cards.Children.Add(factory.Create(model));
        if (modelsBody.Children.OfType<TextBlock>().FirstOrDefault() is { } previous) modelsBody.Children.Remove(previous);
        if (selected.Length == 0) modelsBody.Children.Insert(0, new TextBlock { Name = "ProviderModelsEmpty", Text = DesktopResources.Get("ProviderNoDiscoveredModels"), TextWrapping = TextWrapping.Wrap });
        more.Visibility = selected.Length > visible ? Visibility.Visible : Visibility.Collapsed;
        if (focused is not null && !string.IsNullOrEmpty(focusId)) DispatcherQueue.TryEnqueue(() =>
            ControlAppearance.Descendants(cards).OfType<Control>().FirstOrDefault(control => control.IsEnabled && AutomationProperties.GetAutomationId(control) == focusId)?.Focus(FocusState.Programmatic));
    }

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
