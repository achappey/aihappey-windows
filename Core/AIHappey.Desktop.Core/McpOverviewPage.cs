using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace AIHappey.Desktop.Core;

/// <summary>MCP-specific actions, using the same native card layout/palette as Agents and Skills.</summary>
internal sealed class McpOverviewPage : UserControl
{
    private readonly StackPanel body = new() { Spacing = 16, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(24) };
    private readonly TextBox search = new() { Name = "McpSearch", PlaceholderText = DesktopResources.Get("SearchPlaceholder"), MaxWidth = 360, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly StackPanel filters = new() { Orientation = Orientation.Horizontal, Spacing = 4 };
    private readonly OverviewCardsPanel cards = new() { Name = "McpCards" };
    private readonly TextBlock status = new() { Name = "McpCatalogStatus", TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center };
    private readonly Button retry = new() { Content = DesktopResources.Get("Retry") };
    private readonly Button more = new() { Content = DesktopResources.Get("ShowMore") };
    private readonly Button add = new() { Name = "McpAddServer", Content = DesktopResources.Get("McpAddServer") };
    private readonly Button manage = new() { Name = "McpManageServers", Content = DesktopResources.Get("McpInstalledServers") };
    private readonly ScrollViewer viewer;
    private McpCatalogResult catalog = new([], []);
    private IReadOnlyList<McpConnectionView> installed = [];
    private string filter = "all";
    private string[] sources = [];
    private bool loading, enabled = true;
    private int visible = 50;
    public Action? RetryRequested { get; set; }
    public Action? AddRequested { get; set; }
    public Action? ManageRequested { get; set; }
    public Action<McpCatalogItem>? InstallRequested { get; set; }
    public Action<McpCatalogItem>? RemoveRequested { get; set; }

    public McpOverviewPage()
    {
        Name = "McpOverview";
        var title = new TextBlock { Name = "McpOverviewTitle", Text = DesktopResources.Get("McpTitle"), FontSize = 36,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold, TextAlignment = TextAlignment.Center };
        AutomationProperties.SetHeadingLevel(title, Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level1);
        body.Children.Add(title);
        body.Children.Add(new TextBlock { Text = DesktopResources.Get("McpDescription"), FontSize = 16, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
        actions.Children.Add(add); actions.Children.Add(manage); body.Children.Add(actions); body.Children.Add(search);
        body.Children.Add(new ScrollViewer { Content = filters, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Enabled, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, MaxHeight = 64 });
        body.Children.Add(status); body.Children.Add(retry); body.Children.Add(cards); body.Children.Add(more);
        viewer = new ScrollViewer { Content = body, HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalContentAlignment = HorizontalAlignment.Stretch };
        Content = viewer;
        foreach (var control in new Control[] { search, add, manage, retry, more }) ControlAppearance.Native(control);
        ToolbarControls.Label(search, DesktopResources.Get("McpSearch"));
        AutomationProperties.SetLiveSetting(status, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        search.TextChanged += (_, _) => { visible = 50; Render(); };
        add.Click += (_, _) => AddRequested?.Invoke(); manage.Click += (_, _) => ManageRequested?.Invoke();
        retry.Click += (_, _) => RetryRequested?.Invoke(); more.Click += (_, _) => { visible += 50; Render(); };
        viewer.SizeChanged += (_, _) => SizeBody(); Loaded += (_, _) => SizeBody(); Render();
    }
    private void SizeBody()
    {
        var width = viewer.ViewportWidth > 0 ? viewer.ViewportWidth : viewer.ActualWidth;
        if (width > 0) body.Width = Math.Max(0, Math.Min(760, width - 48));
    }
    public void Loading() { loading = true; Render(); }
    public void SetCatalog(McpCatalogResult value, IEnumerable<string> urls) { catalog = value; sources = urls.Distinct().ToArray(); loading = false; Render(); }
    public void SetInstalled(IReadOnlyList<McpConnectionView> value) { installed = value; Render(); }
    public void Error(string value) { loading = false; Render(); status.Text = value; status.Visibility = retry.Visibility = Visibility.Visible; }
    public void SetActionsEnabled(bool value)
    {
        enabled = value;
        foreach (var control in ControlAppearance.Descendants(this).OfType<ButtonBase>()) control.IsEnabled = value;
        search.IsEnabled = value && !loading;
    }
    private void Render()
    {
        var focusedId = XamlRoot is null ? null : (Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(XamlRoot) is Button b ? AutomationProperties.GetAutomationId(b) : null);
        cards.Children.Clear(); filters.Children.Clear();
        AddFilter("all", DesktopResources.Get("All")); AddFilter("installed", DesktopResources.Get("McpInstalledServers"));
        foreach (var url in sources)
        { var label = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : DesktopResources.Get("McpRegistry"); AddFilter(url, label); }
        var items = filter == "installed" ? installed.Select(s => s.Server.CatalogItem)
            : catalog.Items.Where(i => filter == "all" || i.RegistryUrl == filter);
        var query = search.Text.Trim();
        var selected = items.Where(i => i.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || i.Description.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        if (!loading) foreach (var item in selected.Take(visible)) cards.Children.Add(Card(item));
        status.Text = loading ? DesktopResources.Get("Loading") : catalog.FailedSources.Count > 0
            ? DesktopResources.Get("McpRegistryPartialFailure") : selected.Length == 0 ? DesktopResources.Get(sources.Length == 0 && filter != "installed" ? "McpNoRegistries" : "McpNoServers") : "";
        status.Visibility = string.IsNullOrEmpty(status.Text) ? Visibility.Collapsed : Visibility.Visible;
        retry.Visibility = !loading && catalog.FailedSources.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        more.Visibility = !loading && selected.Length > visible ? Visibility.Visible : Visibility.Collapsed;
        SetActionsEnabled(enabled);
        if (!string.IsNullOrEmpty(focusedId)) DispatcherQueue.TryEnqueue(() =>
            (ControlAppearance.Descendants(cards).OfType<Button>().FirstOrDefault(b => AutomationProperties.GetAutomationId(b) == focusedId) as Control ?? search).Focus(FocusState.Programmatic));
    }
    private void AddFilter(string id, string label)
    {
        var button = new ToggleButton { Content = label, IsChecked = filter == id, Padding = new Thickness(12, 6, 12, 6), CornerRadius = new CornerRadius(6) };
        ControlAppearance.Native(button); button.Click += (_, _) => { filter = id; visible = 50; Render(); }; filters.Children.Add(button);
    }
    private Border Card(McpCatalogItem item)
    {
        var installedServer = installed.FirstOrDefault(s => s.Server.Id == item.Id);
        var content = new StackPanel { Spacing = 12 };
        var header = new Grid { ColumnSpacing = 12, Margin = new Thickness(16, 16, 16, 0) };
        header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        header.Children.Add(new McpServerIcon(installedServer is null ? item.Icons : McpIcons.ForServer(installedServer, item.Icons), 32));
        var title = new TextBlock { Text = item.Name, FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(title, 1); header.Children.Add(title);
        content.Children.Add(header);
        var labels = new StackPanel { Spacing = 6, Margin = new Thickness(16, 0, 16, 0) };
        if (item.Version is { Length: > 0 }) labels.Children.Add(new TextBlock { Text = item.Version, FontSize = 12 });
        if (installedServer is not null) labels.Children.Add(new TextBlock { Text = DesktopResources.Get("McpState" + installedServer.State), FontSize = 12 });
        content.Children.Add(labels);
        content.Children.Add(new TextBlock { Text = item.Description, TextWrapping = TextWrapping.Wrap, MaxLines = 3,
            TextTrimming = TextTrimming.CharacterEllipsis, MinHeight = 36, FontSize = 13, Margin = new Thickness(16, 0, 16, 16) });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var button = new Button { Name = installedServer is null ? "McpInstall" : "McpRemove", Content = DesktopResources.Get(installedServer is null ? "McpInstall" : "McpRemove") };
        ControlAppearance.Native(button); AutomationProperties.SetAutomationId(button, item.Id + ":" + button.Name);
        ToolbarControls.Label(button, DesktopResources.Format("ActionForItem", button.Content, item.Name));
        button.Click += (_, _) => { if (installedServer is null) InstallRequested?.Invoke(item); else RemoveRequested?.Invoke(item); };
        actions.Children.Add(button);
        if (installedServer is not null)
        { var view = new Button { Content = DesktopResources.Get("McpManage") }; ControlAppearance.Native(view); view.Click += (_, _) => ManageRequested?.Invoke(); actions.Children.Add(view); }
        var footer = new Border { Child = actions, Padding = new Thickness(12, 8, 12, 8), BorderThickness = new Thickness(0, 1, 0, 0) };
        ControlAppearance.Separator(footer); content.Children.Add(footer);
        var card = new Border { Name = "McpServerCard", Child = content, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1) };
        ControlAppearance.Apply(card, (_, _) => { }, palette => { card.Background = new SolidColorBrush(palette.Panel); card.BorderBrush = new SolidColorBrush(palette.Stroke); });
        AutomationProperties.SetName(card, item.Name); return card;
    }
}
