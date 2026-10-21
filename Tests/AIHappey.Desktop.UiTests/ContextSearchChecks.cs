using System.Text.Json.Nodes;
using AIHappey.Desktop.Core;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

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
        var installed = new List<DesktopMcpServer> { new() { Id = "manual", Name = "Different name", Url = servers[0].Url } };
        var plugin = new DesktopAgentPlugin { Manifest = JsonNode.Parse("""{"name":"sample-plugin","description":"Useful plugin","version":"2","author":{"name":"Author","email":"contact@example.org"},"keywords":["keyword"]}""")!.AsObject() };
        var original = new ChatPreferences { EnabledSkillIds = ["unavailable"], EnabledAgentPluginIds = ["unavailable-plugin"] };
        var prefetches = 0; var catalogLoads = 0; var failMutation = true; var failSave = true; ChatPreferences? saved = null;
        var dialog = new ContextSearchDialog(original, [plugin], ct => skillLoad.Task.WaitAsync(ct), (_, _) => { prefetches++; throw new IOException("offline"); },
            ct => ++catalogLoads == 1 ? catalogLoad.Task.WaitAsync(ct) : Task.FromResult(new McpCatalogResult(servers, [])), () => installed,
            async (item, install, ct) =>
            {
                await Task.Delay(30, ct); if (failMutation) throw new IOException("private disk detail");
                if (install) installed.Add(new() { Id = item.Id, Name = item.Name, Url = item.Url });
                else installed.RemoveAll(s => s.Id == item.Id || s.Url == item.Url);
            }) { XamlRoot = root.XamlRoot, RequestedTheme = theme };
        dialog.SaveAsync = next => { if (failSave) throw new IOException("private disk detail"); saved = next; return Task.CompletedTask; };
        SystemAppearance.PrepareDialog(dialog); var shown = dialog.ShowAsync(); await Task.Delay(120);
        TextBox Query() => Descendants(dialog).OfType<TextBox>().Single(t => t.Name == "ContextSearchQuery");
        void Tab(string name) => Toggle(Descendants(dialog).OfType<ToggleButton>().Single(t => t.Name == "ContextSearchTab_" + name));
        ToggleSwitch[] Toggles(string name) => Descendants(dialog).OfType<ToggleSwitch>().Where(t => t.Name == name).ToArray();
        Button Button(string name) => Descendants(dialog).OfType<Button>().Single(b => b.Name == name);
        var tabs = Descendants(dialog).OfType<ToggleButton>().Where(t => t.Name.StartsWith("ContextSearchTab_")).ToArray();
        Check(tabs.Length == 4 && tabs.Count(t => t.IsChecked == true) == 1 && tabs[0].IsChecked == true,
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
        skillLoad.SetResult(new([new("provider/sample", "Sample", "Remote description", "remote", "2"), new("local", "Local", "On disk", "local")], new HashSet<string>(), "API"));
        await Task.Delay(80);
        Toggles("ContextSkillToggle").Single(t => (string)t.Tag == "provider/sample").IsOn = true; await Task.Delay(40);
        Check(prefetches == 1 && !indicator.IsIndeterminate && Descendants(dialog).OfType<TextBlock>().Single(t => t.Name == "ContextSearchError").Text == DesktopResources.Get("SkillPrefetchFailed"),
            context + ": failed prefetch stops loading, retains selection and gives safe feedback");
        Tab("plugins"); Query().Text = "keyword"; await Task.Delay(40);
        Check(Toggles("ContextPluginToggle").Length == 1, context + ": plugin metadata is searchable");
        Toggles("ContextPluginToggle").Single().IsOn = true;
        Query().Text = ""; Tab("catalog"); await Task.Delay(40);
        Check(indicator.IsIndeterminate, context + ": independent catalog request displays progress");
        catalogLoad.SetResult(new(servers, ["https://offline.example"])); await Task.Delay(80);
        Check(Descendants(dialog).OfType<Border>().Count(b => b.Name == "ContextCatalogCard") == 20 && Button("ContextSearchMore").Visibility == Visibility.Visible
            && Button("ContextSearchRetry").Visibility == Visibility.Visible, context + ": partial catalog retains paginated results with retry");
        InvokeButton(Button("ContextSearchMore")); await Task.Delay(50);
        Check(Descendants(dialog).OfType<Border>().Count(b => b.Name == "ContextCatalogCard") == 25, context + ": Show more reveals next catalog page");
        Query().Text = "Server 0"; await Task.Delay(40);
        Check(Button("ContextCatalogRemove").Tag as string == servers[0].Id, context + ": endpoint match recognizes manually installed server");
        failMutation = false; InvokeButton(Button("ContextCatalogRemove")); await Task.Delay(100);
        Check(installed.Count == 0 && !dialog.CatalogInstallHappened, context + ": removal does not trigger post-close server management");
        failMutation = true; InvokeButton(Button("ContextCatalogInstall")); await Task.Delay(100);
        Check(installed.Count == 0 && !Descendants(dialog).OfType<TextBlock>().Single(t => t.Name == "ContextSearchError").Text.Contains("private disk"), context + ": catalog action failures are sanitized and retryable");
        failMutation = false; InvokeButton(Button("ContextCatalogInstall")); await Task.Delay(100);
        Check(installed.Count == 1 && dialog.CatalogInstallHappened && Button("ContextCatalogRemove").IsEnabled, context + ": successful installation updates action and requests server management on close");
        InvokeButton(Button("ContextSearchRetry")); await Task.Delay(60);
        Check(catalogLoads == 2 && Button("ContextSearchRetry").Visibility == Visibility.Collapsed, context + ": retry clears partial-registry warning");
        Query().Text = ""; Tab("skills"); await Task.Delay(50);
        Check(Toggles("ContextSkillToggle").Single(t => (string)t.Tag == "provider/sample").IsOn && original.ActivePlugins.Count == 0 && original.EnabledSkillIds.SequenceEqual(["unavailable"]),
            context + ": tab changes preserve isolated draft and unavailable selections");
        window.AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(500 * scale), (int)(900 * scale))); await Task.Delay(180); dialog.UpdateLayout();
        Check(Descendants(dialog).OfType<ScrollViewer>().Where(s => s.HorizontalScrollMode == ScrollMode.Disabled).All(s => s.ScrollableWidth < 1)
            && Toggles("ContextSkillToggle").All(t => Within(t, dialog)), context + ": narrow dialog has no horizontal card overflow or clipped switches");
        InvokeButton(Button("CloseButton")); await Task.Delay(80);
        Check(shown.Status == Windows.Foundation.AsyncStatus.Started && dialog.Result is null && saved is null && Query().IsEnabled,
            context + ": save failure retains editable draft and keeps dialog open");
        failSave = false; InvokeButton(Button("CloseButton")); await shown;
        Check(saved is not null && saved.ActivePlugins.Contains(toolId) && saved.EnabledSkillIds.SequenceEqual(["unavailable", "provider/sample"])
            && saved.EnabledAgentPluginIds.SequenceEqual(["unavailable-plugin", plugin.Id]) && !indicator.IsIndeterminate, context + ": close saves complete unfiltered draft and stops progress");

        var canceled = false; var loadStarted = false;
        var closing = new ContextSearchDialog(original, [], async ct =>
        { loadStarted = true; try { await Task.Delay(Timeout.Infinite, ct); } catch (OperationCanceledException) { canceled = true; throw; } return new([], new HashSet<string>(), ""); },
            (_, _) => Task.CompletedTask, _ => Task.FromResult(new McpCatalogResult([], [])), () => [], (_, _, _) => Task.CompletedTask)
            { XamlRoot = root.XamlRoot, RequestedTheme = theme };
        shown = closing.ShowAsync(); await WaitForSkillUiAsync(() => loadStarted); closing.Shutdown(); await shown; await WaitForSkillUiAsync(() => canceled);
        Check(canceled && closing.Result is null, context + ": shutdown cancels loading without committing preferences");
        await CheckComposerIndicatorsAsync(theme);
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
