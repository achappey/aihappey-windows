using FluentIcons.WinUI;
using FluentIcons.Common;
using System.Text;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;

namespace AIHappey.Desktop.Core;

internal enum DesktopPage { Chat, Images, Videos, Speech, Transcriptions, Realtime, Agents, Skills, Plugins, Mcp, Models, Providers, Files, Usage }

public sealed partial class ChatShell
{
    private readonly DesktopCatalogClient catalogClient;
    private readonly CatalogFavoritesStore catalogFavorites;
    private readonly OverviewPage agentsOverview = new(CatalogKind.Agent);
    private readonly OverviewPage skillsOverview = new(CatalogKind.Skill);
    private readonly Grid overviewHost = new() { Visibility = Visibility.Collapsed };
    private readonly StackPanel chatModes = new() { Orientation = Orientation.Horizontal, Spacing = 4 };
    private readonly StackPanel pageNavigation = new() { Spacing = 2 };
    private readonly StackPanel expandedPageNavigation = new() { Spacing = 2 };
    private readonly Dictionary<DesktopPage, ToggleButton> pageButtons = [];
    private readonly Dictionary<CatalogKind, IReadOnlyList<CatalogItem>> catalogs = [];
    private HashSet<string> favorites = new(StringComparer.Ordinal);
    private string? catalogPartition;
    private DesktopPage activePage;
    private CatalogDetailsDialog? catalogDialogActive;
    private CatalogDetailsDialog? catalogDialog { get => catalogDialogActive; set { catalogDialogActive = value; if (value is null) ScheduleExplorerDrain(); } }
    private CancellationTokenSource? catalogDialogLoad;
    private OverviewPage ActiveOverview => activePage == DesktopPage.Agents ? agentsOverview : skillsOverview;

