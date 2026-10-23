using FluentIcons.Common;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace AIHappey.Desktop.Core;

public sealed class PluginsOverviewPage : UserControl
{
    private readonly StackPanel rows = new() { Spacing = 16 };
    private readonly AutoSuggestBox search = new() { Name = "PluginSearch", PlaceholderText = DesktopResources.Get("Search"), HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Button keywords = new() { Content = DesktopResources.Get("PluginKeywords") }, authors = new() { Content = DesktopResources.Get("PluginAuthor") };
    private readonly NavigationView tabs = new() { Name = "PluginFilters", PaneDisplayMode = NavigationViewPaneDisplayMode.Top,
        IsSettingsVisible = false, IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed,
        IsPaneToggleButtonVisible = false, AlwaysShowHeader = false, Height = 56 };
    private readonly NavigationViewItem all = new() { Tag = "all" }, favorites = new() { Tag = "favorites" };
    private IReadOnlyList<DesktopAgentPlugin> items = [];
    private readonly HashSet<string> selectedKeywords = new(StringComparer.OrdinalIgnoreCase), selectedAuthors = new(StringComparer.OrdinalIgnoreCase);
    private bool favoriteOnly, enabled = true;
    public Func<DesktopAgentPlugin, Task>? DetailsRequested { get; set; }
    public Func<DesktopAgentPlugin, Task>? DownloadRequested { get; set; }
    public Func<DesktopAgentPlugin, Task>? DeleteRequested { get; set; }
    public Func<DesktopAgentPlugin, Task>? FavoriteRequested { get; set; }
    public Func<IReadOnlyList<StorageFile>, Task>? ImportRequested { get; set; }
    public PluginsOverviewPage()
    {
        Name = "PluginsOverview";
        var panel = new StackPanel { Name = "PluginsBody", Spacing = 16, MaxWidth = 900, Margin = new Thickness(24), HorizontalAlignment = HorizontalAlignment.Center };
        panel.Children.Add(new OverviewPageHeader(new TextBlock { Text = DesktopResources.Get("PluginsTitle"), FontSize = 36,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold }, "https://agent-plugins.org/", "https://agent-plugins.org/specification"));
        panel.Children.Add(new TextBlock { Text = DesktopResources.Get("PluginsDescription"), FontSize = 16, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center });
        var filters = new Grid { Name = "PluginSearchFilters", ColumnSpacing = 16, RowSpacing = 8 };
        filters.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        filters.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        filters.RowDefinitions.Add(new() { Height = GridLength.Auto }); filters.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var facets = new StackPanel { Name = "PluginFacets", Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        facets.Children.Add(keywords); facets.Children.Add(authors); filters.Children.Add(search); Grid.SetColumn(facets, 1); filters.Children.Add(facets); panel.Children.Add(filters);
        filters.SizeChanged += (_, _) =>
        {
            var compact = filters.ActualWidth < 560;
            Grid.SetColumnSpan(search, compact ? 2 : 1); Grid.SetRow(facets, compact ? 1 : 0);
        };
        AutomationProperties.SetAutomationId(all, "PluginFilter_all"); AutomationProperties.SetAutomationId(favorites, "PluginFilter_favorites");
        tabs.MenuItems.Add(all); tabs.MenuItems.Add(favorites); tabs.SelectedItem = all;
        panel.Children.Add(tabs); panel.Children.Add(rows);
        var viewer = new ScrollViewer { Content = panel, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Disabled, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        Content = viewer;
        void SizeBody()
        {
            var width = viewer.ViewportWidth > 0 ? viewer.ViewportWidth : viewer.ActualWidth;
            if (width > 0) panel.Width = Math.Max(0, Math.Min(900, width - 48));
        }
        viewer.SizeChanged += (_, _) => SizeBody();
        Loaded += (_, _) => SizeBody();
        foreach (var control in new Control[] { search, keywords, authors }) ControlAppearance.Stock(control);
        AutomationProperties.SetName(search, DesktopResources.Get("Search"));
        search.TextChanged += (_, _) => Render();
        tabs.SelectionChanged += (_, args) =>
        {
            var selected = ReferenceEquals(args.SelectedItem, favorites);
            if (favoriteOnly == selected) return;
            favoriteOnly = selected; Render();
        };
        keywords.Click += (_, _) => Facet(keywords, true); authors.Click += (_, _) => Facet(authors, false);
        AllowDrop = true;
        DragOver += (_, args) => { args.AcceptedOperation = enabled && args.DataView.Contains(StandardDataFormats.StorageItems) ? DataPackageOperation.Copy : DataPackageOperation.None; args.Handled = true; };
        Drop += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            try { if (enabled && args.DataView.Contains(StandardDataFormats.StorageItems) && ImportRequested is not null) await ImportRequested((await args.DataView.GetStorageItemsAsync()).OfType<StorageFile>().ToArray()); }
            finally { deferral.Complete(); }
        };
    }
    public void SetItems(IReadOnlyList<DesktopAgentPlugin> value)
    {
        items = value; selectedKeywords.IntersectWith(items.SelectMany(p => p.Keywords)); selectedAuthors.IntersectWith(items.Select(p => p.Author)); Render();
    }
    public void SetActionsEnabled(bool value)
    {
        enabled = value; search.IsEnabled = tabs.IsEnabled = keywords.IsEnabled = authors.IsEnabled = value;
        foreach (var control in rows.Children.SelectMany(ControlAppearance.Descendants).OfType<Button>()) control.IsEnabled = value;
    }
    private bool Matches(DesktopAgentPlugin plugin, string? keyword = null, string? author = null) =>
        (plugin.Id + " " + plugin.Description + " " + string.Join(' ', plugin.Keywords)).Contains(search.Text.Trim(), StringComparison.OrdinalIgnoreCase)
        && (keyword is null ? selectedKeywords.All(k => plugin.Keywords.Contains(k, StringComparer.OrdinalIgnoreCase)) : selectedKeywords.Append(keyword).All(k => plugin.Keywords.Contains(k, StringComparer.OrdinalIgnoreCase)))
        && (author is null ? selectedAuthors.Count == 0 || selectedAuthors.Contains(plugin.Author) : plugin.Author.Equals(author, StringComparison.OrdinalIgnoreCase));
    private void Facet(Button owner, bool keyword)
    {
        var values = (keyword ? items.SelectMany(p => p.Keywords) : items.Select(p => p.Author)).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.CurrentCultureIgnoreCase);
        var selected = keyword ? selectedKeywords : selectedAuthors; var menu = new MenuFlyout();
        var reset = new MenuFlyoutItem { Text = DesktopResources.Get("All") }; reset.Click += (_, _) => { selected.Clear(); Render(); }; menu.Items.Add(reset);
        foreach (var value in values)
        {
            var count = items.Count(p => keyword ? Matches(p, keyword: value) : Matches(p, author: value));
            var item = new ToggleMenuFlyoutItem { Text = $"{value} ({count})", IsChecked = selected.Contains(value), IsEnabled = count > 0 || selected.Contains(value) };
            item.Click += (_, _) => { if (item.IsChecked) selected.Add(value); else selected.Remove(value); Render(); }; menu.Items.Add(item);
        }
        menu.ShowAt(owner);
    }
    private void Render()
    {
        rows.Children.Clear(); var filtered = items.Where(p => Matches(p)).ToArray();
        all.Content = DesktopResources.Get("All") + $" ({filtered.Length})"; favorites.Content = DesktopResources.Get("Favorites") + $" ({filtered.Count(p => p.Favorite)})";
        ToolbarControls.Label(all, (string)all.Content); ToolbarControls.Label(favorites, (string)favorites.Content);
        foreach (var plugin in filtered.Where(p => !favoriteOnly || p.Favorite))
        {
            var body = new StackPanel { Spacing = 10 }; body.Children.Add(new TextBlock { Text = plugin.Id, FontSize = 20, TextWrapping = TextWrapping.Wrap });
            body.Children.Add(new TextBlock { Text = plugin.Description, TextWrapping = TextWrapping.Wrap });
            body.Children.Add(new TextBlock { Text = DesktopResources.Format("PluginCounts", plugin.Skills().Count, (plugin.Mcp?["mcpServers"] as JsonObject)?.Count ?? 0, plugin.Diagnostics.Count), TextWrapping = TextWrapping.Wrap });
            body.Children.Add(new TextBlock { Text = string.Join(" · ", new[] { DesktopAgent.Text(plugin.Manifest["version"]), plugin.Author, string.Join(", ", plugin.Keywords) }.Where(v => v.Length > 0)), TextWrapping = TextWrapping.Wrap });
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            Add(actions, "PluginDetails", () => DetailsRequested?.Invoke(plugin), Icon.Info);
            Add(actions, "Download", () => DownloadRequested?.Invoke(plugin), Icon.ArrowDownload);
            Add(actions, "Delete", () => DeleteRequested?.Invoke(plugin), Icon.Delete);
            Add(actions, plugin.Favorite ? "PluginUnfavorite" : "PluginFavorite", () => FavoriteRequested?.Invoke(plugin), plugin.Favorite ? Icon.Star : Icon.Star);
            body.Children.Add(actions); var card = new Border { Child = body, Padding = new Thickness(20), CornerRadius = new CornerRadius(8), Tag = plugin.Id }; NativeCardSurface.Card(card); rows.Children.Add(card);
        }
        if (rows.Children.Count == 0) rows.Children.Add(new TextBlock { Text = DesktopResources.Get("NoResults"), HorizontalAlignment = HorizontalAlignment.Center });
    }
    private void Add(StackPanel parent, string key, Func<Task?> action, Icon icon)
    {
        var button = new Button { Content = DesktopIcons.Create(icon), IsEnabled = enabled }; NativeCardSurface.Action(button); ToolbarControls.Label(button, DesktopResources.Get(key));
        button.Click += async (_, _) => { if (action() is { } task) await task; }; parent.Children.Add(button);
    }
}

public sealed class PluginsSettingsView : StackPanel
{
    public PluginsSettingsView(IEnumerable<DesktopAgentPlugin> plugins, List<string> selected)
    {
        Spacing = 12; Name = "PluginsSettingsView"; var rows = new StackPanel { Spacing = 12 };
        var search = new AutoSuggestBox { PlaceholderText = DesktopResources.Get("Search") }; ControlAppearance.Stock(search); Children.Add(search); Children.Add(rows);
        var items = plugins.ToArray();
        void Render()
        {
            rows.Children.Clear();
            foreach (var plugin in items.Where(p => (p.Id + " " + p.Description).Contains(search.Text, StringComparison.OrdinalIgnoreCase)))
            {
                var toggle = new ToggleSwitch { Name = "ChatPluginToggle", Header = plugin.Id, Tag = plugin.Id, IsOn = selected.Contains(plugin.Id), OnContent = "", OffContent = "" };
                ControlAppearance.Stock(toggle); AutomationProperties.SetName(toggle, plugin.Id);
                toggle.Toggled += (_, _) => { selected.RemoveAll(id => id == plugin.Id); if (toggle.IsOn) selected.Add(plugin.Id); };
                var panel = new StackPanel { Spacing = 8 }; panel.Children.Add(toggle); panel.Children.Add(new TextBlock { Text = plugin.Description, TextWrapping = TextWrapping.Wrap });
                var card = new Border { Child = panel, Padding = new Thickness(16) }; NativeCardSurface.Card(card); rows.Children.Add(card);
            }
            foreach (var id in selected.Where(id => !items.Any(p => p.Id == id)).ToArray())
            { var remove = new Button { Content = id + " · " + DesktopResources.Get("PluginUnavailable"), Tag = id }; ControlAppearance.Stock(remove); remove.Click += (_, _) => { selected.Remove(id); Render(); }; rows.Children.Add(remove); }
        }
        search.TextChanged += (_, _) => Render(); Render();
    }
}

public sealed class PluginEditDialog : ContentDialog, IResponsiveDialog
{
    private readonly DesktopAgentPlugin draft;
    private readonly Grid layout = new() { RowSpacing = 12 };
    private readonly NavigationView tabs = new() { PaneDisplayMode = NavigationViewPaneDisplayMode.Top, IsSettingsVisible = false, IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed, IsPaneToggleButtonVisible = false, AlwaysShowHeader = false, Height = 56 };
    private readonly ScrollViewer page = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Disabled };
    private readonly TextBlock error = new() { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    private readonly Dictionary<string, FrameworkElement> pages = [];
    private readonly CancellationTokenSource lifetime = new();
    private readonly bool readOnly;
    private readonly string extensionNamespace;
    private readonly StackPanel skillRows = new() { Spacing = 12 }, serverRows = new() { Spacing = 12 }, fileRows = new() { Spacing = 12 };
    private bool working;
    public DesktopAgentPlugin? Result { get; private set; }
    public Func<DesktopAgentPlugin, CancellationToken, Task>? SaveAsync { get; set; }
    public PluginEditDialog(DesktopAgentPlugin? plugin, bool readOnly, string extensionNamespace,
        IReadOnlyList<DesktopSkill> skills, Func<string, CancellationToken, Task<IReadOnlyList<DesktopSkillFile>>> snapshot,
        IReadOnlyList<McpCatalogItem> catalog, string? authorName = null, string? authorEmail = null)
    {
        draft = plugin?.Clone() ?? new() { Manifest = new() { ["$schema"] = AgentPluginPackages.Schema, ["name"] = "" } }; this.readOnly = readOnly; this.extensionNamespace = extensionNamespace;
        Name = readOnly ? "PluginDetailsDialog" : "PluginEditDialog"; Title = plugin?.Id ?? DesktopResources.Get("PluginCreate");
        CloseButtonText = DesktopResources.Get(readOnly ? "Close" : "Cancel"); if (!readOnly) PrimaryButtonText = DesktopResources.Get("Save");
        Resources["ContentDialogMaxWidth"] = 920d; Resources["ContentDialogMinWidth"] = 0d;
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto }); layout.RowDefinitions.Add(new() { Height = GridLength.Auto }); layout.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        layout.Children.Add(tabs); Grid.SetRow(error, 1); layout.Children.Add(error); Grid.SetRow(page, 2); layout.Children.Add(page); Content = layout;
        var general = Panel();
        Field(general, "name", "Name", plugin is not null); foreach (var (key, label) in new[] { ("version", "PluginVersion"), ("description", "Description"), ("homepage", "PluginHomepage"), ("repository", "PluginRepository"), ("license", "PluginLicense") }) Field(general, key, label);
        Tab("general", DesktopResources.Get("General"), general);
        var author = Panel(); var authorObject = draft.Manifest["author"] as JsonObject;
        if (plugin is null && authorName is { Length: > 0 }) draft.Manifest["author"] = authorObject = new() { ["name"] = authorName, ["email"] = authorEmail ?? "" };
        foreach (var (key, label) in new[] { ("name", "PluginAuthorName"), ("email", "PluginAuthorEmail"), ("url", "PluginAuthorUrl") })
        {
            var input = new TextBox { Header = DesktopResources.Get(label), Text = DesktopAgent.Text(authorObject?[key]), IsReadOnly = readOnly || authorName is { Length: > 0 } }; ControlAppearance.Stock(input); author.Children.Add(input);
            input.TextChanged += (_, _) => { var obj = draft.Manifest["author"] as JsonObject; if (obj is null) draft.Manifest["author"] = obj = new(); obj[key] = input.Text; };
        }
        Tab("author", DesktopResources.Get("PluginAuthor"), author);
        var keywordPanel = Panel(); var keywordInput = new TextBox { Header = DesktopResources.Get("PluginKeywords"), Text = string.Join(", ", draft.Keywords), IsReadOnly = readOnly };
        ControlAppearance.Stock(keywordInput); keywordPanel.Children.Add(keywordInput); keywordInput.TextChanged += (_, _) => draft.Manifest["keywords"] = JsonSerializer.SerializeToNode(keywordInput.Text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct().ToArray()); Tab("keywords", DesktopResources.Get("PluginKeywords"), keywordPanel);
        var skillPanel = Panel(); var skillSearch = new AutoSuggestBox { PlaceholderText = DesktopResources.Get("Search") }; ControlAppearance.Stock(skillSearch); skillPanel.Children.Add(skillSearch); skillPanel.Children.Add(skillRows);
        void Skills()
        {
            skillRows.Children.Clear(); var embedded = draft.Skills();
            foreach (var descriptor in skills.Concat(embedded.Select(s => s.Descriptor)).DistinctBy(s => s.Name).Where(s => (s.Name + " " + s.Description).Contains(skillSearch.Text, StringComparison.OrdinalIgnoreCase)))
            {
                var toggle = new ToggleSwitch { Header = descriptor.Name, IsOn = embedded.Any(s => s.Directory == descriptor.Name), IsEnabled = !readOnly && !working, OnContent = "", OffContent = "" }; ControlAppearance.Stock(toggle); skillRows.Children.Add(toggle);
                skillRows.Children.Add(new TextBlock { Text = descriptor.Description, TextWrapping = TextWrapping.Wrap });
                toggle.Toggled += async (_, _) => await Work(async ct =>
                {
                    if (toggle.IsOn) { var additions = await snapshot(descriptor.Id, ct); ct.ThrowIfCancellationRequested(); draft.Files.RemoveAll(f => f.Path.StartsWith("skills/" + descriptor.Name + "/", StringComparison.Ordinal)); draft.Files.AddRange(additions); }
                    else draft.Files.RemoveAll(f => f.Path.StartsWith("skills/" + descriptor.Name + "/", StringComparison.Ordinal));
                    Skills(); Files();
                });
            }
        }
        skillSearch.TextChanged += (_, _) => Skills(); Skills(); Tab("skills", DesktopResources.Get("Skills"), skillPanel);
        var mcp = Panel(); mcp.Children.Add(new TextBlock { Text = DesktopResources.Get("PluginTransportHint"), TextWrapping = TextWrapping.Wrap });
        if (!readOnly)
        {
            var catalogSearch = new AutoSuggestBox { Header = DesktopResources.Get("PluginMcpCatalog"), PlaceholderText = DesktopResources.Get("Search"), DisplayMemberPath = "Name", TextMemberPath = "Name", ItemsSource = catalog.Take(100).ToArray() };
            ControlAppearance.Stock(catalogSearch); mcp.Children.Add(catalogSearch);
            catalogSearch.TextChanged += (_, _) => { catalogSearch.ItemsSource = catalog.Where(i => (i.Name + " " + i.Description).Contains(catalogSearch.Text, StringComparison.OrdinalIgnoreCase)).Take(100).ToArray(); };
            catalogSearch.SuggestionChosen += (_, args) => { if (args.SelectedItem is McpCatalogItem item) { ServersObject()[item.Name] = new JsonObject { ["type"] = "streamable-http", ["url"] = item.Url }; Servers(); } };
            var serverName = new TextBox { Header = DesktopResources.Get("Name") }; var serverUrl = new TextBox { Header = DesktopResources.Get("PluginEndpoint") }; ControlAppearance.Stock(serverName); ControlAppearance.Stock(serverUrl); mcp.Children.Add(serverName); mcp.Children.Add(serverUrl);
            var add = Button("Add", () => Work(_ => { if (serverName.Text.Length == 0) throw new InvalidDataException("Server name required."); ServersObject()[serverName.Text] = AgentPluginPackages.Server(serverName.Text, new JsonObject { ["type"] = "streamable-http", ["url"] = serverUrl.Text }); Servers(); return Task.CompletedTask; })); mcp.Children.Add(add);
            var bulk = Panel(); bulk.Children.Add(new TextBlock { Text = DesktopResources.Get("PluginAllServers") }); SettingsControls(bulk, null); mcp.Children.Add(bulk);
        }
        mcp.Children.Add(serverRows); Servers(); Tab("mcp", DesktopResources.Get("McpTitle"), mcp);
        var filesPanel = Panel(); filesPanel.Children.Add(new TextBlock { Text = DesktopResources.Get("PluginFilesHint"), TextWrapping = TextWrapping.Wrap });
        if (!readOnly) filesPanel.Children.Add(Button("PluginAddFiles", () => Work(async ct =>
        {
            var picker = new FileOpenPicker(); picker.FileTypeFilter.Add("*"); Initialize(picker); var files = await picker.PickMultipleFilesAsync().AsTask(ct); await AddFiles(files, ct); Files();
        })));
        filesPanel.Children.Add(fileRows); filesPanel.AllowDrop = !readOnly;
        filesPanel.DragOver += (_, args) => args.AcceptedOperation = !working && args.DataView.Contains(StandardDataFormats.StorageItems) ? DataPackageOperation.Copy : DataPackageOperation.None;
        filesPanel.Drop += async (_, args) => { var deferral = args.GetDeferral(); try { await Work(async ct => { await AddFiles((await args.DataView.GetStorageItemsAsync()).OfType<StorageFile>(), ct); Files(); }); } finally { deferral.Complete(); } };
        Files(); Tab("files", DesktopResources.Get("Files"), filesPanel);
        var diagnostics = Panel(); foreach (var item in draft.Diagnostics) diagnostics.Children.Add(new TextBlock { Text = $"{item.Severity} · {item.Boundary} · {item.Path} {item.Entry}\n{item.Message}", TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true }); Tab("diagnostics", DesktopResources.Get("PluginDiagnostics"), diagnostics);
        tabs.SelectionChanged += (_, args) => { if (args.SelectedItem is NavigationViewItem { Tag: string key }) { page.Content = pages[key]; page.ChangeView(null, 0, null, true); } };
        tabs.SelectedItem = tabs.MenuItems[0]; page.Content = general;
        if (!readOnly) PrimaryButtonClick += async (_, args) =>
        {
            args.Cancel = true; if (working) return;
            await Work(async ct => { var validated = AgentPluginPackages.Validate(draft); if (SaveAsync is not null) await SaveAsync(validated, ct); ct.ThrowIfCancellationRequested(); Result = validated; Hide(); });
        };
        Closing += (_, args) => { if (working && !lifetime.IsCancellationRequested && Result is null) args.Cancel = true; };
        Opened += (_, _) => { SizeToRoot(); XamlRoot.Changed += RootChanged; }; Closed += (_, _) => { lifetime.Cancel(); XamlRoot.Changed -= RootChanged; };
    }
    public void CancelAndHide() { lifetime.Cancel(); Hide(); }
    private static StackPanel Panel() => new() { Spacing = 12 };
    private void Field(StackPanel panel, string key, string label, bool immutable = false)
    {
        var input = new TextBox { Name = "Plugin_" + key, Header = DesktopResources.Get(label), Text = DesktopAgent.Text(draft.Manifest[key]), IsReadOnly = readOnly || immutable, AcceptsReturn = key == "description", TextWrapping = TextWrapping.Wrap };
        ControlAppearance.Stock(input); panel.Children.Add(input); input.TextChanged += (_, _) => draft.Manifest[key] = input.Text;
    }
    private void Tab(string key, string title, FrameworkElement view) { pages[key] = view; var item = new NavigationViewItem { Content = title, Tag = key }; AutomationProperties.SetAutomationId(item, "PluginTab_" + key); tabs.MenuItems.Add(item); }
    private async Task Work(Func<CancellationToken, Task> action)
    {
        if (working || readOnly) return; working = true; IsPrimaryButtonEnabled = false; tabs.IsEnabled = false;
        try { await action(lifetime.Token); }
        catch (OperationCanceledException) { }
        catch (Exception e) { error.Text = e is InvalidDataException ? e.Message : DesktopResources.Get("PluginOperationFailed"); error.Visibility = Visibility.Visible; }
        finally { working = false; IsPrimaryButtonEnabled = true; tabs.IsEnabled = true; }
    }
    private Button Button(string key, Func<Task> action) { var button = new Button { Content = DesktopResources.Get(key), IsEnabled = !readOnly || key == "Download" }; ControlAppearance.Stock(button); button.Click += async (_, _) => { try { await action(); } catch (OperationCanceledException) { } catch { error.Text = DesktopResources.Get("PluginOperationFailed"); error.Visibility = Visibility.Visible; } }; return button; }
    private JsonObject ServersObject() { draft.Mcp ??= new() { ["$schema"] = AgentPluginPackages.McpSchema, ["mcpServers"] = new JsonObject() }; return (JsonObject)draft.Mcp["mcpServers"]!; }
    private void SettingsControls(StackPanel body, string? name)
    {
        if (extensionNamespace.Length == 0) return;
        foreach (var key in new[] { "defer_loading", "namespace", "direct", "programmatic" })
        {
            var value = name is null ? null : AgentPluginRuntime.Settings(draft.Manifest, extensionNamespace, name);
            var on = value is { } settings && (key is "direct" or "programmatic" ? settings.TryGetProperty("allowed_callers", out var callers) && callers.EnumerateArray().Any(c => c.GetString() == key) : settings.TryGetProperty(key, out var flag) && flag.ValueKind == JsonValueKind.True);
            var toggle = new ToggleSwitch { Header = DesktopResources.Get("PluginSetting_" + key), IsOn = on, IsEnabled = !readOnly, OnContent = "", OffContent = "" }; ControlAppearance.Stock(toggle); body.Children.Add(toggle);
            toggle.Toggled += (_, _) =>
            {
                foreach (var id in name is null ? ServersObject().Select(p => p.Key).ToArray() : new[] { name })
                {
                    var extensions = DesktopAgent.Object(draft.Manifest, "extensions"); var extension = DesktopAgent.Object(extensions, extensionNamespace); var servers = DesktopAgent.Object(extension, "mcpServers"); var entry = DesktopAgent.Object(servers, id);
                    if (key is "direct" or "programmatic") { var callers = (entry["allowed_callers"] as JsonArray)?.Select(DesktopAgent.Text).Where(c => c != key).ToList() ?? []; if (toggle.IsOn) callers.Add(key); if (callers.Count > 0) entry["allowed_callers"] = JsonSerializer.SerializeToNode(callers); else entry.Remove("allowed_callers"); }
                    else if (toggle.IsOn) entry[key] = true; else entry.Remove(key);
                }
                if (name is null) Servers();
            };
        }
    }
    private void Servers()
    {
        serverRows.Children.Clear();
        foreach (var (name, node) in (draft.Mcp?["mcpServers"] as JsonObject ?? new()).ToArray())
        {
            var body = Panel(); body.Children.Add(new TextBlock { Text = name, FontSize = 18, TextWrapping = TextWrapping.Wrap });
            body.Children.Add(new TextBlock { Text = DesktopAgent.Text(node?["type"]) + " · " + (DesktopAgent.Text(node?["url"]) is { Length: > 0 } url ? url : DesktopAgent.Text(node?["command"])), TextWrapping = TextWrapping.Wrap });
            var raw = new TextBox { Header = DesktopResources.Get("PluginConfiguration"), Text = node!.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), IsReadOnly = readOnly, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
            ControlAppearance.Stock(raw); body.Children.Add(raw);
            if (!readOnly) body.Children.Add(Button("PluginApplyConfiguration", () => Work(_ => { ServersObject()[name] = AgentPluginPackages.Server(name, JsonNode.Parse(raw.Text)); Servers(); return Task.CompletedTask; })));
            SettingsControls(body, name);
            if (!readOnly) body.Children.Add(Button("Delete", () => { ServersObject().Remove(name); Servers(); return Task.CompletedTask; }));
            var card = new Border { Child = body, Padding = new Thickness(16) }; NativeCardSurface.Card(card); serverRows.Children.Add(card);
        }
    }
    private async Task AddFiles(IEnumerable<StorageFile> files, CancellationToken ct)
    {
        var additions = new List<DesktopSkillFile>();
        foreach (var file in files) { var path = AgentPluginPackages.PackagePath(file.Name); if (path is "plugin.json" or "mcp.json") throw new InvalidDataException("Root files are generated."); await using var stream = await file.OpenStreamForReadAsync(); additions.Add(new(path, await AgentPluginPackages.ReadBoundedAsync(stream, stream.Length, ct))); }
        var next = draft.Clone(); foreach (var file in additions) { next.Files.RemoveAll(f => f.Path.Equals(file.Path, StringComparison.OrdinalIgnoreCase)); next.Files.Add(file); } _ = AgentPluginPackages.Validate(next); ct.ThrowIfCancellationRequested(); draft.Files = next.Files;
    }
    private void Files()
    {
        fileRows.Children.Clear();
        foreach (var file in draft.Files.OrderBy(f => f.Path, StringComparer.Ordinal))
        {
            var body = Panel(); body.Children.Add(new TextBlock { Text = file.Path + $" ({file.Data.Length:N0} B)", TextWrapping = TextWrapping.Wrap });
            if (SkillFiles.IsText(file.MimeType) && file.Data.Length < 256_000)
            {
                var text = new TextBox { Text = Encoding.UTF8.GetString(file.Data), IsReadOnly = readOnly || file.Path.StartsWith("skills/", StringComparison.Ordinal), AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 240 }; ControlAppearance.Stock(text); body.Children.Add(text);
                if (!text.IsReadOnly) text.TextChanged += (_, _) => { var index = draft.Files.FindIndex(f => f.Path == file.Path); if (index >= 0) draft.Files[index] = new(file.Path, Encoding.UTF8.GetBytes(text.Text)); };
            }
            body.Children.Add(Button("Download", async () =>
            {
                var picker = new FileSavePicker { SuggestedFileName = Path.GetFileNameWithoutExtension(file.Path) }; picker.FileTypeChoices.Add(DesktopResources.Get("Files"), new List<string> { Path.GetExtension(file.Path) is { Length: > 0 } ext ? ext : ".bin" }); Initialize(picker);
                if (await picker.PickSaveFileAsync() is { } target) await FileIO.WriteBytesAsync(target, draft.Files.First(f => f.Path == file.Path).Data);
            }));
            if (!readOnly && !file.Path.StartsWith("skills/", StringComparison.Ordinal)) body.Children.Add(Button("Delete", () => { draft.Files.RemoveAll(f => f.Path == file.Path); Files(); return Task.CompletedTask; }));
            var card = new Border { Child = body, Padding = new Thickness(16) }; NativeCardSurface.Card(card); fileRows.Children.Add(card);
        }
    }
    private void Initialize(object picker) => WinRT.Interop.InitializeWithWindow.Initialize(picker, Microsoft.UI.Win32Interop.GetWindowFromWindowId(XamlRoot.ContentIslandEnvironment.AppWindowId));
    private void RootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => SizeToRoot();
    public void SizeToRoot() { layout.Width = Math.Max(0, Math.Min(820, XamlRoot.Size.Width - 96)); layout.Height = Math.Max(0, Math.Min(680, XamlRoot.Size.Height - 240)); }
}
