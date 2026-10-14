using FluentIcons.Common;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Text;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace AIHappey.Desktop.Core;

public sealed partial class ChatShell
{
    private readonly DesktopAgentPluginStore pluginStore;
    private readonly PluginsOverviewPage pluginsOverview = new();
    private IReadOnlyList<DesktopAgentPlugin> agentPlugins = [];
    private string? pluginPartition;
    private PluginEditDialog? pluginDialog;
    private ContentDialog? pluginSelectionDialog;
    private int pluginEpoch;
    private readonly Button addPlugin = new() { Name = "AddPlugin", Content = DesktopIcons.Create(Icon.Add), Width = 40, Height = 40, Padding = new Thickness(0), Visibility = Visibility.Collapsed };
    private void PreparePlugins()
    {
        var menu = new MenuFlyout(); addPlugin.Flyout = menu; ControlAppearance.Stock(addPlugin); ToolbarControls.Label(addPlugin, DesktopResources.Get("Add"));
        foreach (var (key, action) in new (string, Func<Task>)[] { ("PluginCreate", () => EditPluginAsync(null, false)), ("PluginImport", () => PickPluginsAsync(false)), ("PluginLoadFolder", () => PickPluginsAsync(true)) })
        { var item = new MenuFlyoutItem { Text = DesktopResources.Get(key) }; ControlAppearance.Native(item); item.Click += async (_, _) => await action(); menu.Items.Add(item); }
        pluginsOverview.DetailsRequested = plugin => EditPluginAsync(plugin, true);
        pluginsOverview.DownloadRequested = DownloadPluginAsync;
        pluginsOverview.DeleteRequested = DeletePluginAsync;
        pluginsOverview.FavoriteRequested = plugin => RunAsync(async ct => { var partition = session.PluginPartition; await pluginStore.FavoriteAsync(partition, plugin.Id, !plugin.Favorite, ct); await LoadPluginsAsync(ct); });
        pluginsOverview.ImportRequested = ImportPluginsAsync;
        if (addContext.Flyout is MenuFlyout context)
        {
            var select = new MenuFlyoutItem { Name = "SelectAgentPlugins", Text = DesktopResources.Get("PluginsTitle"), Icon = DesktopIcons.Create(Icon.PuzzlePiece) }; ControlAppearance.Native(select);
            select.Click += async (_, _) => await SelectPluginsAsync(); context.Items.Add(select);
        }
    }
    private bool CurrentPluginPartition(string partition) => !closing && session.PluginPartition == partition;
    private async Task LoadPluginsAsync(CancellationToken ct)
    {
        var partition = session.PluginPartition; var plugins = await pluginStore.ListAsync(partition, ct); ct.ThrowIfCancellationRequested();
        if (!CurrentPluginPartition(partition)) return;
        agentPlugins = plugins; pluginPartition = partition; pluginEpoch++;
        await Mcp.ReconcilePluginsAsync(AgentPluginRuntime.Servers(EnabledPlugins(session.Settings.Chat), session.PluginExtensionNamespace), ct);
        if (!CurrentPluginPartition(partition)) return;
        pluginsOverview.SetItems(agentPlugins); RenderContextTags();
    }
    private IReadOnlyList<DesktopAgentPlugin> EnabledPlugins(ChatPreferences preferences) => pluginPartition == session.PluginPartition
        ? agentPlugins.Where(p => preferences.EnabledAgentPluginIds.Contains(p.Id, StringComparer.Ordinal)).ToArray() : [];
    private async Task<IReadOnlyList<DesktopSkillFile>> SnapshotPluginSkillAsync(string id, string? version, CancellationToken ct)
    {
        var partition = session.HistoryPartition; await LoadRuntimeSkillsAsync(ct);
        IReadOnlyList<DesktopSkillFile> files; string name;
        var local = localSkills.FirstOrDefault(s => s.Id == id || s.Name == id);
        if (local is not null)
        {
            var draft = await localSkillStore.ReadAsync(partition, local, ct); DesktopSkillPackages.Validate(draft); name = draft.Name;
            files = draft.Files.Append(new DesktopSkillFile("SKILL.md", Encoding.UTF8.GetBytes(DesktopSkillPackages.Markdown(draft)))).ToArray();
        }
        else
        {
            var item = runtimeSkillCatalog.FirstOrDefault(i => i.Id == id || i.Name == id) ?? throw new InvalidDataException("Skill catalog item not found.");
            if (version is { Length: > 0 }) item = item with { Version = version, LatestVersion = version };
            var content = await skillStore.ReadAsync(partition, item, ct); name = content.Skill.Name;
            var list = new List<DesktopSkillFile> { await content.Read("SKILL.md", ct) };
            foreach (var path in content.ResourcePaths) list.Add(await content.Read(path, ct)); files = list;
        }
        ct.ThrowIfCancellationRequested(); if (closing || partition != session.HistoryPartition) throw new OperationCanceledException(ct);
        return files.Select(f => new DesktopSkillFile("skills/" + name + "/" + AgentPluginPackages.PackagePath(f.Path), f.Data.ToArray())).ToArray();
    }
    private async Task EditPluginAsync(DesktopAgentPlugin? plugin, bool detailsOnly)
    {
        if (busy || closing || historyDialogOpen) return;
        var partition = session.PluginPartition; historyDialogOpen = true;
        try
        {
            try { await LoadRuntimeSkillsAsync(downloadLifetime.Token); } catch (Exception e) when (e is not OperationCanceledException) { }
            if (mcpCatalog is null)
                try { mcpCatalog = await new DesktopMcpCatalogClient(mcpCatalogHttp).ListAsync(session.ContextOptions.McpCatalogUrls ?? [], downloadLifetime.Token); } catch (Exception e) when (e is not OperationCanceledException) { }
            if (!CurrentPluginPartition(partition)) return;
            var dialog = new PluginEditDialog(plugin, detailsOnly, session.PluginExtensionNamespace,
                AvailableSkillDescriptors().Where(s => s.Origin is "local" or "remote").ToArray(), (id, ct) => SnapshotPluginSkillAsync(id, null, ct), mcpCatalog?.Items ?? []) { XamlRoot = XamlRoot };
            if (detailsOnly)
            {
                dialog.PrimaryButtonText = DesktopResources.Get("Edit");
                // Details closes before a separate native editor opens (WinUI permits one ContentDialog).
                dialog.PrimaryButtonClick += (_, _) => { };
            }
            dialog.SaveAsync = async (value, ct) => { if (!CurrentPluginPartition(partition)) throw new OperationCanceledException(ct); await pluginStore.SaveAsync(partition, value, plugin is not null, ct); };
            pluginDialog = dialog; SystemAppearance.PrepareDialog(dialog); var result = await dialog.ShowAsync();
            pluginDialog = null;
            if (detailsOnly && result == ContentDialogResult.Primary && CurrentPluginPartition(partition)) { historyDialogOpen = false; await EditPluginAsync(plugin, false); return; }
            if (CurrentPluginPartition(partition)) await LoadPluginsAsync(downloadLifetime.Token);
        }
        catch (OperationCanceledException) { }
        catch { Show(DesktopResources.Get("PluginOperationFailed"), InfoBarSeverity.Error); }
        finally { pluginDialog = null; historyDialogOpen = false; if (!closing) addPlugin.Focus(FocusState.Programmatic); }
    }
    private void InitializePluginPicker(object picker) => WinRT.Interop.InitializeWithWindow.Initialize(picker, Microsoft.UI.Win32Interop.GetWindowFromWindowId(XamlRoot.ContentIslandEnvironment.AppWindowId));
    private async Task PickPluginsAsync(bool folder)
    {
        if (busy || closing || historyDialogOpen) return;
        if (folder)
        {
            await RunAsync(async ct =>
            {
                var partition = session.PluginPartition; var picker = new FolderPicker(); picker.FileTypeFilter.Add("*"); InitializePluginPicker(picker);
                var selected = await picker.PickSingleFolderAsync().AsTask(ct); if (selected is null) return;
                var plugin = await AgentPluginPackages.LoadDirectoryAsync(selected.Path, ct);
                if (!CurrentPluginPartition(partition)) throw new OperationCanceledException(ct);
                await pluginStore.SaveAsync(partition, plugin, true, ct); await LoadPluginsAsync(ct);
                Show(DesktopResources.Format("PluginImported", 1), InfoBarSeverity.Success);
            });
        }
        else
        {
            var picker = new FileOpenPicker(); picker.FileTypeFilter.Add(".zip"); InitializePluginPicker(picker);
            await ImportPluginsAsync(await picker.PickMultipleFilesAsync());
        }
    }
    private async Task ImportPluginsAsync(IReadOnlyList<StorageFile> files)
    {
        if (busy || closing || historyDialogOpen) return;
        await RunAsync(async ct =>
        {
            var partition = session.PluginPartition; var count = 0; var failures = new List<string>();
            foreach (var file in files)
            {
                try
                {
                    if (!file.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException(DesktopResources.Get("PluginZipOnly"));
                    await using var stream = await file.OpenStreamForReadAsync(); var bytes = await AgentPluginPackages.ReadBoundedAsync(stream, stream.Length, ct);
                    foreach (var plugin in await AgentPluginPackages.ImportAsync(bytes, ct)) { if (!CurrentPluginPartition(partition)) throw new OperationCanceledException(ct); await pluginStore.SaveAsync(partition, plugin, true, ct); count++; }
                }
                catch (Exception e) when (e is not OperationCanceledException) { failures.Add(file.Name + ": " + (e is InvalidDataException ? e.Message : DesktopResources.Get("PluginOperationFailed"))); }
            }
            await LoadPluginsAsync(ct); Show(DesktopResources.Format("PluginImported", count) + (failures.Count > 0 ? "\n" + string.Join("\n", failures) : ""), failures.Count > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success);
        });
    }
    private async Task DownloadPluginAsync(DesktopAgentPlugin plugin)
    {
        if (busy || closing || historyDialogOpen) return;
        await RunAsync(async ct => { var partition = session.PluginPartition; var picker = new FileSavePicker { SuggestedFileName = plugin.Id }; picker.FileTypeChoices.Add(DesktopResources.Get("PluginArchive"), new List<string> { ".zip" }); InitializePluginPicker(picker);
            var file = await picker.PickSaveFileAsync().AsTask(ct); if (file is not null && CurrentPluginPartition(partition)) await FileIO.WriteBytesAsync(file, AgentPluginPackages.Export(plugin)); });
    }
    private async Task DeletePluginAsync(DesktopAgentPlugin plugin)
    {
        if (busy || closing || historyDialogOpen) return; var partition = session.PluginPartition; historyDialogOpen = true;
        try
        {
            var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = DesktopResources.Get("Delete"), Content = DesktopResources.Format("PluginDeleteConfirm", plugin.Id), PrimaryButtonText = DesktopResources.Get("Delete"), CloseButtonText = DesktopResources.Get("Cancel"), DefaultButton = ContentDialogButton.Close };
            pluginSelectionDialog = dialog; SystemAppearance.PrepareDialog(dialog);
            if (await dialog.ShowAsync() != ContentDialogResult.Primary || !CurrentPluginPartition(partition)) return;
            var next = session.Settings.Clone(); next.Chat.EnabledAgentPluginIds.RemoveAll(id => id == plugin.Id); await SettingsStore.SaveAsync(session.DataDirectory, next); session.Settings = next;
            await pluginStore.DeleteAsync(partition, plugin.Id, downloadLifetime.Token); await LoadPluginsAsync(downloadLifetime.Token);
        }
        catch (OperationCanceledException) { } catch { Show(DesktopResources.Get("PluginOperationFailed"), InfoBarSeverity.Error); }
        finally { pluginSelectionDialog = null; historyDialogOpen = false; }
    }
    private async Task SelectPluginsAsync()
    {
        if (busy || closing || historyDialogOpen || Service != ServiceKind.Ai) return; var partition = session.PluginPartition; historyDialogOpen = true;
        try
        {
            await LoadPluginsAsync(downloadLifetime.Token); var selected = session.Settings.Chat.EnabledAgentPluginIds.ToList();
            var view = new PluginsSettingsView(agentPlugins, selected) { Width = Math.Max(0, Math.Min(640, XamlRoot.Size.Width - 96)), MaxHeight = Math.Max(0, XamlRoot.Size.Height - 240) };
            var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = DesktopResources.Get("PluginsTitle"), Content = new ScrollViewer { Content = view }, PrimaryButtonText = DesktopResources.Get("Save"), CloseButtonText = DesktopResources.Get("Cancel") };
            pluginSelectionDialog = dialog; SystemAppearance.PrepareDialog(dialog);
            if (await dialog.ShowAsync() == ContentDialogResult.Primary && CurrentPluginPartition(partition)) { var next = session.Settings.Clone(); next.Chat.EnabledAgentPluginIds = selected; await SettingsStore.SaveAsync(session.DataDirectory, next); session.Settings = next; await LoadPluginsAsync(downloadLifetime.Token); }
        }
        catch (OperationCanceledException) { } catch { Show(DesktopResources.Get("PluginOperationFailed"), InfoBarSeverity.Error); }
        finally { pluginSelectionDialog = null; historyDialogOpen = false; if (!closing) addContext.Focus(FocusState.Programmatic); }
    }
    private void RenderPluginTags()
    {
        if (Service != ServiceKind.Ai) return;
        foreach (var id in session.Settings.Chat.EnabledAgentPluginIds)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 }; panel.Children.Add(DesktopIcons.Create(Icon.PuzzlePiece, 14)); panel.Children.Add(new TextBlock { Text = id, VerticalAlignment = VerticalAlignment.Center });
            var remove = new Button { Name = "DisableAgentPlugin", Content = DesktopIcons.Create(Icon.Dismiss, 10), Tag = id, Padding = new Thickness(2), IsEnabled = !busy }; ToolbarControls.Subtle(remove); ToolbarControls.Label(remove, DesktopResources.Format("PluginDisable", id));
            remove.Click += async (_, _) => await RunAsync(async ct => { var next = session.Settings.Clone(); next.Chat.EnabledAgentPluginIds.RemoveAll(p => p == id); await SettingsStore.SaveAsync(session.DataDirectory, next); session.Settings = next; await LoadPluginsAsync(ct); }); panel.Children.Add(remove);
            var badge = new Border { Name = "EnabledAgentPluginBadge", Child = panel, Padding = new Thickness(10, 2, 4, 2), CornerRadius = new CornerRadius(16), Tag = id }; ControlAppearance.TokenBadge(badge); contextTags.Children.Add(badge);
        }
    }
    private void RegisterAgentPluginTools(McpTurnSnapshot snapshot, ChatPreferences preferences)
    {
        var partition = session.PluginPartition; var enabled = EnabledPlugins(preferences).Select(p => p.Clone()).ToArray(); var epoch = pluginEpoch;
        bool Current() => CurrentPluginPartition(partition);
        if (enabled.Length > 0)
        {
            snapshot.AddContext(AgentPluginRuntime.Context(enabled));
            snapshot.AddLocal(AgentPluginTools.ReadDefinition, (input, ct) => Task.FromResult(AgentPluginTools.ReadPackage(enabled, input, () => Current() && epoch == pluginEpoch, ct)));
        }
        if (preferences.PluginEnabled(AgentPluginTools.EditorId))
        {
            var tools = new AgentPluginTools(pluginStore, partition, Current, async ct =>
            {
                var ids = (await pluginStore.ListAsync(partition, ct)).Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
                var next = session.Settings.Clone(); next.Chat.EnabledAgentPluginIds.RemoveAll(id => !ids.Contains(id)); await SettingsStore.SaveAsync(session.DataDirectory, next); session.Settings = next; await LoadPluginsAsync(ct);
            }, SnapshotPluginSkillAsync, async ct => { await LoadRuntimeSkillsAsync(ct); return AvailableSkillDescriptors().Where(s => s.Origin is "local" or "remote").ToArray(); });
            DesktopLocalTools.Register(snapshot, AgentPluginTools.EditorId, tools.CallAsync);
        }
    }
}
