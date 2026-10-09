using System.Reflection;
using AIHappey.Desktop.Core;
using AIHappey.Vercel.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace AIHappey.Desktop.UiTests;

/// <summary>Exercises the real shared WinUI shell on its UI thread, without credentials or network calls.</summary>
public partial class App : Application
{
    private readonly List<string> results = [];
    private Window? window;
    private readonly string report = Environment.GetCommandLineArgs().Skip(1).First();

    public App()
    {
        AppContext.SetSwitch("AIHappey.Desktop.DisableRemoteImages", true);
        Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = "en";
        InitializeComponent();
        UnhandledException += (_, error) => File.WriteAllLines(report, results.Append("UNHANDLED: " + error.Exception));
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            File.WriteAllText(report, "Starting native UI checks.\n");
            // Native brushes stay valid: toolkit and transcript consume WinUI resources.
            Resources["SubtleButtonStyle"] = "deliberately not a Style";
            window = new Window { Title = "AIHappey native UI regression checks" };
            if (Environment.GetCommandLineArgs().Contains("--images-only"))
            {
                foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark }) await CheckImagesAsync(theme);
                results.Add("All images UI checks passed."); File.WriteAllLines(report, results); window.Close(); Exit(); return;
            }
            Check(DesktopBranding.ResolveName(null) == "aihappey" && DesktopBranding.ResolveName(" ") == "aihappey" && DesktopBranding.ResolveName(" chathappey ") == "chathappey", "build branding fallback and custom name");
            if (Environment.GetCommandLineArgs().Contains("--skills-only"))
            {
                foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark }) await CheckSkillsAsync(theme);
                results.Add("All skills UI checks passed.");
                File.WriteAllLines(report, results); window.Close(); Exit(); return;
            }
            if (Environment.GetCommandLineArgs().Contains("--ai-models-only"))
            {
                foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark }) await CheckAiModelsAsync(theme);
                results.Add("All AI model settings UI checks passed.");
                File.WriteAllLines(report, results); window.Close(); Exit(); return;
            }
            if (Environment.GetCommandLineArgs().Contains("--elicitation-only"))
            {
                foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark }) await CheckElicitationAsync(theme);
                results.Add("All Model context and elicitation UI checks passed.");
                File.WriteAllLines(report, results); window.Close(); Exit(); return;
            }
            if (Environment.GetCommandLineArgs().Contains("--mcp-presentation-only"))
            {
                foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark }) await CheckMcpPresentationAsync(theme);
                results.Add("All MCP presentation UI checks passed.");
                File.WriteAllLines(report, results); window.Close(); Exit(); return;
            }
            foreach (var local in new[] { true, false })
                foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
                {
                    await CheckTranscriptAsync(local, theme);
                    if (!Environment.GetCommandLineArgs().Contains("--transcript-only")) await CheckShellAsync(local, theme);
                }
            if (!Environment.GetCommandLineArgs().Contains("--transcript-only"))
                foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
                { await CheckMcpPresentationAsync(theme); await CheckElicitationAsync(theme); await CheckAiModelsAsync(theme); await CheckSkillsAsync(theme); await CheckImagesAsync(theme); }
            results.Add("All native UI checks passed.");
            File.WriteAllLines(report, results);
            window.Close();
            Exit();
        }
        catch (Exception error)
        {
            File.WriteAllLines(report, results.Append("FAILED: " + error));
            Environment.Exit(1);
        }
    }

    private async Task CheckTranscriptAsync(bool local, ElementTheme theme)
    {
        var session = new DesktopSession(new UiHost(local), new UiRuntime(), local ? new DesktopSettings() : new DesktopSettings
        {
            Ai = new() { Location = RuntimeLocation.Remote, RemoteUrl = "https://ui-test.invalid/ai/" },
            Agents = new() { Location = RuntimeLocation.Remote, RemoteUrl = "https://ui-test.invalid/agents/" }
        });
        var shell = new ChatShell(session) { RequestedTheme = theme };
        var context = $"Transcript / {(local ? "HeaderAuth" : "AzureAuth")} / {theme}";
        try
        {
            window!.Content = shell; window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 900)); window.Activate();
            await Task.Delay(200);
            var timestamp = DateTimeOffset.UtcNow.AddMinutes(-2);
            var answer = new TextUIPart { Text = "# Answer\n\n**Important** and *emphasis* with `inline code`.\n\n- First item\n- Second item\n\n> Quote\n\n[Web link](https://example.com/)\n\n| Name | Value |\n| --- | --- |\n| Example | 42 |\n\n```csharp\nConsole.WriteLine(42);\n```\n\n![Untrusted image](https://ui-test.invalid/image.png)" };
            var conversation = new Conversation
            {
                Target = "test-agent", Service = ServiceKind.Agents, Messages =
                [
                    new() { Timestamp = timestamp, Message = new UIMessage { Id = "user", Role = Role.user, Parts = [new TextUIPart { Text = "Please check." }] } },
                    new() { Timestamp = timestamp, Status = "complete", Message = new UIMessage { Id = "mixed", Role = Role.assistant, Parts =
                    [
                        new ReasoningUIPart { Text = "## Thinking\n\n**Checking** the tools." },
                        PortableConversations.Part(System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>("""{"type":"tool-search","toolCallId":"call","state":"output-available","input":{"query":"models"},"output":{}}""")),
                        answer
                    ] } }
                ]
            };
            var open = typeof(ChatShell).GetMethod("OpenConversationAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            for (var repeat = 0; repeat < 3; repeat++)
            {
                await (Task)open.Invoke(shell, new object[] { new Conversation { Target = "test-agent", Service = ServiceKind.Agents } })!;
                await (Task)open.Invoke(shell, new object[] { conversation })!;
                await Task.Delay(150); shell.UpdateLayout();
                var transcript = Field<StackPanel>(shell, "transcript");
                var activity = Descendants(transcript).OfType<Border>().Single(border => border.Name == "ActivityCard");
                Check(activity.Child is Grid layout && layout.Children.OfType<StackPanel>().Count() == 1, context + $": open/reopen {repeat + 1}, activity has one visual parent");
            }
            var body = Field<StackPanel>(shell, "transcript");
            Check(Descendants(body).OfType<ChatMarkdown>().Any(markdown => markdown.Name == "MessageMarkdown" && markdown.Text == answer.Text)
                && RenderedRuns(body).Any(run => run.Text == "Important"), context + ": text renders Markdown with original copy content retained");
            Check(Descendants(body).OfType<CommunityToolkit.WinUI.Controls.MarkdownTextBlock>().All(markdown => markdown.UsePipeTables && markdown.DisableHtml && markdown.IsTextSelectionEnabled), context + ": toolkit tables, disabled HTML and selectable text");
            Check(Descendants(body).OfType<Image>().All(image => image.Source is null), context + ": Markdown images do not fetch untrusted content");
            Check(Descendants(body).OfType<TextBlock>().Where(text => text.Name == "MessageTime").All(text => text.Text == "2 minutes ago"), context + ": localized relative timestamps");
            Check(Descendants(body).OfType<Border>().Count(border => border.Name == "AiGeneratedBadge") == 2, context + ": assistant activity and answer disclose AI generation");
            Check(Descendants(body).OfType<IconElement>().Any(icon => icon.Name == "ToolActivityIcon"), context + ": tool header icon");
            InvokeButton(Descendants(body).OfType<Button>().Single(button => AutomationProperties.GetName(button) == "Previous activity"));
            await Task.Delay(150);
            Check(Descendants(body).OfType<IconElement>().Any(icon => icon.Name == "ReasoningActivityIcon") && RenderedRuns(body).Any(run => run.Text == "Checking"), context + ": brain icon and reasoning Markdown after navigation");
            conversation.Messages[1].Message.Parts[2] = new TextUIPart { Text = answer.Text + "\n\n**Partial streaming" };
            Call(shell, "RenderTranscript"); await Task.Delay(100);
            Check(RenderedRuns(body).Any(run => run.Text.Contains("Partial streaming")), context + ": incomplete streamed Markdown renders without crashing");
            foreach (var width in new[] { 720, 1280 })
            {
                window.AppWindow.Resize(new Windows.Graphics.SizeInt32(width, 900)); await Task.Delay(100); shell.UpdateLayout();
                Check(Field<ScrollViewer>(shell, "scroll").ScrollableWidth < 1, context + $": no transcript horizontal overflow at {width}px");
            }
            shell.RequestedTheme = theme == ElementTheme.Light ? ElementTheme.Dark : ElementTheme.Light; await Task.Delay(150);
            Check(RenderedRuns(body).Any(run => run.Text == "Important"), context + ": Markdown survives native runtime theme switch");
            File.WriteAllLines(report, results);
        }
        finally { await shell.ShutdownAsync(); window!.Content = null; }
    }

    private static IEnumerable<Run> RenderedRuns(DependencyObject root)
        => Descendants(root).OfType<RichTextBlock>().SelectMany(text => text.Blocks.OfType<Paragraph>())
            .SelectMany(paragraph => paragraph.Inlines).SelectMany(InlineRuns);

    private static IEnumerable<Run> InlineRuns(Inline inline)
    {
        if (inline is Run run) yield return run;
        if (inline is Span span)
            foreach (var child in span.Inlines)
                foreach (var nested in InlineRuns(child)) yield return nested;
    }

    private async Task CheckShellAsync(bool local, ElementTheme theme)
    {
        var host = new UiHost(local);
        var runtime = new UiRuntime();
        var settings = local ? new DesktopSettings() : new DesktopSettings
        {
            Ai = new() { Location = RuntimeLocation.Remote, RemoteUrl = "https://ui-test.invalid/ai/" },
            Agents = new() { Location = RuntimeLocation.Remote, RemoteUrl = "https://ui-test.invalid/agents/" }
        };
        var session = new DesktopSession(host, runtime, settings);
        var shell = new ChatShell(session) { RequestedTheme = theme };
        try
        {
            window!.Content = shell;
            window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 900));
            window.Activate();
            await Task.Delay(150);
            Field<SplitView>(shell, "split").IsPaneOpen = true;
            await Task.Delay(100);
            shell.UpdateLayout();
            var context = $"{(local ? "HeaderAuth" : "AzureAuth")} / {theme}";
            Check(shell.IsLoaded && runtime.Resolutions > 0, context + ": startup and native layout");
            Check(shell.ActualTheme == theme, context + ": shell theme follows requested Windows app theme");
            var pane = (Grid)Field<SplitView>(shell, "split").Pane;
            var header = (Grid)((StackPanel)pane.Children[0]).Children[0];
            var appTitle = (TextBlock)header.Children[0];
            var toggle = (Button)header.Children[1];
            var titlePoint = appTitle.TransformToVisual(header).TransformPoint(new Windows.Foundation.Point());
            var togglePoint = toggle.TransformToVisual(header).TransformPoint(new Windows.Foundation.Point());
            Check(appTitle.Text == DesktopBranding.AppName && titlePoint.X < togglePoint.X && Math.Abs(titlePoint.Y + appTitle.ActualHeight / 2 - togglePoint.Y - toggle.ActualHeight / 2) < 2, context + ": branded title and right-aligned toggle share a row");
            var split = Field<SplitView>(shell, "split");
            split.IsPaneOpen = false;
            await Task.Delay(250);
            shell.UpdateLayout();
            var compactPoint = toggle.TransformToVisual(shell).TransformPoint(new Windows.Foundation.Point());
            Check(toggle.Visibility == Visibility.Visible && toggle.IsHitTestVisible && toggle.ActualWidth > 0 && compactPoint.X >= 0 && compactPoint.X + toggle.ActualWidth <= split.CompactPaneLength + 1,
                context + $": compact toggle remains visible inside pane ({compactPoint.X}+{toggle.ActualWidth})");
            ((IInvokeProvider)new ButtonAutomationPeer(toggle).GetPattern(PatternInterface.Invoke)).Invoke();
            await Task.Delay(250);
            Check(split.IsPaneOpen && appTitle.Visibility == Visibility.Visible, context + ": compact toggle reopens sidebar");
            var newChat = Field<Button>(shell, "newChat");
            Check(newChat.Content is StackPanel newChatContent && newChatContent.Children.OfType<SymbolIcon>().Single().Symbol == Symbol.Add
                && newChatContent.Children.OfType<TextBlock>().Single().Text == "New chat", context + ": new chat has native plus icon and label");
            Check(newChat.HorizontalContentAlignment == HorizontalAlignment.Left && newChat.Background is SolidColorBrush newChatFill && newChatFill.Color.A == 0
                && newChat.BorderThickness == new Thickness(0) && newChat.Style is null, context + ": new chat is left-aligned and unfilled without optional styles");
            var sidebarBody = (StackPanel)((StackPanel)pane.Children[0]).Children[1];
            Check(sidebarBody.Children.Count == 2 && sidebarBody.Children[0] == newChat && sidebarBody.Children[1] == Field<Button>(shell, "searchChats"), context + ": top section has New chat and modal Search chats actions only");
            var navigation = Field<StackPanel>(shell, "pageNavigation");
            Check(navigation.Children.OfType<TextBlock>().Select(text => text.Text).SequenceEqual(new[] { "Agents", "Chats" })
                && navigation.Children.OfType<Border>().Select(border => border.Name).SequenceEqual(new[] { "AgentsSeparator", "ArtificialIntelligenceSeparator", "ChatsSeparator" })
                && navigation.Children.OfType<ToggleButton>().Select(button => button.Name).SequenceEqual(new[] { "NavigateImages", "NavigateTranscriptions", "NavigateAgents", "NavigateMcp", "NavigateSkills" })
                && !Descendants(pane).OfType<TextBox>().Any(), context + ": browser-style categories and no extra Chat button or inline history search");
            Check(navigation.Children.OfType<ToggleButton>().Skip(2).Select(button => AutomationProperties.GetName(button))
                .SequenceEqual(new[] { "Agents", DesktopResources.Get("McpTitle"), "Skills" }), context + ": Agents → More context → Skills navigation order");
            foreach (var state in new[] { "Normal", "PointerOver", "Pressed" })
            {
                VisualStateManager.GoToState(newChat, state, false);
                var rendered = Descendants(newChat).OfType<ContentPresenter>().First(part => part.Name == "ContentPresenter");
                Check(rendered.BorderThickness == new Thickness(0) && Readable(rendered.Foreground, rendered.Background, theme), context + ": native new chat feedback in " + state);
            }
            var disclaimer = Field<TextBlock>(shell, "disclaimer");
            var disclaimerPoint = disclaimer.TransformToVisual(shell).TransformPoint(new Windows.Foundation.Point());
            Check(disclaimerPoint.Y + disclaimer.ActualHeight >= shell.ActualHeight - 12 && Grid.GetRow(disclaimer) == 3, context + ": welcome disclaimer stays in bottom footer");
            foreach (var control in new Control[] { shell, Field<TextBox>(shell, "input"), Field<Button>(shell, "searchChats"), Field<Button>(shell, "send"), Field<Button>(shell, "account") })
                Check(Readable(control.Foreground, control.Background, theme), context + ": readable " + control.GetType().Name);
            var selectorText = Descendants(Field<AutoSuggestBox>(shell, "target")).OfType<TextBox>().First();
            var targetCatalog = Field<AutoSuggestBox>(shell, "target");
            targetCatalog.ItemsSource = new[] { new ChatTarget("test-model", "Test model") };
            targetCatalog.IsSuggestionListOpen = true;
            await Task.Delay(100);
            var suggestions = VisualTreeHelper.GetOpenPopupsForXamlRoot(shell.XamlRoot).SelectMany(popup => Descendants(popup.Child)).OfType<ListView>().First(part => part.Name == "SuggestionsList");
            Check(Readable(suggestions.Foreground, suggestions.Background, theme), context + ": readable catalog dropdown popup");
            targetCatalog.IsSuggestionListOpen = false;
            targetCatalog.Text = "test-model";
            await Task.Delay(50);
            var clearButton = Descendants(selectorText).OfType<Button>().First(part => part.Name == "DeleteButton");
            foreach (var state in new[] { "Normal", "PointerOver", "Pressed" })
            {
                VisualStateManager.GoToState(clearButton, state, false);
                var rendered = Descendants(clearButton).OfType<ContentPresenter>().FirstOrDefault();
                Check(Readable(clearButton.Foreground, rendered?.Background ?? clearButton.Background, theme), context + $": rendered clear button in {state}");
            }
            foreach (var text in new[] { selectorText, Field<TextBox>(shell, "input") })
                foreach (var state in new[] { "Normal", "PointerOver", "Focused" })
                {
                    VisualStateManager.GoToState(text, state, false);
                    await Task.Delay(40);
                    var border = Descendants(text).OfType<Border>().First(part => part.Name == "BorderElement");
                    Check(Readable(text.Foreground, border.Background, theme), context + $": rendered {text.PlaceholderText} surface in {state}");
                }
            var models = Field<ToggleButton>(shell, "models");
            var agents = Field<ToggleButton>(shell, "agents");
            Check(models.IsChecked == true && agents.IsChecked == false, context + ": initial mode");
            Check(Readable(((IconElement)models.Content).Foreground, models.Background, theme), context + ": checked brain icon is readable");
            var tooltip = (ToolTip)ToolTipService.GetToolTip(models);
            tooltip.PlacementTarget = models;
            tooltip.IsOpen = true;
            await Task.Delay(80);
            Check(Readable(tooltip.Foreground, tooltip.Background, theme) && Descendants(tooltip).OfType<TextBlock>().All(text => Readable(text.Foreground, tooltip.Background, theme)), context + ": readable tooltip and rendered label");
            tooltip.IsOpen = false;
            agents.IsChecked = true;
            Check(models.IsChecked == false && agents.IsChecked == true, context + ": switch to agents");
            agents.IsChecked = false;
            Check(agents.IsChecked == true, context + ": selected mode cannot be deselected");
            models.IsChecked = true;
            Check(models.IsChecked == true && agents.IsChecked == false, context + ": switch to models");

            var profile = Field<Button>(shell, "account");
            var flyout = (MenuFlyout)profile.Flyout;
            Check(profile.Content is SymbolIcon && profile.CornerRadius.TopLeft == 20, context + ": generic circular profile icon");
            Check(flyout.Items.OfType<MenuFlyoutItem>().Select(item => item.Text).SequenceEqual(new[] { "Settings", local ? "API keys" : "Sign in", "Refresh" }), context + ": profile menu labels");
            flyout.ShowAt(profile);
            await Task.Delay(100);
            foreach (var item in flyout.Items.OfType<MenuFlyoutItem>())
            {
                Check(Readable(item.Foreground, item.Background, theme), context + ": readable profile item " + item.Text);
                Check(item.BorderThickness.Left == 0 && item.BorderThickness.Top == 0, context + ": no inner profile border for " + item.Text);
                foreach (var state in new[] { "Normal", "PointerOver" })
                {
                    VisualStateManager.GoToState(item, state, false);
                    var presenter = Descendants(item).OfType<ContentPresenter>().FirstOrDefault(part => part.Name == "ContentPresenter");
                    if (presenter is not null) Check(presenter.BorderThickness.Left == 0 && presenter.BorderThickness.Top == 0, context + $": borderless rendered profile {state}");
                    Check(Descendants(item).OfType<TextBlock>().All(text => Readable(text.Foreground, item.Background, theme)), context + $": readable rendered profile label in {state}");
                }
            }
            Invoke(flyout.Items.OfType<MenuFlyoutItem>().ElementAt(1));
            await Task.Delay(100);
            Check(host.AccountActions == 1 && profile.Content is SymbolIcon, context + ": profile action preserves icon");
            flyout.Hide();
            var resolutions = runtime.Resolutions;
            flyout.ShowAt(profile);
            await Task.Delay(50);
            Invoke(Field<MenuFlyoutItem>(shell, "refresh"));
            await Task.Delay(50);
            Check(runtime.Resolutions > resolutions, context + ": refresh action");
            flyout.Hide();

            var settingsTask = (Task)Call(shell, "EditSettingsAsync")!;
            await Task.Delay(100);
            var dialog = VisualTreeHelper.GetOpenPopupsForXamlRoot(shell.XamlRoot)
                .SelectMany(popup => Descendants(popup.Child)).OfType<ContentDialog>().FirstOrDefault();
            if (settingsTask.IsFaulted) await settingsTask;
            results.Add("Settings resource diagnostic: title=" + dialog?.Title + "; task=" + settingsTask.Status);
            Check(dialog?.Title?.ToString() == "Settings", context + ": settings dialog native resource resolution");
            dialog!.Hide();
            await settingsTask;

            var conversation = new Conversation
            {
                Service = ServiceKind.Agents, Target = "test-agent", Messages =
                [
                    new() { Message = new UIMessage { Id = "user", Role = Role.user, Parts = [new TextUIPart { Text = string.Join(" ", Enumerable.Repeat("A longer user message to verify wrapping and resize alignment.", 6)) }] } },
                    new() { Message = new UIMessage { Id = "assistant", Role = Role.assistant, Parts = [new TextUIPart { Text = "UI smoke output" }], Metadata = new Dictionary<string, object> { ["model"] = "test-agent", ["usage"] = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>("{\"totalTokens\":17}") } } }
                ]
            };
            // Reopening history must select the right mode without starting a fresh conversation.
            Field<ListView>(shell, "chats").ItemsSource = new[] { conversation };
            Field<ListView>(shell, "chats").SelectedItem = conversation;
            await Task.Delay(100);
            shell.UpdateLayout();
            Check(agents.IsChecked == true && models.IsChecked == false, context + ": reopened agent chat mode");
            var chatList = Field<ListView>(shell, "chats");
            Check(Readable(chatList.Foreground, chatList.Background, theme), context + ": chat list surface");
            var chatRow = (ListViewItem)chatList.ContainerFromIndex(0);
            Check(chatRow.Padding == new Thickness(12, 4, 12, 4) && chatRow.ActualHeight >= 40 && chatRow.ActualHeight <= 42
                && chatRow.UseSystemFocusVisuals && chatList.ItemsPanelRoot is ItemsStackPanel,
                context + $": compact native chat row retains system focus and virtualization ({chatRow.ActualHeight}px)");
            Check(Descendants(chatRow).OfType<Button>().Single(button => button.Name == "ConversationActions").ActualHeight == 32
                && !string.IsNullOrEmpty(AutomationProperties.GetName(chatRow)), context + ": compact row retains accessible 32px actions and chat name");
            chatRow.Focus(FocusState.Keyboard); await Task.Delay(50);
            Check(Descendants(chatRow).OfType<Button>().Single(button => button.Name == "ConversationActions").Opacity == 1,
                context + ": keyboard focus still reveals compact chat actions");
            foreach (var presenter in Descendants(chatList).OfType<ListViewItemPresenter>())
            {
                Check(Readable(chatList.Foreground, presenter.SelectedBackground, theme) && Readable(chatList.Foreground, presenter.PointerOverBackground, theme), context + ": rendered selected/hover chat item");
                Check(presenter.BorderThickness.Left == 0 && presenter.BorderThickness.Top == 0, context + ": no rendered chat item border");
                Check(presenter.SelectedBackground is SolidColorBrush selected && presenter.Background is SolidColorBrush idle && selected.Color != idle.Color, context + ": selected conversation retains background highlight");
            }
            var transcript = Field<StackPanel>(shell, "transcript");
            var copies = Descendants(transcript).OfType<Button>().Where(button => AutomationProperties.GetName(button) == "Copy message").ToArray();
            Check(copies.Length == 2 && copies.All(button => button.Content is SymbolIcon && button.Style is null), context + ": transcript and icon-only copy without named styles");
            Check(copies.All(button => button.Background is SolidColorBrush brush && brush.Color.A == 0), context + ": copy has no idle gray fill");
            Check(Field<Button>(shell, "send").Content is SymbolIcon, context + ": icon-only send");
            Check(!Descendants(shell).OfType<TextBlock>().Any(text => text.Text.Contains("Enter to send")), context + ": keyboard hint removed");
            Check(AutomationProperties.GetHelpText(Field<TextBox>(shell, "input")).Contains("Shift+Enter"), context + ": accessible keyboard help retained");
            var assistant = (Border)((Grid)transcript.Children[1]).Children[0];
            var footer = Descendants(assistant).OfType<Border>().Single(part => part.Name == "MessageFooter");
            var badge = Descendants(footer).OfType<Border>().Single(part => part.Name == "TokenUsage");
            Check(((StackPanel)badge.Child).Children.OfType<TextBlock>().Single().Text == "17" && Descendants(badge).OfType<FontIcon>().Any(), context + ": tokens are icon plus number only");
            Check(Descendants(footer).OfType<Button>().Any(button => AutomationProperties.GetName(button) == "Copy message"), context + ": copy and tokens share footer");
            var tokenCopy = Descendants(footer).OfType<Button>().Single(button => AutomationProperties.GetName(button) == "Copy message");
            Check(Math.Abs(badge.TransformToVisual(footer).TransformPoint(new Windows.Foundation.Point()).Y + badge.ActualHeight / 2
                - tokenCopy.TransformToVisual(footer).TransformPoint(new Windows.Foundation.Point()).Y - tokenCopy.ActualHeight / 2) < 1,
                context + ": token badge vertically centered alongside copy icon");
            Check(!Descendants(assistant).OfType<TextBlock>().Any(text => text.Text.Contains("Tokens:")) && Descendants(assistant).OfType<TextBlock>().Count(text => text.Text.Contains("test-agent")) == 1, context + ": no token label or duplicate model name");
            Check(Descendants(assistant).OfType<Border>().Single(part => part.Name == "MessageHeader").BorderThickness.Bottom == 1 && footer.BorderThickness.Top == 1, context + ": header/body/footer separators");
            Check(Descendants(assistant).OfType<Border>().Any(part => part.Name == "AiGeneratedBadge")
                && !Descendants((Border)((Grid)transcript.Children[0]).Children[0]).OfType<Border>().Any(part => part.Name == "AiGeneratedBadge"), context + ": AI disclosure appears only on assistant messages");
            Check(Descendants(assistant).OfType<TextBlock>().Count(text => text.Name == "MessageTime") == 1
                && !Descendants(assistant).OfType<TextBlock>().Any(text => text.Text.Contains("complete", StringComparison.OrdinalIgnoreCase)), context + ": relative time without message status");

            foreach (var width in new[] { 720, 1280, 1920, 900 })
            {
                window.AppWindow.Resize(new Windows.Graphics.SizeInt32(width, 720));
                await Task.Delay(150);
                shell.UpdateLayout();
                var userCard = (Border)((Grid)transcript.Children[0]).Children[0];
                var assistantCard = (Border)((Grid)transcript.Children[1]).Children[0];
                var userPoint = userCard.TransformToVisual(transcript).TransformPoint(new Windows.Foundation.Point());
                var assistantPoint = assistantCard.TransformToVisual(transcript).TransformPoint(new Windows.Foundation.Point());
                var viewer = Field<ScrollViewer>(shell, "scroll");
                Check(transcript.ActualWidth <= Math.Min(1040, viewer.ViewportWidth - 48) + 1, context + $": transcript bounded at {width}px");
                Check(Math.Abs(userPoint.X + userCard.ActualWidth - transcript.ActualWidth) < 2 && Math.Abs(assistantPoint.X) < 2,
                    context + $": right/left message anchors at {width}px (user {userPoint.X}+{userCard.ActualWidth}, transcript {transcript.ActualWidth}, assistant {assistantPoint.X})");
                Check(userCard.ActualWidth <= transcript.ActualWidth * .8 + 1 && viewer.ScrollableWidth < 1, context + $": wrapped user card fits at {width}px");
            }

            // Exercise the content path that text-only smoke checks previously missed.
            var mixed = new ConversationMessage { Message = new UIMessage { Id = "mixed", Role = Role.assistant }, Status = "streaming" };
            var assembler = new MessageAssembler(mixed);
            foreach (var payload in new[]
            {
                "{\"type\":\"reasoning-start\",\"id\":\"r\"}",
                "{\"type\":\"reasoning-delta\",\"id\":\"r\",\"delta\":\"Checking the available tools.\"}",
                "{\"type\":\"reasoning-end\",\"id\":\"r\"}",
                "{\"type\":\"tool-input-start\",\"toolCallId\":\"call\",\"toolName\":\"search\"}",
                "{\"type\":\"tool-input-available\",\"toolCallId\":\"call\",\"toolName\":\"search\",\"input\":{\"query\":\"models\"}}",
                "{\"type\":\"tool-output-available\",\"toolCallId\":\"call\",\"output\":{\"items\":[\"test-model\"]}}",
                "{\"type\":\"text-delta\",\"id\":\"answer\",\"delta\":\"The tool finished.\"}",
                "{\"type\":\"finish\"}"
            }) assembler.Apply(StreamEvent.Parse(payload));
            results.Add(context + ": starting mixed reasoning/tool native rendering");
            File.WriteAllLines(report, results);
            conversation.Messages[1] = mixed;
            Call(shell, "RenderTranscript");
            await Task.Delay(150);
            shell.UpdateLayout();
            Check(RenderedRuns(transcript).Any(run => run.Text == "The tool finished."), context + ": mixed reasoning/tool transcript renders without native failure");
            var activityCard = Descendants(transcript).OfType<Border>().Single(border => border.Name == "ActivityCard");
            Check(activityCard.Child is Grid activityLayout && activityLayout.Children.OfType<StackPanel>().Count() == 1
                && activityLayout.Children.OfType<Border>().Single().Name == "ActivityAccent", context + ": activity sections attach to exactly one parent alongside native accent");
            Check(Descendants(activityCard).OfType<IconElement>().Any(icon => icon.Name == "ToolActivityIcon"), context + ": latest tool activity has tool header icon");
            var counter = Descendants(activityCard).OfType<TextBlock>().Single(text => text.Name == "ActivityCount");
            var previousActivity = Descendants(activityCard).OfType<Button>().Single(button => AutomationProperties.GetName(button) == "Previous activity");
            Check(Math.Abs(counter.TransformToVisual(activityCard).TransformPoint(new Windows.Foundation.Point()).Y + counter.ActualHeight / 2
                - previousActivity.TransformToVisual(activityCard).TransformPoint(new Windows.Foundation.Point()).Y - previousActivity.ActualHeight / 2) < 1,
                context + ": activity counter vertically centered alongside navigation icons");
            Check(counter.Text == "2/2" && !Descendants(activityCard).OfType<TextBlock>().Any(text => text.Text == "Output" || text.Text.Contains("test-model")), context + ": latest activity shows input only, not tool output");
            InvokeButton(previousActivity);
            await Task.Delay(50);
            Check(RenderedRuns(transcript).Any(run => run.Text == "Checking the available tools."), context + ": previous activity shows reasoning");
            Check(Descendants(transcript).OfType<IconElement>().Any(icon => icon.Name == "ReasoningActivityIcon")
                && !Descendants(transcript).OfType<IconElement>().Any(icon => icon.Name == "ToolActivityIcon"), context + ": activity navigation switches header icon to brain");
            var activityList = Descendants(transcript).OfType<Button>().Single(button => AutomationProperties.GetName(button) == "Show activity list");
            InvokeButton(activityList);
            await Task.Delay(100);
            var details = Field<SplitView>(shell, "details");
            var detailsBody = Field<StackPanel>(shell, "detailsBody");
            Check(details.IsPaneOpen && details.PanePlacement == SplitViewPanePlacement.Right && Descendants(detailsBody).OfType<Button>().Count(button => button.Name == "ActivityListItem") == 2,
                context + ": grouped activity list opens native right-hand panel instead of dialog");
            InvokeButton(Descendants(detailsBody).OfType<Button>().Last(button => button.Name == "ActivityListItem"));
            await Task.Delay(50);
            Check(Descendants(transcript).OfType<TextBlock>().Single(text => text.Name == "ActivityCount").Text == "2/2", context + ": native activity panel selects transcript page");
            details.IsPaneOpen = false;
            await Task.Delay(150);

            // Exercise the exact conversation-opening path from the double-parent crash.
            var openConversation = typeof(ChatShell).GetMethod("OpenConversationAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            await (Task)openConversation.Invoke(shell, new object[] { new Conversation { Target = "test-agent", Service = ServiceKind.Agents } })!;
            await (Task)openConversation.Invoke(shell, new object[] { conversation })!;
            await Task.Delay(150);
            Check(Descendants(transcript).OfType<Border>().Count(border => border.Name == "ActivityCard") == 1
                && RenderedRuns(transcript).Any(run => run.Text == "The tool finished."), context + ": reopening reasoning/tool conversation avoids double-parent native crash");

            for (var source = 0; source < 6; source++) mixed.Message.Parts.Add(new SourceUIPart { SourceId = "source" + source, Title = "Source " + source, Url = $"https://domain{source}.example/article" });
            mixed.Message.Parts.Add(new FileUIPart { Filename = "report.txt", MediaType = "text/plain", Url = "data:text/plain;base64,aGVsbG8=" });
            mixed.Message.Parts.Add(PortableConversations.Part(System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>("""
                {"type":"tool-export","toolCallId":"export","state":"output-available","input":{},"output":{"content":[{"type":"resource","resource":{"uri":"mcp://reports/report.pdf","mimeType":"application/pdf","blob":"aGVsbG8="}}],"structuredContent":{"keep":true}}}
                """)));
            Call(shell, "RenderTranscript");
            await Task.Delay(100);
            shell.UpdateLayout();
            Check(transcript.Children.Count == 4 && Descendants(transcript).OfType<Button>().Count(button => button.Name == "SourcesButton") == 1
                && Descendants(transcript).OfType<Button>().Count(button => button.Name == "AttachmentsButton") == 1, context + ": sources/files leave raw stream and belong to one answer footer");
            Check(Descendants(transcript).OfType<Button>().Count(button => button.Name == "SourceFavicon") == 5 && Descendants(transcript).OfType<Button>().Any(button => button.Name == "SourcesOverflow"),
                context + ": source footer has five domain shortcuts and accessible overflow");
            var sourcesButton = Descendants(transcript).OfType<Button>().Single(button => button.Name == "SourcesButton");
            InvokeButton(sourcesButton);
            await Task.Delay(100);
            Check(details.IsPaneOpen && Field<TextBlock>(shell, "detailsTitle").Text == "Sources" && Descendants(detailsBody).OfType<TextBlock>().Count(text => text.Text.StartsWith("https://domain")) == 6,
                context + ": all source URLs are accessible in native panel");
            Check(!VisualTreeHelper.GetOpenPopupsForXamlRoot(shell.XamlRoot).SelectMany(popup => Descendants(popup.Child)).OfType<ContentDialog>().Any(), context + ": detail views do not create dialogs");
            var attachmentsButton = Descendants(transcript).OfType<Button>().Single(button => button.Name == "AttachmentsButton");
            Call(shell, "OpenDetails", "attachments", MessageDetails.Project(conversation.Messages).Single(row => row.Attachments.Count > 0).Block.Key, attachmentsButton, (object)null!);
            await Task.Delay(100);
            Check(Field<TextBlock>(shell, "detailsTitle").Text == "Attachments" && Descendants(detailsBody).OfType<Button>().Count(button => button.Name == "DownloadAttachment" && button.IsEnabled) == 2,
                context + ": direct and MCP embedded attachments have native-panel download actions");
            window.AppWindow.Resize(new Windows.Graphics.SizeInt32(600, 720));
            await Task.Delay(100);
            shell.UpdateLayout();
            Check(details.OpenPaneLength <= details.ActualWidth + 1 && Field<ScrollViewer>(shell, "scroll").ScrollableWidth < 1, context + ": right-hand panel and wrapped footer fit narrow viewport");
            InvokeButton(Descendants(details.Pane).OfType<Button>().Single(button => button.Name == "CloseDetails"));
            await Task.Delay(150);
            Check(!details.IsPaneOpen, context + ": native detail panel closes normally");
            window.AppWindow.Resize(new Windows.Graphics.SizeInt32(900, 720));
            await Task.Delay(100);

            Call(shell, "SetBusy", true, true);
            shell.UpdateLayout();
            Check(!models.IsEnabled && !agents.IsEnabled && !profile.IsEnabled && Field<Button>(shell, "stop").Visibility == Visibility.Visible, context + ": busy/stop state");
            Call(shell, "SetBusy", false, false);
            Check(models.IsEnabled && agents.IsEnabled && profile.IsEnabled, context + ": idle state restored");
            await Task.Delay(100);
            foreach (var presenter in Descendants(chatList).OfType<ListViewItemPresenter>())
                Check(presenter.BorderThickness.Left == 0 && presenter.BorderThickness.Top == 0, context + ": borderless chat after loading-to-idle");
            window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 900));
            split.IsPaneOpen = true;
            await Task.Delay(150);
            chatList.ItemsSource = new[] { conversation, new Conversation { Title = "Recycled chat" } };
            chatList.SelectedItem = conversation;
            await Task.Delay(100);
            shell.UpdateLayout();
            var checkedContainers = 0;
            for (var index = 0; index < chatList.Items.Count; index++)
            {
                if (chatList.ContainerFromIndex(index) is not ListViewItem item) continue;
                checkedContainers++;
                item.IsEnabled = false;
                item.IsEnabled = true;
                VisualStateManager.GoToState(item, "PointerOver", false);
                var presenter = Descendants(item).OfType<ListViewItemPresenter>().Single();
                Check(item.BorderThickness.Left == 0 && presenter.BorderThickness.Left == 0 && presenter.BorderThickness.Top == 0, context + ": recycled/re-enabled chat remains borderless");
            }
            Check(checkedContainers > 0, context + ": real recycled containers exercised");

            await CheckSidebarActionsAsync(shell, session, conversation, theme, context);
            await CheckCatalogAndSearchAsync(shell, theme, context);

            foreach (var (control, key) in new (Control, string)[]
            {
                (models, "ToggleButtonBackgroundChecked"), (agents, "ToggleButtonBackgroundChecked"),
                (newChat, "ButtonBackground"),
                (Field<AutoSuggestBox>(shell, "target"), "TextControlBorderBrush")
            }.Concat(copies.Select(copy => ((Control)copy, "ButtonBackground"))))
            {
                // Materialize our high-contrast dictionary without changing the user's Windows settings.
                var contrast = (ResourceDictionary)control.Resources.ThemeDictionaries["HighContrast"];
                Check(contrast.TryGetValue(key, out var value) && value is SolidColorBrush, context + ": concrete high-contrast brush for " + key);
                foreach (var name in new[] { "Default", "Light" })
                {
                    var forced = new ResourceDictionary();
                    foreach (var entry in contrast) forced[entry.Key] = entry.Value;
                    control.Resources.ThemeDictionaries[name] = forced;
                }
            }
            shell.RequestedTheme = theme == ElementTheme.Light ? ElementTheme.Dark : ElementTheme.Light;
            await Task.Delay(100);
            shell.UpdateLayout();
            Check(shell.IsLoaded, context + ": theme change and high-contrast palette materialization");
            window.AppWindow.Resize(new Windows.Graphics.SizeInt32(720, 600));
            await Task.Delay(100);
            shell.UpdateLayout();
            var target = Field<AutoSuggestBox>(shell, "target");
            var targetPoint = target.TransformToVisual(shell).TransformPoint(new Windows.Foundation.Point());
            var profilePoint = profile.TransformToVisual(shell).TransformPoint(new Windows.Foundation.Point());
            Check(target.ActualWidth >= 119 && targetPoint.X + target.ActualWidth <= profilePoint.X + 1 && profilePoint.X + profile.ActualWidth <= shell.ActualWidth + 1,
                context + $": narrow-window toolbar fits (window {shell.ActualWidth}, selector {targetPoint.X}+{target.ActualWidth}, profile {profilePoint.X}+{profile.ActualWidth})");
        }
        finally
        {
            await shell.ShutdownAsync();
            window!.Content = null;
            if (Directory.Exists(session.DataDirectory)) Directory.Delete(session.DataDirectory, true);
        }
    }

    private async Task CheckSidebarActionsAsync(ChatShell shell, DesktopSession session, Conversation active, ElementTheme theme, string context)
    {
        var history = Field<HistoryStore>(shell, "history");
        var other = new Conversation { Title = string.Join(" ", Enumerable.Repeat("A long conversation title", 12)) };
        await history.SaveAsync(session.HistoryPartition, active);
        await history.SaveAsync(session.HistoryPartition, other);
        await (Task)Call(shell, "LoadHistoryAsync", CancellationToken.None)!;
        await Task.Delay(100);
        shell.UpdateLayout();
        var chats = Field<ListView>(shell, "chats");
        var input = Field<TextBox>(shell, "input");
        input.Text = "Keep this draft";
        input.Focus(FocusState.Programmatic);
        Check(chats.SelectedItem is Conversation selected && selected.Id == active.Id && Field<Conversation>(shell, "current") == active,
            context + ": history reload retains selected chat by identity without replacing open transcript");
        var otherItem = (Conversation)chats.Items.Cast<Conversation>().Single(chat => chat.Id == other.Id);
        var container = (ListViewItem)chats.ContainerFromItem(otherItem);
        var row = ConversationRow(shell, container);
        var more = Descendants(container).OfType<Button>().Single(button => button.Name == "ConversationActions");
        var title = ((Grid)container.ContentTemplateRoot).Children.OfType<TextBlock>().Single();
        Check(more.Opacity == 0 && !more.IsHitTestVisible && more.Visibility == Visibility.Visible && more.IsTabStop,
            context + ": idle row actions hidden but keyboard accessible with reserved space");
        Check(title.Text == other.Title && title.TextTrimming == TextTrimming.CharacterEllipsis, context + ": conversation title trims instead of overlapping actions");
        var titleWidth = title.ActualWidth;
        RowCall(row, "SetHovered", true);
        Check(more.Opacity == 1 && more.IsHitTestVisible, context + ": row hover reveals native three-dot button");
        shell.UpdateLayout();
        var morePoint = more.TransformToVisual(container).TransformPoint(new Windows.Foundation.Point());
        var titlePoint = title.TransformToVisual(container).TransformPoint(new Windows.Foundation.Point());
        Check(Math.Abs(title.ActualWidth - titleWidth) < 1 && titlePoint.X + title.ActualWidth <= morePoint.X && morePoint.X + more.ActualWidth <= container.ActualWidth + 1,
            context + ": trailing actions fit without hover title reflow");
        RowCall(row, "SetHovered", false);
        container.Focus(FocusState.Keyboard);
        await Task.Delay(40);
        Check(more.Opacity == 1, context + ": keyboard row focus reveals actions");
        more.Focus(FocusState.Keyboard);
        await Task.Delay(40);
        Check(more.Opacity == 1, context + ": keyboard button focus retains actions");
        input.Focus(FocusState.Programmatic);
        await Task.Delay(40);
        Check(more.Opacity == 0, context + ": actions hide after hover and keyboard focus leave");

        var menu = (MenuFlyout)more.Flyout;
        Check(menu.Items.OfType<MenuFlyoutItem>().Select(item => item.Text).SequenceEqual(new[] { "Rename", "Delete" }) && menu.Items.Count == 2,
            context + ": native row menu contains only rename and delete");
        RowCall(row, "SetHovered", true);
        // Use the button's actual native Invoke pattern, not a custom click handler.
        ((IInvokeProvider)new ButtonAutomationPeer(more).GetPattern(PatternInterface.Invoke)).Invoke();
        await Task.Delay(100);
        RowCall(row, "SetHovered", false);
        Check(menu.IsOpen && more.Opacity == 1 && Field<Conversation>(shell, "current").Id == active.Id,
            context + ": invoking row menu keeps actions visible without switching the active chat");
        foreach (var item in menu.Items.OfType<MenuFlyoutItem>())
            Check(item.Icon is SymbolIcon && Readable(item.Foreground, item.Background, theme) && item.BorderThickness == new Thickness(0),
                context + ": readable native row menu item " + item.Text);

        Invoke((MenuFlyoutItem)menu.Items[0]);
        var dialog = await OpenDialogAsync(shell);
        Check(dialog.Title.ToString() == "Rename chat" && ((TextBox)dialog.Content).Text == other.Title,
            context + ": row rename targets unselected conversation");
        dialog.Hide();
        menu.Hide();
        await HistoryIdleAsync(shell);
        Check((await history.ListAsync(session.HistoryPartition)).Single(chat => chat.Id == other.Id).Title == other.Title,
            context + ": cancel rename leaves history unchanged");

        menu.ShowAt(more);
        await Task.Delay(50);
        Invoke((MenuFlyoutItem)menu.Items[0]);
        dialog = await OpenDialogAsync(shell);
        ((TextBox)dialog.Content).Text = "  Renamed row  ";
        Confirm(dialog);
        menu.Hide();
        await HistoryIdleAsync(shell);
        Check((await history.ListAsync(session.HistoryPartition)).Single(chat => chat.Id == other.Id).Title == "Renamed row"
            && Field<Conversation>(shell, "current") == active && input.Text == "Keep this draft",
            context + ": rename persists only menu target and preserves active chat and draft");
        Check(chats.SelectedItem is Conversation afterRename && afterRename.Id == active.Id, context + ": selection survives rename history refresh");

        await Task.Delay(80);
        shell.UpdateLayout();
        otherItem = chats.Items.Cast<Conversation>().Single(chat => chat.Id == other.Id);
        container = (ListViewItem)chats.ContainerFromItem(otherItem);
        row = ConversationRow(shell, container);
        more = Descendants(container).OfType<Button>().Single(button => button.Name == "ConversationActions");
        menu = (MenuFlyout)more.Flyout;
        Check(AutomationProperties.GetName(more).Contains("Renamed row"), context + ": refreshed/recycled action button reflects current row");
        menu.ShowAt(more);
        await Task.Delay(50);
        Invoke((MenuFlyoutItem)menu.Items[1]);
        dialog = await OpenDialogAsync(shell);
        Check(dialog.Title.ToString() == "Delete chat?", context + ": row delete retains native confirmation");
        dialog.Hide();
        menu.Hide();
        await HistoryIdleAsync(shell);
        Check((await history.ListAsync(session.HistoryPartition)).Any(chat => chat.Id == other.Id), context + ": cancel delete keeps menu target");

        menu.ShowAt(more);
        await Task.Delay(50);
        Call(shell, "SetBusy", true, true);
        await Task.Delay(50);
        Check(!chats.IsEnabled && !more.IsEnabled && !menu.IsOpen, context + ": busy state disables row actions and dismisses open menu");
        typeof(ChatShell).GetField("busy", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(shell, true);
        await (Task)Call(shell, "RenameAsync", otherItem)!;
        await (Task)Call(shell, "DeleteAsync", otherItem)!;
        Check(!Field<bool>(shell, "historyDialogOpen"), context + ": busy guards block history action dialogs");
        typeof(ChatShell).GetField("busy", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(shell, false);
        Call(shell, "SetBusy", false, false);

        menu.ShowAt(more);
        await Task.Delay(50);
        Invoke((MenuFlyoutItem)menu.Items[1]);
        dialog = await OpenDialogAsync(shell);
        Confirm(dialog);
        menu.Hide();
        await HistoryIdleAsync(shell);
        Check(!(await history.ListAsync(session.HistoryPartition)).Any(chat => chat.Id == other.Id)
            && Field<Conversation>(shell, "current") == active && input.Text == "Keep this draft"
            && chats.SelectedItem is Conversation afterDelete && afterDelete.Id == active.Id,
            context + ": deleting another row preserves active conversation, selection, and draft");

        // Filtered/replaced entries use the same native containers without accumulating stale handlers.
        var replacements = Enumerable.Range(0, 60).Select(index => new Conversation { Title = "Replacement " + index }).ToArray();
        chats.ItemsSource = replacements;
        await Task.Delay(100);
        shell.UpdateLayout();
        chats.ScrollIntoView(replacements[^1]);
        await Task.Delay(200);
        shell.UpdateLayout();
        var replacementContainer = (ListViewItem)chats.ContainerFromItem(replacements[^1]);
        var replacementRow = ConversationRow(shell, replacementContainer);
        var replacementButton = Descendants(replacementContainer).OfType<Button>().Single(button => button.Name == "ConversationActions");
        Check(AutomationProperties.GetName(replacementButton).Contains(replacements[^1].Title), context + ": recycled row actions bind to new conversation");
        RowCall(replacementRow, "SetHovered", true);
        var replacementMenu = (MenuFlyout)replacementButton.Flyout;
        replacementMenu.ShowAt(replacementButton);
        await Task.Delay(50);
        Invoke((MenuFlyoutItem)replacementMenu.Items[0]);
        dialog = await OpenDialogAsync(shell);
        Check(((TextBox)dialog.Content).Text == replacements[^1].Title, context + ": recycled row rename has no stale target or duplicate handler");
        dialog.Hide();
        replacementMenu.Hide();
        await HistoryIdleAsync(shell);
        await (Task)Call(shell, "LoadHistoryAsync", CancellationToken.None)!;
        await Task.Delay(100);

        var activeEntry = chats.Items.Cast<Conversation>().Single(chat => chat.Id == active.Id);
        var deleteTask = (Task)Call(shell, "DeleteAsync", activeEntry)!;
        dialog = await OpenDialogAsync(shell);
        Confirm(dialog);
        await deleteTask;
        Check(Field<Conversation>(shell, "current").Id != active.Id && Field<Conversation>(shell, "current").Service == ServiceKind.Agents
            && input.Text == "" && chats.SelectedItem is null && (await history.ListAsync(session.HistoryPartition)).Count == 0,
            context + ": deleting active row starts empty chat and retains current service");
        ((IInvokeProvider)new ButtonAutomationPeer(Field<Button>(shell, "newChat")).GetPattern(PatternInterface.Invoke)).Invoke();
        Check(Field<Conversation>(shell, "current").Messages.Count == 0, context + ": restyled native new chat remains functional");
    }

    private async Task CheckCatalogAndSearchAsync(ChatShell shell, ElementTheme theme, string context)
    {
        var input = Field<TextBox>(shell, "input");
        var chats = Enumerable.Range(0, 8).Select(index => new Conversation
        {
            Title = index == 7 ? "Agent fixture" : "Recent chat " + index,
            Updated = DateTimeOffset.UtcNow.AddMinutes(-index), Service = index == 7 ? ServiceKind.Agents : ServiceKind.Ai,
            Target = index == 7 ? "backend-agent-id" : "test-model",
            Messages = [new() { Message = new UIMessage { Id = "search-message", Role = Role.user, Parts = [new TextUIPart { Text = index == 7 ? "Needle in this message" : "Regular fixture text" }] } }]
        }).ToArray();
        typeof(ChatShell).GetField("conversations", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(shell, chats);
        typeof(ChatShell).GetField("current", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(shell, chats[0]);
        Call(shell, "FilterHistory"); input.Text = "Preserve search draft";
        var searchTask = (Task)Call(shell, "SearchConversationsAsync")!;
        var dialog = await OpenDialogAsync(shell);
        await Task.Delay(80);
        dialog.UpdateLayout();
        var query = Descendants(dialog).OfType<TextBox>().Single(text => text.Name == "ConversationSearchQuery");
        Check(dialog.Title.ToString() == "Search chats" && Descendants(dialog).OfType<Border>().Count(border => border.Name == "ConversationSearchCard") == 6,
            context + ": centered search modal initially shows six recent cards");
        Check(FocusManager.GetFocusedElement(shell.XamlRoot) == query && Readable(query.Foreground, query.Background, theme), context + ": modal search autofocus and readable native input");
        query.Text = "no such fixture";
        await Task.Delay(250);
        Check(Descendants(dialog).OfType<TextBlock>().Any(text => text.Text == "No results."), context + ": search modal no-results state");
        query.Text = "Needle"; query.Text = "Recent chat 2";
        await Task.Delay(250);
        Check(Descendants(dialog).OfType<Border>().Count(border => border.Name == "ConversationSearchCard") == 1
            && Descendants(dialog).OfType<TextBlock>().Any(text => text.Text == "Recent chat 2"), context + ": debounced title search rejects stale message query");
        dialog.Hide(); await searchTask;
        Check(Field<Conversation>(shell, "current") == chats[0] && input.Text == "Preserve search draft"
            && Field<ListView>(shell, "chats").Items.Count == 8 && FocusManager.GetFocusedElement(shell.XamlRoot) == Field<Button>(shell, "searchChats"),
            context + ": cancel search restores action focus and preserves draft, conversation, and unfiltered history");

        searchTask = (Task)Call(shell, "SearchConversationsAsync")!;
        dialog = await OpenDialogAsync(shell);
        query = Descendants(dialog).OfType<TextBox>().Single(text => text.Name == "ConversationSearchQuery");
        query.Text = "needle";
        await Task.Delay(250);
        Check(Descendants(dialog).OfType<TextBlock>().Any(text => text.Text.Contains("Needle in this message")), context + ": search result includes matched message snippet");
        InvokeButton(Descendants(dialog).OfType<Button>().Single(button => button.Name == "OpenSearchConversation"));
        await searchTask;
        Check(Field<Conversation>(shell, "current") == chats[7] && Field<AutoSuggestBox>(shell, "target").Text == "backend-agent-id"
            && Field<ToggleButton>(shell, "agents").IsChecked == true && input.Text == "", context + ": result opens agent conversation with its original service and backend target");
        Call(shell, "SetBusy", true, true);
        Check(!Field<Button>(shell, "searchChats").IsEnabled, context + ": modal search action respects streaming busy state");
        Call(shell, "SetBusy", false, false);

        using var definition = System.Text.Json.JsonDocument.Parse("""{"name":"Agent fixture","instructions":"Read-only instructions","model":{"id":"provider/model"},"future":{"keep":true}}""");
        var agent = new CatalogItem(CatalogKind.Agent, "backend-agent-id", "Agent fixture", "Agent description") { Model = "provider/model", Definition = definition.RootElement.Clone() };
        var overview = Field<object>(shell, "agentsOverview");
        overview.GetType().GetMethod("SetItems")!.Invoke(overview, new object[] { new[] { agent, agent with { Id = "second", Name = "Second agent" } }, new HashSet<string>(), "localhost" });
        var pageType = typeof(ChatShell).Assembly.GetType("AIHappey.Desktop.Core.DesktopPage")!;
        Call(shell, "ShowPage", Enum.Parse(pageType, "Agents"));
        await Task.Delay(100); shell.UpdateLayout();
        var cards = Descendants((DependencyObject)overview).OfType<Border>().Where(border => border.Name == "CatalogCard").ToArray();
        Check(cards.Length == 2 && Math.Abs(cards[0].TransformToVisual(shell).TransformPoint(new()).Y - cards[1].TransformToVisual(shell).TransformPoint(new()).Y) < 2,
            context + ": shared overview cards occupy two responsive columns");
        var view = Descendants(cards[0]).OfType<Button>().Single(button => button.Name == "CatalogDetails");
        var detailsTask = (Task)Call(shell, "OpenCatalogDetailsAsync", agent, view)!;
        dialog = await OpenDialogAsync(shell); await Task.Delay(80); dialog.UpdateLayout();
        Check(dialog.Title.ToString() == "Agent fixture" && !Field<SplitView>(shell, "details").IsPaneOpen
            && Descendants(dialog).OfType<ToggleButton>().Select(button => button.Content.ToString()).SequenceEqual(new[] { "General", "Instructions", "Definition" }),
            context + ": agent details use a centered read-only tabbed modal, not the chat sidebar");
        Check(Readable(dialog.Foreground, dialog.Background, theme), context + ": catalog modal follows host theme");
        Toggle(Descendants(dialog).OfType<ToggleButton>().Single(button => button.Name == "CatalogTabInstructions"));
        Check(Descendants(dialog).OfType<TextBlock>().Any(text => text.Text == "Read-only instructions"), context + ": agent instructions tab displays backend content without editing");
        Toggle(Descendants(dialog).OfType<ToggleButton>().Single(button => button.Name == "CatalogTabDefinition"));
        Check(Descendants(dialog).OfType<TextBlock>().Any(text => text.Text.Contains("\"future\"")), context + ": full backend definition remains visible read-only");
        window!.AppWindow.Resize(new Windows.Graphics.SizeInt32(600, 720));
        await Task.Delay(100); dialog.UpdateLayout();
        Check(((FrameworkElement)dialog.Content).ActualWidth <= shell.ActualWidth - 32
            && Descendants(dialog).OfType<ScrollViewer>().All(viewer => viewer.ScrollableWidth < 1), context + ": modal content is bounded with no narrow-window horizontal overflow");
        dialog.Hide(); await detailsTask;
        Check(FocusManager.GetFocusedElement(shell.XamlRoot) == view, context + ": catalog modal restores focus to its View action");
        shell.UpdateLayout();
        cards = Descendants((DependencyObject)overview).OfType<Border>().Where(border => border.Name == "CatalogCard").ToArray();
        Check(cards[1].TransformToVisual(shell).TransformPoint(new()).Y > cards[0].TransformToVisual(shell).TransformPoint(new()).Y,
            context + ": shared overview becomes one column at narrow widths");
        window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 900));
        await Task.Delay(100);
        var skill = new CatalogItem(CatalogKind.Skill, "provider/skill", "Skill fixture", "Skill description") { Version = "1", LatestVersion = "2" };
        detailsTask = (Task)Call(shell, "OpenCatalogDetailsAsync", skill, view)!;
        dialog = await OpenDialogAsync(shell); await Task.Delay(80);
        Check(Descendants(dialog).OfType<ToggleButton>().Select(button => button.Content.ToString()).SequenceEqual(new[] { "General", "Versions" }), context + ": skill modal General/Versions tabs");
        Toggle(Descendants(dialog).OfType<ToggleButton>().Single(button => button.Name == "CatalogTabVersions"));
        Check(Descendants(dialog).OfType<TextBlock>().Any(text => text.Text.Contains("could not be loaded")), context + ": unavailable skill versions are contained inside the modal");
        dialog.GetType().GetMethod("SetVersions")!.Invoke(dialog, new object?[] { new[] { new CatalogVersion("v1", "1", null, null, "Version description") }, false, null });
        Check(Descendants(dialog).OfType<TextBlock>().Any(text => text.Text == "Default") && Descendants(dialog).OfType<Button>().Any(button => AutomationProperties.GetName(button) == "Download version 1"),
            context + ": skill version cards show default badge and explicit download action");
        dialog.Hide(); await detailsTask;
        Call(shell, "ShowPage", Enum.Parse(pageType, "Chat"));
        Call(shell, "RenderTranscript");
        InvokeButton(Field<Button>(shell, "newChat"));
        Check(Field<Conversation>(shell, "current").Messages.Count == 0 && Field<StackPanel>(shell, "composer").Visibility == Visibility.Visible,
            context + ": New chat alone returns to welcome page without an extra Chat navigation entry");
    }

    private static void Toggle(ToggleButton button) => ((IToggleProvider)new ToggleButtonAutomationPeer(button).GetPattern(PatternInterface.Toggle)).Toggle();

    private static object ConversationRow(ChatShell shell, ListViewItem container)
    {
        var table = Field<object>(shell, "conversationRows");
        object?[] args = [container, null];
        if (!(bool)table.GetType().GetMethod("TryGetValue")!.Invoke(table, args)!) throw new InvalidOperationException("Conversation row was not initialized.");
        return args[1]!;
    }

    private static void RowCall(object row, string method, params object[] args) => row.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(row, args);
    private static async Task<ContentDialog> OpenDialogAsync(ChatShell shell)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            var dialog = VisualTreeHelper.GetOpenPopupsForXamlRoot(shell.XamlRoot).SelectMany(popup => Descendants(popup.Child)).OfType<ContentDialog>().FirstOrDefault();
            if (dialog is not null) return dialog;
            await Task.Delay(40);
        }
        throw new InvalidOperationException("Expected native conversation dialog did not open.");
    }
    private static void Confirm(ContentDialog dialog)
    {
        dialog.UpdateLayout();
        var primary = Descendants(dialog).OfType<Button>().Single(button => button.Name == "PrimaryButton");
        ((IInvokeProvider)new ButtonAutomationPeer(primary).GetPattern(PatternInterface.Invoke)).Invoke();
    }
    private static async Task HistoryIdleAsync(ChatShell shell)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (!Field<bool>(shell, "historyDialogOpen") && !Field<bool>(shell, "busy")) return;
            await Task.Delay(30);
        }
        throw new InvalidOperationException("Conversation action did not finish.");
    }

    private void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException(name);
        results.Add("PASS: " + name);
        File.WriteAllLines(report, results);
    }

    private static void InvokeButton(Button button) => ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
    private static T Field<T>(ChatShell shell, string name) => (T)typeof(ChatShell).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(shell)!;
    private static bool Readable(Brush foreground, Brush background, ElementTheme theme)
    {
        if (foreground is not SolidColorBrush text || background is not SolidColorBrush fill) return false;
        var textLight = text.Color.R + text.Color.G + text.Color.B > 384;
        var fillLight = fill.Color.A == 0 ? theme == ElementTheme.Light : fill.Color.R + fill.Color.G + fill.Color.B > 384;
        return textLight != fillLight;
    }
    private static object? Call(ChatShell shell, string name, params object[] args) => typeof(ChatShell).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(shell, args);
    private static void Invoke(MenuFlyoutItem item) => ((IInvokeProvider)new MenuFlyoutItemAutomationPeer(item).GetPattern(PatternInterface.Invoke)).Invoke();
    private static IEnumerable<DependencyObject> Descendants(DependencyObject element)
    {
        yield return element;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(element, index))) yield return child;
    }
}

internal sealed class UiHost(bool local) : IDesktopHost
{
    public string ProfileId { get; } = "UiTests-" + Guid.NewGuid().ToString("N");
    public bool AllowLocal => local;
    public string AccountLabel => local ? "API keys" : "Sign in";
    public string HistoryIdentity => "ui-test";
    public int AccountActions { get; private set; }
    public Task AuthenticateAsync(HttpRequestMessage request, ServiceKind service, CancellationToken cancellationToken) => throw new InvalidOperationException("UI tests must not authenticate or contact a gateway.");
    public Task ManageAccountAsync(object xamlRoot, CancellationToken cancellationToken) { AccountActions++; return Task.CompletedTask; }
}

internal sealed class UiRuntime : IRuntimeResolver
{
    public int Resolutions { get; private set; }
    public Task<Uri> ResolveAsync(ServiceKind service, DesktopSettings settings, CancellationToken cancellationToken)
    {
        Resolutions++;
        throw new InvalidOperationException("Gateway access is intentionally disabled in native UI tests.");
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
