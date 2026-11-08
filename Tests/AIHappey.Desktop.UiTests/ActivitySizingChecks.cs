using AIHappey.Desktop.Core;
using FluentIcons.Common;
using FluentIcons.WinUI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace AIHappey.Desktop.UiTests;

public partial class App
{
    private async Task CheckActivitySizingAsync(ChatShell shell, string context)
    {
        var pane = Field<SplitView>(shell, "conversationActivity");
        var outer = Field<SplitView>(shell, "split");
        var panel = (Grid)pane.Pane;
        var body = Field<Grid>(shell, "conversationActivityBody");
        var toggle = Field<ToggleButton>(shell, "activityToggle");
        var expand = Descendants(panel).OfType<Button>().Single(b => b.Name == "ExpandConversationActivity");
        var shrink = Descendants(panel).OfType<Button>().Single(b => b.Name == "ShrinkConversationActivity");
        var close = Descendants(panel).OfType<Button>().Single(b => b.Name == "CloseConversationActivity");
        var input = Field<TextBox>(shell, "input");
        var originalDraft = input.Text;
        input.Text = "Keep this unsent activity-sizing draft";

        async Task Settle()
        {
            await Task.Delay(300); shell.UpdateLayout();
        }
        void CheckSize(int size, string reason)
        {
            var width = pane.ActualWidth;
            var expected = size switch { 0 => Math.Min(480, width), 1 => Math.Min(width, Math.Max(480, width * .75)), _ => width };
            Check(pane.IsPaneOpen && toggle.IsChecked == true && Math.Abs(pane.OpenPaneLength - expected) < 1
                && Math.Abs(panel.ActualWidth - expected) < 2 && panel.ActualWidth <= width + 1,
                context + $": {reason} keeps Activities open and bounded at size {size} (pane={panel.ActualWidth}, expected={expected}, workspace={width})");
            Check(expand.IsEnabled == (size < 2) && shrink.IsEnabled == (size > 0),
                context + ": " + reason + " disables only the corresponding endpoint resize action");
            var expectedMode = width < 900 || size == 2 || width - expected < 420 ? SplitViewDisplayMode.Overlay : SplitViewDisplayMode.Inline;
            Check(pane.DisplayMode == expectedMode, context + ": " + reason + " uses the native inline/overlay layout without squeezing chat");
            var bounds = panel.TransformToVisual(shell).TransformBounds(new(0, 0, panel.ActualWidth, panel.ActualHeight));
            var navigation = ((FrameworkElement)outer.Pane).TransformToVisual(shell).TransformBounds(new(0, 0, outer.OpenPaneLength, shell.ActualHeight));
            Check(outer.IsPaneOpen && bounds.Left >= navigation.Right - 1 && bounds.Right <= shell.ActualWidth + 1,
                context + ": " + reason + " leaves the expanded left navigation outside the activity pane");
            foreach (var button in new[] { expand, shrink, close })
            {
                var buttonBounds = button.TransformToVisual(panel).TransformBounds(new(0, 0, button.ActualWidth, button.ActualHeight));
                Check(buttonBounds.Left >= 0 && buttonBounds.Right <= panel.ActualWidth + 1 && button.ActualWidth == 32,
                    context + ": " + reason + " keeps " + button.Name + " inside the header");
            }
        }
        NavigationView SelectData()
        {
            var navigation = Descendants(body).OfType<NavigationView>().Single(n => n.Name == "ConversationActivityTabsNavigation");
            navigation.SelectedItem = navigation.MenuItems.OfType<NavigationViewItem>().Single(n => n.Name == "ConversationActivityData");
            return navigation;
        }

        try
        {
            window!.AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 900));
            outer.IsPaneOpen = true;
            await Settle();
            CheckSize(0, "initial compact default");
            Check(expand.Content is FluentIcon { Icon: Icon.PanelRightExpand }
                && shrink.Content is FluentIcon { Icon: Icon.PanelRightContract }
                && new[] { expand, shrink, close }.All(b => b.IsTabStop && b.Style is null)
                && expand.TransformToVisual(panel).TransformPoint(new()).X < shrink.TransformToVisual(panel).TransformPoint(new()).X
                && shrink.TransformToVisual(panel).TransformPoint(new()).X < close.TransformToVisual(panel).TransformPoint(new()).X,
                context + ": native icon-only header buttons use browser-recognizable panel icons and expand/shrink/close order");
            foreach (var (button, key) in new[] { (expand, "ExpandActivities"), (shrink, "ShrinkActivities") })
                Check(AutomationProperties.GetName(button) == DesktopResources.Get(key)
                    && ToolTipService.GetToolTip(button) is ToolTip { Content: string label } && label == DesktopResources.Get(key),
                    context + ": " + button.Name + " has a localized accessible name and native tooltip");

