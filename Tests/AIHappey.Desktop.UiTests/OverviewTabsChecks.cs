using AIHappey.Desktop.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace AIHappey.Desktop.UiTests;

public partial class App
{
    private async Task CheckOverviewTabsAsync(ElementTheme theme)
    {
        var root = new Grid { RequestedTheme = theme };
        window!.Content = root; window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1000, 800)); window.Activate();
        await Task.Delay(100);
        var assembly = typeof(ChatShell).Assembly;
        foreach (var kind in new[] { CatalogKind.Agent, CatalogKind.Skill })
        {
            var context = $"Overview tabs / {kind} / {theme}";
            var page = (UserControl)Activator.CreateInstance(assembly.GetType("AIHappey.Desktop.Core.OverviewPage")!, [kind])!;
            root.Children.Add(page);
            var items = Enumerable.Range(0, 55).Select(index => new CatalogItem(kind, "fixture-" + index, index == 0 ? "Needle" : "Fixture " + index, "Description")).ToArray();
            var local = new CatalogItem(kind, "local", "Local fixture", "Local description") { Origin = CatalogOrigin.Local };
            SetOverview(page, items.Append(local).ToArray(), new HashSet<string> { items[0].Key });
            await Task.Delay(100); page.UpdateLayout();
            var tabs = Descendants(page).OfType<NavigationView>().Single(view => view.Name == "CatalogFilters");
            CheckNativeTabs(tabs, context);
            Check(tabs.MenuItems.OfType<NavigationViewItem>().Select(tab => (string)tab.Tag).SequenceEqual(new[] { "all", "favorites", "backend", "local" })
                && CardCount(page, "CatalogCard") == 50, context + ": filter order, capability-driven Local and first fifty cards");
            var original = tabs.MenuItems.ToArray();
            SelectNativeTab(tabs, "favorites");
            Check(CardCount(page, "CatalogCard") == 1, context + ": native Favorites selection filters results");
            var favoritesTab = (NavigationViewItem)tabs.SelectedItem;
            Check(favoritesTab.Focus(FocusState.Keyboard) && new NavigationViewItemAutomationPeer(favoritesTab).GetName() == favoritesTab.Content.ToString(),
                context + ": native keyboard focus and accessible count label");
            InvokeOverview(page, "SetFavorites", new HashSet<string> { items[1].Key });
            Check(ReferenceEquals(tabs.SelectedItem, favoritesTab) && ReferenceEquals(FocusManager.GetFocusedElement(root.XamlRoot), favoritesTab)
                && tabs.MenuItems.SequenceEqual(original) && CardCount(page, "CatalogCard") == 1,
                context + ": favorite refresh retains selected item, focus and navigation identities");
            var search = Descendants(page).OfType<TextBox>().Single(box => box.Name == "CatalogSearch");
            search.Text = "Needle";
            await Task.Delay(80); page.UpdateLayout();
            Check(CardCount(page, "CatalogCard") == 0 && ((NavigationViewItem)tabs.MenuItems[0]).Content.ToString()!.Contains("(1)")
                && favoritesTab.Content.ToString()!.Contains("(0)") && ReferenceEquals(tabs.SelectedItem, favoritesTab), context + ": search updates counts without resetting selection");
            search.Text = ""; await Task.Delay(80); SelectNativeTab(tabs, "all");
            InvokeButton(Descendants(page).OfType<Button>().Single(button => button.Content?.ToString() == DesktopResources.Get("ShowMore")));
            Check(CardCount(page, "CatalogCard") == 56, context + ": Show more retains pagination");
            SelectNativeTab(tabs, "backend");
            Check(CardCount(page, "CatalogCard") == 50, context + ": changing filters resets pagination");
            SelectNativeTab(tabs, "local");
            Check(CardCount(page, "CatalogCard") == 1, context + ": Local filter shows only local items");
            SetOverview(page, items, new HashSet<string>());
            Check((string)((NavigationViewItem)tabs.SelectedItem).Tag == "all" && tabs.MenuItems.Count == 3, context + ": removed Local capability falls back to All");
            var all = tabs.SelectedItem;
            InvokeOverview(page, "Loading", new object?[] { null });
            Check(!tabs.IsEnabled && !search.IsEnabled && tabs.MenuItems.Count == 3 && CardCount(page, "CatalogCard") == 0, context + ": loading retains but disables navigation");
            InvokeOverview(page, "Error", "Fixture failure");
            Check(!tabs.IsEnabled && Descendants(page).OfType<TextBlock>().Any(text => text.Text == "Fixture failure"), context + ": error retains stable tabs and retry state");
            SetOverview(page, items, new HashSet<string>());
            InvokeOverview(page, "SetActionsEnabled", false);
            Check(!tabs.IsEnabled && !search.IsEnabled && Descendants(page).OfType<Border>().Where(card => card.Name == "CatalogCard")
                .SelectMany(Descendants).OfType<Button>().All(button => !button.IsEnabled), context + ": busy state disables filters and card actions without disabling Cancel");
            InvokeOverview(page, "SetFavorites", new HashSet<string>());
            Check(!tabs.IsEnabled, context + ": refresh does not re-enable busy navigation");
            InvokeOverview(page, "SetActionsEnabled", true);
            await CheckTabViewportAsync(root, page, tabs, all, context);
            root.Children.Clear();
        }

        {
            var context = "Overview tabs / Model context / " + theme;
            var page = (UserControl)Activator.CreateInstance(assembly.GetType("AIHappey.Desktop.Core.McpOverviewPage")!)!;
            root.Children.Add(page);
            var sources = Enumerable.Range(0, 8).Select(index => $"https://registry-{index}.example.invalid/v0/servers").ToArray();
            var items = sources.Select((source, index) => new McpCatalogItem("server-" + index, "Server " + index, "Description", "https://server.example.invalid/", RegistryUrl: source)).ToArray();
            InvokeOverview(page, "SetCatalog", new McpCatalogResult(items, []), sources);
            var server = new DesktopMcpServer { Id = items[0].Id, Name = items[0].Name, Url = items[0].Url };
            InvokeOverview(page, "SetInstalled", (object)new McpConnectionView[] { new(server, McpConnectionState.Connected, null, null) });
            await Task.Delay(100); page.UpdateLayout();
            var tabs = Descendants(page).OfType<NavigationView>().Single(view => view.Name == "McpFilters");
            CheckNativeTabs(tabs, context);
            Check(tabs.MenuItems.Count == 10 && CardCount(page, "McpServerCard") == 8, context + ": All, Installed and dynamic registry tabs");
            SelectNativeTab(tabs, "installed");
            Check(CardCount(page, "McpServerCard") == 1 && Descendants(page).OfType<Button>().Any(button => button.Name == "McpRemove"), context + ": Installed retains remove/manage actions");
            // Select the first registry, which remains in the primary strip at wide widths.
            SelectNativeTab(tabs, sources[0]);
            var selected = tabs.SelectedItem;
            var original = tabs.MenuItems.ToArray();
            var search = Descendants(page).OfType<TextBox>().Single(box => box.Name == "McpSearch");
            search.Text = "missing";
            await Task.Delay(80); page.UpdateLayout();
            Check(CardCount(page, "McpServerCard") == 0 && ReferenceEquals(tabs.SelectedItem, selected) && tabs.MenuItems.SequenceEqual(original), context + ": registry search retains navigation identities");
            search.Text = "";
            await Task.Delay(80);
            InvokeOverview(page, "SetCatalog", new McpCatalogResult(items, []), sources.Reverse().ToArray());
            Check(ReferenceEquals(tabs.SelectedItem, selected) && tabs.MenuItems.OfType<NavigationViewItem>().Skip(2).Select(tab => (string)tab.Tag).SequenceEqual(sources.Reverse()), context + ": registry reorder retains selected identity");
            InvokeOverview(page, "SetCatalog", new McpCatalogResult(items, []), sources.Skip(1).ToArray());
            Check((string)((NavigationViewItem)tabs.SelectedItem).Tag == "all", context + ": removed registry falls back to All");
            var all = tabs.SelectedItem;
            InvokeOverview(page, "Loading");
            Check(!tabs.IsEnabled && !search.IsEnabled && CardCount(page, "McpServerCard") == 0, context + ": loading disables navigation and hides cards");
            InvokeOverview(page, "Error", "Registry fixture failure");
            Check(tabs.IsEnabled && Descendants(page).OfType<TextBlock>().Any(text => text.Text == "Registry fixture failure"), context + ": registry error retains browsing and retry");
            InvokeOverview(page, "SetActionsEnabled", false);
            InvokeOverview(page, "SetInstalled", (object)Array.Empty<McpConnectionView>());
            Check(!tabs.IsEnabled && !search.IsEnabled, context + ": install refresh preserves busy state");
            InvokeOverview(page, "SetActionsEnabled", true);
            await CheckTabViewportAsync(root, page, tabs, all, context);
            root.Children.Clear();
        }

        foreach (var kind in new[] { CatalogKind.Agent, CatalogKind.Skill })
        {
            var context = $"Details tabs / {kind} / {theme}";
            var item = new CatalogItem(kind, "provider/fixture", "Details fixture", "Description") { Definition = Json("""{"instructions":"Read-only instructions","future":{"keep":true}}"""), Version = "1" };
            var dialog = (ContentDialog)Activator.CreateInstance(assembly.GetType("AIHappey.Desktop.Core.CatalogDetailsDialog")!, [item])!;
            dialog.XamlRoot = root.XamlRoot; SystemAppearance.PrepareDialog(dialog);
            var shown = dialog.ShowAsync(); await Task.Delay(120); dialog.UpdateLayout();
            var tabs = Descendants(dialog).OfType<NavigationView>().Single(view => view.Name == "CatalogDetailsTabs");
            CheckNativeTabs(tabs, context);
            var keys = kind == CatalogKind.Agent ? new[] { "general", "instructions", "definition" } : new[] { "general", "versions" };
            Check(tabs.MenuItems.OfType<NavigationViewItem>().Select(tab => (string)tab.Tag).SequenceEqual(keys)
                && (string)((NavigationViewItem)tabs.SelectedItem).Tag == "general", context + ": unchanged tab order and initial General selection");
            SelectNativeTab(tabs, keys[1]);
            var selected = (NavigationViewItem)tabs.SelectedItem;
            Check(selected.Focus(FocusState.Keyboard) && new NavigationViewItemAutomationPeer(selected).GetName() == selected.Content.ToString(), context + ": details keyboard focus and accessible name");
            if (kind == CatalogKind.Agent)
            {
                Check(Descendants(dialog).OfType<TextBlock>().Any(text => text.Text == "Read-only instructions"), context + ": Instructions remains read-only");
                SelectNativeTab(tabs, "definition");
                Check(Descendants(dialog).OfType<TextBlock>().Any(text => text.Text.Contains("\"future\"")), context + ": full Definition retained");
                selected = (NavigationViewItem)tabs.SelectedItem;
            }
            else
            {
                InvokeOverview(dialog, "SetVersions", Array.Empty<CatalogVersion>(), true, null);
                Check(Descendants(dialog).OfType<TextBlock>().Any(text => text.Text == DesktopResources.Get("LoadingVersions")), context + ": asynchronous Versions loading state");
                InvokeOverview(dialog, "SetVersions", Array.Empty<CatalogVersion>(), false, "Version fixture failure");
                Check(Descendants(dialog).OfType<TextBlock>().Any(text => text.Text == "Version fixture failure"), context + ": Versions error remains inside dialog");
                InvokeOverview(dialog, "SetVersions", new CatalogVersion[] { new("v1", "1", null, null, "Version description") }, false, null);
                Check(ReferenceEquals(tabs.SelectedItem, selected) && Descendants(dialog).OfType<Button>().Any(button => AutomationProperties.GetName(button) == "Download version 1"), context + ": Versions refresh retains tab and explicit download");
            }
            InvokeOverview(dialog, "SetActionsEnabled", false);
            Check(tabs.IsEnabled && dialog.CloseButtonText == DesktopResources.Get("Close"), context + ": read-only navigation and Close remain available during HTTP operations");
            await CheckTabViewportAsync(root, dialog, tabs, selected, context);
            dialog.Hide(); await shown;
        }
        File.WriteAllLines(report, results); window.Content = null;
    }

    private void CheckNativeTabs(NavigationView tabs, string context) => Check(tabs.PaneDisplayMode == NavigationViewPaneDisplayMode.Top
        && !tabs.IsSettingsVisible && !tabs.IsPaneToggleButtonVisible && !tabs.AlwaysShowHeader
        && tabs.IsBackButtonVisible == NavigationViewBackButtonVisible.Collapsed, context + ": stock top navigation matches Models configuration");

    private async Task CheckTabViewportAsync(Grid root, FrameworkElement surface, NavigationView tabs, object selected, string context)
    {
        var originalTheme = root.RequestedTheme;
        root.RequestedTheme = originalTheme == ElementTheme.Light ? ElementTheme.Dark : ElementTheme.Light;
        await Task.Delay(100); surface.UpdateLayout();
        Check(ReferenceEquals(tabs.SelectedItem, selected), context + ": runtime theme switch retains selection");
        foreach (var width in new[] { 500, 1000 })
        {
            window!.AppWindow.Resize(new Windows.Graphics.SizeInt32(width, 800)); await Task.Delay(120); surface.UpdateLayout();
            Check(tabs.ActualWidth <= root.ActualWidth && Descendants(surface).OfType<ScrollViewer>().All(view => view.ScrollableWidth < 1)
                && ReferenceEquals(tabs.SelectedItem, selected), context + $": {width}px viewport uses native overflow without horizontal scrolling or selection loss");
        }
        root.RequestedTheme = originalTheme;
        await Task.Delay(80);
    }

    private static void SelectNativeTab(NavigationView tabs, string key)
    {
        var item = tabs.MenuItems.OfType<NavigationViewItem>().Single(tab => (string)tab.Tag == key);
        ((ISelectionItemProvider)new NavigationViewItemAutomationPeer(item).GetPattern(PatternInterface.SelectionItem)).Select();
        if (tabs.XamlRoot?.Content is FrameworkElement root) root.UpdateLayout();
    }

    private static int CardCount(DependencyObject page, string name) => Descendants(page).OfType<Border>().Count(card => card.Name == name);
    private static void SetOverview(UserControl page, CatalogItem[] items, HashSet<string> favorites) => InvokeOverview(page, "SetItems", items, favorites, "backend.example");
    private static void InvokeOverview(object target, string name, params object?[] args)
    {
        target.GetType().GetMethod(name)!.Invoke(target, args);
        if (target is FrameworkElement surface) surface.UpdateLayout();
    }
}
