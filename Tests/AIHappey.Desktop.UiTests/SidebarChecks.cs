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
        var pages = navigation.Children.OfType<ToggleButton>().ToArray();
        var buttons = new ButtonBase[] { newChat, search }.Concat(pages).ToArray();
        Check(buttons.Select(AutomationProperties.GetName).SequenceEqual(new[] { "New chat", "Search chats", "Images", "Videos", "Transcriptions" }),
            context + ": compact actions match primary navigation order");
        split.IsPaneOpen = false;
        await Task.Delay(250); shell.UpdateLayout();
        void CheckCompactBounds(string state)
        {
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
        Check(buttons.All(button => !button.IsEnabled), context + ": all five compact actions respect busy state");
        Call(shell, "SetBusy", false, false);
        Check(buttons.All(button => button.IsEnabled), context + ": compact actions re-enable after busy state");
        shell.RequestedTheme = theme == ElementTheme.Light ? ElementTheme.Dark : ElementTheme.Light;
        await Task.Delay(100); shell.UpdateLayout(); CheckCompactBounds("theme switch");
        shell.RequestedTheme = theme;
        await Task.Delay(100);
        InvokeButton(toggle); await Task.Delay(250); shell.UpdateLayout();
        Check(split.IsPaneOpen && expanded.Visibility == Visibility.Visible && Field<ListView>(shell, "chats").Visibility == Visibility.Visible
            && buttons.All(button => double.IsNaN(button.Width) && double.IsNaN(button.Height)
                && button.Padding == new Thickness(12, 10, 12, 10) && button.HorizontalContentAlignment == HorizontalAlignment.Left
                && ((StackPanel)button.Content).Spacing == 12 && ((StackPanel)button.Content).Children.OfType<TextBlock>().Single().Visibility == Visibility.Visible),
            context + ": menu toggle restores expanded labels, dimensions, categories, and history");
        window!.AppWindow.Resize(new Windows.Graphics.SizeInt32(600, 900));
        await Task.Delay(250); shell.UpdateLayout();
        Check(!split.IsPaneOpen, context + ": narrow window automatically uses the compact sidebar");
        CheckCompactBounds("narrow window");
        window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 900));
        await Task.Delay(100);
        InvokeButton(toggle); await Task.Delay(250); shell.UpdateLayout();
        Check(split.IsPaneOpen, context + ": expanded sidebar remains available after narrow-window collapse");
    }
}