            var navigation = SelectData();
            await Settle();
            var content = body.Children.Single();
            var dataTab = (NavigationViewItem)navigation.SelectedItem;
            var viewer = (ScrollViewer)dataTab.Tag;
            viewer.ChangeView(null, 80, null, true);
            await Settle();
            foreach (var (button, size) in new[] { (expand, 1), (expand, 2), (shrink, 1), (shrink, 0) })
            {
                Check(button.Focus(FocusState.Keyboard), context + ": resize action accepts native keyboard focus");
                InvokeButton(button);
                await Settle();
                CheckSize(size, "header resize");
                Check(ReferenceEquals(body.Children.Single(), content) && ReferenceEquals(navigation.SelectedItem, dataTab)
                    && viewer.VerticalOffset > 0 && viewer.ViewportHeight > 0 && viewer.ViewportHeight <= body.ActualHeight + 1,
                    context + ": resizing retains tab/content instances and the bounded scrolled Data viewport");
                Check(input.Text == "Keep this unsent activity-sizing draft", context + ": resizing does not edit the composer draft");
            }

            // Refreshing activity is independent of the selected size. Closing/reopening can
            // rebuild activity cards, but must retain both the size and selected tab.
            InvokeButton(expand); await Settle(); InvokeButton(expand); await Settle();
            InvokeButton(close); await Settle();
            Check(!pane.IsPaneOpen && toggle.IsChecked == false, context + ": Close dismisses full-workspace Activities and clears its toolbar toggle");
            toggle.IsChecked = true; await Settle();
            CheckSize(2, "reopening full-workspace Activities");
            Call(shell, "RefreshConversationActivity"); await Settle();
            CheckSize(2, "activity refresh");
            Check(Descendants(body).OfType<NavigationView>().Single(n => n.Name == "ConversationActivityTabsNavigation")
                .SelectedItem is NavigationViewItem { Name: "ConversationActivityData" },
                context + ": reopening and refreshing preserve the selected Data tab");

            InvokeButton(shrink);
            InvokeButton(close);
            await Settle();
            Check(!pane.IsPaneOpen && toggle.IsChecked == false, context + ": explicit Close is not undone by a pending display-mode transition");
            toggle.IsChecked = true; await Settle();
            CheckSize(1, "reopening wider Activities");
            InvokeButton(shrink); await Settle();

            // Cross the inline/overlay breakpoint in both directions at every selected size.
            for (var size = 0; size < 3; size++)
            {
                if (size > 0) { InvokeButton(expand); await Settle(); }
                foreach (var width in new[] { 720, 520, 1280 })
                {
                    window.AppWindow.Resize(new Windows.Graphics.SizeInt32(width, 900)); await Settle();
                    CheckSize(size, "window resize to " + width);
                }
            }
            // Collapsing navigation grows the workspace; full size follows it without
            // accidentally taking over the navigation rail or dismissing Activities.
            outer.IsPaneOpen = false; await Settle();
            var fullBounds = panel.TransformToVisual(shell).TransformBounds(new(0, 0, panel.ActualWidth, panel.ActualHeight));
            Check(pane.IsPaneOpen && Math.Abs(panel.ActualWidth - pane.ActualWidth) < 2
                && fullBounds.Left >= outer.CompactPaneLength - 1,
                context + ": full size follows sidebar collapse while keeping the navigation rail available");
            outer.IsPaneOpen = true; await Settle();
            CheckSize(2, "sidebar expansion");
        }
        finally
        {
            input.Text = originalDraft;
            while (shrink.IsEnabled) { InvokeButton(shrink); await Settle(); }
            outer.IsPaneOpen = true;
            window!.AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 900));
            await Settle();
        }
    }
}
