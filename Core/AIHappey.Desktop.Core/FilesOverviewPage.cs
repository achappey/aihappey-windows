using FluentIcons.WinUI;
using FluentIcons.Common;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;

namespace AIHappey.Desktop.Core;

internal sealed class FilesOverviewPage : UserControl
{
    private readonly StackPanel body = new() { Spacing = 16, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(24) };
    private readonly TextBox search = new() { Name = "FilesSearch", PlaceholderText = DesktopResources.Get("SearchPlaceholder"), MaxWidth = 360, Height = 40, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly NavigationView tabs = new() { Name = "FilesFilters", PaneDisplayMode = NavigationViewPaneDisplayMode.Top,
        IsSettingsVisible = false, IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed, IsPaneToggleButtonVisible = false, AlwaysShowHeader = false, Height = 56 };
    private readonly Dictionary<string, NavigationViewItem> filters = [];
    private readonly OverviewCardsPanel cards = new() { Name = "FilesCards" };
    private readonly TextBlock status = new() { Name = "FilesStatus", TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly Button retry = new() { Name = "FilesRetry", Content = DesktopResources.Get("Retry"), HorizontalAlignment = HorizontalAlignment.Center, Visibility = Visibility.Collapsed };
    private readonly Button more = new() { Name = "FilesMore", Content = DesktopResources.Get("ShowMore"), HorizontalAlignment = HorizontalAlignment.Center };
    private readonly ScrollViewer viewer;
    private IReadOnlyList<SharedFileView> items = [];
    private string active = "all";
    private int visible = 50;
    private bool actionsEnabled = true, loading, failed, updating;
    public Action? RetryRequested { get; set; }
    public Action<SharedFileReference>? OpenRequested { get; set; }
    public Action<SharedFileReference>? RemoveRequested { get; set; }
    public Func<bool>? CanDrop { get; set; }
    public Func<DataPackageView, Task<bool>>? DropRequested { get; set; }
    public FilesOverviewPage()
    {
        Name = "FilesOverview";
        var title = new TextBlock { Name = "OverviewTitle", Text = DesktopResources.Get("Files"), FontSize = 36, FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        AutomationProperties.SetHeadingLevel(title, Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level1);
        var description = new TextBlock { Name = "OverviewDescription", Text = DesktopResources.Get("FilesDescription"), FontSize = 16,
            TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center };
        NativeCardSurface.Secondary(description);
        body.Children.Add(title); body.Children.Add(description); body.Children.Add(search); body.Children.Add(tabs);
        body.Children.Add(status); body.Children.Add(retry); body.Children.Add(cards); body.Children.Add(more);
        viewer = new ScrollViewer { Content = body, HorizontalScrollMode = ScrollMode.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        Content = viewer;
        foreach (var (key, resource, icon) in new[] { ("all", "AllCount", Icon.List), ("files", "FilesCount", Icon.Document), ("folders", "FoldersCount", Icon.Folder) })
        {
            var tab = new NavigationViewItem { Name = "FilesFilter", Tag = key, Icon = DesktopIcons.Create(icon), Content = DesktopResources.Format(resource, 0) };
            AutomationProperties.SetAutomationId(tab, "FilesFilter_" + key); filters.Add(key, tab); tabs.MenuItems.Add(tab);
        }
        ControlAppearance.Native(search); ToolbarControls.Label(search, DesktopResources.Get("FilesSearch"));
        foreach (var button in new[] { retry, more }) ControlAppearance.Native(button);
        NativeCardSurface.Secondary(status); AutomationProperties.SetLiveSetting(status, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        search.TextChanged += (_, _) => { visible = 50; Render(); };
        tabs.SelectionChanged += (_, args) =>
        {
            if (updating || args.SelectedItem is not NavigationViewItem { Tag: string key } || active == key) return;
            active = key; visible = 50; Render();
        };
        more.Click += (_, _) => { visible += 50; Render(); };
        retry.Click += (_, _) => RetryRequested?.Invoke();
        viewer.SizeChanged += (_, _) => SizeBody(); Loaded += (_, _) => SizeBody();
        AllowDrop = true;
        AddHandler(DragEnterEvent, new DragEventHandler(DragOverFiles), true);
        AddHandler(DragOverEvent, new DragEventHandler(DragOverFiles), true);
        AddHandler(DropEvent, new DragEventHandler(DropFiles), true);
        Render();
    }
    private void SizeBody()
    {
        var width = viewer.ViewportWidth > 0 ? viewer.ViewportWidth : viewer.ActualWidth;
        if (width > 0) body.Width = Math.Max(0, Math.Min(760, width - 48));
    }
    public void SetItems(IReadOnlyList<SharedFileView> value) { items = value; loading = failed = false; Render(); }
    public void Loading()
    {
        loading = true; failed = false; cards.Children.Clear(); status.Text = DesktopResources.Get("Loading"); status.Visibility = Visibility.Visible;
        retry.Visibility = more.Visibility = Visibility.Collapsed; SetActionsEnabled(actionsEnabled);
    }
    public void Error(string message)
    {
        loading = false; failed = true; cards.Children.Clear(); status.Text = message; status.Visibility = retry.Visibility = Visibility.Visible;
        more.Visibility = Visibility.Collapsed; SetActionsEnabled(actionsEnabled);
    }
    public void SetActionsEnabled(bool enabled)
    {
        actionsEnabled = enabled; search.IsEnabled = tabs.IsEnabled = enabled && !loading && !failed;
        retry.IsEnabled = more.IsEnabled = enabled;
        foreach (var button in ControlAppearance.Descendants(cards).OfType<Button>())
            button.IsEnabled = enabled && (button.Name != "FilesOpen" || button.Tag is SharedFileView { Available: true });
    }
    private void Render()
    {
        if (loading || failed) return;
        var focused = XamlRoot is null ? null : FocusManager.GetFocusedElement(XamlRoot) as Button;
        var focusId = focused is null ? null : AutomationProperties.GetAutomationId(focused);
        var query = search.Text.Trim();
        var searched = items.Where(v => query.Length == 0 || (v.Reference.Name + " " + v.Reference.Path).Contains(query, StringComparison.CurrentCultureIgnoreCase))
            .OrderBy(v => v.Reference.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(v => v.Reference.Path, StringComparer.OrdinalIgnoreCase).ToArray();
        updating = true;
        foreach (var (key, resource, count) in new[] { ("all", "AllCount", searched.Length), ("files", "FilesCount", searched.Count(v => !v.Reference.IsFolder)), ("folders", "FoldersCount", searched.Count(v => v.Reference.IsFolder)) })
        { filters[key].Content = DesktopResources.Format(resource, count); ToolbarControls.Label(filters[key], filters[key].Content.ToString()!); }
        tabs.SelectedItem = filters[active]; updating = false;
        var selected = searched.Where(v => active == "all" || v.Reference.IsFolder == (active == "folders")).ToArray();
        cards.Children.Clear(); foreach (var view in selected.Take(visible)) cards.Children.Add(Card(view));
        status.Text = items.Count == 0 ? DesktopResources.Get("FilesEmpty") : DesktopResources.Get("CatalogNoResults");
        status.Visibility = selected.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        retry.Visibility = Visibility.Collapsed; more.Visibility = selected.Length > visible ? Visibility.Visible : Visibility.Collapsed;
        SetActionsEnabled(actionsEnabled);
        if (!string.IsNullOrEmpty(focusId) && focused is not null && !focused.IsLoaded)
            DispatcherQueue.TryEnqueue(() => (ControlAppearance.Descendants(cards).OfType<Button>().FirstOrDefault(b => AutomationProperties.GetAutomationId(b) == focusId) as Control ?? search).Focus(FocusState.Programmatic));
    }
    private Border Card(SharedFileView view)
    {
        var item = view.Reference; var grid = new Grid();
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto }); grid.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) }); grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var header = new Grid { Margin = new Thickness(16, 16, 16, 0), ColumnSpacing = 12 };
        header.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); header.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        header.Children.Add(new FluentIcon { Icon = item.IsFolder ? Icon.Folder : Icon.Document, FontSize = 24, Width = 32, VerticalAlignment = VerticalAlignment.Top });
        var labels = new StackPanel { Spacing = 6 };
        labels.Children.Add(new TextBlock { Text = item.Name, FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, MaxLines = 2, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis });
        var badge = new Border { HorizontalAlignment = HorizontalAlignment.Left, Child = new TextBlock { Text = DesktopResources.Get(item.IsFolder ? "FilesFolder" : "FilesFile"), FontSize = 12 } };
        NativeCardSurface.Badge(badge); labels.Children.Add(badge); Grid.SetColumn(labels, 1); header.Children.Add(labels); grid.Children.Add(header);
        var detail = new StackPanel { Spacing = 8, Margin = new Thickness(16, 18, 16, 16) };
        var path = new TextBlock { Text = item.Path, TextWrapping = TextWrapping.Wrap, MaxLines = 3, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 13 };
        NativeCardSurface.Secondary(path); ToolTipService.SetToolTip(path, item.Path); detail.Children.Add(path);
        var availability = new TextBlock { Name = "FilesAvailability", Text = DesktopResources.Get("FilesStatus_" + view.Status), TextWrapping = TextWrapping.Wrap, FontSize = 13 };
        NativeCardSurface.Secondary(availability); detail.Children.Add(availability);
        if (view.Size is { } size) { var sizeText = new TextBlock { Text = DesktopResources.Format("FilesSize", size), FontSize = 12 }; NativeCardSurface.Secondary(sizeText); detail.Children.Add(sizeText); }
        Grid.SetRow(detail, 1); grid.Children.Add(detail);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        foreach (var (name, label, icon) in new[] { ("FilesOpen", "FilesOpen", Icon.Open), ("FilesRemove", "FilesRemove", Icon.Dismiss) })
        {
            var button = new Button { Name = name, Tag = view, Content = DesktopIcons.Create(icon, 18), Width = 36, Height = 36, Padding = new Thickness(0) };
            NativeCardSurface.Action(button); ToolbarControls.Label(button, DesktopResources.Format("ActionForItem", DesktopResources.Get(label), item.Name));
            AutomationProperties.SetAutomationId(button, item.Id + ":" + name);
            button.Click += (_, _) => { if (name == "FilesOpen") OpenRequested?.Invoke(item); else RemoveRequested?.Invoke(item); }; actions.Children.Add(button);
        }
        var footer = new Border { Child = actions, Padding = new Thickness(12, 8, 12, 8), BorderThickness = new Thickness(0, 1, 0, 0) };
        NativeCardSurface.Divider(footer); Grid.SetRow(footer, 2); grid.Children.Add(footer);
        var card = new Border { Name = "FilesCard", Tag = view, Child = grid }; NativeCardSurface.Card(card); AutomationProperties.SetName(card, item.Name); return card;
    }
    private void DragOverFiles(object sender, DragEventArgs args)
    {
        args.Handled = true;
        var accepts = actionsEnabled && !loading && CanDrop?.Invoke() == true && args.DataView.Contains(StandardDataFormats.StorageItems)
            && args.AllowedOperations.HasFlag(DataPackageOperation.Copy);
        args.AcceptedOperation = accepts ? DataPackageOperation.Copy : DataPackageOperation.None;
        args.DragUIOverride.Caption = accepts ? DesktopResources.Get("FilesDropCaption") : ""; args.DragUIOverride.IsCaptionVisible = accepts;
    }
    private async void DropFiles(object sender, DragEventArgs args)
    {
        args.Handled = true; args.AcceptedOperation = DataPackageOperation.None;
        if (!actionsEnabled || CanDrop?.Invoke() != true || DropRequested is null || !args.DataView.Contains(StandardDataFormats.StorageItems)
            || !args.AllowedOperations.HasFlag(DataPackageOperation.Copy)) return;
        var deferral = args.GetDeferral();
        try { if (await DropRequested(args.DataView)) args.AcceptedOperation = DataPackageOperation.Copy; }
        finally { deferral.Complete(); }
    }
}
