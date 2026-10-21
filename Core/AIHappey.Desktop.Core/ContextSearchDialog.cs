using FluentIcons.Common;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace AIHappey.Desktop.Core;

/// <summary>Native composer discovery. Filtering never replaces the isolated preference draft.</summary>
public sealed class ContextSearchDialog : ContentDialog, IResponsiveDialog
{
    private readonly ChatPreferences draft;
    private readonly IReadOnlyList<DesktopAgentPlugin> plugins;
    private readonly Func<CancellationToken, Task<DesktopSkillSelection>> loadSkills;
    private readonly Func<string, CancellationToken, Task> prefetch;
    private readonly Func<CancellationToken, Task<McpCatalogResult>> loadCatalog;
    private readonly Func<IReadOnlyList<DesktopMcpServer>> installed;
    private readonly Func<McpCatalogItem, bool, CancellationToken, Task> changeServer;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Grid layout = new() { Name = "ContextSearchLayout", RowSpacing = 12 };
    private readonly TextBox search = new() { Name = "ContextSearchQuery", PlaceholderText = DesktopResources.Get("SearchPlaceholder") };
    private readonly StackPanel tabs = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private readonly Dictionary<string, ToggleButton> tabButtons = [];
    private readonly Dictionary<string, TextBlock> tabLabels = [];
    private readonly OverviewCardsPanel cards = new() { Name = "ContextSearchCards" };
    private readonly ScrollViewer viewer = new() { HorizontalScrollMode = ScrollMode.Disabled,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly StackPanel body = new() { Spacing = 12 };
    private readonly TextBlock status = new() { Name = "ContextSearchStatus", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock error = new() { Name = "ContextSearchError", TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    private readonly ProgressBar progress = new() { Name = "ContextSearchProgress", Height = 4, Visibility = Visibility.Collapsed };
    private readonly Button retry = new() { Name = "ContextSearchRetry", Content = DesktopResources.Get("Retry"), Visibility = Visibility.Collapsed };
    private readonly Button more = new() { Name = "ContextSearchMore", Content = DesktopResources.Get("ShowMore"), Visibility = Visibility.Collapsed };
    private DesktopSkillSelection skills = new([], new HashSet<string>(), "");
    private McpCatalogResult catalog = new([], []);
    private string tab = "tools";
    private string? skillWarning, catalogWarning;
    private bool skillsLoading, catalogLoading, pending, saving, closed, discard;
    private int visible = 20;
    public bool CatalogInstallHappened { get; private set; }
    public ChatPreferences? Result { get; private set; }
    public Func<ChatPreferences, Task>? SaveAsync { get; set; }

    public ContextSearchDialog(ChatPreferences preferences, IReadOnlyList<DesktopAgentPlugin> plugins,
        Func<CancellationToken, Task<DesktopSkillSelection>> loadSkills, Func<string, CancellationToken, Task> prefetch,
        Func<CancellationToken, Task<McpCatalogResult>> loadCatalog, Func<IReadOnlyList<DesktopMcpServer>> installed,
        Func<McpCatalogItem, bool, CancellationToken, Task> changeServer)
    {
        draft = preferences.Clone(); this.plugins = plugins; this.loadSkills = loadSkills; this.prefetch = prefetch;
        this.loadCatalog = loadCatalog; this.installed = installed; this.changeServer = changeServer;
        Name = "ContextSearchDialog"; Title = DesktopResources.Get("Search"); CloseButtonText = DesktopResources.Get("Close");
        Resources["ContentDialogMaxWidth"] = 920d; Resources["ContentDialogMinWidth"] = 0d;
        for (var row = 0; row < 5; row++) layout.RowDefinitions.Add(new() { Height = row == 3 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
        ControlAppearance.Stock(search); ToolbarControls.Label(search, DesktopResources.Get("Search")); layout.Children.Add(search);
        var navigation = new ScrollViewer { Content = tabs, HorizontalScrollMode = ScrollMode.Enabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(navigation, 1); layout.Children.Add(navigation);
        foreach (var (key, label, icon) in new[] { ("tools", "Tools", Icon.Toolbox), ("skills", "Skills", Icon.Star),
            ("plugins", "PluginsTitle", Icon.PuzzlePiece), ("catalog", "McpTitle", Icon.Connector) })
        {
            var button = new ToggleButton { Name = "ContextSearchTab_" + key, Tag = label };
            ControlAppearance.Stock(button); tabButtons.Add(key, button); tabs.Children.Add(button);
            button.Click += (_, _) => { tab = key; Render(); viewer.ChangeView(null, 0, null, true); };
            var heading = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var labelText = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
            heading.Children.Add(DesktopIcons.Create(icon)); heading.Children.Add(labelText);
            button.Content = heading; tabLabels.Add(key, labelText);
        }
        AutomationProperties.SetName(progress, DesktopResources.Get("Loading")); Grid.SetRow(progress, 2); layout.Children.Add(progress);
        body.Children.Add(status); body.Children.Add(retry); body.Children.Add(cards); body.Children.Add(more);
        viewer.Content = body; Grid.SetRow(viewer, 3); layout.Children.Add(viewer);
        Grid.SetRow(error, 4); layout.Children.Add(error); Content = layout;
        AutomationProperties.SetLiveSetting(status, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        AutomationProperties.SetLiveSetting(error, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Assertive);
        ControlAppearance.Stock(retry); ControlAppearance.Stock(more);
        search.TextChanged += (_, _) => { visible = 20; Render(); viewer.ChangeView(null, 0, null, true); };
        retry.Click += async (_, _) => { if (tab == "skills") await LoadSkillsAsync(); else await LoadCatalogAsync(); };
        more.Click += (_, _) => { visible += 20; Render(); };
        Opened += (_, _) =>
        {
            SizeToRoot(); XamlRoot.Changed += RootChanged; search.Focus(FocusState.Programmatic);
            _ = LoadSkillsAsync(); _ = LoadCatalogAsync();
        };
        Closing += CommitOnClose;
        Closed += (_, _) => { closed = true; XamlRoot.Changed -= RootChanged; lifetime.Cancel(); StopProgress(); };
        Render();
    }

    public void Shutdown() { discard = true; lifetime.Cancel(); StopProgress(); Hide(); }
    public void RefreshInstalled() { if (!closed && !discard && !pending && !saving) Render(); }
    public Task RefreshSkillsAsync() => closed || discard || skillsLoading ? Task.CompletedTask : LoadSkillsAsync();
    private bool Matches(params string?[] values) => string.Join(" ", values).Contains(search.Text.Trim(), StringComparison.OrdinalIgnoreCase);
    private DesktopMcpServer? Installed(McpCatalogItem item) => installed().FirstOrDefault(server =>
        server.Id.Equals(item.Id, StringComparison.OrdinalIgnoreCase) || server.Name.Equals(item.Name, StringComparison.OrdinalIgnoreCase)
        || server.Url.Equals(item.Url, StringComparison.OrdinalIgnoreCase));

    private async Task LoadSkillsAsync()
    {
        if (closed || discard || skillsLoading) return;
        skillsLoading = true; Render();
        try { skills = await loadSkills(lifetime.Token); lifetime.Token.ThrowIfCancellationRequested(); skillWarning = skills.Warning; }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch { skillWarning = DesktopResources.Get("SkillsCatalogFailed"); }
        finally { skillsLoading = false; if (!closed && !discard) Render(); }
    }
    private async Task LoadCatalogAsync()
    {
        if (closed || discard || catalogLoading) return;
        catalogLoading = true; Render();
        try
        {
            var value = await loadCatalog(lifetime.Token); lifetime.Token.ThrowIfCancellationRequested(); catalog = value;
            catalogWarning = value.FailedSources.Count > 0 ? DesktopResources.Get("McpRegistryPartialFailure") : null;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch { catalogWarning = DesktopResources.Get("McpRegistryPartialFailure"); }
        finally { catalogLoading = false; if (!closed && !discard) Render(); }
    }
    private void Render()
    {
        if (closed || discard) return;
        var focusedId = XamlRoot is null ? null : (Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(XamlRoot) is Control focused
            ? AutomationProperties.GetAutomationId(focused) : null);
        var tools = DesktopLocalTools.Plugins.Where(p => Matches(p.Id, DesktopResources.Get(p.ResourceKey),
            string.Join(" ", p.Tools.Select(t => t.GetRawText())))).OrderBy(p => DesktopResources.Get(p.ResourceKey), StringComparer.CurrentCultureIgnoreCase).ToArray();
        var skillItems = skills.Items.Where(s => Matches(s.Id, s.Name, s.Description, s.Version, s.Origin, s.Server))
            .OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        var pluginItems = plugins.Where(p => Matches(p.Id, p.Description, p.Author, p.Manifest["author"]?.ToJsonString(),
                string.Join(" ", p.Keywords), DesktopAgent.Text(p.Manifest["version"])))
            .OrderBy(p => p.Id, StringComparer.CurrentCultureIgnoreCase).ToArray();
        var servers = catalog.Items.Where(s => Matches(s.Id, s.Name, s.Description, s.Version, s.Url, s.RegistryUrl))
            .OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        var counts = new Dictionary<string, int> { ["tools"] = tools.Length, ["skills"] = skillItems.Length, ["plugins"] = pluginItems.Length, ["catalog"] = servers.Length };
        foreach (var (key, button) in tabButtons)
        {
            tabLabels[key].Text = DesktopResources.Get((string)button.Tag) + $" ({counts[key]})";
            AutomationProperties.SetName(button, tabLabels[key].Text);
            button.IsChecked = key == tab;
        }
        cards.Children.Clear();
        if (tab == "tools") foreach (var tool in tools)
            ToggleCard(tool.Id, DesktopResources.Get(tool.ResourceKey), null, null, draft.PluginEnabled(tool.Id),
                enabled => { Set(draft.ActivePlugins, tool.Id, enabled); return Task.CompletedTask; }, "ContextToolToggle");
        if (tab == "skills") foreach (var skill in skillItems)
            ToggleCard(skill.Id, skill.Label, skill.Description, skill.Version, draft.EnabledSkillIds.Contains(skill.Id), async enabled =>
            {
                Set(draft.EnabledSkillIds, skill.Id, enabled);
                if (enabled && skill.Origin == "remote")
                {
                    try { await prefetch(skill.Id, lifetime.Token); }
                    catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
                    catch { if (!closed && !discard) ShowError(DesktopResources.Get("SkillPrefetchFailed")); }
                }
            }, "ContextSkillToggle");
        if (tab == "plugins") foreach (var plugin in pluginItems)
            ToggleCard(plugin.Id, plugin.Id, plugin.Description,
                string.Join(" · ", new[] { DesktopAgent.Text(plugin.Manifest["version"]), plugin.Author,
                    DesktopResources.Format("PluginCounts", plugin.Skills().Count, (plugin.Mcp?["mcpServers"] as System.Text.Json.Nodes.JsonObject)?.Count ?? 0, plugin.Diagnostics.Count) }.Where(v => v.Length > 0)),
                draft.EnabledAgentPluginIds.Contains(plugin.Id), enabled => { Set(draft.EnabledAgentPluginIds, plugin.Id, enabled); return Task.CompletedTask; }, "ContextPluginToggle");
        if (tab == "catalog") foreach (var server in servers.Take(visible)) CatalogCard(server);
        var loading = tab == "skills" && skillsLoading || tab == "catalog" && catalogLoading;
        var warning = tab == "skills" ? skillWarning : tab == "catalog" ? catalogWarning : null;
        status.Text = warning ?? (cards.Children.Count == 0 && !loading ? DesktopResources.Get("NoResults") : "");
        status.Visibility = status.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        retry.Visibility = warning is not null && !loading ? Visibility.Visible : Visibility.Collapsed;
        more.Visibility = tab == "catalog" && servers.Length > visible ? Visibility.Visible : Visibility.Collapsed;
        progress.IsIndeterminate = loading || pending || saving;
        progress.Visibility = progress.IsIndeterminate ? Visibility.Visible : Visibility.Collapsed;
        SetEditable(!pending && !saving);
        if (!string.IsNullOrEmpty(focusedId) && !pending && !saving) DispatcherQueue.TryEnqueue(() =>
        {
            if (!closed && !discard)
                ControlAppearance.Descendants(cards).OfType<Control>().FirstOrDefault(control => AutomationProperties.GetAutomationId(control) == focusedId)?.Focus(FocusState.Programmatic);
        });
    }
    private static void Set(List<string> values, string id, bool enabled)
    {
        values.RemoveAll(value => value == id); if (enabled) values.Add(id);
    }
    private void ToggleCard(string id, string title, string? description, string? metadata, bool enabled, Func<bool, Task> change, string name)
    {
        var toggle = new ToggleSwitch { Name = name, Tag = id, IsOn = enabled, OnContent = "", OffContent = "", MinWidth = 0,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        ControlAppearance.Stock(toggle); ToolbarControls.Label(toggle, title); AutomationProperties.SetAutomationId(toggle, name + "_" + id);
        var content = CardContent(title, description, metadata, toggle);
        var card = new Border { Name = "ContextSearchCard", Tag = id, Child = content, Padding = new Thickness(16) };
        NativeCardSurface.Card(card); cards.Children.Add(card);
        toggle.Toggled += async (_, _) => { if (!closed && !discard) await change(toggle.IsOn); };
    }
    private static StackPanel CardContent(string title, string? description, string? metadata, FrameworkElement action)
    {
        var content = new StackPanel { Spacing = 12 };
        var header = new Grid { ColumnSpacing = 12 };
        header.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        header.Children.Add(new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(action, 1); header.Children.Add(action); content.Children.Add(header);
        if (!string.IsNullOrWhiteSpace(metadata))
        { var label = new TextBlock { Text = metadata, TextWrapping = TextWrapping.Wrap }; NativeCardSurface.Secondary(label, true); content.Children.Add(label); }
        if (!string.IsNullOrWhiteSpace(description))
        { var text = new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap, MaxLines = 4, TextTrimming = TextTrimming.CharacterEllipsis }; NativeCardSurface.Secondary(text); ToolTipService.SetToolTip(text, description); content.Children.Add(text); }
        return content;
    }
    private void CatalogCard(McpCatalogItem item)
    {
        var existing = Installed(item);
        var button = new Button { Name = existing is null ? "ContextCatalogInstall" : "ContextCatalogRemove",
            Tag = item.Id, Content = DesktopResources.Get(existing is null ? "McpInstall" : "McpRemove"), IsEnabled = existing?.PluginId is null };
        ControlAppearance.Stock(button); ToolbarControls.Label(button, DesktopResources.Format("ActionForItem", button.Content, item.Name));
        var content = CardContent(item.Name, item.Description, item.Version, new McpServerIcon(item.Icons, 32));
        content.Children.Add(button);
        var card = new Border { Name = "ContextCatalogCard", Tag = item.Id, Child = content, Padding = new Thickness(16) };
        NativeCardSurface.Card(card); cards.Children.Add(card);
        button.Click += async (_, _) =>
        {
            if (pending || saving || closed || discard) return;
            pending = true; error.Visibility = Visibility.Collapsed; Render();
            try
            {
                await changeServer(item, existing is null, lifetime.Token); lifetime.Token.ThrowIfCancellationRequested();
                if (existing is null) CatalogInstallHappened = true;
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch (Exception e) { if (!closed && !discard) ShowError(DesktopMcpManager.SafeError(e)); }
            finally
            {
                pending = false;
                if (!closed && !discard)
                {
                    Render();
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (!closed && !discard)
                            (ControlAppearance.Descendants(cards).OfType<Button>().FirstOrDefault(b => Equals(b.Tag, item.Id)) as Control ?? search).Focus(FocusState.Programmatic);
                    });
                }
            }
        };
    }
    private void SetEditable(bool value)
    {
        search.IsEnabled = viewer.IsEnabled = value;
        foreach (var button in tabButtons.Values) button.IsEnabled = value;
        IsPrimaryButtonEnabled = IsSecondaryButtonEnabled = value;
    }
    private void ShowError(string text) { error.Text = text; error.Visibility = Visibility.Visible; }
    private void StopProgress() { progress.IsIndeterminate = false; progress.Visibility = Visibility.Collapsed; }
    private async void CommitOnClose(ContentDialog sender, ContentDialogClosingEventArgs args)
    {
        if (discard) return;
        if (pending || saving) { args.Cancel = true; return; }
        var deferral = args.GetDeferral(); saving = true; SetEditable(false);
        progress.IsIndeterminate = true; progress.Visibility = Visibility.Visible;
        try { if (SaveAsync is not null) await SaveAsync(draft.Clone()); if (!discard) Result = draft.Clone(); }
        catch { if (!discard) { args.Cancel = true; ShowError(DesktopResources.Get("ChatSettingsSaveFailed")); } }
        finally { saving = false; if (!closed && !discard) Render(); deferral.Complete(); }
    }
    private void RootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => SizeToRoot();
    void IResponsiveDialog.SizeToRoot() => SizeToRoot();
    private void SizeToRoot()
    {
        layout.Width = Math.Max(0, Math.Min(820, XamlRoot.Size.Width - 96));
        layout.Height = Math.Max(0, Math.Min(680, XamlRoot.Size.Height - 240));
    }
}