    private UIElement BuildPageNavigation()
    {
        var primary = CreateNavigationGroup("Primary", pageNavigation);
        AddNavigationLink(primary, "new", newChat, Icon.Add, () => { NewConversation(); return Task.CompletedTask; });
        var agentLinks = CreateNavigationGroup("Agents", expandedPageNavigation, DesktopResources.Get("Agents"));
        var resourceLinks = CreateNavigationGroup("Files", expandedPageNavigation);
        foreach (var (page, label, icon) in new[]
        {
            (DesktopPage.Realtime, DesktopResources.Get("RealtimeTitle"), (IconElement)DesktopIcons.Create(Icon.Live)),
            (DesktopPage.Images, DesktopResources.Get("Images"), (IconElement)DesktopIcons.Create(Icon.Image)),
            (DesktopPage.Videos, DesktopResources.Get("Videos"), (IconElement)DesktopIcons.Create(Icon.Video)),
            (DesktopPage.Speech, DesktopResources.Get("Speech"), (IconElement)DesktopIcons.Create(Icon.Speaker2)),
            (DesktopPage.Transcriptions, DesktopResources.Get("Transcriptions"), (IconElement)DesktopIcons.Create(Icon.Mic)),
            (DesktopPage.Agents, DesktopResources.Get("Agents"), (IconElement)DesktopIcons.Create(Icon.Bot)),
            (DesktopPage.Plugins, DesktopResources.Get("PluginsTitle"), (IconElement)DesktopIcons.Create(Icon.PuzzlePiece)),
            (DesktopPage.Mcp, DesktopResources.Get("McpTitle"), (IconElement)DesktopIcons.Create(Icon.Connector)),
            (DesktopPage.Skills, DesktopResources.Get("Skills"), (IconElement)DesktopIcons.Create(Icon.Star)),
            (DesktopPage.Files, DesktopResources.Get("Files"), (IconElement)DesktopIcons.Create(Icon.Folder)),
            (DesktopPage.Models, DesktopResources.Get("Models"), (IconElement)DesktopIcons.Create(Icon.Brain)),
            (DesktopPage.Providers, DesktopResources.Get("Providers"), (IconElement)DesktopIcons.Create(Icon.Cloud)),
            (DesktopPage.Usage, DesktopResources.Get("UsageTitle"), (IconElement)DesktopIcons.Create(Icon.DataUsage))
        })
        {
            if (page == DesktopPage.Models)
            {
                expandedPageNavigation.Children.Add(SidebarSeparator("ArtificialIntelligenceSeparator"));
                var header = new Grid { ColumnSpacing = 12 };
                header.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); header.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
                header.Children.Add(DesktopIcons.Create(Icon.Brain));
                var title = new TextBlock { Text = DesktopResources.Get("ArtificialIntelligence"), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(title, 1); header.Children.Add(title);
                var chevron = DesktopIcons.Create(Icon.ChevronUp, 12); chevron.Name = "ArtificialIntelligenceChevron";
                Grid.SetColumn(chevron, 2); header.Children.Add(chevron);
                aiCategory = new Button { Name = "ArtificialIntelligenceCategory", Content = header,
                    HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    MinHeight = 36, Padding = new Thickness(12, 4, 12, 4), CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(0) };
                ToolbarControls.Subtle(aiCategory); ToolbarControls.Label(aiCategory, DesktopResources.Get("ArtificialIntelligence"));
                void UpdateCategory() { aiNavigation.Visibility = aiCategoryExpanded ? Visibility.Visible : Visibility.Collapsed; chevron.Icon = aiCategoryExpanded ? Icon.ChevronUp : Icon.ChevronDown; }
                aiCategory.Click += (_, _) => { aiCategoryExpanded = !aiCategoryExpanded; UpdateCategory(); };
                expandedPageNavigation.Children.Add(aiCategory); expandedPageNavigation.Children.Add(aiNavigation);
            }
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            content.Children.Add(icon); content.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
            var button = new ToggleButton { Name = "Navigate" + page, Content = content, IsChecked = page == DesktopPage.Chat,
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left,
                MinHeight = 36, Padding = new Thickness(12, 4, 12, 4), BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(6) };
            ControlAppearance.Apply(button, ControlAppearance.NativeResources, palette =>
            { button.Background = new SolidColorBrush(activePage == page ? palette.Selected : palette.Background); button.Foreground = new SolidColorBrush(palette.Text); });
            ToolbarControls.Label(button, label);
            button.Click += async (_, _) => await NavigateAsync(page);
            pageButtons.Add(page, button);
            if (page is DesktopPage.Models or DesktopPage.Providers or DesktopPage.Usage) { button.Margin = new Thickness(24, 0, 0, 0); aiNavigation.Children.Add(button); }
            else
            {
                var key = page == DesktopPage.Mcp ? "servers" : page.ToString().ToLowerInvariant();
                var group = NavigationPreferences.PrimaryKeys.Contains(key) ? primary : page == DesktopPage.Files ? resourceLinks : agentLinks;
                AddNavigationLink(group, key, button, page switch
                {
                    DesktopPage.Images => Icon.Image, DesktopPage.Videos => Icon.Video, DesktopPage.Speech => Icon.Speaker2,
                    DesktopPage.Realtime => Icon.Live, DesktopPage.Transcriptions => Icon.Mic, DesktopPage.Agents => Icon.Bot, DesktopPage.Mcp => Icon.Connector,
                    DesktopPage.Skills => Icon.Star, DesktopPage.Plugins => Icon.PuzzlePiece, _ => Icon.Folder
                }, () => NavigateAsync(page));
            }
            if (page == DesktopPage.Realtime)
                AddNavigationLink(primary, "search-conversations", searchChats, Icon.Search, SearchConversationsAsync);
        }
        expandedPageNavigation.Children.Add(SidebarSeparator("ChatsSeparator"));
        expandedPageNavigation.Children.Add(SidebarHeading(DesktopResources.Get("Chats")));
        pageNavigation.Children.Add(expandedPageNavigation);
        UpdateNavigationLayout();
        overviewHost.Children.Add(imagesPage); overviewHost.Children.Add(agentsOverview); overviewHost.Children.Add(skillsOverview); overviewHost.Children.Add(mcpOverview);
        overviewHost.Children.Add(speechPage);
        overviewHost.Children.Add(transcriptionsPage);
        overviewHost.Children.Add(videosPage);
        overviewHost.Children.Add(modelsOverview);
        overviewHost.Children.Add(providersOverview);
        overviewHost.Children.Add(filesOverview);
        overviewHost.Children.Add(pluginsOverview);
        overviewHost.Children.Add(usagePage);
        usagePage.RetryRequested = async () => await RunAsync(LoadUsageAsync);
        usagePage.CancelRequested = () => operation?.Cancel();
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
        if (page != DesktopPage.Chat) await RunAsync(ct => LoadActiveOverviewAsync(ct, useCache: true));
    }

