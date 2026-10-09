using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AIHappey.Desktop.Core;

public sealed partial class ChatShell
{
    private readonly DesktopSkillStore skillStore;
    private readonly SemaphoreSlim skillCatalogGate = new(1, 1);
    private IReadOnlyList<CatalogItem> runtimeSkillCatalog = [];
    private string? runtimeSkillPartition;

    private async Task LoadRuntimeSkillsAsync(CancellationToken ct, bool refresh = false)
    {
        await skillCatalogGate.WaitAsync(ct);
        try
        {
            var partition = session.HistoryPartition;
            if (!refresh && runtimeSkillPartition == partition) return;
            var cached = await skillStore.CatalogAsync(partition, ct);
            if (runtimeSkillPartition != partition) { runtimeSkillCatalog = cached; runtimeSkillPartition = null; }
            try
            {
                var items = await catalogClient.ListAsync(CatalogKind.Skill, ct);
                ct.ThrowIfCancellationRequested();
                if (closing || session.HistoryPartition != partition) return;
                await skillStore.SaveCatalogAsync(partition, items, ct);
                runtimeSkillCatalog = items; runtimeSkillPartition = partition;
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                if (session.HistoryPartition != partition || closing) return;
                if (cached.Count == 0) throw;
                runtimeSkillCatalog = cached; runtimeSkillPartition = partition;
            }
            RenderContextTags();
        }
        finally { skillCatalogGate.Release(); }
    }
    private async Task<DesktopSkillSelection> LoadSkillSelectionAsync(CancellationToken ct)
    {
        string? warning = null;
        try { await LoadRuntimeSkillsAsync(ct, refresh: true); }
        catch (OperationCanceledException) { throw; }
        catch { warning = DesktopResources.Get("SkillsCatalogFailed"); }
        var favorites = await catalogFavorites.LoadAsync(session.HistoryPartition, ct);
        var config = session.Settings.Ai;
        var source = config.Location == RuntimeLocation.Local ? "localhost" : DesktopSettings.RemoteUri(config.RemoteUrl).Host;
        return new(AvailableSkillDescriptors(), runtimeSkillCatalog.Where(i => favorites.Contains(i.Key)).Select(i => i.Id).ToHashSet(StringComparer.Ordinal), source, warning);
    }
    private IReadOnlyList<DesktopSkill> AvailableSkillDescriptors() =>
        (runtimeSkillPartition == session.HistoryPartition ? runtimeSkillCatalog.Select(i => new DesktopSkill(i.Id, i.Name, i.Description, "remote", i.LatestVersion ?? i.Version)) : [])
        .Concat(Mcp.CaptureSkills().Select(s => s.Descriptor)).DistinctBy(s => s.Id).ToArray();
    private async Task PrefetchSkillAsync(string id, CancellationToken ct)
    {
        var partition = session.HistoryPartition;
        if (runtimeSkillPartition != partition) return;
        var item = runtimeSkillCatalog.FirstOrDefault(i => i.Id == id);
        if (item is not null) await skillStore.ReadAsync(partition, item, ct);
    }
    private McpTurnSnapshot CaptureSkillRuntime(ChatPreferences preferences)
    {
        var snapshot = Mcp.Capture(); var partition = session.HistoryPartition;
        var selected = preferences.EnabledSkillIds.ToHashSet(StringComparer.Ordinal);
        var readers = new List<(DesktopSkill Skill, Func<CancellationToken, Task<DesktopSkillContent>> Load)>();
        if (runtimeSkillPartition == partition)
            foreach (var item in runtimeSkillCatalog.Where(i => selected.Contains(i.Id)))
                readers.Add((new(item.Id, item.Name, item.Description, "remote", item.LatestVersion ?? item.Version), async ct =>
                {
                    if (closing || session.HistoryPartition != partition) throw new OperationCanceledException(ct);
                    var content = await skillStore.ReadAsync(partition, item, ct);
                    if (closing || session.HistoryPartition != partition) throw new OperationCanceledException(ct);
                    return content;
                }));
        foreach (var skill in Mcp.CaptureSkills().Where(s => selected.Contains(s.Descriptor.Id))) readers.Add((skill.Descriptor, skill.LoadAsync));
        new DesktopSkillTurn(readers).Register(snapshot); return snapshot;
    }
    private void RenderSkillTags()
    {
        if (Service != ServiceKind.Ai) return;
        var byId = AvailableSkillDescriptors().ToDictionary(s => s.Id, StringComparer.Ordinal);
        foreach (var id in session.Settings.Chat.EnabledSkillIds)
        {
            var label = byId.TryGetValue(id, out var skill) ? skill.Label : id;
            var content = new Grid { ColumnSpacing = 6, MaxWidth = 320 };
            content.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            content.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            content.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            content.Children.Add(new FontIcon { Glyph = "\uE734", FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
            var text = new TextBlock { Text = label, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(text, 1); content.Children.Add(text);
            var remove = new Button { Name = "DisableSkill", Tag = id, Content = new FontIcon { Glyph = "\uE711", FontSize = 10 }, Width = 24, Height = 24, Padding = new Thickness(0), IsEnabled = !busy };
            ToolbarControls.Subtle(remove); ToolbarControls.Label(remove, DesktopResources.Format("DisableSkill", label));
            remove.Click += async (_, _) =>
            {
                if (busy || closing) return;
                await RunAsync(async ct =>
                {
                    var next = session.Settings.Clone(); next.Chat.EnabledSkillIds.RemoveAll(s => s == id);
                    await SettingsStore.SaveAsync(session.DataDirectory, next); session.Settings = next;
                });
                RenderContextTags(); if (!closing) input.Focus(FocusState.Programmatic);
            };
            Grid.SetColumn(remove, 2); content.Children.Add(remove);
            var tag = new Border { Name = "EnabledSkillBadge", Tag = id, Child = content, Padding = new Thickness(10, 2, 4, 2), CornerRadius = new CornerRadius(16) };
            ControlAppearance.TokenBadge(tag); ToolbarControls.Label(tag, skill is null ? label + " · " + DesktopResources.Get("SkillUnavailableHint") : skill.Description);
            contextTags.Children.Add(tag);
        }
    }
}
