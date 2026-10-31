using FluentIcons.Common;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Vercel.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI.Core;

namespace AIHappey.Desktop.Core;

/// <summary>The only conversation UI for both authentication hosts. Uses stock WinUI controls and resources.</summary>
public sealed partial class ChatShell : UserControl
{
    private readonly DesktopSession session;
    private readonly HttpClient http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
    private readonly DesktopChatClient client;
    private readonly HistoryStore history;
    private readonly SplitView split = new() { DisplayMode = SplitViewDisplayMode.CompactInline, IsPaneOpen = true, OpenPaneLength = 280, CompactPaneLength = 48 };
    private readonly Grid workspace = new();
    private readonly StackPanel transcript = new() { Spacing = 16, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 24, 0, 24) };
    private readonly ScrollViewer scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Disabled, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly StackPanel composer = new() { Spacing = 8, MaxWidth = 1040, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(24, 12, 24, 16) };
    private readonly TextBlock welcome = new() { Text = DesktopResources.Get("Welcome"), TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 16), FontSize = 28 };
    private readonly TextBlock disclaimer = new() { Name = "Disclaimer", Text = DesktopResources.Format("Disclaimer", DesktopBranding.DisclaimerName), FontSize = 12, TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(16, 0, 16, 8) };
    private readonly TextBox input = new() { PlaceholderText = DesktopResources.Get("AskAnything"), AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 96, MaxHeight = 240 };
    private readonly ToggleButton models = ToolbarControls.CreateModeButton(DesktopIcons.Create(Icon.Brain), DesktopResources.Get("Models"));
    private readonly ToggleButton agents = ToolbarControls.CreateModeButton(DesktopIcons.Create(Icon.Bot), DesktopResources.Get("Agents"));
    private readonly ChatTargetPicker target = new() { PlaceholderText = DesktopResources.Get("SelectModel"), MinWidth = 120, MaxWidth = 420, Height = 40, CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), VerticalContentAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly MenuFlyoutItem refresh = new() { Text = DesktopResources.Get("Refresh"), Icon = DesktopIcons.Create(Icon.ArrowSync) };
    private readonly Button account = new() { Content = DesktopIcons.Create(Icon.Person), Width = 40, Height = 40, Padding = new Thickness(0), CornerRadius = new CornerRadius(20) };
    private readonly MenuFlyoutItem settingsButton = new() { Text = DesktopResources.Get("Settings"), Icon = DesktopIcons.Create(Icon.Settings) };
    private readonly MenuFlyoutItem manageAccount = new() { Icon = DesktopIcons.Create(Icon.PersonAccounts) };
    private readonly Button send = new() { Content = DesktopIcons.Create(Icon.Send), Width = 40, Height = 40, Padding = new Thickness(0), CornerRadius = new CornerRadius(6), HorizontalAlignment = HorizontalAlignment.Right };
    private readonly Button dictate = new() { Name = "Dictate", Content = DesktopIcons.Create(Icon.Mic), Width = 40, Height = 40, Padding = new Thickness(0) };
    private readonly Button stop = new() { Name = "Stop", Content = DesktopIcons.Create(Icon.Stop), Width = 40, Height = 40, Padding = new Thickness(0), CornerRadius = new CornerRadius(6), Visibility = Visibility.Collapsed };
    private readonly Button newChat = new() { HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(12, 10, 12, 10), CornerRadius = new CornerRadius(6) };
    private readonly ListView chats = new() { SelectionMode = ListViewSelectionMode.Single };
    private readonly ConditionalWeakTable<ListViewItem, ConversationRow> conversationRows = new();
    private readonly InfoBar notice = new() { Name = "ChatNotice", IsOpen = false, IsClosable = true,
        HorizontalAlignment = HorizontalAlignment.Stretch, Visibility = Visibility.Collapsed };
    private readonly TextBlock noticeMessage = new() { Name = "ChatNoticeMessage", TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
    private readonly ScrollViewer noticeScroll = new() { Name = "ChatNoticeScroll", MaxHeight = 240, Margin = new Thickness(0, 0, 0, 12),
        HorizontalScrollMode = ScrollMode.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly ProgressRing progress = new() { Name = "HeaderActivityProgress", Width = 20, Height = 20, IsActive = false,
        VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
    private IReadOnlyList<ChatTarget> targets = [];
    private IReadOnlyList<Conversation> conversations = [];
    private Conversation current = new();
    private CancellationTokenSource? operation;
    private bool busy;
    private bool suppress;
    private bool initialized;
    private Task runtimeWarmup = Task.CompletedTask;
    private readonly CancellationTokenSource runtimeWarmupLifetime = new();
    private bool followBottom = true;
    private bool closing;
    private bool updatingMode;
    private bool historyDialogActive;
    private bool historyDialogOpen { get => historyDialogActive; set { historyDialogActive = value; if (!value) ScheduleExplorerDrain(); } }
    private readonly Dictionary<string, int> activityPages = [];
    private ServiceKind service;
    private ServiceKind Service => service;

    public ChatShell(DesktopSession session)
    {
        this.session = session;
        client = new(session, http);
        appAgentClient = new DesktopAppAgentClient(session, client, http);
        history = new(Path.Combine(session.DataDirectory, "conversations"));
        conversationPinsStore = new(Path.Combine(session.DataDirectory, "conversation-pins"));
        catalogClient = new(client, http);
        catalogFavorites = new(Path.Combine(session.DataDirectory, "catalog-favorites"));
        agentStore = new(Path.Combine(session.DataDirectory, "agents"));
        skillStore = new(Path.Combine(session.DataDirectory, "skills"), (item, version, ct) => catalogClient.DownloadSkillAsync(item.Id, version, ct));
        localSkillStore = new(Path.Combine(session.DataDirectory, "local-skills"));
        pluginStore = new(Path.Combine(session.DataDirectory, "agent-plugins"));
        sharedFileStore = new(Path.Combine(session.DataDirectory, "shared-files"));
        PrepareContext();
        session.ElicitationHandler = ShowElicitationAsync;
        PrepareMcp();
        PrepareToolApprovals();
        PrepareChatSettings();
        PrepareImages();
        PrepareVideos();
        PrepareSpeech();
        PrepareTranscriptions();
        PrepareRealtime();
        PrepareModelsOverview();
        PrepareProvidersOverview();
        PrepareSystemContext();
        PrepareTranscript();
        PrepareSearchChats();
        var mainInterface = BuildLayout();
        mainInterface.Visibility = Visibility.Collapsed;
        var loading = new StackPanel
        {
            Name = "StartupLoading", Spacing = 16,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };
        var indicator = new ProgressRing { Name = "StartupProgress", IsActive = true, Width = 32, Height = 32 };
        AutomationProperties.SetName(indicator, DesktopResources.Get("Working"));
        loading.Children.Add(indicator);
        loading.Children.Add(new TextBlock { Text = DesktopBranding.AppName, HorizontalAlignment = HorizontalAlignment.Center });
        var root = new Grid();
        root.Children.Add(mainInterface);
        root.Children.Add(loading);
        Content = root;
        PrepareAgentActions();
        PrepareSkillActions();
        PreparePlugins();
        PrepareFiles();
        PrepareFileDrop();
        PrepareConversationDrop();
        ControlAppearance.Apply(this, (_, _) => { }, palette =>
        {
            Background = new SolidColorBrush(palette.Surface);
            Foreground = new SolidColorBrush(palette.Text);
        });
        foreach (var control in new Control[] { input, chats, account, send, stop, settingsButton, manageAccount, refresh }) ControlAppearance.Native(control);
        ToolbarControls.Subtle(newChat);
        ToolbarControls.Label(newChat, DesktopResources.Get("NewChat"));
        ControlAppearance.BorderlessItems(chats, compact: true);
        chats.ItemTemplate = ConversationRow.Template();
        chats.ContainerContentChanging += PrepareConversationRow;
        UpdateAccountMenu();
        UpdateMode(ServiceKind.Ai);
        ToolbarControls.Label(account, DesktopResources.Get("UserProfile"));
        ToolbarControls.Label(send, DesktopResources.Get("SendMessage"));
        ToolbarControls.Label(stop, DesktopResources.Get("Stop"));
        ToolbarControls.Subtle(dictate);
        ToolbarControls.Label(dictate, DesktopResources.Get("SpeechDictate"));
        ToolbarControls.Label(target, DesktopResources.Get("SelectModel"));
        AutomationProperties.SetName(input, DesktopResources.Get("Message"));
        AutomationProperties.SetHelpText(input, DesktopResources.Get("InputHelp"));
        AutomationProperties.SetHelpText(settingsButton, DesktopResources.Get("ConnectionSettings"));
        AutomationProperties.SetHelpText(refresh, DesktopResources.Get("RefreshTargets"));
        Loaded += async (_, _) =>
        {
            if (initialized) return;
            initialized = true;
            try
            {
                await RunAsync(async ct =>
                {
                    session.Settings.Validate(session.Host.AllowLocal);
                    await Task.Run(() => session.Host.InitializeAsync(ct), ct);
                    if (session.Host.RequiresSignIn)
                    {
                        // The loaded XamlRoot can host the modal; show the shell behind it rather than an error bar.
                        indicator.IsActive = false;
                        loading.Visibility = Visibility.Collapsed;
                        mainInterface.Visibility = Visibility.Visible;
                        await session.Host.ManageAccountAsync(XamlRoot, ct);
                    }
                    await ReloadAccountAsync(ct, connectServices: false);
                });
            }
            finally
            {
                indicator.IsActive = false;
                loading.Visibility = Visibility.Collapsed;
                mainInterface.Visibility = Visibility.Visible;
            }
            if (closing) return;
            // Optional services must not prevent model discovery or keep the loading view open.
            runtimeWarmup = WarmupServicesAsync();
            appAgentsReady = true;
            explorerReady = true; ScheduleExplorerDrain();
            RefreshWelcome();
        };
        newChat.Click += (_, _) => NewConversation();
        refresh.Click += async (_, _) => await RunAsync(RefreshActivePageAsync);
        send.Click += async (_, _) => await SendAsync();
        dictate.Click += async (_, _) => await ToggleComposerDictationAsync(DesktopPage.Chat);
        stop.Click += (_, _) => operation?.Cancel();
        settingsButton.Click += async (_, _) => await EditSettingsAsync();
        manageAccount.Click += async (_, _) => await RunAsync(async ct =>
        {
            runtimeWarmupLifetime.Cancel();
            await runtimeWarmup;
            accountTransition = true;
            try
            {
            await Mcp.ResetAsync(ct);
            await StopVideoJobsAsync();
            await session.Host.ManageAccountAsync(XamlRoot, ct);
            await ReloadAccountAsync(ct);
            }
            finally { accountTransition = false; ScheduleExplorerDrain(); }
        });
        models.Checked += async (_, _) => { if (!updatingMode) await SelectServiceAsync(ServiceKind.Ai); };
        agents.Checked += async (_, _) => { if (!updatingMode) await SelectServiceAsync(ServiceKind.Agents); };
        models.Unchecked += (_, _) => { if (!updatingMode) UpdateMode(Service); };
        agents.Unchecked += (_, _) => { if (!updatingMode) UpdateMode(Service); };
        target.Opening = () => UpdateTargetSuggestions();
        target.SelectionChanged += () => { if (!busy) RememberSelection(); UpdateModelFavorites(); UpdateComposerUsage(); };
        chats.SelectionChanged += async (_, _) =>
        {
            if (suppress || busy || chats.SelectedItem is not Conversation selected) return;
            await OpenConversationAsync(selected);
        };
        input.KeyDown += async (_, args) =>
        {
            if (args.Key != VirtualKey.Enter) return;
            var shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down);
            if (shift) return;
            args.Handled = true;
            if (!busy) await SendAsync();
        };
        scroll.ViewChanged += (_, _) => followBottom = scroll.ScrollableHeight - scroll.VerticalOffset < 80;
        scroll.SizeChanged += (_, _) => SizeTranscript();
        SizeChanged += (_, args) =>
        {
            // Keep the selector/profile accessible at narrow widths and high display scaling.
            if (args.NewSize.Width < split.OpenPaneLength + 360) split.IsPaneOpen = false;
            // Long diagnostics must remain readable without pushing the composer out of view.
            noticeScroll.MaxHeight = Math.Max(48, Math.Min(240, args.NewSize.Height * .3));
        };
        RenderTranscript();
    }

    private async Task WarmupServicesAsync()
    {
        if (session.Host.RequiresSignIn) return;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(runtimeWarmupLifetime.Token);
        lifetime.CancelAfter(TimeSpan.FromMinutes(2));
        async Task AttemptAsync(string serviceName, Func<Task> start)
        {
            try { await start(); }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch (Exception error) { Trace.WriteLine($"Optional {serviceName} startup failed: {error.GetType().Name}"); }
        }
        await Task.WhenAll(
            AttemptAsync("Video", () => StartVideoJobsAsync(lifetime.Token)),
            AttemptAsync("MCP", () => Task.Run(() => Mcp.ReconnectAsync(lifetime.Token), lifetime.Token)),
            AttemptAsync("Agents", async () =>
            {
                if (!closing && session.Host.AllowLocal && session.Settings.Agents.Location == RuntimeLocation.Local)
                    await Task.Run(() => session.Runtime.ResolveAsync(ServiceKind.Agents, session.Settings, lifetime.Token), lifetime.Token);
            }));
    }

    private UIElement BuildLayout()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        noticeScroll.Content = noticeMessage;
        notice.Content = noticeScroll;
        ControlAppearance.Stock(notice);
        notice.Closed += (_, _) => { if (!notice.IsOpen) notice.Visibility = Visibility.Collapsed; };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetLiveSetting(noticeMessage, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Assertive);
        var notices = new StackPanel(); notices.Children.Add(notice); PrepareExplorerNotice(notices);
        root.Children.Add(notices);
        Grid.SetRow(split, 1); root.Children.Add(split);

        var sidebar = new Grid { Padding = new Thickness(8), HorizontalAlignment = HorizontalAlignment.Left };
        ControlAppearance.Apply(sidebar, (_, _) => { }, palette => sidebar.Background = new SolidColorBrush(palette.Panel));
        sidebar.RowDefinitions.Add(new() { Height = GridLength.Auto });
        sidebar.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        var nav = new StackPanel { Spacing = 8 };
        var toggle = new Button { Name = "SidebarToggle", Content = DesktopIcons.Create(Icon.PanelLeft), Width = 32, Height = 32, Padding = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Right };
        ControlAppearance.Native(toggle);
        ToolbarControls.Label(toggle, DesktopResources.Get("ToggleHistory"));
        toggle.Click += (_, _) => split.IsPaneOpen = !split.IsPaneOpen;
        var sidebarHeader = new Grid { ColumnSpacing = 8, Margin = new Thickness(0, 0, 0, 12) };
        sidebarHeader.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        sidebarHeader.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var appTitle = new TextBlock { Name = "AppTitle", Text = DesktopBranding.AppName, FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        sidebarHeader.Children.Add(appTitle);
        Grid.SetColumn(toggle, 1); sidebarHeader.Children.Add(toggle);
        nav.Children.Add(sidebarHeader);
        var newChatContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        newChatContent.Children.Add(DesktopIcons.Create(Icon.Add));
        newChatContent.Children.Add(new TextBlock { Text = DesktopResources.Get("NewChat"), VerticalAlignment = VerticalAlignment.Center });
        newChat.Content = newChatContent;
        sidebar.Children.Add(nav);
        nav.Children.Add(BuildPageNavigation());
        Grid.SetRow(chats, 1); sidebar.Children.Add(chats);
        void UpdateSidebar()
        {
            var open = split.IsPaneOpen;
            // SplitView clips the expanded pane to CompactPaneLength; it does not remeasure it.
            // Set a real compact width so the trailing toggle stays inside the visible 48px.
            sidebar.Width = open ? split.OpenPaneLength : split.CompactPaneLength;
            sidebarHeader.ColumnDefinitions[0].Width = open ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            sidebarHeader.ColumnSpacing = open ? 8 : 0;
            sidebarHeader.Margin = open ? new Thickness(0, 0, 0, 12) : new Thickness(0);
            pageNavigation.Spacing = open ? 2 : 8;
            appTitle.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            chats.Visibility = expandedPageNavigation.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            UpdateNavigationLayout();
        }
        split.RegisterPropertyChangedCallback(SplitView.IsPaneOpenProperty, (_, _) => UpdateSidebar());
        split.Pane = sidebar;
        UpdateSidebar();

        workspace.RowDefinitions.Add(new() { Height = GridLength.Auto });
        workspace.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        workspace.RowDefinitions.Add(new() { Height = GridLength.Auto });
        workspace.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var top = new Grid { Margin = new Thickness(16, 8, 16, 8), ColumnSpacing = 8 };
        top.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star), MaxWidth = 420 });
        top.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        chatModes.Children.Add(models); chatModes.Children.Add(agents); top.Children.Add(chatModes);
        ControlAppearance.Stock(target);
        ModelHeader(chatModelToolbar, target, () => Service == ServiceKind.Ai && target.Selection.Ids.Count == 1 ? targets.FirstOrDefault(m => m.Id == target.Text) : null, () => activePage == DesktopPage.Chat);
        ModelHeader(imageModelToolbar, imageTarget, () => imageTarget.Selection.Ids.Count == 1 ? SelectedImageModel : null, () => CanUseImages);
        ModelHeader(videoModelToolbar, videoTarget, () => videoTarget.Selection.Ids.Count == 1 ? SelectedVideoModel : null, () => CanUseVideos && !videoSubmitting, videoFavorite);
        ModelHeader(speechModelToolbar, speechTarget, () => speechTarget.Selection.Ids.Count == 1 ? SelectedSpeechModel : null, () => CanUseSpeech);
        ModelHeader(transcriptionModelToolbar, transcriptionTarget, () => SelectedTranscriptionModel, () => CanUseTranscriptions);
        ModelHeader(realtimeHeader, realtimeTarget, () => realtimeModels.FirstOrDefault(m => m.Id == realtimeTarget.Text), () => activePage == DesktopPage.Realtime && realtime is null, realtimeFavorite);
        PrepareAgentRealtimeHeader();
        Grid.SetColumn(chatModelToolbar, 1); top.Children.Add(chatModelToolbar);
        Grid.SetColumn(imageModelToolbar, 1); top.Children.Add(imageModelToolbar);
        Grid.SetColumn(videoModelToolbar, 1); top.Children.Add(videoModelToolbar);
        Grid.SetColumn(speechModelToolbar, 1); top.Children.Add(speechModelToolbar);
        Grid.SetColumn(transcriptionModelToolbar, 1); top.Children.Add(transcriptionModelToolbar);
        Grid.SetColumn(realtimeHeader, 1); top.Children.Add(realtimeHeader);
        AutomationProperties.SetName(progress, DesktopResources.Get("Working"));
        Grid.SetColumn(progress, 3); top.Children.Add(progress);
        var profileMenu = new MenuFlyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight };
        profileMenu.Items.Add(settingsButton); profileMenu.Items.Add(providerSettings);
        if (!session.Host.HasProviderApiKeys) profileMenu.Items.Add(manageAccount);
        profileMenu.Items.Add(new MenuFlyoutSeparator()); profileMenu.Items.Add(refresh);
        profileMenu.Opening += (_, _) =>
        {
            BuildProviderSettingsMenu();
            var palette = ControlAppearance.Palette(this);
            var style = new Style(typeof(MenuFlyoutPresenter));
            style.Setters.Add(new Setter(FrameworkElement.RequestedThemeProperty, ActualTheme));
            style.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(palette.Panel)));
            style.Setters.Add(new Setter(Control.ForegroundProperty, new SolidColorBrush(palette.Text)));
            style.Setters.Add(new Setter(Control.BorderBrushProperty, new SolidColorBrush(palette.Stroke)));
            style.Setters.Add(new Setter(Control.CornerRadiusProperty, new CornerRadius(8)));
            profileMenu.MenuFlyoutPresenterStyle = style;
            foreach (var item in profileMenu.Items.OfType<MenuFlyoutItem>()) item.RequestedTheme = ActualTheme;
        };
        ActualThemeChanged += (_, _) => profileMenu.Hide();
        profileMenu.Opened += (_, _) =>
        {
            foreach (var item in profileMenu.Items.OfType<MenuFlyoutItem>()) ControlAppearance.Refresh(item);
        };
        account.Flyout = profileMenu;
        Grid.SetColumn(modelFilters, 4); top.Children.Add(modelFilters);
        Grid.SetColumn(providerFilters, 4); top.Children.Add(providerFilters);
        Grid.SetColumn(addAgent, 4); top.Children.Add(addAgent);
        Grid.SetColumn(addSkill, 4); top.Children.Add(addSkill);
        Grid.SetColumn(addMcpServer, 4); top.Children.Add(addMcpServer);
        Grid.SetColumn(addPlugin, 4); top.Children.Add(addPlugin);
        Grid.SetColumn(addFiles, 4); top.Children.Add(addFiles);
        Grid.SetColumn(activityToggle, 4); top.Children.Add(activityToggle);
        Grid.SetColumn(account, 5); top.Children.Add(account);
        workspace.Children.Add(top);
        scroll.Content = transcript; Grid.SetRow(scroll, 1); workspace.Children.Add(scroll);
        composer.Children.Add(welcome); composer.Children.Add(composerBadgeRow);
        composer.Children.Add(input);
        composer.Children.Add(realtimePreview);
        var actions = new Grid { Name = "ComposerActions", ColumnSpacing = 4 };
        actions.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        actions.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var composerSettings = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        composerSettings.Children.Add(addContext); composerSettings.Children.Add(chatSettings);
        actions.Children.Add(composerSettings);
        var composerSessionActions = new StackPanel { Name = "ComposerSessionActions", Orientation = Orientation.Horizontal, Spacing = 4 };
        foreach (var control in new UIElement[] { viewSystemContext, dictate, realtimeControls, stop, send }) composerSessionActions.Children.Add(control);
        Grid.SetColumn(composerSessionActions, 1); actions.Children.Add(composerSessionActions);
        composer.Children.Add(actions);
        Grid.SetRow(disclaimer, 3); workspace.Children.Add(disclaimer);
        workspace.Children.Add(composer); split.Content = BuildConversationActivityLayout(BuildDetailsLayout(workspace));
        Grid.SetRow(overviewHost, 1); Grid.SetRowSpan(overviewHost, 2); workspace.Children.Add(overviewHost);
        return root;
    }

    private void SizeTranscript()
    {
        // A vertical ScrollViewer does not constrain its content's desired width reliably on
        // resize. Bound it to the actual viewport, shared with the centered composer width.
        var viewport = scroll.ViewportWidth > 0 ? scroll.ViewportWidth : scroll.ActualWidth;
        if (viewport > 0) transcript.Width = Math.Max(0, Math.Min(1040, viewport - 48));
    }

    private void UpdateAccountMenu()
    {
        // Keep identity/account labels in the menu, never on the generic profile icon.
        manageAccount.Text = session.Host.AllowLocal ? session.Host.AccountLabel
            : session.Host.AccountLabel == DesktopResources.Get("SignIn") ? DesktopResources.Get("SignIn") : DesktopResources.Get("ManageAccount");
    }

    private async Task ReloadAccountAsync(CancellationToken ct, bool connectServices = true)
    {
        welcomeSpeech.Cancel();
        await StopRealtimeAsync();
        var ready = !session.Host.RequiresSignIn;
        if (ready && connectServices) await StartVideoJobsAsync(ct);
        UpdateAccountMenu();
        current = new() { Service = Service };
        input.Text = ""; ResetContext();
        targets = []; TargetId = "";
        rememberedSelections.Clear(); target.IsSuggestionListOpen = false;
        aiModelTargets = null;
        InvalidateCatalogs();
        RenderTranscript();
        await LoadLocalAgentsAsync(ct);
        await LoadHistoryAsync(ct, connectServices && ready);
        await DiscoverAsync(ct);
        if (ready && activePage != DesktopPage.Chat) await LoadActiveOverviewAsync(ct);
    }

    private void UpdateMode(ServiceKind selected)
    {
        if (selected != Service) DiscardDictation();
        target.IsSuggestionListOpen = false;
        service = selected;
        updatingMode = true;
        try
        {
            models.IsChecked = selected == ServiceKind.Ai;
            agents.IsChecked = selected == ServiceKind.Agents;
        }
        finally { updatingMode = false; }
        var label = selected == ServiceKind.Ai ? DesktopResources.Get("SelectModel") : DesktopResources.Get("SelectAgent");
        target.PlaceholderText = label;
        ToolbarControls.Label(target, label);
        chatSettings.Visibility = Visibility.Visible;
        searchContext.Visibility = selected == ServiceKind.Ai ? Visibility.Visible : Visibility.Collapsed;
        RenderMcpTags();
        RenderContextTags();
        UpdateComposerUsage();
    }

    private async Task SelectServiceAsync(ServiceKind selected)
    {
        if (busy || !initialized || selected == Service)
        {
            UpdateMode(Service);
            return;
        }
        RememberSelection();
        UpdateMode(selected);
        TargetId = ""; targets = []; target.ItemsSource = null; target.IsSuggestionListOpen = false;
        NewConversation();
        if (rememberedSelections.TryGetValue(selected, out var previous)) target.SetSelection(previous.Ids, previous.FavoriteRouter);
        await RunAsync(DiscoverAsync);
    }

    private async Task DiscoverAsync(CancellationToken ct)
    {
        // Canceling startup sign-in or explicitly signing out is a normal idle state, not a gateway failure.
        if (session.Host.RequiresSignIn)
        {
            targets = []; TargetId = ""; aiModelTargets = null;
            UpdateAccountMenu(); UpdateTargetSuggestions(); RefreshWelcome();
            return;
        }
        var selected = TargetId;
        var selection = target.Selection.Ids;
        var routerSelected = target.Selection.FavoriteRouter;
        await LoadSelectionCatalogsAsync(ct);
        targets = Service == ServiceKind.Agents ? await AgentTargetsAsync(ct)
            : await Task.Run(() => client.ListAsync(Service, ct), ct);
        if (Service == ServiceKind.Agents) target.SetSelection(selection.Select(id => DesktopAgentTargets.Restore(id, targets)), routerSelected);
        if (Service == ServiceKind.Ai) aiModelTargets = targets;
        UpdateAccountMenu();
        UpdateTargetSuggestions();
        if (string.IsNullOrWhiteSpace(selected) && !routerSelected)
        {
            if (Service == ServiceKind.Ai) SelectNewChatModel();
            else TargetId = targets.FirstOrDefault()?.Id ?? "";
        }
        if (targets.Count == 0 || Service == ServiceKind.Ai && AiModelCatalog.ChatSuggestions(PickerModels(targets), session.Settings.AiModels).Count == 0)
            Show(DesktopResources.Get("NoTargets"), InfoBarSeverity.Warning);
        RefreshWelcome();
        if (Service == ServiceKind.Agents) RenderTranscript();
    }

    private async Task SendAsync(McpSelectedPrompt? selectedPrompt = null)
    {
        if (activePage == DesktopPage.Realtime) { await SendRealtimeAsync(selectedPrompt); return; }
        await realtimeCleanup;
        if (busy || closing || historyDialogOpen || catalogDialog is not null || dictation is not null || dictationChanging || composerDictation is not null
            || selectedPrompt is null && string.IsNullOrWhiteSpace(input.Text) && contextAttachments.Count == 0 && selectedResources.Count == 0) return;
        UpdateTargetSuggestions();
        var selected = TargetId;
        var selectedIds = target.Selection.Ids.ToArray();
        var favoriteRouter = target.Selection.FavoriteRouter;
        if (!favoriteRouter && (selectedIds.Length == 0 || selectedIds.Any(id => !targets.Any(x => x.Id == id))))
        { Show(DesktopResources.Get("SelectTarget"), InfoBarSeverity.Warning); return; }
        var prompt = selectedPrompt is null ? input.Text.Trim() : "";
        var snapshot = contextAttachments.ToArray();
        var resourceSnapshot = selectedResources.ToArray();
        var inferencePreferences = session.Settings.Chat.Clone();
        string? selectedProvider = null;
        var extractDocuments = session.Settings.ConvertAttachmentsToText;
        var partition = session.HistoryPartition;
        await RunAsync(async ct =>
        {
            // Finish preparation before committing a turn or clearing its draft. Cancellation retains input/context.
            if (selectedPrompt is not null && (Service != ServiceKind.Ai || !selectedPrompt.Entry.IsCurrent()))
                throw new InvalidOperationException(DesktopResources.Get("McpDisconnected"));
            var prepared = await ComposerAttachments.PrepareAsync(prompt, snapshot, Service, extractDocuments, documentExtractor, ct,
                resourceSnapshot, selectedPrompt?.Parts);
            await LoadSelectionCatalogsAsync(ct);
            var resolved = await ResolveTurnSelectionAsync(current.Messages.Select(m => m.Message).Append(prepared.Message).ToList(), selectedIds, favoriteRouter, ct);
            selected = resolved[0];
            var agentTarget = targets.First(t => t.Id == selected);
            selectedProvider = agentTarget.ProviderKey;
            System.Text.Json.Nodes.JsonObject? agentRequest = null;
            if (Service == ServiceKind.Agents)
            {
                var workflow = session.Settings.AgentWorkflow.Clone();
                // Favorite routing chooses one agent; manual team workflows are a distinct mode.
                if (!favoriteRouter && resolved.Length > 1) workflow.Validate(aiModelTargets ?? []);
                else workflow.Type = "sequential";
                agentRequest = DesktopAgentRequests.Build(resolved, targets, localAgents, workflow);
            }
            var wireTarget = Service == ServiceKind.Agents ? agentTarget.RemoteAgentId ?? selected : selected;
            activeConversationTools = null;
            if (Service == ServiceKind.Ai ? NeedsSkillCatalog(inferencePreferences)
                : inferencePreferences.PluginEnabled(DesktopLocalTools.SkillSearch))
                await LoadRuntimeSkillsAsync(ct);
            var mcpTurn = CaptureSkillRuntime(inferencePreferences, agentToolsOnly: Service == ServiceKind.Agents);
            activeConversationTools = mcpTurn.ConversationTools;
            activeMcpTurn = mcpTurn;
            var systemContext = Service == ServiceKind.Ai ? CaptureSystemContext(inferencePreferences, mcpTurn) : null;
            if (selectedPrompt is not null && !selectedPrompt.Entry.IsCurrent())
                throw new InvalidOperationException(DesktopResources.Get("McpDisconnected"));
            current.Service = Service; current.Target = selected;
            SaveConversationSelection(selectedIds, favoriteRouter);
            var nameConversation = current.Messages.Count == 0 && current.Title == DesktopResources.Get("NewChat");
            var initialTitle = current.Title;
            current.Messages.Add(new() { Message = prepared.Message });
            var output = new ConversationMessage { Message = new UIMessage { Id = Guid.NewGuid().ToString("N"), Role = Role.assistant,
                Metadata = new Dictionary<string, object> { ["model"] = Service == ServiceKind.Agents ? agentTarget.LocalAgentName ?? agentTarget.Label : selected,
                    ["timestamp"] = DateTimeOffset.UtcNow.ToString("O") } }, Status = "streaming" };
            if (Service == ServiceKind.Agents && !string.IsNullOrWhiteSpace(agentTarget.AgentModelId))
                output.Message.Metadata!["agentModel"] = agentTarget.AgentModelId;
            if (ChatPreferences.ResolveProvider(ServiceKind.Ai, Service == ServiceKind.Agents ? agentTarget.AgentModelId ?? "" : selected, selectedProvider) is { } providerKey)
                output.Message.Metadata!["providerKey"] = providerKey;
            current.Messages.Add(output);
            if (selectedPrompt is null) input.Text = "";
            ResetContext(); followBottom = true; RenderTranscript();
            if (prepared.Warnings.Count > 0) Show(string.Join("\n", prepared.Warnings), InfoBarSeverity.Warning);
            var watch = Stopwatch.StartNew();
            var saveAt = TimeSpan.Zero;
            var renderAt = TimeSpan.Zero;
            await SaveTurnHistoryAsync(partition, current, ct);
            var activityScope = transientConversationData.SetScope(partition, current.Id);
            try
            {
                await DesktopChatTurn.RunAsync(current, output,
                    (messages, token) => client.StreamAsync(current.Service, wireTarget, current.Id, messages, token,
                        inferencePreferences, selectedProvider, systemContext, mcpTurn, agentRequest: agentRequest), ApproveToolAsync,
                    async (force, token) =>
                {
                    if (force || watch.Elapsed - renderAt > TimeSpan.FromMilliseconds(100))
                    { RenderTranscript(); renderAt = watch.Elapsed; }
                    if (force || watch.Elapsed - saveAt > TimeSpan.FromSeconds(1))
                    { await SaveTurnHistoryAsync(partition, current, token); saveAt = watch.Elapsed; }
                }, mcpTurn, session.ActiveLanguage, ct, session.ElicitAsync,
                    item => CaptureConversationData(activityScope, item, output));
                if (output.Status is "interrupted" or "approval required") Show(DesktopResources.Get("StreamInterrupted"), InfoBarSeverity.Warning);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { /* The turn runner retains pending approvals on stop. */ }
            finally
            {
                activeMcpTurn = null;
                activeSharedFileTools?.Dispose(); activeSharedFileTools = null;
                if (output.Status == "streaming") output.Status = "interrupted";
                try { await SaveTurnHistoryAsync(partition, current); await LoadHistoryAsync(CancellationToken.None); }
                catch { Show(DesktopResources.Get("HistorySaveFailed"), InfoBarSeverity.Error); }
                var wasDeleted = activeConversationTools?.IsDeleted(current.Id) == true;
                activeConversationTools = null;
                if (wasDeleted) { current = new() { Service = Service }; SelectNewChatModel(); }
                else if (nameConversation) StartConversationNaming(current, partition, prepared.Message, initialTitle);
                RenderTranscript();
            }
        }, inference: true);
    }

    private void RenderTranscript()
    {
        var draft = realtime?.AgentVoice?.Draft;
        var empty = current.Messages.Count == 0 && draft is null;
        RefreshWelcome();
        welcome.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        scroll.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        Grid.SetRow(composer, empty ? 1 : 2);
        composer.VerticalAlignment = empty ? VerticalAlignment.Center : VerticalAlignment.Bottom;
        SizeTranscript();
        RenderTranscriptRows(MessageDetails.Project(draft is null ? current.Messages : current.Messages.Append(draft).ToList()));
        RefreshDetails();
        RefreshConversationActivity();
        UpdateComposerUsage();
        if (followBottom && !empty)
            DispatcherQueue.TryEnqueue(() =>
            {
                if (scroll.IsLoaded && scroll.Visibility == Visibility.Visible)
                    scroll.ChangeView(null, scroll.ScrollableHeight, null, true);
            });
    }

    private static TextBlock SelectableText(string text) => new() { Text = text, IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap };

    private static Button ActivityButton(string label, Icon icon)
    {
        var button = new Button { Content = DesktopIcons.Create(icon, 14), Width = 32, Height = 32, Padding = new Thickness(0) };
        ToolbarControls.Subtle(button); ToolbarControls.Label(button, label); return button;
    }

    private static void RenderPart(StackPanel content, UIMessagePart part, bool chartsEnabled = false)
    {
        if (part.Type == "text") { content.Children.Add(new ChatMarkdown { Name = "MessageMarkdown", ChartsEnabled = chartsEnabled, Text = PortableConversations.Text(part) }); return; }
        if (part.Type == "reasoning")
        {
            content.Children.Add(new TextBlock { Text = DesktopResources.Get("Reasoning"), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            content.Children.Add(new ChatMarkdown { Name = "ReasoningMarkdown", Text = PortableConversations.Text(part) }); return;
        }
        var raw = PortableConversations.Element(part);
        if (PortableConversations.IsTool(part))
        {
            content.Children.Add(new TextBlock { Text = PortableConversations.ToolName(part), TextWrapping = TextWrapping.Wrap, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            // Activity cards are input-only. Results/decisions remain in history and the approval dialog,
            // not as raw payloads between assistant text blocks.
            foreach (var (field, title) in new[] { ("input", DesktopResources.Get("Input")), ("inputText", DesktopResources.Get("StreamingInput")) })
                if (raw.TryGetProperty(field, out var value) && value.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined) AddStructured(content, title, value);
            return;
        }
        // Safe read-only fallback for files, sources, data, widgets and future parts. No external
        // content is executed or fetched merely because it appeared in a conversation document.
        AddStructured(content, part.Type, raw);
    }

    private static void AddStructured(StackPanel content, string title, JsonElement value)
    {
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(new TextBlock { Text = title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        var text = value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : JsonSerializer.Serialize(value, PortableConversations.Json);
        panel.Children.Add(SelectableText(text));
        var border = new Border { Child = panel, Padding = new Thickness(12), CornerRadius = new CornerRadius(6) };
        ControlAppearance.TokenBadge(border); content.Children.Add(border);
    }

    private void NewConversation()
    {
        if (busy && operation is not null || closing || historyDialogOpen) return;
        DiscardDictation();
        ShowPage(DesktopPage.Chat);
        current = new() { Service = Service };
        SelectNewChatModel();
        input.Text = ""; ResetContext();
        suppress = true; chats.SelectedItem = null; suppress = false;
        RenderTranscript();
    }

    private async Task LoadHistoryAsync(CancellationToken ct, bool connectServices = true)
    {
        var mcpPartition = session.McpPartition;
        if (await Task.Run(() => Mcp.LoadAsync(mcpPartition, ct, connectServices), ct)) Show(DesktopResources.Get("McpInvalidStoredEntries"), InfoBarSeverity.Warning);
        await LoadPluginsAsync(ct, connectServices);
        var partition = session.HistoryPartition;
        await LoadConversationHistoryAsync(partition, ct);
    }

    private void PrepareConversationRow(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.ItemContainer is not ListViewItem container) return;
        if (args.InRecycleQueue)
        {
            if (conversationRows.TryGetValue(container, out var recycled)) recycled.Bind(null);
            return;
        }
        if (args.Phase == 0)
        {
            if (conversationRows.TryGetValue(container, out var previous)) previous.Bind(null);
            container.Loaded -= ConversationContainerLoaded;
            container.Loaded += ConversationContainerLoaded;
            args.RegisterUpdateCallback(1, PrepareConversationRow);
            return;
        }
        BindConversationRow(container, args.Item as Conversation);
    }

    private void ConversationContainerLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is ListViewItem container) BindConversationRow(container, container.Content as Conversation);
    }

    private void BindConversationRow(ListViewItem container, Conversation? conversation)
    {
        if (container.ContentTemplateRoot is not Grid root) return;
        if (!conversationRows.TryGetValue(container, out var row))
        {
            row = new ConversationRow(container, root, RenameAsync, ExportConversationAsync, ToggleConversationPinAsync, DeleteAsync, IsConversationPinned);
            conversationRows.Add(container, row);
        }
        row.Bind(conversation);
    }

    private void FilterHistory()
    {
        suppress = true;
        try
        {
            chats.ItemsSource = ConversationHistory.Order(conversations, conversationPinsPartition == session.HistoryPartition ? conversationPins : new HashSet<string>());
            chats.SelectedItem = conversations.FirstOrDefault(x => x.Id == current.Id);
        }
        finally { suppress = false; }
    }

    private async Task RenameAsync(Conversation selected)
    {
        if (busy || closing || historyDialogOpen || catalogDialog is not null) return;
        historyDialogOpen = true;
        try
        {
            // The open chat may have newer messages than its deserialized history-list entry.
            var chat = selected.Id == current.Id ? current : selected;
            var title = new TextBox { Text = chat.Title, MaxLength = 120 };
            var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = DesktopResources.Get("RenameChat"), Content = title, PrimaryButtonText = DesktopResources.Get("Save"), CloseButtonText = DesktopResources.Get("Cancel") };
            SystemAppearance.PrepareDialog(dialog);
            if (await dialog.ShowAsync() != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(title.Text)) return;
            await RunAsync(async ct => { chat.Title = title.Text.Trim(); await history.SaveAsync(session.HistoryPartition, chat, ct); await LoadHistoryAsync(ct); });
        }
        finally { historyDialogOpen = false; }
    }

    private async Task DeleteAsync(Conversation selected)
    {
        if (busy || closing || historyDialogOpen || catalogDialog is not null) return;
        historyDialogOpen = true;
        try
        {
            var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = DesktopResources.Get("DeleteChat"), Content = DesktopResources.Get("DeleteChatHint"), PrimaryButtonText = DesktopResources.Get("Delete"), CloseButtonText = DesktopResources.Get("Cancel") };
            SystemAppearance.PrepareDialog(dialog);
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            await RunAsync(async ct =>
            {
                history.Delete(session.HistoryPartition, selected.Id);
                await RemoveConversationPinAsync(session.HistoryPartition, selected.Id, ct);
                if (current.Id == selected.Id)
                {
                    current = new() { Service = Service };
                    SelectNewChatModel();
                    input.Text = ""; ResetContext();
                    RenderTranscript();
                }
                await LoadHistoryAsync(ct);
            });
        }
        finally { historyDialogOpen = false; }
    }

    private async Task EditSettingsAsync()
    {
        if (busy || closing || pendingProviderSettingsWrites > 0 || catalogDialog is not null || historyDialogOpen) return;
        DiscardDictation();
        historyDialogOpen = true;
        try { await LoadLocalAgentsAsync(downloadLifetime.Token); }
        catch (Exception) { historyDialogOpen = false; Show(DesktopResources.Get("OperationFailed"), InfoBarSeverity.Error); return; }
        var dialog = new SettingsDialog(session.Settings, session.Host.AllowLocal, session.ActiveLanguage, aiModelTargets,
            ct => client.ListAsync(ServiceKind.Ai, ct), localAgents) { XamlRoot = XamlRoot };
        SystemAppearance.PrepareDialog(dialog);
        ContentDialogResult result;
        try { result = await dialog.ShowAsync(); }
        finally { historyDialogOpen = false; }
        if (result != ContentDialogResult.Primary || dialog.Result is not { } next) return;
        await RunAsync(async ct =>
        {
            var connectionsChanged = new[] { ServiceKind.Ai, ServiceKind.Agents }.Any(kind =>
                session.Settings.For(kind).Location != next.For(kind).Location || session.Settings.For(kind).RemoteUrl != next.For(kind).RemoteUrl);
            var elicitationChanged = session.Settings.ModelContext.EnableFormElicitation != next.ModelContext.EnableFormElicitation;
            var mcpSkillsChanged = session.Settings.ModelContext.EnableSkills != next.ModelContext.EnableSkills;
            var imageStorageChanged = !string.Equals(session.Settings.Images.EffectiveRoot, next.Images.EffectiveRoot, StringComparison.OrdinalIgnoreCase);
            var videoStorageChanged = !string.Equals(session.Settings.Videos.EffectiveRoot, next.Videos.EffectiveRoot, StringComparison.OrdinalIgnoreCase);
            if (connectionsChanged)
            {
                await runtimeWarmup.WaitAsync(ct);
                await StopVideoJobsAsync();
            }
            await SettingsStore.SaveAsync(session.DataDirectory, next);
            session.Settings = next;
            ReconcileRememberedSelections();
            RenderTranscript();
            RefreshWelcome();
            RefreshProviderFiltering();
            if (elicitationChanged || mcpSkillsChanged) await Mcp.ReconnectAsync(ct);
            RenderContextTags();
            if (next.Language != session.ActiveLanguage) Show(DesktopResources.Get("RestartRequired"), InfoBarSeverity.Informational);
            // Language and composer preferences do not change the runtime, account, history partition, or current draft.
            if (!connectionsChanged)
            {
                if (imageStorageChanged && activePage == DesktopPage.Images) await ReloadImageLibraryAsync(ct);
                if (videoStorageChanged && activePage == DesktopPage.Videos) await ReloadVideoLibraryAsync(ct);
                return;
            }
            await session.Runtime.DisposeAsync();
            await StartVideoJobsAsync(ct);
            current = new() { Service = Service }; targets = []; TargetId = "";
            aiModelTargets = null;
            input.Text = ""; ResetContext();
            InvalidateCatalogs();
            RenderTranscript(); await LoadHistoryAsync(ct); await DiscoverAsync(ct);
            if (activePage != DesktopPage.Chat) await LoadActiveOverviewAsync(ct);
        });
    }

    private async Task RunAsync(Func<CancellationToken, Task> action, bool inference = false)
    {
        if (busy || closing || pendingProviderSettingsWrites > 0) return;
        busy = true; operation = new CancellationTokenSource();
        if (!inference) operation.CancelAfter(TimeSpan.FromMinutes(2));
        SetBusy(true, inference);
        try { await action(operation.Token); }
        catch (OperationCanceledException) { Show(DesktopResources.Get("OperationCanceled"), InfoBarSeverity.Warning); }
        catch (Exception e)
        {
            Show(e is GatewayException or InvalidOperationException or JsonException ? GatewayErrors.Display(e.Message) ?? DesktopResources.Get("OperationFailed")
                : DesktopResources.Get("OperationFailed"), InfoBarSeverity.Error);
        }
        finally { operation.Dispose(); operation = null; busy = false; SetBusy(false, false); ScheduleExplorerDrain(); }
    }

    private void SetBusy(bool value, bool inference)
    {
        if (value && (dictation is not null || dictationChanging)) DiscardDictation();
        ResetFileDrop();
        input.IsReadOnly = value && inference;
        models.IsEnabled = agents.IsEnabled = target.IsEnabled = refresh.IsEnabled = account.IsEnabled = settingsButton.IsEnabled = manageAccount.IsEnabled = newChat.IsEnabled = searchChats.IsEnabled = chats.IsEnabled = send.IsEnabled = !value;
        addContext.IsEnabled = !value;
        chatSettings.IsEnabled = !value;
        dictate.IsEnabled = !value;
        manageMcp.IsEnabled = !value; RenderMcpTags();
        modelFilters.IsEnabled = !value;
        providerFilters.IsEnabled = !value;
        RenderContextTags();
        RenderApprovalBadges();
        SetOverviewBusy(value);
        providerDialog?.SetActionsEnabled(!value);
        SetNavigationBusy(value);
        imageTarget.IsEnabled = !value;
        imagesPage.SetBusy(value, inference && activePage == DesktopPage.Images);
        videoTarget.IsEnabled = !value; videosPage.SetBusy(value); UpdateVideoFavorite();
        transcriptionTarget.IsEnabled = !value && transcriptionRealtime is null;
        speechTarget.IsEnabled = !value;
        UpdateModelFavorites();
        speechPage.SetBusy(value, inference && activePage == DesktopPage.Speech);
        transcriptionsPage.SetBusy(value, inference && activePage == DesktopPage.Transcriptions);
        UpdateDictationAvailability();
        progress.IsActive = value;
        progress.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        stop.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        send.Visibility = inference ? Visibility.Collapsed : Visibility.Visible;
        if (!value) ControlAppearance.RefreshItems(chats);
        UpdateRealtimeControls();
        UpdateTranscriptionRealtimeAvailability();
    }

    private void Show(string message, InfoBarSeverity severity)
    {
        if (activePage == DesktopPage.Images) { imagesPage.Notice(message, severity); return; }
        if (activePage == DesktopPage.Videos) { videosPage.Notice(message, severity); return; }
        if (activePage == DesktopPage.Speech) { speechPage.Notice(message, severity); return; }
        if (activePage == DesktopPage.Transcriptions) { transcriptionsPage.Notice(message, severity); return; }
        // Only dismissal or a newer error may replace an error, never unrelated background work.
        if (notice.IsOpen && notice.Severity == InfoBarSeverity.Error && severity != InfoBarSeverity.Error) return;
        var label = severity switch
        {
            InfoBarSeverity.Error => DesktopResources.Get("Error"),
            InfoBarSeverity.Warning => DesktopResources.Get("Warning"),
            InfoBarSeverity.Success => DesktopResources.Get("Success"),
            _ => DesktopResources.Get("Information")
        };
        notice.Title = label;
        notice.Severity = severity;
        noticeMessage.Text = message;
        noticeScroll.ChangeView(null, 0, null, true);
        notice.Visibility = Visibility.Visible;
        notice.IsOpen = true;
    }

    public async Task ShutdownAsync()
    {
        closing = true; operation?.Cancel(); downloadLifetime.Cancel();
        await StopRealtimeTranscriptionAsync();
        DiscardDictation(); await dictationCleanup;
        Microsoft.Win32.SystemEvents.PowerModeChanged -= RealtimePowerChanged;
        await StopRealtimeAsync();
        await realtimeCleanup;
        runtimeWarmupLifetime.Cancel();
        await runtimeWarmup;
        runtimeWarmupLifetime.Dispose();
        explorerConfirmation?.Hide(); CancelExplorerBatches();
        appAgentLifetime.Cancel(); welcomeRequest?.Cancel(); welcomeSpeech.Dispose();
        toolOutputDialog?.Hide();
        videoWorkLifetime.Cancel();
        videoLinkDialog?.Hide(); videoDeleteDialog?.Hide(); videoPreviewDialog?.ReleasePlayer(); videoPreviewDialog?.Hide();
        if (videoSettingsDialog is not null) { videoSettingsDialog.DiscardOnShutdown = true; videoSettingsDialog.Hide(); }
        await StopVideoJobsAsync(); await videoSubmission;
        elicitationLifetime.Cancel(); elicitationDialog?.Hide(); session.ElicitationHandler = null;
        ResetFileDrop();
        catalogDialogLoad?.Cancel(); catalogDialog?.Hide();
        providerDialog?.Hide();
        providerSelectionFlyout?.Hide();
        agentEditor?.Hide();
        skillEditor?.CancelAndHide();
        githubSkillImportDialog?.CancelAndHide();
        pluginDialog?.CancelAndHide(); pluginSelectionDialog?.Hide();
        skillDeleteDialog?.Hide();
        removeSharedFileDialog?.Hide();
        searchDialog?.Hide(); linkDialog?.Hide();
        imageLinkDialog?.Hide(); imagePreviewDialog?.Hide();
        speechDetailsDialog?.Hide();
        if (speechSettingsDialog is not null) { speechSettingsDialog.DiscardOnShutdown = true; speechSettingsDialog.Hide(); }
        transcriptionDetailsDialog?.Hide(); transcriptionDeleteDialog?.Hide();
        if (transcriptionSettingsDialog is not null) { transcriptionSettingsDialog.DiscardOnShutdown = true; transcriptionSettingsDialog.Hide(); }
        if (imageSettingsDialog is not null) { imageSettingsDialog.DiscardOnShutdown = true; imageSettingsDialog.Hide(); }
        resourcesDialog?.Hide();
        promptsDialog?.Hide();
        approvalDialog?.Hide();
        systemContextDialog?.Hide();
        mcpDialog?.Shutdown(); Mcp.Changed -= McpChanged;
        if (chatSettingsDialog is not null) { chatSettingsDialog.DiscardOnShutdown = true; chatSettingsDialog.CancelSkillLoading(); chatSettingsDialog.Hide(); }
        contextSearchDialog?.Shutdown();
        while (busy || pendingProviderSettingsWrites > 0) await Task.Delay(20);
        await Task.WhenAll(appAgentTasks.ToArray());
        appAgentLifetime.Dispose();
        activeSharedFileTools?.Dispose(); activeSharedFileTools = null;
        await approvalSettingsWrite.WaitAsync(); approvalSettingsWrite.Release();
        // A save picker may remain open until dismissed; no download continues after shutdown.
        await session.DisposeAsync(); http.Dispose(); contextHttp.Dispose(); mcpCatalogHttp.Dispose();
    }
}