    private void ShowPage(DesktopPage page)
    {
        if (page != DesktopPage.Realtime && realtime is not null) realtimeCleanup = StopRealtimeAsync();
        if (page == DesktopPage.Realtime && activePage != DesktopPage.Realtime)
        {
            if (Service != ServiceKind.Ai) UpdateMode(ServiceKind.Ai);
            current = new() { Service = ServiceKind.Ai };
        }
        ResetFileDrop();
        details.IsPaneOpen = false;
        activePage = page;
        var chat = page is DesktopPage.Chat or DesktopPage.Realtime;
        overviewHost.Visibility = chat ? Visibility.Collapsed : Visibility.Visible;
        chatModes.Visibility = target.Visibility = composer.Visibility = disclaimer.Visibility = chat ? Visibility.Visible : Visibility.Collapsed;
        if (page == DesktopPage.Realtime) chatModes.Visibility = target.Visibility = Visibility.Collapsed;
        else { dictate.Visibility = Visibility.Visible; input.PlaceholderText = DesktopResources.Get("AskAnything"); ToolbarControls.Label(send, DesktopResources.Get("SendMessage")); }
        target.IsSuggestionListOpen = false;
        chatModelToolbar.Visibility = page == DesktopPage.Chat ? Visibility.Visible : Visibility.Collapsed;
        scroll.Visibility = chat && current.Messages.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        agentsOverview.Visibility = page == DesktopPage.Agents ? Visibility.Visible : Visibility.Collapsed;
        skillsOverview.Visibility = page == DesktopPage.Skills ? Visibility.Visible : Visibility.Collapsed;
        modelsOverview.Visibility = page == DesktopPage.Models ? Visibility.Visible : Visibility.Collapsed;
        modelFilters.Visibility = page == DesktopPage.Models ? Visibility.Visible : Visibility.Collapsed;
        providersOverview.Visibility = page == DesktopPage.Providers ? Visibility.Visible : Visibility.Collapsed;
        usagePage.Visibility = page == DesktopPage.Usage ? Visibility.Visible : Visibility.Collapsed;
        providerFilters.Visibility = page == DesktopPage.Providers ? Visibility.Visible : Visibility.Collapsed;
        addAgent.Visibility = page == DesktopPage.Agents ? Visibility.Visible : Visibility.Collapsed;
        addSkill.Visibility = page == DesktopPage.Skills ? Visibility.Visible : Visibility.Collapsed;
        addMcpServer.Visibility = page == DesktopPage.Mcp ? Visibility.Visible : Visibility.Collapsed;
        pluginsOverview.Visibility = addPlugin.Visibility = page == DesktopPage.Plugins ? Visibility.Visible : Visibility.Collapsed;
        filesOverview.Visibility = addFiles.Visibility = page == DesktopPage.Files ? Visibility.Visible : Visibility.Collapsed;
        if (page is DesktopPage.Models or DesktopPage.Providers or DesktopPage.Usage && aiCategory is not null)
        {
            aiCategoryExpanded = true; aiNavigation.Visibility = Visibility.Visible;
            if (aiCategory.Content is Grid categoryHeader && categoryHeader.Children.OfType<FluentIcon>().FirstOrDefault(icon => icon.Name == "ArtificialIntelligenceChevron") is { } chevron) chevron.Icon = Icon.ChevronUp;
        }
        mcpOverview.Visibility = page == DesktopPage.Mcp ? Visibility.Visible : Visibility.Collapsed;
        imagesPage.Visibility = imageTarget.Visibility = page == DesktopPage.Images ? Visibility.Visible : Visibility.Collapsed;
        videosPage.Visibility = videoModelToolbar.Visibility = page == DesktopPage.Videos ? Visibility.Visible : Visibility.Collapsed;
        videosPage.RefreshShimmers();
        speechPage.Visibility = speechTarget.Visibility = page == DesktopPage.Speech ? Visibility.Visible : Visibility.Collapsed;
        transcriptionsPage.Visibility = transcriptionTarget.Visibility = page == DesktopPage.Transcriptions ? Visibility.Visible : Visibility.Collapsed;
        imageModelToolbar.Visibility = imageTarget.Visibility;
        speechModelToolbar.Visibility = speechTarget.Visibility;
        transcriptionModelToolbar.Visibility = transcriptionTarget.Visibility;
        UpdateModelFavorites();
        RefreshConversationActivity();
        UpdatePageButtons();
        UpdateRealtimeControls();
        if (page == DesktopPage.Realtime) RenderTranscript();
        AutomationProperties.SetHelpText(refresh, chat ? DesktopResources.Get("RefreshTargets") : DesktopResources.Get(page switch
        { DesktopPage.Images => "ImageRefresh", DesktopPage.Videos => "VideoRefresh", DesktopPage.Speech => "SpeechRefresh", DesktopPage.Transcriptions => "TranscriptionRefresh", DesktopPage.Files => "FilesRefresh", DesktopPage.Agents => "RefreshAgents", DesktopPage.Mcp => "McpRefresh", DesktopPage.Models => "RefreshModels", DesktopPage.Providers => "RefreshProviders", DesktopPage.Usage => "UsageRefresh", _ => "RefreshSkills" }));
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
        addAgent.IsEnabled = !value;
        addSkill.IsEnabled = !value;
        addMcpServer.IsEnabled = !value;
        addPlugin.IsEnabled = !value; pluginsOverview.SetActionsEnabled(!value);
        addFiles.IsEnabled = !value; filesOverview.SetActionsEnabled(!value);
        agentsOverview.SetActionsEnabled(!value); skillsOverview.SetActionsEnabled(!value);
        mcpOverview.SetActionsEnabled(!value);
        modelsOverview.SetActionsEnabled(!value);
        providersOverview.SetActionsEnabled(!value);
        usagePage.SetActionsEnabled(!value);
        catalogDialog?.SetActionsEnabled(!value);
    }

