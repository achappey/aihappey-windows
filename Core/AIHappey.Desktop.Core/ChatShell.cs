using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Vercel.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
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
    private readonly TextBlock disclaimer = new() { Name = "Disclaimer", Text = DesktopResources.Get("Disclaimer"), FontSize = 12, TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(16, 0, 16, 8) };
    private readonly TextBox input = new() { PlaceholderText = DesktopResources.Get("AskAnything"), AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 96, MaxHeight = 240 };
    private readonly ToggleButton models = ToolbarControls.CreateModeButton(ToolbarControls.BrainIcon(), DesktopResources.Get("Models"));
    private readonly ToggleButton agents = ToolbarControls.CreateModeButton(ToolbarControls.BotIcon(), DesktopResources.Get("Agents"));
    private readonly AutoSuggestBox target = new() { PlaceholderText = DesktopResources.Get("SelectModel"), MinWidth = 120, MaxWidth = 420, Height = 40, CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), VerticalContentAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly MenuFlyoutItem refresh = new() { Text = DesktopResources.Get("Refresh"), Icon = new SymbolIcon(Symbol.Refresh) };
    private readonly Button account = new() { Content = new SymbolIcon(Symbol.Contact), Width = 40, Height = 40, Padding = new Thickness(0), CornerRadius = new CornerRadius(20) };
    private readonly MenuFlyoutItem settingsButton = new() { Text = DesktopResources.Get("Settings"), Icon = new SymbolIcon(Symbol.Setting) };
    private readonly MenuFlyoutItem manageAccount = new() { Icon = new FontIcon { Glyph = "\uE8D7" } };
    private readonly Button send = new() { Content = new SymbolIcon(Symbol.Send), Width = 40, Height = 40, Padding = new Thickness(0), CornerRadius = new CornerRadius(6), HorizontalAlignment = HorizontalAlignment.Right };
    private readonly Button stop = new() { Content = DesktopResources.Get("Stop"), Visibility = Visibility.Collapsed };
    private readonly Button newChat = new() { HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(12, 10, 12, 10), CornerRadius = new CornerRadius(6) };
    private readonly ListView chats = new() { SelectionMode = ListViewSelectionMode.Single };
    private readonly ConditionalWeakTable<ListViewItem, ConversationRow> conversationRows = new();
    private readonly Grid notice = new() { ColumnSpacing = 8, Padding = new Thickness(16, 8, 16, 8), Visibility = Visibility.Collapsed };
    private readonly TextBlock noticeMessage = new() { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock progress = new() { Text = DesktopResources.Get("Working"), VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
    private IReadOnlyList<ChatTarget> targets = [];
    private IReadOnlyList<Conversation> conversations = [];
    private Conversation current = new();
    private CancellationTokenSource? operation;
    private bool busy;
    private bool suppress;
    private bool initialized;
    private bool followBottom = true;
    private bool closing;
    private bool updatingMode;
    private bool historyDialogOpen;
    private readonly Dictionary<string, int> activityPages = [];
    private ServiceKind service;
    private ServiceKind Service => service;

    public ChatShell(DesktopSession session)
    {
        this.session = session;
        client = new(session, http);
        history = new(Path.Combine(session.DataDirectory, "conversations"));
        catalogClient = new(client, http);
        catalogFavorites = new(Path.Combine(session.DataDirectory, "catalog-favorites"));
        PrepareContext();
        PrepareMcp();
        PrepareChatSettings();
        PrepareSystemContext();
        PrepareTranscript();
        Content = BuildLayout();
        PrepareFileDrop();
        ControlAppearance.Apply(this, (_, _) => { }, palette =>
        {
            Background = new SolidColorBrush(palette.Surface);
            Foreground = new SolidColorBrush(palette.Text);
        });
        foreach (var control in new Control[] { input, chats, account, send, stop, settingsButton, manageAccount, refresh }) ControlAppearance.Native(control);
        ToolbarControls.Subtle(newChat);
        PrepareSearchChats();
        ToolbarControls.Label(newChat, DesktopResources.Get("NewChat"));
        ControlAppearance.BorderlessItems(chats);
        chats.ItemTemplate = ConversationRow.Template();
        chats.ContainerContentChanging += PrepareConversationRow;
        UpdateAccountMenu();
        UpdateMode(ServiceKind.Ai);
        ToolbarControls.Label(account, DesktopResources.Get("UserProfile"));
        ToolbarControls.Label(send, DesktopResources.Get("SendMessage"));
        ToolbarControls.Label(target, DesktopResources.Get("SelectModel"));
        AutomationProperties.SetName(input, DesktopResources.Get("Message"));
        AutomationProperties.SetHelpText(input, DesktopResources.Get("InputHelp"));
        AutomationProperties.SetHelpText(settingsButton, DesktopResources.Get("ConnectionSettings"));
        AutomationProperties.SetHelpText(refresh, DesktopResources.Get("RefreshTargets"));
        Loaded += async (_, _) =>
        {
            if (initialized) return;
            initialized = true;
            await RunAsync(async ct =>
            {
                session.Settings.Validate(session.Host.AllowLocal);
                await session.Host.InitializeAsync(ct);
                UpdateAccountMenu();
                await LoadHistoryAsync(ct);
                if (session.Host.AllowLocal && session.Settings.Agents.Location == RuntimeLocation.Local)
                    await session.Runtime.ResolveAsync(ServiceKind.Agents, session.Settings, ct);
                await DiscoverAsync(ct);
            });
        };
        newChat.Click += (_, _) => NewConversation();
        refresh.Click += async (_, _) => await RunAsync(RefreshActivePageAsync);
        send.Click += async (_, _) => await SendAsync();
        stop.Click += (_, _) => operation?.Cancel();
        settingsButton.Click += async (_, _) => await EditSettingsAsync();
        manageAccount.Click += async (_, _) => await RunAsync(async ct =>
        {
            await Mcp.ResetAsync(ct);
            await session.Host.ManageAccountAsync(XamlRoot, ct);
            UpdateAccountMenu();
            current = new() { Service = Service };
            input.Text = ""; ResetContext();
            targets = []; target.Text = "";
            InvalidateCatalogs();
            RenderTranscript();
            await LoadHistoryAsync(ct);
            await DiscoverAsync(ct);
            if (activePage != DesktopPage.Chat) await LoadActiveOverviewAsync(ct);
        });
        models.Checked += async (_, _) => { if (!updatingMode) await SelectServiceAsync(ServiceKind.Ai); };
        agents.Checked += async (_, _) => { if (!updatingMode) await SelectServiceAsync(ServiceKind.Agents); };
        models.Unchecked += (_, _) => { if (!updatingMode) UpdateMode(Service); };
        agents.Unchecked += (_, _) => { if (!updatingMode) UpdateMode(Service); };
        target.QueryIcon = new FontIcon { Glyph = "\uE70D", FontSize = 12 };
        target.QuerySubmitted += (_, args) =>
        {
            if (args.ChosenSuggestion is ChatTarget selected) target.Text = selected.Id;
            else
            {
                target.ItemsSource = targets.Take(100).ToArray();
                target.IsSuggestionListOpen = true;
            }
        };
        target.TextChanged += (_, args) =>
        {
            if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
            target.ItemsSource = targets.Where(x => x.Label.Contains(target.Text, StringComparison.OrdinalIgnoreCase) || x.Id.Contains(target.Text, StringComparison.OrdinalIgnoreCase)).Take(100).ToArray();
        };
        target.SuggestionChosen += (_, args) => target.Text = ((ChatTarget)args.SelectedItem).Id;
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
        };
        RenderTranscript();
    }

    private UIElement BuildLayout()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        notice.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        notice.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        notice.Children.Add(noticeMessage);
        var dismiss = new Button { Content = DesktopResources.Get("Dismiss") };
        ControlAppearance.Native(dismiss);
        dismiss.Click += (_, _) => notice.Visibility = Visibility.Collapsed;
        Grid.SetColumn(dismiss, 1); notice.Children.Add(dismiss);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetLiveSetting(noticeMessage, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Assertive);
        root.Children.Add(notice);
        Grid.SetRow(split, 1); root.Children.Add(split);

        var sidebar = new Grid { Padding = new Thickness(8), HorizontalAlignment = HorizontalAlignment.Left };
        ControlAppearance.Apply(sidebar, (_, _) => { }, palette => sidebar.Background = new SolidColorBrush(palette.Panel));
        sidebar.RowDefinitions.Add(new() { Height = GridLength.Auto });
        sidebar.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        var nav = new StackPanel { Spacing = 8 };
        var toggle = new Button { Name = "SidebarToggle", Content = new FontIcon { Glyph = "\uE700" }, Width = 32, Height = 32, Padding = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Right };
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
        var sidebarBody = new StackPanel { Spacing = 8 };
        var newChatContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        newChatContent.Children.Add(new SymbolIcon(Symbol.Add));
        newChatContent.Children.Add(new TextBlock { Text = DesktopResources.Get("NewChat"), VerticalAlignment = VerticalAlignment.Center });
        newChat.Content = newChatContent;
        sidebarBody.Children.Add(newChat); sidebarBody.Children.Add(searchChats);
        nav.Children.Add(sidebarBody); sidebar.Children.Add(nav);
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
            appTitle.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            sidebarBody.Visibility = chats.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            pageNavigation.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
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
        chatModes.Children.Add(models); chatModes.Children.Add(agents); top.Children.Add(chatModes);
        ToolbarControls.Outline(target);
        Grid.SetColumn(target, 1); top.Children.Add(target);
        Grid.SetColumn(progress, 3); top.Children.Add(progress);
        var profileMenu = new MenuFlyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight };
        profileMenu.Items.Add(settingsButton); profileMenu.Items.Add(manageAccount);
        profileMenu.Items.Add(new MenuFlyoutSeparator()); profileMenu.Items.Add(refresh);
        profileMenu.Opening += (_, _) =>
        {
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
        Grid.SetColumn(account, 4); top.Children.Add(account);
        workspace.Children.Add(top);
        scroll.Content = transcript; Grid.SetRow(scroll, 1); workspace.Children.Add(scroll);
        composer.Children.Add(welcome); composer.Children.Add(contextTagScroll); composer.Children.Add(mcpTagScroll); composer.Children.Add(input);
        var actions = new Grid();
        actions.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        actions.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        actions.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        actions.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var composerSettings = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        composerSettings.Children.Add(addContext); composerSettings.Children.Add(chatSettings);
        actions.Children.Add(composerSettings);
        Grid.SetColumn(viewSystemContext, 1); actions.Children.Add(viewSystemContext);
        Grid.SetColumn(stop, 2); actions.Children.Add(stop); Grid.SetColumn(send, 3); actions.Children.Add(send);
        composer.Children.Add(actions);
        Grid.SetRow(disclaimer, 3); workspace.Children.Add(disclaimer);
        workspace.Children.Add(composer); split.Content = BuildDetailsLayout(workspace);
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

    private void UpdateMode(ServiceKind selected)
    {
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
        chatSettings.Visibility = selected == ServiceKind.Ai ? Visibility.Visible : Visibility.Collapsed;
        RenderMcpTags();
    }

    private async Task SelectServiceAsync(ServiceKind selected)
    {
        if (busy || !initialized || selected == Service)
        {
            UpdateMode(Service);
            return;
        }
        UpdateMode(selected);
        target.Text = ""; targets = []; target.ItemsSource = null; target.IsSuggestionListOpen = false;
        NewConversation();
        await RunAsync(DiscoverAsync);
    }

    private async Task DiscoverAsync(CancellationToken ct)
    {
        var selected = target.Text;
        targets = await client.ListAsync(Service, ct);
        UpdateAccountMenu();
        target.ItemsSource = targets.Take(100).ToArray();
        if (string.IsNullOrWhiteSpace(selected)) target.Text = targets.FirstOrDefault()?.Id ?? "";
        if (targets.Count == 0) Show(DesktopResources.Get("NoTargets"), InfoBarSeverity.Warning);
    }

    private async Task SendAsync()
    {
        if (busy || closing || historyDialogOpen || catalogDialog is not null
            || string.IsNullOrWhiteSpace(input.Text) && contextAttachments.Count == 0 && selectedResources.Count == 0) return;
        var selected = target.Text.Trim();
        if (!targets.Any(x => x.Id == selected)) { Show(DesktopResources.Get("SelectTarget"), InfoBarSeverity.Warning); return; }
        var prompt = input.Text.Trim();
        var snapshot = contextAttachments.ToArray();
        var resourceSnapshot = selectedResources.ToArray();
        var inferencePreferences = session.Settings.Chat.Clone();
        var selectedProvider = targets.First(x => x.Id == selected).ProviderKey;
        var extractDocuments = session.Settings.ConvertAttachmentsToText;
        var partition = session.HistoryPartition;
        await RunAsync(async ct =>
        {
            // Finish preparation before committing a turn or clearing its draft. Cancellation retains input/context.
            var prepared = await ComposerAttachments.PrepareAsync(prompt, snapshot, Service, extractDocuments, documentExtractor, ct, resourceSnapshot);
            var mcpTurn = Service == ServiceKind.Ai ? Mcp.Capture() : McpTurnSnapshot.Empty;
            activeMcpTurn = mcpTurn;
            var systemContext = Service == ServiceKind.Ai ? CaptureSystemContext(inferencePreferences, mcpTurn) : null;
            current.Service = Service; current.Target = selected;
            if (current.Messages.Count == 0)
            {
                var title = string.IsNullOrWhiteSpace(prompt) ? string.Join(", ", snapshot.Select(file => file.Name).Concat(resourceSnapshot.Select(r => r.Name))) : prompt;
                current.Title = title.Length > 60 ? title[..60] + "…" : title;
            }
            current.Messages.Add(new() { Message = prepared.Message });
            var output = new ConversationMessage { Message = new UIMessage { Id = Guid.NewGuid().ToString("N"), Role = Role.assistant,
                Metadata = new Dictionary<string, object> { ["model"] = selected, ["timestamp"] = DateTimeOffset.UtcNow.ToString("O") } }, Status = "streaming" };
            // Only complete prior turns and user input go back to the service, including their original context parts.
            var requestMessages = current.Messages.Where(PortableConversations.CanReplay).Select(x => x.Message).ToList();
            current.Messages.Add(output); input.Text = ""; ResetContext(); followBottom = true; RenderTranscript();
            if (prepared.Warnings.Count > 0) Show(string.Join("\n", prepared.Warnings), InfoBarSeverity.Warning);
            var assembler = new MessageAssembler(output);
            var watch = Stopwatch.StartNew();
            var saveAt = TimeSpan.Zero;
            var renderAt = TimeSpan.Zero;
            await history.SaveAsync(partition, current, ct);
            try
            {
                var executed = new HashSet<string>(StringComparer.Ordinal);
                for (var round = 0; ; round++)
                {
                    await foreach (var item in client.StreamAsync(current.Service, selected, current.Id, requestMessages, ct, inferencePreferences, selectedProvider, systemContext, mcpTurn))
                    {
                        assembler.Apply(item);
                        if (watch.Elapsed - renderAt > TimeSpan.FromMilliseconds(100) || assembler.Finished)
                        { RenderTranscript(); renderAt = watch.Elapsed; }
                        if (watch.Elapsed - saveAt > TimeSpan.FromSeconds(1))
                        { await history.SaveAsync(partition, current, ct); saveAt = watch.Elapsed; }
                        if (assembler.Finished) break;
                    }
                    if (!assembler.Finished || output.Status != "complete" || current.Service != ServiceKind.Ai) break;
                    var calls = await DesktopMcpToolExecution.ExecutePendingAsync(output, mcpTurn, executed, session.ActiveLanguage, ct);
                    if (calls == 0) break;
                    await history.SaveAsync(partition, current, ct); RenderTranscript();
                    if (round + 1 >= DesktopMcpToolExecution.MaxRounds) throw new GatewayException(DesktopResources.Get("McpToolLimit"));
                    requestMessages = current.Messages.Where(x => x != output && PortableConversations.CanReplay(x)).Select(x => x.Message).Append(output.Message).ToList();
                    assembler.Continue();
                }
                if (!assembler.Finished) { output.Status = "interrupted"; Show(DesktopResources.Get("StreamInterrupted"), InfoBarSeverity.Warning); }
            }
            catch (OperationCanceledException) { output.Status = "stopped"; }
            catch { if (!assembler.ApprovalRequired) output.Status = "failed"; throw; }
            finally
            {
                activeMcpTurn = null;
                if (output.Status == "streaming") output.Status = "interrupted";
                try { await history.SaveAsync(partition, current); await LoadHistoryAsync(CancellationToken.None); }
                catch { Show(DesktopResources.Get("HistorySaveFailed"), InfoBarSeverity.Error); }
                RenderTranscript();
            }
        }, inference: true);
    }

    private void RenderTranscript()
    {
        var empty = current.Messages.Count == 0;
        welcome.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        scroll.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        Grid.SetRow(composer, empty ? 1 : 2);
        composer.VerticalAlignment = empty ? VerticalAlignment.Center : VerticalAlignment.Bottom;
        SizeTranscript();
        transcript.Children.Clear();
        messageTimes.Clear();
        var liveActivity = new HashSet<string>();
        foreach (var projected in MessageDetails.Project(current.Messages))
        {
            var message = projected.Message;
            var block = projected.Block;
            var user = message.Message.Role == Role.user;
            var content = new StackPanel { Spacing = 8 };
            var key = current.Id + ":" + block.Key;
            if (block.Activity) liveActivity.Add(key);
            var page = block.Activity && activityPages.TryGetValue(key, out var chosen) ? Math.Clamp(chosen, 0, block.Parts.Count - 1) : block.Parts.Count - 1;
            var displayed = page >= 0 ? block.Parts[page] : null;
            var header = MessageHeader(message, user, block.Activity ? displayed : null);
            if (displayed is not null) RenderPart(content, displayed);
            else content.Children.Add(SelectableText(block.Key.EndsWith(":details", StringComparison.Ordinal) ? DesktopResources.Get("AttachmentsSources") : DesktopResources.Get("Working")));
            string? tokenCount = null;
            if (!user && !block.Activity && message.Message.Metadata is not null)
            {
                try
                {
                    var metadata = FinishMessageMetadata.FromDictionary(message.Message.Metadata, current.Target, message.Timestamp);
                    tokenCount = metadata.Usage.TotalTokens?.ToString();
                }
                catch (JsonException) { /* Optional metadata must not break rendering. */ }
            }
            var copy = ToolbarControls.CopyButton();
            copy.Click += (_, _) =>
            {
                try { var package = new DataPackage(); package.SetText(displayed is null ? "" : displayed.Type is "text" or "reasoning" ? PortableConversations.Text(displayed) : PortableConversations.Element(displayed).GetRawText()); Clipboard.SetContent(package); }
                catch { Show(DesktopResources.Get("ClipboardUnavailable"), InfoBarSeverity.Warning); }
            };
            var footerActions = new MessageFooterPanel();
            footerActions.Children.Add(copy);
            if (block.Activity)
            {
                var previous = ActivityButton(DesktopResources.Get("PreviousActivity"), "\uE76B");
                var next = ActivityButton(DesktopResources.Get("NextActivity"), "\uE76C");
                previous.IsEnabled = page > 0; next.IsEnabled = page < block.Parts.Count - 1;
                previous.Click += (_, _) => { activityPages[key] = page - 1; RenderTranscript(); };
                next.Click += (_, _) => { activityPages[key] = page + 1; RenderTranscript(); };
                footerActions.Children.Add(previous);
                footerActions.Children.Add(new TextBlock { Name = "ActivityCount", Text = $"{page + 1}/{block.Parts.Count}", VerticalAlignment = VerticalAlignment.Center });
                footerActions.Children.Add(next);
                var list = ActivityButton(DesktopResources.Get("ActivityList"), "\uE8FD");
                list.Click += (_, _) => ShowActivity(block.Key, key, page, list);
                footerActions.Children.Add(list);
            }
            if (tokenCount is not null)
            {
                var usage = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
                usage.Children.Add(new FontIcon { Glyph = "\uE943", FontSize = 14 });
                usage.Children.Add(new TextBlock { Text = tokenCount, VerticalAlignment = VerticalAlignment.Center });
                var badge = new Border { Name = "TokenUsage", Child = usage, Padding = new Thickness(10, 4, 10, 4), CornerRadius = new CornerRadius(16), VerticalAlignment = VerticalAlignment.Center };
                ToolbarControls.Label(badge, DesktopResources.Format("TokenUsage", tokenCount));
                badge.Style = TranscriptStyle("TranscriptSurfaceStyle");
                footerActions.Children.Add(badge);
            }
            AddDetailsActions(footerActions, projected);
            var footer = new Border { Name = "MessageFooter", Child = footerActions, Padding = new Thickness(12, 8, 12, 8), BorderThickness = new Thickness(0, 1, 0, 0) };
            footer.Style = TranscriptStyle("TranscriptSeparatorStyle");
            var sections = new StackPanel();
            sections.Children.Add(header);
            sections.Children.Add(new Border { Name = "MessageBody", Child = content, Padding = new Thickness(16) });
            sections.Children.Add(footer);
            var card = new Border
            {
                Name = block.Activity ? "ActivityCard" : "MessageCard", CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
                HorizontalAlignment = user ? HorizontalAlignment.Right : HorizontalAlignment.Left
            };
            card.Style = TranscriptStyle(user ? "TranscriptUserCardStyle" : "TranscriptCardStyle");
            if (block.Activity)
            {
                var activity = new Grid();
                activity.ColumnDefinitions.Add(new() { Width = new GridLength(4) });
                activity.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
                activity.Children.Add(new Border { Name = "ActivityAccent", Style = TranscriptStyle("TranscriptActivityAccentStyle"), CornerRadius = new CornerRadius(7, 0, 0, 7) });
                Grid.SetColumn(sections, 1); activity.Children.Add(sections); card.Child = activity;
            }
            else card.Child = sections;
            // Assemble before attachment: WinUI elements cannot have two visual parents.
            // Every row spans the transcript. Relative columns keep cards <= 80% of its width,
            // align users right / assistants left, and shrink correctly for wrapped long text.
            var row = new Grid();
            row.ColumnDefinitions.Add(new() { Width = new GridLength(user ? 1 : 4, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new() { Width = new GridLength(user ? 4 : 1, GridUnitType.Star) });
            Grid.SetColumn(card, user ? 1 : 0);
            row.Children.Add(card); transcript.Children.Add(row);
        }
        foreach (var key in activityPages.Keys.Where(key => !liveActivity.Contains(key)).ToArray()) activityPages.Remove(key);
        RefreshDetails();
        if (followBottom && !empty)
            DispatcherQueue.TryEnqueue(() =>
            {
                if (scroll.IsLoaded && scroll.Visibility == Visibility.Visible)
                    scroll.ChangeView(null, scroll.ScrollableHeight, null, true);
            });
    }

    private static TextBlock SelectableText(string text) => new() { Text = text, IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap };

    private static Button ActivityButton(string label, string glyph)
    {
        var button = new Button { Content = new FontIcon { Glyph = glyph, FontSize = 14 }, Width = 32, Height = 32, Padding = new Thickness(0) };
        ToolbarControls.Subtle(button); ToolbarControls.Label(button, label); return button;
    }

    private static void RenderPart(StackPanel content, UIMessagePart part)
    {
        if (part.Type == "text") { content.Children.Add(new ChatMarkdown { Name = "MessageMarkdown", Text = PortableConversations.Text(part) }); return; }
        if (part.Type == "reasoning")
        {
            content.Children.Add(new TextBlock { Text = DesktopResources.Get("Reasoning"), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            content.Children.Add(new ChatMarkdown { Name = "ReasoningMarkdown", Text = PortableConversations.Text(part) }); return;
        }
        var raw = PortableConversations.Element(part);
        if (PortableConversations.IsTool(part))
        {
            content.Children.Add(new TextBlock { Text = PortableConversations.ToolName(part), TextWrapping = TextWrapping.Wrap, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            content.Children.Add(SelectableText(PortableConversations.String(raw, "state") ?? DesktopResources.Get("ToolActivity")));
            foreach (var (field, title) in new[] { ("input", DesktopResources.Get("Input")), ("output", DesktopResources.Get("Output")), ("inputText", DesktopResources.Get("StreamingInput")), ("errorText", DesktopResources.Get("Error")), ("approval", DesktopResources.Get("Approval")) })
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
        ShowPage(DesktopPage.Chat);
        current = new() { Service = Service };
        input.Text = ""; ResetContext();
        suppress = true; chats.SelectedItem = null; suppress = false;
        RenderTranscript();
    }

    private async Task LoadHistoryAsync(CancellationToken ct)
    {
        if (await Mcp.LoadAsync(session.McpPartition, ct)) Show(DesktopResources.Get("McpInvalidStoredEntries"), InfoBarSeverity.Warning);
        conversations = await history.ListAsync(session.HistoryPartition, ct);
        FilterHistory();
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
            row = new ConversationRow(container, root, RenameAsync, DeleteAsync);
            conversationRows.Add(container, row);
        }
        row.Bind(conversation);
    }

    private void FilterHistory()
    {
        suppress = true;
        try
        {
            chats.ItemsSource = conversations;
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
                if (current.Id == selected.Id)
                {
                    current = new() { Service = Service };
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
        if (busy || closing || catalogDialog is not null || historyDialogOpen) return;
        historyDialogOpen = true;
        var dialog = new SettingsDialog(session.Settings, session.Host.AllowLocal, session.ActiveLanguage) { XamlRoot = XamlRoot };
        SystemAppearance.PrepareDialog(dialog);
        ContentDialogResult result;
        try { result = await dialog.ShowAsync(); }
        finally { historyDialogOpen = false; }
        if (result != ContentDialogResult.Primary || dialog.Result is not { } next) return;
        await RunAsync(async ct =>
        {
            var connectionsChanged = new[] { ServiceKind.Ai, ServiceKind.Agents }.Any(kind =>
                session.Settings.For(kind).Location != next.For(kind).Location || session.Settings.For(kind).RemoteUrl != next.For(kind).RemoteUrl);
            await SettingsStore.SaveAsync(session.DataDirectory, next);
            session.Settings = next;
            if (next.Language != session.ActiveLanguage) Show(DesktopResources.Get("RestartRequired"), InfoBarSeverity.Informational);
            // Language and composer preferences do not change the runtime, account, history partition, or current draft.
            if (!connectionsChanged) return;
            await session.Runtime.DisposeAsync();
            current = new() { Service = Service }; targets = []; target.Text = "";
            input.Text = ""; ResetContext();
            InvalidateCatalogs();
            RenderTranscript(); await LoadHistoryAsync(ct); await DiscoverAsync(ct);
            if (activePage != DesktopPage.Chat) await LoadActiveOverviewAsync(ct);
        });
    }

    private async Task RunAsync(Func<CancellationToken, Task> action, bool inference = false)
    {
        if (busy || closing) return;
        busy = true; operation = new CancellationTokenSource();
        if (!inference) operation.CancelAfter(TimeSpan.FromMinutes(2));
        notice.Visibility = Visibility.Collapsed;
        SetBusy(true, inference);
        try { await action(operation.Token); }
        catch (OperationCanceledException) { Show(DesktopResources.Get("OperationCanceled"), InfoBarSeverity.Warning); }
        catch (Exception e)
        {
            Show(e is GatewayException or InvalidOperationException ? e.Message : DesktopResources.Get("OperationFailed"), InfoBarSeverity.Error);
        }
        finally { operation.Dispose(); operation = null; busy = false; SetBusy(false, false); }
    }

    private void SetBusy(bool value, bool inference)
    {
        ResetFileDrop();
        input.IsReadOnly = value && inference;
        models.IsEnabled = agents.IsEnabled = target.IsEnabled = refresh.IsEnabled = account.IsEnabled = settingsButton.IsEnabled = manageAccount.IsEnabled = newChat.IsEnabled = searchChats.IsEnabled = chats.IsEnabled = send.IsEnabled = !value;
        addContext.IsEnabled = !value;
        chatSettings.IsEnabled = !value;
        manageMcp.IsEnabled = !value; RenderMcpTags();
        RenderContextTags();
        SetOverviewBusy(value);
        progress.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        stop.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        send.Visibility = inference ? Visibility.Collapsed : Visibility.Visible;
        if (!value) ControlAppearance.RefreshItems(chats);
    }

    private void Show(string message, InfoBarSeverity severity)
    {
        var label = severity switch
        {
            InfoBarSeverity.Error => DesktopResources.Get("Error"),
            InfoBarSeverity.Warning => DesktopResources.Get("Warning"),
            InfoBarSeverity.Success => DesktopResources.Get("Success"),
            _ => DesktopResources.Get("Information")
        };
        noticeMessage.Text = $"{label}: {message}";
        notice.Visibility = Visibility.Visible;
    }

    public async Task ShutdownAsync()
    {
        closing = true; operation?.Cancel(); downloadLifetime.Cancel();
        ResetFileDrop();
        catalogDialogLoad?.Cancel(); catalogDialog?.Hide();
        searchDialog?.Hide(); linkDialog?.Hide();
        resourcesDialog?.Hide();
        systemContextDialog?.Hide();
        mcpDialog?.Shutdown(); Mcp.Changed -= McpChanged;
        if (chatSettingsDialog is not null) { chatSettingsDialog.DiscardOnShutdown = true; chatSettingsDialog.Hide(); }
        while (busy) await Task.Delay(20);
        // A save picker may remain open until dismissed; no download continues after shutdown.
        await session.DisposeAsync(); http.Dispose(); contextHttp.Dispose(); mcpCatalogHttp.Dispose();
    }
}
