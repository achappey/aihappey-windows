using AIHappey.Desktop.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace AIHappey.Desktop.UiTests;

public partial class App
{
    private async Task CheckSidebarAsync(bool local, ElementTheme theme)
    {
        var settings = local ? new DesktopSettings() : new DesktopSettings
        {
            Ai = new() { Location = RuntimeLocation.Remote, RemoteUrl = "https://ui-test.invalid/ai/" },
            Agents = new() { Location = RuntimeLocation.Remote, RemoteUrl = "https://ui-test.invalid/agents/" }
        };
        // Exercise every primary action, independently of the user's hide/show defaults.
        settings.Navigation.HiddenItemKeys = [];
        var shell = new ChatShell(new DesktopSession(new UiHost(local), new UiRuntime(), settings)) { RequestedTheme = theme };
        try
        {
            window!.Content = shell; window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 900)); window.Activate();
            await Task.Delay(200); await HistoryIdleAsync(shell);
            await CheckCompactSidebarAsync(shell, theme, $"Sidebar / {(local ? "HeaderAuth" : "AzureAuth")} / {theme}");
        }
        finally { await shell.ShutdownAsync(); window!.Content = null; }
    }

    private async Task CheckCompactSidebarAsync(ChatShell shell, ElementTheme theme, string context)
    {
        var split = Field<SplitView>(shell, "split");
        var pane = (Grid)split.Pane;
        var toggle = Descendants(pane).OfType<Button>().Single(button => button.Name == "SidebarToggle");
        var expanded = Field<StackPanel>(shell, "expandedPageNavigation");
        var navigation = Field<StackPanel>(shell, "pageNavigation");
        var newChat = Field<Button>(shell, "newChat");
        var search = Field<Button>(shell, "searchChats");
        var primary = Descendants(navigation).OfType<StackPanel>().Single(panel => panel.Name == "NavigationGroupPrimary");
        var pages = Descendants(primary).OfType<ToggleButton>().ToArray();
        var buttons = new ButtonBase[] { newChat, search }.Concat(pages).ToArray();
        Check(buttons.Select(AutomationProperties.GetName).SequenceEqual(new[] { "New chat", "Search chats", "Images", "Videos", "Speech", "Transcriptions" }),
            context + ": compact actions match primary navigation order");
        var header = Descendants(pane).OfType<Grid>().Single(grid => grid.Children.Contains(toggle));
        split.IsPaneOpen = false;
        await Task.Delay(250); shell.UpdateLayout();
        void CheckCompactBounds(string state)
        {
            Check(header.Margin == new Thickness(0) && navigation.Spacing == 8,
                context + $": {state}, compact header has no expanded bottom margin and page icons use 8px spacing");
            var togglePoint = toggle.TransformToVisual(shell).TransformPoint(new());
            Check(Math.Abs(togglePoint.X + toggle.ActualWidth / 2 - split.CompactPaneLength / 2) < 1,
                context + $": {state}, menu button is centered in the compact rail");
            var previousBottom = toggle.TransformToVisual(shell).TransformPoint(new()).Y + toggle.ActualHeight;
            foreach (var button in buttons)
            {
                var point = button.TransformToVisual(shell).TransformPoint(new());
                var content = (StackPanel)button.Content;
                Check(button.Visibility == Visibility.Visible && button.IsHitTestVisible && button.ActualWidth == 32 && button.ActualHeight == 32
                    && point.X >= 0 && point.X + button.ActualWidth <= split.CompactPaneLength + 1 && point.Y >= previousBottom
                    && content.Children.OfType<IconElement>().Single().Visibility == Visibility.Visible
                    && content.Children.OfType<TextBlock>().Single().Visibility == Visibility.Collapsed
                    && button.Padding == new Thickness(0) && button.HorizontalContentAlignment == HorizontalAlignment.Center,
                    context + $": {state}, visible icon-only {AutomationProperties.GetName(button)} stays inside compact pane");
                var icon = content.Children.OfType<IconElement>().Single();
                var iconPoint = icon.TransformToVisual(shell).TransformPoint(new());
                Check(Math.Abs(point.Y - previousBottom - 8) < 1
                    && Math.Abs(point.X + button.ActualWidth / 2 - split.CompactPaneLength / 2) < 1
                    && Math.Abs(iconPoint.X + icon.ActualWidth / 2 - point.X - button.ActualWidth / 2) < 1
                    && Math.Abs(iconPoint.Y + icon.ActualHeight / 2 - point.Y - button.ActualHeight / 2) < 1,
                    context + $": {state}, {AutomationProperties.GetName(button)} has an 8px gap and a centered icon");
                Check(button.IsTabStop && button.Focus(FocusState.Keyboard)
                    && ToolTipService.GetToolTip(button) is ToolTip tooltip && tooltip.Content.ToString() == AutomationProperties.GetName(button)
                    && Readable(button.Foreground, button.Background, shell.ActualTheme),
                    context + $": {state}, accessible keyboard focus, tooltip, and readable {AutomationProperties.GetName(button)}");
                previousBottom = point.Y + button.ActualHeight;
            }
            Check(expanded.Visibility == Visibility.Collapsed && Field<ListView>(shell, "chats").Visibility == Visibility.Collapsed,
                context + ": compact mode hides other categories and conversation history");
        }
        CheckCompactBounds("initial");

        var pageType = typeof(ChatShell).Assembly.GetType("AIHappey.Desktop.Core.DesktopPage")!;
        foreach (var button in pages)
        {
            var pageName = button.Name["Navigate".Length..];
            // Exercise the same guarded route used by these existing buttons' Click handlers.
            await (Task)Call(shell, "NavigateAsync", Enum.Parse(pageType, pageName))!;
            shell.UpdateLayout();
            Check(Field<object>(shell, "activePage").ToString() == pageName && button.IsChecked == true
                && pages.Where(other => other != button).All(other => other.IsChecked == false) && !split.IsPaneOpen
                && button.Background is SolidColorBrush selected && selected.Color.A > 0,
                context + $": compact {pageName} route opens the page, highlights only its icon, and stays compact");
            Toggle(button);
            await (Task)Call(shell, "NavigateAsync", Enum.Parse(pageType, pageName))!;
            Check(button.IsChecked == true && !split.IsPaneOpen, context + $": repeated {pageName} navigation retains selected state");
        }
        var current = Field<Conversation>(shell, "current");
        Field<TextBox>(shell, "input").Text = "Compact search draft";
        InvokeButton(search);
        var dialog = await OpenDialogAsync(shell);
        Check(dialog.Title.ToString() == "Search chats" && !split.IsPaneOpen, context + ": compact search button opens the existing search dialog");
        dialog.Hide(); await HistoryIdleAsync(shell); await Task.Delay(80);
        Check(FocusManager.GetFocusedElement(shell.XamlRoot) == search && Field<Conversation>(shell, "current") == current
            && Field<TextBox>(shell, "input").Text == "Compact search draft" && !split.IsPaneOpen,
            context + ": closing compact search restores icon focus and preserves the conversation and draft");
        InvokeButton(newChat);
        Check(Field<Conversation>(shell, "current") != current && Field<TextBox>(shell, "input").Text == ""
            && Field<StackPanel>(shell, "composer").Visibility == Visibility.Visible && pages.All(button => button.IsChecked == false) && !split.IsPaneOpen,
            context + ": compact New chat starts a fresh chat and returns to the composer without expanding");
        Call(shell, "SetBusy", true, true);
        Check(buttons.All(button => !button.IsEnabled), context + ": all compact actions respect busy state");
        Call(shell, "SetBusy", false, false);
        Check(buttons.All(button => button.IsEnabled), context + ": compact actions re-enable after busy state");
        shell.RequestedTheme = theme == ElementTheme.Light ? ElementTheme.Dark : ElementTheme.Light;
        await Task.Delay(100); shell.UpdateLayout(); CheckCompactBounds("theme switch");
        shell.RequestedTheme = theme;
        await Task.Delay(100);
        InvokeButton(toggle); await Task.Delay(250); shell.UpdateLayout();
        Check(split.IsPaneOpen && expanded.Visibility == Visibility.Visible && Field<ListView>(shell, "chats").Visibility == Visibility.Visible
            && header.Margin == new Thickness(0, 0, 0, 12) && navigation.Spacing == 2
            && buttons.All(button => double.IsNaN(button.Width) && double.IsNaN(button.Height)
                && button.MinHeight == 36 && button.ActualHeight <= 36.5 && button.Padding == new Thickness(12, 4, 12, 4) && button.HorizontalContentAlignment == HorizontalAlignment.Left
                && ((StackPanel)button.Content).Spacing == 12 && ((StackPanel)button.Content).Children.OfType<TextBlock>().Single().Visibility == Visibility.Visible),
            context + ": menu toggle restores expanded labels, dimensions, categories, and history");
        Check(Descendants(expanded).OfType<Border>().Where(border => border.Name.EndsWith("Separator"))
                .All(border => border.Margin == new Thickness(0, 8, 0, 6))
            && Descendants(expanded).OfType<TextBlock>().Where(text => text.Name == "SectionHeading")
                .All(text => text.Margin == new Thickness(12, 0, 12, 4))
            && Descendants(navigation).OfType<StackPanel>().Where(panel => panel.Name.StartsWith("NavigationGroup"))
                .All(panel => panel.Spacing == 2)
            && Descendants(expanded).OfType<ToggleButton>().All(button => button.MinHeight == 36)
            && Descendants(expanded).OfType<Button>().Single(button => button.Name == "ArtificialIntelligenceCategory").MinHeight == 36,
            context + ": expanded navigation uses compact section gaps and 36px category/page rows");
        InvokeButton(toggle); await Task.Delay(250); shell.UpdateLayout();
        CheckCompactBounds("repeated collapse");
        InvokeButton(toggle); await Task.Delay(250); shell.UpdateLayout();
        window!.AppWindow.Resize(new Windows.Graphics.SizeInt32(600, 900));
        await Task.Delay(250); shell.UpdateLayout();
        Check(!split.IsPaneOpen, context + ": narrow window automatically uses the compact sidebar");
        CheckCompactBounds("narrow window");
        window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 900));
        await Task.Delay(100);
        InvokeButton(toggle); await Task.Delay(250); shell.UpdateLayout();
        Check(split.IsPaneOpen, context + ": expanded sidebar remains available after narrow-window collapse");
        await CheckCompactHiddenNavigationAsync(shell, context);
    }

    private async Task CheckCompactHiddenNavigationAsync(ChatShell shell, string context)
    {
        var session = Field<DesktopSession>(shell, "session");
        var original = session.Settings.Navigation.HiddenItemKeys.ToList();
        var split = Field<SplitView>(shell, "split");
        var group = Field<System.Collections.IEnumerable>(shell, "navigationGroups").Cast<object>()
            .Single(value => (string)value.GetType().GetProperty("Name")!.GetValue(value)! == "Primary");
        var primary = (StackPanel)group.GetType().GetProperty("Panel")!.GetValue(group)!;
        var hidden = (StackPanel)group.GetType().GetProperty("Hidden")!.GetValue(group)!;
        var more = (Button)group.GetType().GetProperty("More")!.GetValue(group)!;
        var menu = (MenuFlyout)group.GetType().GetProperty("CompactMenu")!.GetValue(group)!;
        var speech = Descendants(primary).OfType<ToggleButton>().Single(button => button.Name == "NavigateSpeech");
        bool MenuVisible() => VisualTreeHelper.GetOpenPopupsForXamlRoot(shell.XamlRoot).Any(popup => Descendants(popup).OfType<MenuFlyoutPresenter>().Any());
        try
        {
            split.IsPaneOpen = false;
            session.Settings.Navigation.HiddenItemKeys = ["speech"];
            Call(shell, "UpdateNavigationLayout"); await Task.Delay(100); shell.UpdateLayout();
            Check(primary.Children.Contains(more) && hidden.Visibility == Visibility.Collapsed
                && !primary.Children.OfType<Grid>().Any(row => row.Name == "NavigationRowspeech")
                && more.Focus(FocusState.Keyboard), context + ": hidden Speech leaves the compact rail and More remains keyboard-accessible");

            speech.IsEnabled = false;
            Call(shell, "BuildCompactNavigationMenu", group);
            Check(menu.Items.Single() is MenuFlyoutItem { IsEnabled: false }, context + ": compact hidden menu preserves the destination's disabled state");
            speech.IsEnabled = true;
            InvokeButton(more); await Task.Delay(100);
            var entry = (MenuFlyoutItem)menu.Items.Single();
            Check(entry.Name == "NavigationOpenspeech" && entry.Text == AutomationProperties.GetName(speech)
                && entry.Icon is not null && entry.RequestedTheme == shell.ActualTheme && MenuVisible()
                && entry.Focus(FocusState.Keyboard), context + ": hidden Speech is one native, labeled, themed, keyboard-accessible direct action without Open/Show submenus");
            Invoke(entry); await Task.Delay(150); shell.UpdateLayout();
            Check(Field<object>(shell, "activePage").ToString() == "Speech" && !split.IsPaneOpen && !MenuVisible()
                && session.Settings.Navigation.HiddenItemKeys.SequenceEqual(["speech"])
                && hidden.Visibility == Visibility.Collapsed, context + ": direct hidden action opens Speech, dismisses the menu, and preserves hidden preferences and compact mode");

            split.IsPaneOpen = true; await Task.Delay(100);
            InvokeButton(more); await Task.Delay(100);
            Check(hidden.Visibility == Visibility.Visible && !MenuVisible(), context + ": expanded More still reveals hidden navigation rows instead of opening the compact menu");
            var visibilityMenu = (MenuFlyout)speech.ContextFlyout;
            visibilityMenu.ShowAt(speech); await Task.Delay(100);
            Check(visibilityMenu.Items.Single() is MenuFlyoutItem { Name: "NavigationVisibilityspeech" } action
                && action.Text == DesktopResources.Get("NavigationShow"), context + ": expanded hidden-row menu retains its Show control");
            visibilityMenu.Hide();

            split.IsPaneOpen = false;
            session.Settings.Navigation.HiddenItemKeys = NavigationPreferences.PrimaryKeys.ToList();
            Call(shell, "UpdateNavigationLayout"); await Task.Delay(100);
            InvokeButton(more); await Task.Delay(100);
            Check(!primary.Children.OfType<Grid>().Any() && primary.Children.Contains(more)
                && menu.Items.OfType<MenuFlyoutItem>().Select(item => item.Name).SequenceEqual(NavigationPreferences.PrimaryKeys.Select(key => "NavigationOpen" + key)),
                context + ": an entirely hidden primary category keeps every destination directly reachable through More");
            var current = Field<Conversation>(shell, "current");
            Invoke(menu.Items.OfType<MenuFlyoutItem>().Single(item => item.Name == "NavigationOpennew")); await Task.Delay(100);
            Check(Field<Conversation>(shell, "current") != current && Field<object>(shell, "activePage").ToString() == "Chat"
                && !split.IsPaneOpen && session.Settings.Navigation.HiddenItemKeys.SequenceEqual(NavigationPreferences.PrimaryKeys),
                context + ": hidden New chat retains its original action without showing hidden icons");

            Call(shell, "SetBusy", true, true);
            Check(!more.IsEnabled && !MenuVisible(), context + ": compact overflow respects busy state and dismisses its menu");
            Call(shell, "SetBusy", false, false);
            session.Settings.Navigation.HiddenItemKeys = [];
            Call(shell, "UpdateNavigationLayout");
            Check(!primary.Children.Contains(more) && primary.Children.OfType<Grid>().Count() == NavigationPreferences.PrimaryKeys.Count,
                context + ": no hidden items means no empty compact overflow menu");
        }
        finally
        {
            menu.Hide(); speech.IsEnabled = true;
            session.Settings.Navigation.HiddenItemKeys = original;
            Call(shell, "UpdateNavigationLayout");
        }
    }
}
