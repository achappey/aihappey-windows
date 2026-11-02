using FluentIcons.Common;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace AIHappey.Desktop.Core;

public sealed partial class ChatShell
{
    private readonly DesktopLocalSkillStore localSkillStore;
    private IReadOnlyList<DesktopSkill> localSkills = [];
    private string? localSkillPartition;
    private SkillEditDialog? skillEditor;
    private GitHubSkillImportDialog? githubSkillImportDialog;
    private ContentDialog? skillDeleteDialog;
    private readonly Button addSkill = new() { Name = "AddSkill", Content = DesktopIcons.Create(Icon.Add), Width = 40, Height = 40,
        Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
    private void PrepareSkillActions()
    {
        var menu = new MenuFlyout(); var create = new MenuFlyoutItem { Text = DesktopResources.Get("New"), Icon = DesktopIcons.Create(Icon.Add) };
        var import = new MenuFlyoutSubItem { Name = "SkillImportMenu", Text = DesktopResources.Get("SkillImportMenu"), Icon = DesktopIcons.Create(Icon.Attach) };
        var zip = new MenuFlyoutItem { Name = "SkillImportZip", Text = "ZIP", Icon = DesktopIcons.Create(Icon.Attach) };
        var github = new MenuFlyoutItem { Name = "SkillFromGitHub", Text = DesktopResources.Get("SkillFromGitHub"), Icon = DesktopIcons.Create(Icon.Link) };
        var folder = new MenuFlyoutItem { Name = "SkillImportFolder", Text = DesktopResources.Get("SkillFolder"), Icon = DesktopIcons.Create(Icon.FolderOpen) };
        import.Items.Add(zip); import.Items.Add(folder); import.Items.Add(github);
        menu.Items.Add(create); menu.Items.Add(import); addSkill.Flyout = menu;
        ControlAppearance.Stock(addSkill); ToolbarControls.Label(addSkill, DesktopResources.Get("Add"));
        create.Click += async (_, _) => await EditSkillAsync(null, addSkill);
        zip.Click += async (_, _) => await ImportSkillsAsync();
        github.Click += async (_, _) => await ImportGitHubSkillsAsync();
        folder.Click += async (_, _) => await ImportSkillFolderAsync();
        skillsOverview.ImportRequested = data => ImportOverviewFilesAsync(data, DesktopPage.Skills, ".zip", "SkillZipOnly", ImportSkillFilesAsync);
        skillsOverview.EditRequested = async (item, owner) => await EditSkillAsync(item, owner);
        skillsOverview.DeleteRequested = async item => await DeleteSkillAsync(item);
    }
    private async Task LoadLocalSkillsAsync(CancellationToken ct)
    {
        var partition = session.HistoryPartition; var items = await localSkillStore.ListAsync(partition, ct);
        ct.ThrowIfCancellationRequested(); if (partition != session.HistoryPartition || closing) throw new OperationCanceledException(ct);
        localSkills = items; localSkillPartition = partition;
    }
    private IReadOnlyList<CatalogItem> LocalSkillCatalog() => (localSkillPartition == session.HistoryPartition ? localSkills : []).Select(s => new CatalogItem(CatalogKind.Skill, s.Id, s.Name, s.Description)
        { Origin = CatalogOrigin.Local, Version = s.Version, LatestVersion = s.Version }).ToArray();
    private async Task RefreshLocalSkillsAsync(CancellationToken ct)
    {
        await LoadLocalSkillsAsync(ct); var partition = session.HistoryPartition;
        if (catalogPartition != partition)
        {
            var savedFavorites = await catalogFavorites.LoadAsync(partition, ct);
            ct.ThrowIfCancellationRequested(); if (closing || session.HistoryPartition != partition) throw new OperationCanceledException(ct);
            catalogs.Clear(); favorites = new(savedFavorites, StringComparer.Ordinal); catalogPartition = partition;
        }
        catalogs.TryGetValue(CatalogKind.Skill, out var previous);
        var backend = (previous ?? (runtimeSkillPartition == partition ? runtimeSkillCatalog : [])).Where(i => i.Origin == CatalogOrigin.Backend).ToArray();
        var items = LocalSkillCatalog().Concat(backend).DistinctBy(i => i.Id).ToArray(); catalogs[CatalogKind.Skill] = items;
        var config = session.Settings.Ai;
        skillsOverview.SetItems(items, favorites, config.Location == RuntimeLocation.Local ? "localhost" : DesktopSettings.RemoteUri(config.RemoteUrl).Host);
        RenderContextTags();
    }
    private async Task EnableLocalSkillsAsync(IEnumerable<string> ids, string partition, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); if (closing || session.HistoryPartition != partition) throw new OperationCanceledException(ct);
        var next = session.Settings.Clone(); next.Chat.EnabledSkillIds = next.Chat.EnabledSkillIds.Concat(ids).Distinct(StringComparer.Ordinal).ToList();
        await SettingsStore.SaveAsync(session.DataDirectory, next);
        ct.ThrowIfCancellationRequested(); if (closing || session.HistoryPartition != partition) throw new OperationCanceledException(ct);
        session.Settings = next;
    }
    private async Task EditSkillAsync(CatalogItem? item, Button owner)
    {
        if (busy || closing || historyDialogOpen || skillEditor is not null || catalogDialog is not null || item is { Origin: not CatalogOrigin.Local }) return;
        historyDialogOpen = true; var partition = session.HistoryPartition; DesktopSkill? saved = null;
        try
        {
            await LoadLocalSkillsAsync(downloadLifetime.Token);
            var existing = item is null ? null : localSkills.FirstOrDefault(s => s.Id == item.Id);
            if (item is not null && existing is null) throw new InvalidDataException(DesktopResources.Get("SkillUnavailableHint"));
            var draft = existing is null ? new DesktopSkillDraft() : await localSkillStore.ReadAsync(partition, existing, downloadLifetime.Token);
            if (closing || session.HistoryPartition != partition) return;
            skillEditor = new(draft, existing is not null, () => !closing && session.HistoryPartition == partition) { XamlRoot = XamlRoot };
            skillEditor.SaveAsync = async (value, ct) =>
            {
                ct.ThrowIfCancellationRequested(); if (session.HistoryPartition != partition) throw new OperationCanceledException(ct);
                saved = await localSkillStore.SaveAsync(partition, value, existing?.Id, ct);
            };
            SystemAppearance.PrepareDialog(skillEditor); await skillEditor.ShowAsync();
            if (!closing && session.HistoryPartition == partition)
            {
                try { if (saved is not null && existing is null) await EnableLocalSkillsAsync([saved.Id], partition, downloadLifetime.Token); }
                finally { if (!closing && session.HistoryPartition == partition) await RefreshLocalSkillsAsync(downloadLifetime.Token); }
                if (saved is not null) Show(DesktopResources.Format("SkillSavedVersion", saved.Version!), InfoBarSeverity.Success);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { Show(error is InvalidDataException ? error.Message : DesktopResources.Get("ChatSettingsSaveFailed"), InfoBarSeverity.Error); }
        finally { skillEditor = null; historyDialogOpen = false; if (!closing && owner.IsLoaded) owner.Focus(FocusState.Programmatic); }
    }
    private async Task ImportSkillsAsync()
    {
        if (busy || closing || historyDialogOpen) return;
        await RunAsync(async ct =>
        {
            var picker = new FileOpenPicker(); picker.FileTypeFilter.Add(".zip");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, Microsoft.UI.Win32Interop.GetWindowFromWindowId(XamlRoot.ContentIslandEnvironment.AppWindowId));
            var partition = session.HistoryPartition;
            var files = await picker.PickMultipleFilesAsync().AsTask(ct);
            CheckSkillImportPartition(partition, ct);
            if (files.Count > 0) await ImportSkillFilesAsync(files, ct);
        });
    }
    private void CheckSkillImportPartition(string partition, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); if (closing || partition != session.HistoryPartition) throw new OperationCanceledException(ct);
    }
    private async Task SaveImportedSkillsAsync(DesktopSkillImport parsed, string partition, List<string> imported, CancellationToken ct)
    {
        foreach (var draft in parsed.Skills)
        {
            CheckSkillImportPartition(partition, ct);
            var previous = (await localSkillStore.ListAsync(partition, ct)).FirstOrDefault(s => s.Name == draft.Name);
            CheckSkillImportPartition(partition, ct);
            var stored = await localSkillStore.SaveAsync(partition, draft, previous?.Id, ct); imported.Add(stored.Id);
        }
    }
    private async Task FinishSkillImportAsync(string partition, List<string> imported, List<string> errors, CancellationToken ct)
    {
        CheckSkillImportPartition(partition, ct);
        try { if (imported.Count > 0) await EnableLocalSkillsAsync(imported, partition, ct); }
        finally { if (!closing && session.HistoryPartition == partition) await RefreshLocalSkillsAsync(ct); }
        CheckSkillImportPartition(partition, ct);
        Show(DesktopResources.Format("SkillImportedCount", imported.Count) + (errors.Count > 0 ? "\n" + string.Join("\n", errors) : ""),
            errors.Count > 0 || imported.Count == 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success);
    }
    private async Task ImportSkillFilesAsync(IReadOnlyList<StorageFile> files, CancellationToken ct)
    {
        var partition = session.HistoryPartition; var errors = new List<string>(); var imported = new List<string>();
        foreach (var file in files)
        {
            CheckSkillImportPartition(partition, ct);
            try
            {
                if (!file.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException(DesktopResources.Get("SkillZipOnly"));
                if ((await file.GetBasicPropertiesAsync()).Size > DesktopCatalogClient.MaxDownloadBytes) throw new InvalidDataException(DesktopResources.Get("SkillPackageTooLarge"));
                await using var input = await file.OpenStreamForReadAsync(); using var content = new StreamContent(input);
                var parsed = await DesktopSkillPackages.ImportAsync(await DesktopCatalogClient.ReadBoundedAsync(content, DesktopCatalogClient.MaxDownloadBytes, ct), ct);
                errors.AddRange(parsed.Diagnostics.Select(e => file.Name + ": " + e));
                foreach (var value in parsed.Skills)
                    try { await SaveImportedSkillsAsync(new([value], []), partition, imported, ct); }
                    catch (Exception error) when (error is not OperationCanceledException) { errors.Add(value.Name + ": " + error.Message); }
            }
            catch (Exception error) when (error is not OperationCanceledException) { errors.Add(file.Name + ": " + error.Message); }
        }
        await FinishSkillImportAsync(partition, imported, errors, ct);
    }
    private async Task ImportSkillFolderAsync()
    {
        if (busy || closing || historyDialogOpen) return;
        await RunAsync(async ct =>
        {
            var partition = session.HistoryPartition; var picker = new FolderPicker(); picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, Microsoft.UI.Win32Interop.GetWindowFromWindowId(XamlRoot.ContentIslandEnvironment.AppWindowId));
            var folder = await picker.PickSingleFolderAsync().AsTask(ct); if (folder is null) return;
            CheckSkillImportPartition(partition, ct);
            Show(DesktopResources.Get("SkillFolderScanning"), InfoBarSeverity.Informational);
            var parsed = await Task.Run(() => DesktopSkillDirectories.ImportAsync(folder.Path, ct), ct);
            CheckSkillImportPartition(partition, ct);
            var imported = new List<string>(); var errors = parsed.Diagnostics.ToList();
            foreach (var draft in parsed.Skills)
                try { await SaveImportedSkillsAsync(new([draft], []), partition, imported, ct); }
                catch (Exception error) when (error is not OperationCanceledException) { errors.Add(draft.Name + ": " + error.Message); }
            await FinishSkillImportAsync(partition, imported, errors, ct);
        });
    }
    private async Task ImportGitHubSkillsAsync()
    {
        if (busy || closing || historyDialogOpen) return;
        historyDialogOpen = true; var partition = session.HistoryPartition; var imported = new List<string>();
        // Isolated from gateway credentials, cookies, integrated auth, and redirects.
        using var githubHttp = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseDefaultCredentials = false });
        try
        {
            githubSkillImportDialog = new(new GitHubSkillsClient(githubHttp), async (draft, ct) =>
            {
                ct.ThrowIfCancellationRequested(); if (closing || partition != session.HistoryPartition) throw new OperationCanceledException(ct);
                var previous = (await localSkillStore.ListAsync(partition, ct)).FirstOrDefault(s => s.Name == draft.Name);
                ct.ThrowIfCancellationRequested(); if (closing || partition != session.HistoryPartition) throw new OperationCanceledException(ct);
                var stored = await localSkillStore.SaveAsync(partition, draft, previous?.Id, ct); imported.Add(stored.Id);
            }, () => !closing && partition == session.HistoryPartition) { XamlRoot = XamlRoot };
            SystemAppearance.PrepareDialog(githubSkillImportDialog); await githubSkillImportDialog.ShowAsync();
            if (!closing && partition == session.HistoryPartition)
            {
                try { if (imported.Count > 0) await EnableLocalSkillsAsync(imported, partition, downloadLifetime.Token); }
                finally { if (!closing && partition == session.HistoryPartition) await RefreshLocalSkillsAsync(downloadLifetime.Token); }
                if (imported.Count > 0) Show(DesktopResources.Format("SkillImportedCount", imported.Count), InfoBarSeverity.Success);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { Show(error is GitHubSkillException or InvalidDataException ? error.Message : DesktopResources.Get("ChatSettingsSaveFailed"), InfoBarSeverity.Error); }
        finally { githubSkillImportDialog = null; historyDialogOpen = false; if (!closing && addSkill.IsLoaded) addSkill.Focus(FocusState.Programmatic); }
    }
    private async Task DeleteSkillAsync(CatalogItem item)
    {
        if (busy || closing || historyDialogOpen || item.Origin != CatalogOrigin.Local) return;
        historyDialogOpen = true; var partition = session.HistoryPartition;
        try
        {
            var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = DesktopResources.Get("Delete"), Content = DesktopResources.Format("SkillDeleteConfirm", item.Name),
                PrimaryButtonText = DesktopResources.Get("Delete"), CloseButtonText = DesktopResources.Get("Cancel"), DefaultButton = ContentDialogButton.Close };
            skillDeleteDialog = dialog;
            SystemAppearance.PrepareDialog(dialog); if (await dialog.ShowAsync() != ContentDialogResult.Primary || closing || partition != session.HistoryPartition) return;
            await localSkillStore.DeleteAsync(partition, item.Id, downloadLifetime.Token);
            if (closing || partition != session.HistoryPartition) return;
            var next = session.Settings.Clone(); next.Chat.EnabledSkillIds.RemoveAll(id => id == item.Id);
            try
            {
                await SettingsStore.SaveAsync(session.DataDirectory, next);
                if (closing || partition != session.HistoryPartition) return;
                session.Settings = next;
                var nextFavorites = new HashSet<string>(favorites, StringComparer.Ordinal); nextFavorites.Remove(item.Key);
                await catalogFavorites.SaveAsync(partition, nextFavorites, downloadLifetime.Token);
                if (closing || partition != session.HistoryPartition) return;
                favorites = nextFavorites;
            }
            finally { if (!closing && partition == session.HistoryPartition) await RefreshLocalSkillsAsync(downloadLifetime.Token); }
        }
        catch (OperationCanceledException) { }
        catch { Show(DesktopResources.Get("ChatSettingsSaveFailed"), InfoBarSeverity.Error); }
        finally { skillDeleteDialog = null; historyDialogOpen = false; }
    }
}
