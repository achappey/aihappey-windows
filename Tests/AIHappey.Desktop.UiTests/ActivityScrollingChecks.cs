using System.Text.Json;
using AIHappey.Desktop.Core;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace AIHappey.Desktop.UiTests;

public partial class App
{
    private async Task CheckActivityScrollingAsync(ChatShell shell, DesktopSession session, Conversation conversation, string context)
    {
        var buffer = Field<TransientConversationData>(shell, "transientConversationData");
        var scope = buffer.SetScope(session.HistoryPartition, conversation.Id);
        var before = PortableConversations.Write(conversation);
        for (var index = 0; index < 3; index++)
        {
            var item = StreamEvent.Parse(JsonSerializer.Serialize(new
            {
                type = "data-aihappey-debug", id = "scroll:" + index, transient = true,
                data = new { sequence = index, payload = string.Join("\n", Enumerable.Range(0, 90).Select(line => $"Debug event {index}, payload line {line}")) }
            }));
            Call(shell, "CaptureConversationData", scope, item, conversation.Messages[1]);
        }
        var toggle = Field<ToggleButton>(shell, "activityToggle");
        toggle.IsChecked = true;
        try
        {
            foreach (var width in new[] { 1280, 720 })
            {
                window!.AppWindow.Resize(new Windows.Graphics.SizeInt32(width, 900)); await Task.Delay(400); shell.UpdateLayout();
                var pane = Field<SplitView>(shell, "conversationActivity");
                // Changing SplitView display mode can dismiss the pane. Reopen it as the
                // user would before testing the narrow overlay's content viewport.
                for (var attempt = 0; attempt < 8; attempt++)
                {
                    toggle.IsChecked = true; pane.IsPaneOpen = true;
                    await Task.Delay(250);
                    if (pane.IsPaneOpen) break;
                }
                Call(shell, "RefreshConversationActivity");
                await Task.Delay(150); shell.UpdateLayout();
                var body = Field<Grid>(shell, "conversationActivityBody");
                var navigation = Descendants(body).OfType<NavigationView>().Single(n => n.Name == "ConversationActivityTabsNavigation");
                var dataTab = navigation.MenuItems.OfType<NavigationViewItem>().Single(n => n.Name == "ConversationActivityData");
                navigation.SelectedItem = dataTab;
                await Task.Delay(100); shell.UpdateLayout();
                var viewer = (ScrollViewer)dataTab.Tag;
                Check(pane.IsPaneOpen && viewer.ViewportHeight > 0 && viewer.ViewportHeight <= body.ActualHeight + 1
                    && body.ActualHeight <= shell.ActualHeight + 1 && viewer.ScrollableHeight > 100,
                    context + $": activity Data has a bounded scrollable viewport at {width}px (open={pane.IsPaneOpen}, viewport={viewer.ViewportHeight}, body={body.ActualHeight}, shell={shell.ActualHeight}, scrollable={viewer.ScrollableHeight})");
                Check(Descendants(viewer).OfType<Border>().Count(b => b.Name == "ConversationDataCard") == 3,
                    context + ": activity Data displays all transient debug items");
                viewer.ChangeView(null, viewer.ScrollableHeight, null, true);
                await Task.Delay(100); shell.UpdateLayout();
                Check(viewer.VerticalOffset > 0 && Math.Abs(viewer.VerticalOffset - viewer.ScrollableHeight) < 2,
                    context + $": activity Data scrolls past the long newest item to the bottom at {width}px");
                viewer.ChangeView(null, 0, null, true);
                await Task.Delay(60);
                Check(viewer.VerticalOffset < 1, context + ": activity Data can scroll back to the newest item");
                Call(shell, "RefreshConversationActivity"); await Task.Delay(80);
                navigation = Descendants(body).OfType<NavigationView>().Single(n => n.Name == "ConversationActivityTabsNavigation");
                Check(navigation.SelectedItem is NavigationViewItem { Name: "ConversationActivityData" },
                    context + ": activity refresh preserves the selected Data tab");
            }
            Check(PortableConversations.Write(conversation) == before, context + ": scrolling debug activity does not mutate saved messages");
        }
        finally
        {
            toggle.IsChecked = false;
            buffer.SetScope(session.HistoryPartition, "clear-scrolling-fixture");
            buffer.SetScope(session.HistoryPartition, conversation.Id);
            window!.AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 900));
            await Task.Delay(80);
        }
    }
}