    private async Task RefreshActivePageAsync(CancellationToken ct)
    {
        if (activePage == DesktopPage.Chat) await DiscoverAsync(ct);
        else await LoadActiveOverviewAsync(ct);
    }

    private void InvalidateCatalogs()
    {
        if (realtime is not null) realtimeCleanup = StopRealtimeAsync();
        realtimeModels = []; realtimeTarget.Text = "";
        catalogs.Clear(); favorites.Clear(); catalogPartition = null;
        InvalidateImages();
        InvalidateVideos();
        InvalidateSpeech();
        InvalidateTranscriptions();
        InvalidateModelsOverview();
        InvalidateProvidersOverview();
        usagePage.Reset();
        InvalidateFiles();
        runtimeSkillCatalog = []; runtimeSkillPartition = null;
        localAgents = []; localAgentPartition = null; agentEditor?.Hide();
        localSkills = []; localSkillPartition = null; skillEditor?.CancelAndHide();
        agentPlugins = []; pluginPartition = null; pluginEpoch++; pluginsOverview.SetItems([]); pluginDialog?.CancelAndHide(); pluginSelectionDialog?.Hide();
        skillDeleteDialog?.Hide();
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
            var items = useCache && catalogs.TryGetValue(page.Kind, out var cached) ? cached : await LoadCatalogItemsAsync(page.Kind, ct);
            if (page.Kind == CatalogKind.Agent)
            {
                await LoadLocalAgentsAsync(ct);
                items = items.Where(i => i.Origin != CatalogOrigin.Local).Concat(localAgents.Select(a => a.CatalogItem())).ToArray();
            }
            if (page.Kind == CatalogKind.Skill)
            {
                await LoadLocalSkillsAsync(ct);
                items = LocalSkillCatalog().Concat(items.Where(i => i.Origin == CatalogOrigin.Backend)).DistinctBy(i => i.Id).ToArray();
            }
            ct.ThrowIfCancellationRequested();
            if (closing || session.HistoryPartition != partition) return;
            catalogs[page.Kind] = items;
            if (page.Kind == CatalogKind.Skill)
            {
                var backend = items.Where(i => i.Origin == CatalogOrigin.Backend).ToArray();
                await skillStore.SaveCatalogAsync(partition, backend, ct);
                runtimeSkillCatalog = backend; runtimeSkillPartition = partition; RenderContextTags();
            }
            var config = session.Settings.For(page.Kind == CatalogKind.Agent ? ServiceKind.Agents : ServiceKind.Ai);
            var source = config.Location == RuntimeLocation.Local ? "localhost" : DesktopSettings.RemoteUri(config.RemoteUrl).Host;
            page.SetItems(items, favorites, source);
            if (page.Kind == CatalogKind.Agent && Service == ServiceKind.Agents)
            {
                targets = ProjectAgentTargets(items);
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
        if (item.Kind != CatalogKind.Agent) return;
        await RunAsync(async ct =>
        {
            // Confirm eligibility against the current service, not the display name/underlying model.
            var available = await AgentTargetsAsync(ct);
            var key = item.Origin == CatalogOrigin.Local ? "local:" + item.Id : "remote:" + item.Id;
            if (!available.Any(candidate => candidate.Id == key)) throw new GatewayException(DesktopResources.Get("AgentUnavailable"));
            UpdateMode(ServiceKind.Agents); targets = available; target.ItemsSource = available.Take(100).ToArray(); TargetId = key;
            current = new() { Service = ServiceKind.Agents, Target = key }; input.Text = "";
            suppress = true; chats.SelectedItem = null; suppress = false;
            ShowPage(DesktopPage.Chat); RenderTranscript();
            input.Focus(FocusState.Programmatic);
        });
    }

    private async Task OpenCatalogDetailsAsync(CatalogItem item, Button owner)
    {
        if (item.Kind == CatalogKind.Skill && item.Origin == CatalogOrigin.Local) { await EditSkillAsync(item, owner); return; }
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
        var border = new Border { Name = name, Height = 1, Margin = new Thickness(0, 8, 0, 6) };
        ControlAppearance.Apply(border, (_, _) => { }, palette => border.Background = new SolidColorBrush(palette.Stroke));
        return border;
    }
    private static TextBlock SidebarHeading(string title)
    {
        var heading = new TextBlock { Name = "SectionHeading", Text = title, FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(12, 0, 12, 4) };
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
                : item.Origin == CatalogOrigin.Local
                    ? DesktopSkillPackages.Export(await localSkillStore.ReadAsync(partition,
                        localSkills.FirstOrDefault(s => s.Id == item.Id) ?? throw new InvalidDataException(DesktopResources.Get("SkillUnavailableHint")), ct))
                    : await catalogClient.DownloadSkillAsync(item.Id, version, ct);
            ct.ThrowIfCancellationRequested();
            await using var output = await destination.OpenStreamForWriteAsync(); output.SetLength(0); await output.WriteAsync(bytes, ct);
            Show(json ? DesktopResources.Get("AgentDownloaded") : DesktopResources.Get("SkillDownloaded"), InfoBarSeverity.Success);
        });
    }
}
