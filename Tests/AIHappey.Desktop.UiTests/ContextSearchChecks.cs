using System.Text.Json.Nodes;
using AIHappey.Desktop.Core;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AIHappey.Desktop.UiTests;

public partial class App
{
    private async Task CheckContextSearchAsync(ElementTheme theme)
    {
        var context = "Context search / " + theme;
        var root = new Grid { RequestedTheme = theme }; window!.Content = root; window.Activate(); await Task.Delay(80);
        var scale = root.XamlRoot.RasterizationScale;
        window.AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(1100 * scale), (int)(900 * scale))); await Task.Delay(80);
        var skillLoad = new TaskCompletionSource<DesktopSkillSelection>();
        var catalogLoad = new TaskCompletionSource<McpCatalogResult>();
        var servers = Enumerable.Range(0, 25).Select(i => new McpCatalogItem("registry:server" + i, "Server " + i, "Catalog description", "https://server" + i + ".example/mcp", "1")).ToArray();
        var installed = new List<DesktopMcpServer> { new() { Id = "manual", Name = "Different name", Url = servers[0].Url },
            new() { Id = servers[1].Id, Name = servers[1].Name, Url = servers[1].Url, PluginId = "sample-plugin" } };
        var plugin = new DesktopAgentPlugin { Manifest = JsonNode.Parse("""{"name":"sample-plugin","description":"Useful plugin","version":"2","author":{"name":"Author","email":"contact@example.org"},"keywords":["keyword"]}""")!.AsObject() };
        var original = new ChatPreferences { EnabledSkillIds = ["unavailable"], EnabledAgentPluginIds = ["unavailable-plugin"] };
        var prefetches = 0; var catalogLoads = 0; var failMutation = true; var failSave = true; ChatPreferences? saved = null;
        TaskCompletionSource? mutationGate = null;
        var dialog = new ContextSearchDialog(original, [plugin], ct => skillLoad.Task.WaitAsync(ct), (_, _) => { prefetches++; throw new IOException("offline"); },
            ct => ++catalogLoads == 1 ? catalogLoad.Task.WaitAsync(ct) : Task.FromResult(new McpCatalogResult(servers, [])), () => installed,
            async (item, install, ct) =>
            {
                await Task.Delay(30, ct);
                if (mutationGate is not null) await mutationGate.Task.WaitAsync(ct);
                if (failMutation) throw new IOException("private disk detail");
                if (install) installed.Add(new() { Id = item.Id, Name = item.Name, Url = item.Url });
                else installed.RemoveAll(s => s.Id == item.Id || s.Url == item.Url);
            }) { XamlRoot = root.XamlRoot, RequestedTheme = theme };
        dialog.SaveAsync = next => { if (failSave) throw new IOException("private disk detail"); saved = next; return Task.CompletedTask; };
        SystemAppearance.PrepareDialog(dialog); var shown = dialog.ShowAsync(); await Task.Delay(120);
        try
        {
        TextBox Query() => Descendants(dialog).OfType<TextBox>().Single(t => t.Name == "ContextSearchQuery");
        var navigation = Descendants(dialog).OfType<NavigationView>().Single(t => t.Name == "ContextSearchTabs");
        void Tab(string name) => SelectNativeTab(navigation, name);
        ToggleSwitch[] Toggles(string name) => Descendants(dialog).OfType<ToggleSwitch>().Where(t => t.Name == name).ToArray();
        SettingsExpander[] Rows(string name) => Descendants(dialog).OfType<SettingsExpander>().Where(e => e.Name == name).ToArray();
        ToggleSwitch CatalogToggle() => (ToggleSwitch)Rows("ContextCatalogRow").Single().Content;
        Button Button(string name) => Descendants(dialog).OfType<Button>().Single(b => b.Name == name);
        var tabs = navigation.MenuItems.OfType<NavigationViewItem>().ToArray();
        Check(tabs.Length == 4 && ReferenceEquals(navigation.SelectedItem, tabs[0]) && tabs.All(t => t.Icon is not null)
            && navigation.PaneDisplayMode == NavigationViewPaneDisplayMode.Top,
            context + ": four exclusive native tabs start with Tools");
        Check(Query().TransformToVisual(dialog).TransformPoint(new()).Y < tabs[0].TransformToVisual(dialog).TransformPoint(new()).Y,
            context + ": single accessible search field above navigation");
        var tool = Toggles("ContextToolToggle").First(); var toolId = (string)tool.Tag; tool.IsOn = true;
        Query().Text = "no-matching-result"; await Task.Delay(40);
        Check(Toggles("ContextToolToggle").Length == 0 && tabs.All(t => AutomationProperties.GetName(t).EndsWith("(0)")), context + ": query filters every tab and updates counts");
        Query().Text = DesktopLocalTools.Plugins.Single(p => p.Id == toolId).Tools[0].GetProperty("name").GetString()!; await Task.Delay(40);
        Check(Toggles("ContextToolToggle").Any(t => (string)t.Tag == toolId && t.IsOn), context + ": tool-definition search retains hidden selection");
        Query().Text = ""; Tab("skills"); await Task.Delay(40);
        var indicator = Descendants(dialog).OfType<ProgressBar>().Single(p => p.Name == "ContextSearchProgress");
        Check(indicator.IsIndeterminate && indicator.Visibility == Visibility.Visible, context + ": skills load uses accessible native progress bar");
        var description = string.Join(" ", Enumerable.Repeat("Complete remote skill description.", 30));
        skillLoad.SetResult(new([new("provider/sample", "Sample", description, "remote", "2"), new("local", "Local", "On disk", "local")], new HashSet<string>(), "API"));
        await Task.Delay(80);
        Check(Rows("ContextSkillRow").Length == 2 && Rows("ContextSkillRow").All(e => !e.IsExpanded && e.Content is ToggleSwitch)
            && !Descendants(dialog).OfType<Border>().Any(b => b.Name == "ContextSearchCard"), context + ": skills use initially collapsed native rows with header toggles");
        var skillRow = Rows("ContextSkillRow").Single(e => e.Tag as string == "provider/sample"); skillRow.IsExpanded = true; await Task.Delay(80);
        Check(Descendants(skillRow).OfType<TextBlock>().Any(t => t.Text == description && t.MaxLines == 0)
            && Descendants(skillRow).OfType<TextBlock>().Any(t => t.Text == "2"), context + ": expanded skill shows complete description and version");
        Toggles("ContextSkillToggle").Single(t => (string)t.Tag == "provider/sample").IsOn = true; await Task.Delay(40);
        Check(prefetches == 1 && !indicator.IsIndeterminate && Descendants(dialog).OfType<TextBlock>().Single(t => t.Name == "ContextSearchError").Text == DesktopResources.Get("SkillPrefetchFailed"),
            context + ": failed prefetch stops loading, retains selection and gives safe feedback");
        Tab("plugins"); Query().Text = "keyword"; await Task.Delay(40);
        Check(Toggles("ContextPluginToggle").Length == 1, context + ": plugin metadata is searchable");
        Toggles("ContextPluginToggle").Single().IsOn = true;
        Query().Text = ""; Tab("catalog"); await Task.Delay(40);
        Check(indicator.IsIndeterminate, context + ": independent catalog request displays progress");
        catalogLoad.SetResult(new(servers, ["https://offline.example"])); await Task.Delay(80);
        Check(Rows("ContextCatalogRow").Length == 20 && Rows("ContextCatalogRow").All(e => !e.IsExpanded && e.Content is ToggleSwitch) && Button("ContextSearchMore").Visibility == Visibility.Visible
            && Button("ContextSearchRetry").Visibility == Visibility.Visible, context + ": partial catalog retains paginated results with retry");
        InvokeButton(Button("ContextSearchMore")); await Task.Delay(50);
        Check(Rows("ContextCatalogRow").Length == 25, context + ": Show more reveals next catalog page");
        Query().Text = "https://server1.example/mcp";
        await WaitForSkillUiAsync(() => Field<StackPanel>(dialog, "rows").Children.Count == 1); dialog.UpdateLayout();
        Check(Rows("ContextCatalogRow").Length == 1, context + ": endpoint query selects one catalog row");
        Check(CatalogToggle().IsOn && !CatalogToggle().IsEnabled, context + ": plugin-owned server is installed but cannot be removed independently");
        Query().Text = "Server 0";
        await WaitForSkillUiAsync(() => Field<StackPanel>(dialog, "rows").Children.OfType<SettingsExpander>().SingleOrDefault()?.Tag as string == servers[0].Id); dialog.UpdateLayout();
        Check(CatalogToggle().Tag as string == servers[0].Id && CatalogToggle().IsOn, context + ": endpoint match recognizes manually installed server");
        Rows("ContextCatalogRow").Single().IsExpanded = true; await Task.Delay(80);
        Check(Button("ContextCatalogConfigure").Tag as string == servers[0].Id, context + ": installed server keeps configuration action in expanded details");
        failMutation = false; CatalogToggle().IsOn = false; await Task.Delay(100);
        Check(installed.Count == 1 && !dialog.CatalogInstallHappened && !CatalogToggle().IsOn && Rows("ContextCatalogRow").Single().IsExpanded,
            context + ": removal resets header toggle, retains expansion, and does not trigger post-close server management");
        failMutation = true; CatalogToggle().IsOn = true; await Task.Delay(100);
        Check(installed.Count == 1 && !CatalogToggle().IsOn && !Descendants(dialog).OfType<TextBlock>().Single(t => t.Name == "ContextSearchError").Text.Contains("private disk"), context + ": catalog action failures roll back the switch and are sanitized and retryable");
        failMutation = false; mutationGate = new(); CatalogToggle().IsOn = true;
        await WaitForSkillUiAsync(() => Field<bool>(dialog, "pending"));
        InvokeButton(Button("CloseButton")); await Task.Delay(40);
        Check(shown.Status == Windows.Foundation.AsyncStatus.Started && !Query().IsEnabled && !navigation.IsEnabled && saved is null,
            context + ": pending catalog mutation blocks editing and close without saving");
        mutationGate.SetResult();
        await WaitForSkillUiAsync(() => installed.Count == 2 && !Field<bool>(dialog, "pending")); dialog.UpdateLayout();
        Check(dialog.CatalogInstallHappened && CatalogToggle().IsOn && CatalogToggle().IsEnabled,
            context + ": successful installation updates toggle and requests server management on close");
        await WaitForSkillUiAsync(() => Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(root.XamlRoot) is ToggleSwitch { Name: "ContextCatalogToggle" });
        Check(true, context + ": successful installation restores header-toggle focus");
        InvokeButton(Button("ContextSearchRetry")); await Task.Delay(60);
        Check(catalogLoads == 2 && Button("ContextSearchRetry").Visibility == Visibility.Collapsed, context + ": retry clears partial-registry warning");
        Query().Text = ""; Tab("skills"); await Task.Delay(50);
        Check(Toggles("ContextSkillToggle").Single(t => (string)t.Tag == "provider/sample").IsOn && original.ActivePlugins.Count == 0 && original.EnabledSkillIds.SequenceEqual(["unavailable"]),
            context + ": tab changes preserve isolated draft and unavailable selections");
        Check(Rows("ContextSkillRow").Single(e => e.Tag as string == "provider/sample").IsExpanded, context + ": tab changes preserve skill expansion");
        Query().Text = "no-matching-result"; await Task.Delay(40); Query().Text = ""; await Task.Delay(40);
        Check(Rows("ContextSkillRow").Single(e => e.Tag as string == "provider/sample").IsExpanded, context + ": filtering preserves expansion");
        window.AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(500 * scale), (int)(900 * scale))); await Task.Delay(180); dialog.UpdateLayout();
        Check(Descendants(dialog).OfType<ScrollViewer>().Where(s => s.HorizontalScrollMode == ScrollMode.Disabled).All(s => s.ScrollableWidth < 1)
            && Toggles("ContextSkillToggle").All(t => Within(t, dialog)), context + ": narrow dialog has no horizontal card overflow or clipped switches");
        Check(navigation.ActualWidth <= ((FrameworkElement)dialog.Content).ActualWidth + 1
            && ReferenceEquals(navigation.SelectedItem, tabs[1]), context + ": native tab overflow fits narrow dialog and retains selection");
        window.AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(500 * scale), (int)(600 * scale))); await Task.Delay(180); dialog.UpdateLayout();
        var contentViewer = Field<ScrollViewer>(dialog, "viewer");
        Check(contentViewer.TransformToVisual(dialog).TransformPoint(new()).Y + contentViewer.ActualHeight <= Button("CloseButton").TransformToVisual(dialog).TransformPoint(new()).Y
            && contentViewer.ScrollableHeight > 0, context + ": expanded content scrolls above footer in short window");
        InvokeButton(Button("CloseButton")); await Task.Delay(80);
        Check(shown.Status == Windows.Foundation.AsyncStatus.Started && dialog.Result is null && saved is null && Query().IsEnabled,
            context + ": save failure retains editable draft and keeps dialog open");
        failSave = false; InvokeButton(Button("CloseButton")); await shown;
        Check(saved is not null && saved.ActivePlugins.Contains(toolId) && saved.EnabledSkillIds.SequenceEqual(["unavailable", "provider/sample"])
            && saved.EnabledAgentPluginIds.SequenceEqual(["unavailable-plugin", plugin.Id]) && !indicator.IsIndeterminate, context + ": close saves complete unfiltered draft and stops progress");
        }
        finally { if (shown.Status == Windows.Foundation.AsyncStatus.Started) { dialog.Shutdown(); await shown; } }

        var canceled = false; var loadStarted = false;
        var closing = new ContextSearchDialog(original, [], async ct =>
        { loadStarted = true; try { await Task.Delay(Timeout.Infinite, ct); } catch (OperationCanceledException) { canceled = true; throw; } return new([], new HashSet<string>(), ""); },
            (_, _) => Task.CompletedTask, _ => Task.FromResult(new McpCatalogResult([], [])), () => [], (_, _, _) => Task.CompletedTask)
            { XamlRoot = root.XamlRoot, RequestedTheme = theme };
        shown = closing.ShowAsync(); await WaitForSkillUiAsync(() => loadStarted); closing.Shutdown(); await shown; await WaitForSkillUiAsync(() => canceled);
        Check(canceled && closing.Result is null, context + ": shutdown cancels loading without committing preferences");
        await CheckContextConfigurationAsync(root, theme);
        await CheckAgentSettingsAsync(theme);
        await CheckComposerIndicatorsAsync(theme);
    }

    private async Task CheckContextConfigurationAsync(Grid root, ElementTheme theme)
    {
        var context = "Context configuration / " + theme;
        var item = new McpCatalogItem("secure", "Secure server", "Requires a key", "https://secure.example/mcp")
        { HeaderDefinitions = [new() { Name = "X-Api-Key", IsRequired = true, IsSecret = true }] };
        var mutations = 0;
        foreach (var existing in new[] { false, true })
        {
            var server = item.CreateServer(); server.Headers["X-Api-Key"] = "fixture";
            var dialog = new ContextSearchDialog(new(), [], _ => Task.FromResult(new DesktopSkillSelection([], new HashSet<string>(), "")),
                (_, _) => Task.CompletedTask, _ => Task.FromResult(new McpCatalogResult([item], [])), () => existing ? [server] : [],
                (_, _, _) => { mutations++; return Task.CompletedTask; }) { XamlRoot = root.XamlRoot, RequestedTheme = theme };
            var shown = dialog.ShowAsync(); await Task.Delay(100);
            // The previous check leaves a narrow window, so Catalog may be in native
            // overflow without a realized container/automation peer until opened.
            var navigation = Descendants(dialog).OfType<NavigationView>().Single();
            navigation.SelectedItem = navigation.MenuItems.OfType<NavigationViewItem>().Single(t => t.Tag as string == "catalog");
            await WaitForSkillUiAsync(() => Field<StackPanel>(dialog, "rows").Children.Count == 1); dialog.UpdateLayout();
            var toggle = Descendants(dialog).OfType<ToggleSwitch>().Single(t => t.Name == "ContextCatalogToggle");
            if (existing)
            {
                Descendants(dialog).OfType<SettingsExpander>().Single(e => e.Name == "ContextCatalogRow").IsExpanded = true; await Task.Delay(80);
                InvokeButton(Descendants(dialog).OfType<Button>().Single(b => b.Name == "ContextCatalogConfigure"));
            }
            else toggle.IsOn = true;
            await shown;
            Check(mutations == 0 && dialog.ServerConfigurationRequested is { Id: "secure" } requested && requested.HeaderDefinitions.Count == 1
                && !dialog.CatalogInstallHappened && toggle.IsOn == existing, context + ": " + (existing ? "edit retains configuration and installed state" : "install hands off required configuration without premature installation"));
        }
    }

    private async Task CheckComposerIndicatorsAsync(ElementTheme theme)
    {
        var context = "Composer indicators / " + theme;
        var session = new DesktopSession(new UiHost(true), new UiRuntime(), new());
        var shell = new ChatShell(session) { RequestedTheme = theme }; window!.Content = shell; await Task.Delay(180); await HistoryIdleAsync(shell);
        try
        {
            var menu = (MenuFlyout)Field<Button>(shell, "addContext").Flyout;
            Check(menu.Items.OfType<MenuFlyoutItem>().Any(i => i.Name == "SearchContext") && !menu.Items.OfType<MenuFlyoutItem>().Any(i => i.Name == "SelectAgentPlugins"), context + ": plus menu has Search instead of standalone Plugins");
            var ring = Field<ProgressRing>(shell, "progress");
            foreach (var streaming in new[] { false, true })
            {
                Call(shell, "SetBusy", true, streaming);
                Check(ring.IsActive && ring.Visibility == Visibility.Visible && AutomationProperties.GetName(ring).Length > 0, context + ": header spinner runs during " + (streaming ? "streaming" : "network work"));
                Call(shell, "SetBusy", false, false);
                Check(!ring.IsActive && ring.Visibility == Visibility.Collapsed, context + ": header spinner stops when idle or canceled");
            }
            Call(shell, "UpdateMode", ServiceKind.Agents);
            Check(menu.Items.OfType<MenuFlyoutItem>().Single(i => i.Name == "SearchContext").Visibility == Visibility.Collapsed, context + ": model context search hidden in agent mode");
            Call(shell, "UpdateMode", ServiceKind.Ai);
        }
        finally { await shell.ShutdownAsync(); window.Content = null; if (Directory.Exists(session.DataDirectory)) Directory.Delete(session.DataDirectory, true); }
    }

    private async Task CheckToolLayoutAndSkillProgressAsync(ElementTheme theme)
    {
        var context = "Settings layout and progress / " + theme;
        var root = new Grid { RequestedTheme = theme, Padding = new Thickness(24) }; window!.Content = root; window.Activate(); await Task.Delay(80);
        var draft = new ChatPreferences(); var tools = new ChatToolSettingsView(draft, _ => []); root.Children.Add(tools);
        Descendants(tools).OfType<SettingsExpander>().Single(e => e.Name == "GeneralToolsCard").IsExpanded = true;
        var scale = root.XamlRoot.RasterizationScale;
        foreach (var width in new[] { 1000, 500 })
        {
            window.AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(width * scale), (int)(900 * scale))); await Task.Delay(180); root.UpdateLayout();
            var choice = Descendants(tools).OfType<ComboBox>().Single(c => c.Name == "ChatToolChoice");
            var limit = Descendants(tools).OfType<TextBox>().Single(c => c.Name == "ChatMaxToolCalls");
            var point = choice.TransformToVisual(root).TransformPoint(new()); var limitPoint = limit.TransformToVisual(root).TransformPoint(new());
            Check(Math.Abs(choice.ActualWidth - limit.ActualWidth) < 1 && Math.Abs(point.Y - limitPoint.Y) < 1 && limitPoint.X > point.X,
                context + ": equal-width tool fields share a row at " + width);
            Check(!Descendants(tools).OfType<TextBlock>().Any(t => t.Text == DesktopResources.Get("ChatToolRequiredHint")), context + ": explanatory tool-choice text removed");
            limit.Text = "0"; await Task.Delay(40); Check(!tools.IsValid, context + ": invalid call limit still rejected");
            limit.Text = "3"; await Task.Delay(40); Check(tools.IsValid && draft.MaxToolCalls == 3 && choice.Items.OfType<ComboBoxItem>().Any(i => (string)i.Tag == "required"), context + ": valid limit retains tool-choice normalization");
        }
        root.Children.Clear();
        var completion = new TaskCompletionSource<DesktopSkillSelection>();
        var skills = new SkillsSettingsView(new(), ct => completion.Task.WaitAsync(ct), null); root.Children.Add(skills); await Task.Delay(80);
        var bar = Descendants(skills).OfType<ProgressBar>().Single();
        Check(bar.IsIndeterminate && bar.Visibility == Visibility.Visible && !Descendants(skills).OfType<TextBlock>().Any(t => t.Text == DesktopResources.Get("Working")), context + ": Skills shows progress without Working text");
        completion.SetException(new IOException("offline")); await Task.Delay(80);
        Check(!bar.IsIndeterminate && bar.Visibility == Visibility.Collapsed && Descendants(skills).OfType<Button>().Single(b => b.Name == "ChatSkillsRetry").Visibility == Visibility.Visible, context + ": failure stops progress and exposes retry");
        completion = new(); var refresh = skills.RefreshAsync(); await Task.Delay(30);
        Check(bar.IsIndeterminate, context + ": retry restarts progress");
        completion.SetResult(new([], new HashSet<string>(), "")); await refresh;
        Check(!bar.IsIndeterminate && bar.Visibility == Visibility.Collapsed, context + ": successful retry stops progress");
        completion = new(); refresh = skills.RefreshAsync(); await Task.Delay(30); skills.Dispose(); await refresh;
        Check(!bar.IsIndeterminate && bar.Visibility == Visibility.Collapsed, context + ": dispose/cancellation stops progress");
    }
}
