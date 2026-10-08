using System.Text;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;

namespace AIHappey.Desktop.Core;

internal enum DesktopPage { Chat, Agents, Skills }

public sealed partial class ChatShell
{
    private readonly DesktopCatalogClient catalogClient;
    private readonly CatalogFavoritesStore catalogFavorites;
    private readonly OverviewPage agentsOverview = new(CatalogKind.Agent);
    private readonly OverviewPage skillsOverview = new(CatalogKind.Skill);
    private readonly Grid overviewHost = new() { Visibility = Visibility.Collapsed };
    private readonly StackPanel chatModes = new() { Orientation = Orientation.Horizontal, Spacing = 4 };
    private readonly StackPanel pageNavigation = new() { Spacing = 4 };
    private readonly Dictionary<DesktopPage, ToggleButton> pageButtons = [];
    private readonly Dictionary<CatalogKind, IReadOnlyList<CatalogItem>> catalogs = [];
    private HashSet<string> favorites = new(StringComparer.Ordinal);
    private string? catalogPartition;
    private DesktopPage activePage;
    private CatalogDetailsDialog? catalogDialog;
    private CancellationTokenSource? catalogDialogLoad;
    private OverviewPage ActiveOverview => activePage == DesktopPage.Agents ? agentsOverview : skillsOverview;

    private UIElement BuildPageNavigation()
    {
        foreach (var (page, label, icon) in new[]
        {
            (DesktopPage.Agents, DesktopResources.Get("Agents"), (IconElement)ToolbarControls.BotIcon()),
            (DesktopPage.Skills, DesktopResources.Get("Skills"), (IconElement)new FontIcon { Glyph = "\uE734" })
        })
        {
            if (page == DesktopPage.Agents)
            { pageNavigation.Children.Add(SidebarSeparator("AgentsSeparator")); pageNavigation.Children.Add(SidebarHeading(DesktopResources.Get("Agents"))); }
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            content.Children.Add(icon); content.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
            var button = new ToggleButton { Name = "Navigate" + page, Content = content, IsChecked = page == DesktopPage.Chat,
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(12, 10, 12, 10), BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(6) };
            ControlAppearance.Apply(button, ControlAppearance.NativeResources, palette =>
            { button.Background = new SolidColorBrush(activePage == page ? palette.Selected : palette.Background); button.Foreground = new SolidColorBrush(palette.Text); icon.Foreground = button.Foreground; });
            ToolbarControls.Label(button, label);
            button.Click += async (_, _) => await NavigateAsync(page);
            pageButtons.Add(page, button); pageNavigation.Children.Add(button);
        }
        pageNavigation.Children.Add(SidebarSeparator("ChatsSeparator"));
        pageNavigation.Children.Add(SidebarHeading(DesktopResources.Get("Chats")));
        overviewHost.Children.Add(agentsOverview); overviewHost.Children.Add(skillsOverview);
        foreach (var page in new[] { agentsOverview, skillsOverview })
        {
            page.RetryRequested = async () => await RunAsync(ct => LoadOverviewAsync(page, ct));
            page.CancelRequested = () => operation?.Cancel();
            page.FavoriteRequested = async item => await ToggleCatalogFavoriteAsync(item);
            page.DetailsRequested = async (item, owner) => await OpenCatalogDetailsAsync(item, owner);
            page.DownloadRequested = async item => await DownloadCatalogAsync(item, null);
            page.ChatRequested = async item => await StartAgentChatAsync(item);
        }
        return pageNavigation;
    }

    private async Task NavigateAsync(DesktopPage page)
    {
        if (busy || downloading || closing || !initialized || catalogDialog is not null || historyDialogOpen)
        { UpdatePageButtons(); return; }
        ShowPage(page);
        if (page != DesktopPage.Chat) await RunAsync(ct => LoadOverviewAsync(ActiveOverview, ct, useCache: true));
    }

    private void ShowPage(DesktopPage page)
    {
        ResetFileDrop();
        details.IsPaneOpen = false;
        activePage = page;
        var chat = page == DesktopPage.Chat;
        overviewHost.Visibility = chat ? Visibility.Collapsed : Visibility.Visible;
        chatModes.Visibility = target.Visibility = composer.Visibility = disclaimer.Visibility = chat ? Visibility.Visible : Visibility.Collapsed;
        scroll.Visibility = chat && current.Messages.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        agentsOverview.Visibility = page == DesktopPage.Agents ? Visibility.Visible : Visibility.Collapsed;
        skillsOverview.Visibility = page == DesktopPage.Skills ? Visibility.Visible : Visibility.Collapsed;
        UpdatePageButtons();
        AutomationProperties.SetHelpText(refresh, chat ? DesktopResources.Get("RefreshTargets") : page == DesktopPage.Agents ? DesktopResources.Get("RefreshAgents") : DesktopResources.Get("RefreshSkills"));
    }

