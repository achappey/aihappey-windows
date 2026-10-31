using FluentIcons.Common;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AIHappey.Desktop.Core;

public sealed partial class ChatShell
{
    private readonly MenuFlyoutItem searchContext = new() { Name = "SearchContext", Text = DesktopResources.Get("Search"), Icon = DesktopIcons.Create(Icon.Search) };
    private ContextSearchDialog? contextSearchDialog;

    private async Task SearchContextAsync()
    {
        if (busy || closing || historyDialogOpen || catalogDialog is not null || Service != ServiceKind.Ai || !initialized) return;
        var partition = session.HistoryPartition;
        var pluginScope = session.PluginPartition;
        bool Current() => !closing && partition == session.HistoryPartition && pluginScope == session.PluginPartition;
        void RequireCurrent(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (!Current()) throw new OperationCanceledException(ct);
        }
        var dialog = new ContextSearchDialog(session.Settings.Chat, agentPlugins, async ct =>
        {
            RequireCurrent(ct); var selection = await LoadSkillSelectionAsync(ct); RequireCurrent(ct); return selection;
        }, async (id, ct) => { RequireCurrent(ct); await PrefetchSkillAsync(id, ct); }, async ct =>
        {
            RequireCurrent(ct);
            var result = await new DesktopMcpCatalogClient(mcpCatalogHttp).ListAsync(session.ContextOptions.McpCatalogUrls ?? [], ct);
            RequireCurrent(ct); mcpCatalog = result; return result;
        }, () => Mcp.Servers.Select(view => view.Server).ToArray(), async (item, install, ct) =>
        {
            RequireCurrent(ct);
            // Match both registry identity and endpoint so manually added servers are not duplicated.
            var existing = Mcp.Servers.FirstOrDefault(view => view.Server.Id.Equals(item.Id, StringComparison.OrdinalIgnoreCase)
                || view.Server.Name.Equals(item.Name, StringComparison.OrdinalIgnoreCase) || view.Server.Url.Equals(item.Url, StringComparison.OrdinalIgnoreCase));
            if (install && existing is null)
            {
                var server = item.CreateServer(); server.Headers = McpHeaderConfiguration.Initial(item);
                await Mcp.InstallAsync(server, ct);
            }
            else if (!install && existing is not null) await Mcp.RemoveAsync(existing.Server.Id, ct);
        }) { XamlRoot = XamlRoot };
        dialog.SaveAsync = async preferences =>
        {
            RequireCurrent(downloadLifetime.Token);
            var next = session.Settings.Clone(); next.Chat = preferences.Clone();
            await SettingsStore.SaveAsync(session.DataDirectory, next);
            RequireCurrent(downloadLifetime.Token); session.Settings = next;
            await LoadPluginsAsync(downloadLifetime.Token); RenderContextTags();
        };
        contextSearchDialog = dialog; historyDialogOpen = true; SystemAppearance.PrepareDialog(dialog);
        try { await dialog.ShowAsync(); }
        finally
        {
            contextSearchDialog = null; historyDialogOpen = false;
            if (Current()) addContext.Focus(FocusState.Programmatic);
        }
        if (Current() && dialog.Result is not null && dialog.ServerConfigurationRequested is { } configure) await ManageMcpAsync(edit: configure);
        else if (Current() && dialog.Result is not null && dialog.CatalogInstallHappened) await ManageMcpAsync();
    }
}
