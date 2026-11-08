using AIHappey.Desktop.Core;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace AIHappey.Desktop.UiTests;

public partial class App
{
    private async Task CheckMcpCatalogAsync(ElementTheme theme)
    {
        var root = new Grid { RequestedTheme = theme };
        window!.Content = root; window.Activate(); await Task.Delay(100);
        var scale = root.XamlRoot.RasterizationScale;
        window.AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(1000 * scale), (int)(800 * scale)));
        await CheckMcpHeaderEditorAsync(root, "MCP catalog management / " + theme);
        await CheckMcpCatalogDialogAsync(root, theme);
        window.Content = null;
    }

    private async Task CheckMcpCatalogDialogAsync(Grid root, ElementTheme theme)
    {
        var context = "MCP catalog dialog / " + theme;
        var assembly = typeof(ChatShell).Assembly;
        var type = assembly.GetType("AIHappey.Desktop.Core.McpCatalogDialog")!;
        var stateType = assembly.GetType("AIHappey.Desktop.Core.McpDialogState")!;
        var state = Activator.CreateInstance(stateType)!;
        var items = Enumerable.Range(0, 25).Select(i => new McpCatalogItem("catalog" + i, "Catalog server " + i,
            "Complete server description", "https://catalog" + i + ".example/mcp", "1")).ToArray();
        items[2] = items[2] with { HeaderDefinitions = [new() { Name = "X-Api-Key", IsRequired = true, IsSecret = true }] };
        var installed = new List<DesktopMcpServer> { new() { Id = "manual", Name = "Manual server", Url = items[0].Url },
            new() { Id = items[1].Id, Name = items[1].Name, Url = items[1].Url, PluginId = "plugin" } };
        var loaded = new TaskCompletionSource<McpCatalogResult>();
        TaskCompletionSource? mutation = null;
        var loads = 0; var mutations = 0; var failMutation = true;
        Func<CancellationToken, Task<McpCatalogResult>> load = ct => ++loads == 1 ? loaded.Task.WaitAsync(ct) : Task.FromResult(new McpCatalogResult(items, []));
        Func<IReadOnlyList<DesktopMcpServer>> read = () => installed;
        Func<McpCatalogItem, bool, CancellationToken, Task> change = async (item, install, ct) =>
        {
            mutations++;
            if (mutation is not null) await mutation.Task.WaitAsync(ct);
            if (failMutation) throw new IOException("private disk detail");
            if (install) installed.Add(item.CreateServer());
            else installed.RemoveAll(server => server.Id == item.Id || server.Url == item.Url);
        };
        ContentDialog Create() => (ContentDialog)Activator.CreateInstance(type, [load, read, change, true, state])!;
        var dialog = Create(); dialog.XamlRoot = root.XamlRoot;
        SystemAppearance.PrepareDialog(dialog); var shown = dialog.ShowAsync();
        try
        {
            await WaitForSkillUiAsync(() => loads == 1);
            TextBox Query() => Descendants(dialog).OfType<TextBox>().Single(box => box.Name == "McpCatalogQuery");
            Button Button(string name) => Descendants(dialog).OfType<Button>().Single(button => button.Name == name);
            SettingsExpander[] Rows() => Descendants(dialog).OfType<SettingsExpander>().Where(row => row.Name == "McpCatalogRow").ToArray();
            Button Action() => Descendants((DependencyObject)Rows().Single().ItemsHeader).OfType<Button>()
                .Single(button => button.Name is "McpCatalogInstall" or "McpCatalogRemove");
            var progress = Descendants(dialog).OfType<ProgressBar>().Single();
            Check(progress.IsIndeterminate && !Descendants(dialog).OfType<NavigationView>().Any()
                && dialog.CloseButtonText == "Close", context + ": focused modal has search, progress and Close without extra tabs");
            loaded.SetResult(new(items, ["https://offline.example"]));
            await WaitForSkillUiAsync(() => Rows().Length == 20); dialog.UpdateLayout();
            Check(Rows().All(row => !row.IsExpanded && row.Content is null
                    && Descendants((DependencyObject)row.Header).OfType<UserControl>().Any(icon => icon.Name == "McpServerIcon"))
                && !Descendants(dialog).OfType<ToggleSwitch>().Any()
                && Button("McpCatalogRetry").Visibility == Visibility.Visible && !progress.IsIndeterminate,
                context + ": paginated partial results use collapsed logo headers without installation switches");
            InvokeButton(Button("McpCatalogMore")); await Task.Delay(60);
            Check(Rows().Length == 25, context + ": Show more reveals remaining results");
            Query().Text = items[0].Url;
            await WaitForSkillUiAsync(() => Rows().Length == 1 && Rows().Single().Tag as string == items[0].Id);
            Rows().Single().IsExpanded = true; await Task.Delay(80);
            Check(Action().Name == "McpCatalogRemove" && Action().Content is IconElement
                && new ButtonAutomationPeer(Action()).GetName().Contains(items[0].Name)
                && ToolTipService.GetToolTip(Action()) is not null,
                context + ": endpoint matching gives manual installations an accessible icon-only uninstall button");
            InvokeButton(Action()); await Task.Delay(80);
            Check(installed.Count == 2 && Action().Name == "McpCatalogRemove"
                && !Descendants(dialog).OfType<TextBlock>().Single(text => text.Name == "McpCatalogError").Text.Contains("private disk"),
                context + ": failed removal retains installation and sanitized retryable feedback");
            failMutation = false; mutation = new(); InvokeButton(Action()); await Task.Delay(60);
            InvokeButton(Button("CloseButton")); await Task.Delay(60);
            Check(shown.Status == Windows.Foundation.AsyncStatus.Started && !Query().IsEnabled && progress.IsIndeterminate,
                context + ": pending mutations block duplicate editing and closing");
            mutation.SetResult(); await WaitForSkillUiAsync(() => installed.Count == 1 && !Field<bool>(dialog, "pending"));
            mutation = null;
            Check(Rows().Single().IsExpanded && Action().Name == "McpCatalogInstall", context + ": removal retains expansion and changes footer to Install");
            InvokeButton(Action()); await WaitForSkillUiAsync(() => installed.Count == 2 && !Field<bool>(dialog, "pending"));
            Check(!installed.Single(server => server.Id == items[0].Id).Enabled && Action().Name == "McpCatalogRemove",
                context + ": installation changes footer to Uninstall without enabling a server");
            await WaitForSkillUiAsync(() => Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(root.XamlRoot) is Button { Name: "McpCatalogRemove" });
            Query().Text = items[1].Url;
            await WaitForSkillUiAsync(() => Rows().Length == 1 && Rows().Single().Tag as string == items[1].Id);
            Check(!Action().IsEnabled, context + ": plugin-owned servers cannot be independently uninstalled");
            Query().Text = items[2].Url;
            await WaitForSkillUiAsync(() => Rows().Length == 1 && Rows().Single().Tag as string == items[2].Id);
            Rows().Single().IsExpanded = true; await Task.Delay(80);
            var previousMutations = mutations;
            InvokeButton(Action()); await shown;
            Check(type.GetProperty("ServerConfigurationRequested")!.GetValue(dialog) is DesktopMcpServer requested
                && requested.Id == items[2].Id && requested.HeaderDefinitions.Count == 1 && mutations == previousMutations,
                context + ": required configuration hands off without a premature installation");
            installed.Add(items[2].CreateServer());
            dialog = Create(); dialog.XamlRoot = root.XamlRoot; SystemAppearance.PrepareDialog(dialog);
            shown = dialog.ShowAsync(); await Task.Delay(150); dialog.UpdateLayout();
            Check(Query().Text == items[2].Url && Rows().Single().IsExpanded && Action().Name == "McpCatalogRemove" && loads == 1,
                context + ": returning from configuration retains search, expansion and loaded catalog while refreshing installation");
            Query().Text = "no-matching-server"; await WaitForSkillUiAsync(() => Rows().Length == 0);
            Check(Rows().Length == 0, context + ": unmatched search has no server rows");
            Query().Text = ""; await WaitForSkillUiAsync(() => Rows().Length == 20);
            foreach (var row in Rows()) row.IsExpanded = true;
            var scale = root.XamlRoot.RasterizationScale;
            foreach (var (width, height) in new[] { (500, 600), (1000, 800) })
            {
                window!.AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(width * scale), (int)(height * scale)));
                await Task.Delay(180); dialog.UpdateLayout();
                var viewer = Field<ScrollViewer>(dialog, "viewer");
                var close = Button("CloseButton");
                Check(Descendants(dialog).OfType<ScrollViewer>().All(scroll => scroll.ScrollableWidth < 1)
                    && viewer.TransformToVisual(dialog).TransformPoint(new()).Y + viewer.ActualHeight <= close.TransformToVisual(dialog).TransformPoint(new()).Y + 1
                    && Query().TransformToVisual(dialog).TransformPoint(new()).Y < viewer.TransformToVisual(dialog).TransformPoint(new()).Y
                    && close.Focus(FocusState.Keyboard), context + $": {width}x{height} keeps search above scrolling rows and Close below, without horizontal overflow");
            }
            InvokeButton(Button("McpCatalogRetry")); await WaitForSkillUiAsync(() => loads == 2 && !Field<bool>(dialog, "loading"));
            Check(Button("McpCatalogRetry").Visibility == Visibility.Collapsed, context + ": retry clears registry failure warning");
            InvokeButton(Button("CloseButton")); await shown;
        }
        finally
        {
            if (shown.Status == Windows.Foundation.AsyncStatus.Started) { type.GetMethod("Shutdown")!.Invoke(dialog, null); await shown; }
        }

        var canceled = false;
        Func<CancellationToken, Task<McpCatalogResult>> wait = async ct =>
        {
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { canceled = true; throw; }
            return new([], []);
        };
        dialog = (ContentDialog)Activator.CreateInstance(type, [wait, read, change, true, Activator.CreateInstance(stateType)!])!;
        dialog.XamlRoot = root.XamlRoot; shown = dialog.ShowAsync(); await Task.Delay(100);
        type.GetMethod("Shutdown")!.Invoke(dialog, null); await shown;
        await WaitForSkillUiAsync(() => canceled);
        Check(!Descendants(dialog).OfType<ProgressBar>().Single().IsIndeterminate, context + ": shutdown cancels outstanding registry loading and stops progress");
    }
}