    private void UpdatePageButtons()
    {
        foreach (var (page, button) in pageButtons)
        {
            button.IsChecked = activePage == page;
            button.Background = new SolidColorBrush(activePage == page ? ControlAppearance.Palette(button).Selected : ControlAppearance.Palette(button).Background);
            ControlAppearance.Refresh(button);
        }
    }

    private void SetOverviewBusy(bool value)
    {
        foreach (var button in pageButtons.Values) button.IsEnabled = !value;
        agentsOverview.SetActionsEnabled(!value); skillsOverview.SetActionsEnabled(!value);
        catalogDialog?.SetActionsEnabled(!value);
    }

    private async Task RefreshActivePageAsync(CancellationToken ct)
    {
        if (activePage == DesktopPage.Chat) await DiscoverAsync(ct);
        else await LoadOverviewAsync(ActiveOverview, ct);
    }

    private void InvalidateCatalogs()
    {
        catalogs.Clear(); favorites.Clear(); catalogPartition = null;
        catalogDialogLoad?.Cancel(); catalogDialog?.Hide();
        searchDialog?.Hide();
        details.IsPaneOpen = false;
        agentsOverview.Loading(); skillsOverview.Loading();
    }

    private async Task LoadOverviewAsync(OverviewPage page, CancellationToken ct, bool useCache = false)
    {
        var partition = session.HistoryPartition;
        page.Loading();
        try
        {
            if (catalogPartition != partition)
            {
                catalogs.Clear();
                favorites = await catalogFavorites.LoadAsync(partition, ct);
                catalogPartition = partition;
            }
            var items = useCache && catalogs.TryGetValue(page.Kind, out var cached) ? cached : await catalogClient.ListAsync(page.Kind, ct);
            ct.ThrowIfCancellationRequested();
            if (closing || session.HistoryPartition != partition) return;
            catalogs[page.Kind] = items;
            var config = session.Settings.For(page.Kind == CatalogKind.Agent ? ServiceKind.Agents : ServiceKind.Ai);
            var source = config.Location == RuntimeLocation.Local ? "localhost" : DesktopSettings.RemoteUri(config.RemoteUrl).Host;
            page.SetItems(items, favorites, source);
            if (page.Kind == CatalogKind.Agent && Service == ServiceKind.Agents)
            {
                targets = items.Select(item => new ChatTarget(item.Id, item.Name)).ToArray();
                target.ItemsSource = targets.Take(100).ToArray();
            }
        }
        catch (OperationCanceledException) { page.Error(DesktopResources.Get("CatalogCanceled")); throw; }
        catch (Exception error)
        {
            page.Error(error is GatewayException or InvalidOperationException ? error.Message : DesktopResources.Get("CatalogFailed"));
            throw;
        }
    }

    private async Task ToggleCatalogFavoriteAsync(CatalogItem item)
    {
        await RunAsync(async ct =>
        {
            var next = new HashSet<string>(favorites, StringComparer.Ordinal);
            if (!next.Remove(item.Key)) next.Add(item.Key);
            await catalogFavorites.SaveAsync(session.HistoryPartition, next, ct);
            favorites = next;
            agentsOverview.SetFavorites(favorites); skillsOverview.SetFavorites(favorites);
        });
    }

    private async Task StartAgentChatAsync(CatalogItem item)
    {
        if (item.Kind != CatalogKind.Agent || item.Origin != CatalogOrigin.Backend) return;
        await RunAsync(async ct =>
        {
            // Confirm eligibility against the current service, not the display name/underlying model.
            var available = await client.ListAsync(ServiceKind.Agents, ct);
            if (!available.Any(candidate => candidate.Id == item.Id)) throw new GatewayException(DesktopResources.Get("AgentUnavailable"));
            UpdateMode(ServiceKind.Agents); targets = available; target.ItemsSource = available.Take(100).ToArray(); target.Text = item.Id;
            current = new() { Service = ServiceKind.Agents, Target = item.Id }; input.Text = "";
            suppress = true; chats.SelectedItem = null; suppress = false;
            ShowPage(DesktopPage.Chat); RenderTranscript();
            input.Focus(FocusState.Programmatic);
        });
    }

