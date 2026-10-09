using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace AIHappey.Desktop.Core;

public sealed partial class ChatShell
{
    private readonly DesktopAgentStore agentStore;
    private IReadOnlyList<DesktopAgent> localAgents = [];
    private string? localAgentPartition;
    private AgentEditDialog? agentEditor;
    private void PrepareAgentActions()
    {
        var menu = new MenuFlyout(); var create = new MenuFlyoutItem { Text = DesktopResources.Get("AgentCreate"), Icon = new SymbolIcon(Symbol.Add) };
        var import = new MenuFlyoutItem { Text = DesktopResources.Get("AgentImport"), Icon = new SymbolIcon(Symbol.OpenFile) };
        menu.Items.Add(create); menu.Items.Add(import); agentsOverview.AddAgent.Flyout = menu;
        create.Click += async (_, _) => await EditAgentAsync(null, agentsOverview.AddAgent);
        import.Click += async (_, _) => await ImportAgentsAsync();
        agentsOverview.EditRequested = async (item, owner) => await EditAgentAsync(item, owner);
        agentsOverview.DeleteRequested = async item => await DeleteAgentAsync(item);
    }
    private async Task LoadLocalAgentsAsync(CancellationToken ct)
    {
        var partition = session.AgentPartition;
        var items = await agentStore.ListAsync(partition, session.DefaultAgents, ct);
        ct.ThrowIfCancellationRequested(); if (session.AgentPartition != partition || closing) return;
        localAgents = items; localAgentPartition = partition;
    }
    private static IReadOnlyList<ChatTarget> ProjectAgentTargets(IEnumerable<CatalogItem> items) => DesktopAgentTargets.Project(items);
    private async Task<IReadOnlyList<CatalogItem>> LoadCatalogItemsAsync(CatalogKind kind, CancellationToken ct)
    {
        if (kind != CatalogKind.Agent) return await catalogClient.ListAsync(kind, ct);
        await LoadLocalAgentsAsync(ct);
        try { return await catalogClient.ListAsync(kind, ct); }
        catch (Exception error) when (error is not OperationCanceledException && localAgents.Count > 0)
        { Show(DesktopResources.Get("AgentBackendUnavailable"), InfoBarSeverity.Warning); return []; }
    }
    private async Task<IReadOnlyList<ChatTarget>> AgentTargetsAsync(CancellationToken ct)
    {
        var backend = await LoadCatalogItemsAsync(CatalogKind.Agent, ct);
        return ProjectAgentTargets(backend.Concat(localAgents.Select(a => a.CatalogItem())));
    }
    private async Task RefreshLocalAgentsAsync(CancellationToken ct)
    {
        await LoadLocalAgentsAsync(ct);
        catalogs.TryGetValue(CatalogKind.Agent, out var previous);
        var items = (previous ?? []).Where(i => i.Origin == CatalogOrigin.Backend).Concat(localAgents.Select(a => a.CatalogItem())).ToArray();
        catalogs[CatalogKind.Agent] = items;
        var config = session.Settings.Agents;
        agentsOverview.SetItems(items, favorites, config.Location == RuntimeLocation.Local ? "localhost" : DesktopSettings.RemoteUri(config.RemoteUrl).Host);
        if (Service == ServiceKind.Agents) { targets = ProjectAgentTargets(items); UpdateTargetSuggestions(); }
    }
    private async Task EditAgentAsync(CatalogItem? item, Button owner)
    {
        if (busy || closing || historyDialogOpen || agentEditor is not null || catalogDialog is not null) return;
        historyDialogOpen = true; var partition = session.AgentPartition;
        try
        {
            await LoadLocalAgentsAsync(downloadLifetime.Token);
            var agent = item is null ? DesktopAgent.Empty() : localAgents.FirstOrDefault(a => a.Name == item.Id)?.Clone();
            if (agent is null) { Show(DesktopResources.Get("AgentUnavailable"), InfoBarSeverity.Warning); return; }
            IReadOnlyList<ChatTarget> models;
            try { models = aiModelTargets ?? await client.ListAsync(ServiceKind.Ai, downloadLifetime.Token); }
            catch (Exception error) when (error is not OperationCanceledException) { models = []; }
            if (closing || partition != session.AgentPartition) return;
            agentEditor = new(agent, item is not null, session, catalogClient, mcpCatalogHttp, models, Mcp.Capture()) { XamlRoot = XamlRoot };
            agentEditor.SaveAsync = async (value, ct) =>
            {
                ct.ThrowIfCancellationRequested(); if (partition != session.AgentPartition) throw new OperationCanceledException();
                await agentStore.SaveAsync(partition, value, item?.Id, ct);
            };
            SystemAppearance.PrepareDialog(agentEditor); await agentEditor.ShowAsync();
            if (!closing && partition == session.AgentPartition) await RefreshLocalAgentsAsync(downloadLifetime.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { Show(error is InvalidOperationException or InvalidDataException ? error.Message : DesktopResources.Get("ChatSettingsSaveFailed"), InfoBarSeverity.Error); }
        finally { agentEditor = null; historyDialogOpen = false; if (!closing && owner.IsLoaded) owner.Focus(FocusState.Programmatic); }
    }
    private async Task ImportAgentsAsync()
    {
        if (busy || closing || historyDialogOpen) return;
        await RunAsync(async ct =>
        {
            var partition = session.AgentPartition; await LoadLocalAgentsAsync(ct);
            var picker = new FileOpenPicker(); picker.FileTypeFilter.Add(".json");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, Microsoft.UI.Win32Interop.GetWindowFromWindowId(XamlRoot.ContentIslandEnvironment.AppWindowId));
            var files = await picker.PickMultipleFilesAsync(); if (files.Count == 0) return;
            var errors = new List<string>(); var imported = 0;
            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested(); if (session.AgentPartition != partition || closing) throw new OperationCanceledException();
                try
                {
                    if ((await file.GetBasicPropertiesAsync()).Size > DesktopAgent.MaxDocumentBytes) throw new InvalidDataException(DesktopResources.Get("ResponseSizeLimit"));
                    await using var input = await file.OpenStreamForReadAsync(); using var content = new StreamContent(input);
                    var bytes = await DesktopCatalogClient.ReadBoundedAsync(content, DesktopAgent.MaxDocumentBytes, ct);
                    var agent = DesktopAgent.Parse(System.Text.Encoding.UTF8.GetString(bytes));
                    await agentStore.SaveAsync(partition, agent, null, ct); imported++;
                }
                catch (Exception error) when (error is not OperationCanceledException)
                { errors.Add(file.Name + ": " + (error is JsonException ? DesktopResources.Get("InvalidCatalogDocument") : error.Message)); }
            }
            await RefreshLocalAgentsAsync(ct);
            Show(DesktopResources.Format("AgentImportedCount", imported) + (errors.Count > 0 ? "\n" + string.Join("\n", errors) : ""), errors.Count > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success);
        });
    }
    private async Task DeleteAgentAsync(CatalogItem item)
    {
        if (busy || closing || historyDialogOpen || item.Origin != CatalogOrigin.Local) return;
        historyDialogOpen = true;
        var partition = session.AgentPartition;
        try
        {
            var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = DesktopResources.Get("Delete"), Content = DesktopResources.Format("AgentDeleteConfirm", item.Name),
                PrimaryButtonText = DesktopResources.Get("Delete"), CloseButtonText = DesktopResources.Get("Cancel"), DefaultButton = ContentDialogButton.Close };
            SystemAppearance.PrepareDialog(dialog); if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            if (partition != session.AgentPartition || closing) return;
            await agentStore.DeleteAsync(partition, item.Id, downloadLifetime.Token);
            favorites.Remove(item.Key); await catalogFavorites.SaveAsync(session.HistoryPartition, favorites, downloadLifetime.Token);
            await RefreshLocalAgentsAsync(downloadLifetime.Token);
        }
        catch (OperationCanceledException) { }
        catch { Show(DesktopResources.Get("ChatSettingsSaveFailed"), InfoBarSeverity.Error); }
        finally { historyDialogOpen = false; }
    }
}