    private async Task OpenCatalogDetailsAsync(CatalogItem item, Button owner)
    {
        if (busy || closing || historyDialogOpen || catalogDialog is not null) return;
        var dialog = new CatalogDetailsDialog(item) { XamlRoot = XamlRoot };
        SystemAppearance.PrepareDialog(dialog);
        catalogDialog = dialog;
        var startChat = false;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(downloadLifetime.Token);
        lifetime.CancelAfter(TimeSpan.FromMinutes(2)); catalogDialogLoad = lifetime;
        dialog.Closing += (_, _) => lifetime.Cancel();
        dialog.DownloadRequested = async version => await DownloadCatalogAsync(item, version);
        dialog.StartChatRequested = () => { startChat = true; dialog.Hide(); };
        Task load = Task.CompletedTask;
        if (item.Kind == CatalogKind.Skill && CatalogRoutes.SupportsSkill(item.Id))
        {
            dialog.SetVersions([], true);
            dialog.Opened += (_, _) => load = LoadCatalogVersionsAsync(dialog, item, lifetime.Token);
        }
        try { await dialog.ShowAsync(); }
        finally
        {
            lifetime.Cancel(); catalogDialog = null; catalogDialogLoad = null;
            await load;
            if (!closing && owner.IsLoaded) owner.Focus(FocusState.Programmatic);
        }
        if (startChat && !closing) await StartAgentChatAsync(item);
    }

    private async Task LoadCatalogVersionsAsync(CatalogDetailsDialog dialog, CatalogItem item, CancellationToken ct)
    {
        var partition = session.HistoryPartition;
        try
        {
            var versions = await catalogClient.VersionsAsync(item.Id, ct);
            ct.ThrowIfCancellationRequested();
            if (catalogDialog == dialog && session.HistoryPartition == partition) dialog.SetVersions(versions, false);
        }
        catch (OperationCanceledException) { if (catalogDialog == dialog && !closing) dialog.SetVersions([], false, DesktopResources.Get("VersionsCanceled")); }
        catch (Exception error) { if (catalogDialog == dialog && !closing) dialog.SetVersions([], false,
            error is GatewayException ? error.Message : DesktopResources.Get("VersionsFailed")); }
    }

    private static Border SidebarSeparator(string name)
    {
        var border = new Border { Name = name, Height = 1, Margin = new Thickness(0, 16, 0, 12) };
        ControlAppearance.Apply(border, (_, _) => { }, palette => border.Background = new SolidColorBrush(palette.Stroke));
        return border;
    }
    private static TextBlock SidebarHeading(string title)
    {
        var heading = new TextBlock { Name = "SectionHeading", Text = title, FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(12, 0, 12, 8) };
        AutomationProperties.SetHeadingLevel(heading, Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level2);
        return heading;
    }

    private async Task DownloadCatalogAsync(CatalogItem item, string? version)
    {
        if (!item.CanDownload || busy || closing) return;
        await RunAsync(async ct =>
        {
            var partition = session.HistoryPartition;
            var json = item.Kind == CatalogKind.Agent;
            var extension = json ? ".json" : ".zip";
            var name = AttachmentDownloads.SafeName(item.Name + (version is null ? "" : "-" + version) + extension, json ? "application/json" : "application/zip");
            var picker = new FileSavePicker { SuggestedFileName = Path.GetFileNameWithoutExtension(name) };
            picker.FileTypeChoices.Add(json ? DesktopResources.Get("AgentDefinition") : DesktopResources.Get("SkillArchive"), new List<string> { extension });
            WinRT.Interop.InitializeWithWindow.Initialize(picker, Microsoft.UI.Win32Interop.GetWindowFromWindowId(XamlRoot.ContentIslandEnvironment.AppWindowId));
            var destination = await picker.PickSaveFileAsync();
            if (destination is null) return;
            ct.ThrowIfCancellationRequested();
            if (closing || session.HistoryPartition != partition) return;
            // Fully validate/stage before opening the chosen file for writing. Never extract or execute a skill archive.
            var bytes = json ? Encoding.UTF8.GetBytes(JsonSerializer.Serialize(item.Definition!.Value, new JsonSerializerOptions { WriteIndented = true }))
                : await catalogClient.DownloadSkillAsync(item.Id, version, ct);
            ct.ThrowIfCancellationRequested();
            await using var output = await destination.OpenStreamForWriteAsync(); output.SetLength(0); await output.WriteAsync(bytes, ct);
            Show(json ? DesktopResources.Get("AgentDownloaded") : DesktopResources.Get("SkillDownloaded"), InfoBarSeverity.Success);
        });
    }
}
