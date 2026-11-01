using AIHappey.Desktop.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AIHappey.Desktop.UiTests;

public partial class App
{
    private async Task CheckCanvasActivityCardsAsync(ChatShell shell, string context)
    {
        var body = Field<Grid>(shell, "conversationActivityBody");
        var saved = PortableConversations.Write(Field<Conversation>(shell, "current"));
        var fixture = PortableConversations.Read("""
            {"id":"canvas-fixture","messages":[
              {"id":"created","role":"assistant","metadata":{"timestamp":"2026-11-01T12:00:00Z"},"parts":[
                {"type":"tool-apply_patch","toolCallId":"plain","state":"output-available","input":{"operation":{"type":"create_file","path":"my notes.txt","diff":"+# Original"}},"output":{"isError":false,"content":[{"type":"text","text":"created"}]}},
                {"type":"tool-local_canvas_create","toolCallId":"markdown","state":"output-available","input":{"path":"/notes","file_text":"# Markdown"},"output":{"isError":false,"content":[{"type":"text","text":"OK"}]}}]},
              {"id":"changed","role":"assistant","metadata":{"timestamp":"2026-11-01T12:01:00Z"},"parts":[
                {"type":"tool-str_replace_editor","toolCallId":"edit","state":"output-available","input":{"command":"str_replace","path":"my notes.txt","old_str":"Original","new_str":"Latest <script>literal</script>"},"output":{"isError":false,"content":[{"type":"text","text":"replaced"}]}}]}]}
            """);
        var before = PortableConversations.Write(fixture);
        var documents = ConversationCanvasProjection.Project(fixture.Messages);
        var list = (UIElement)Call(shell, "CanvasActivityList", documents)!;
        body.Children.Clear(); body.Children.Add(new ScrollViewer { Content = list });
        try
        {
            await Task.Delay(120); shell.UpdateLayout();
            var cards = Descendants(list).OfType<Border>().Where(card => card.Name == "ConversationCanvasCard").ToArray();
            Check(cards.Length == 2, context + ": Canvas renders history-only patch and Canvas-plugin documents in the existing cards");
            var plainCard = cards.Single(card => AutomationProperties.GetName(card) == "my notes");
            var plain = Descendants(plainCard).OfType<TextBlock>().Single(text => text.Name == "CanvasPlainText");
            var markdown = Descendants(plainCard).OfType<ChatMarkdown>().Single();
            Check(plain.Visibility == Visibility.Visible && plain.IsTextSelectionEnabled && plain.Text == "# Latest <script>literal</script>"
                && markdown.Visibility == Visibility.Collapsed && markdown.Text == "",
                context + ": plain-text Canvas is literal selectable text, not hidden Markdown rendering");
            Check(Descendants(plainCard).OfType<TextBlock>().Single(text => text.Name == "CanvasVersionTimestamp").Text != "-"
                && !Descendants(plainCard).OfType<Button>().Single(button => button.Name == "OpenCanvasLink").IsEnabled,
                context + ": Canvas retains date/time header and never launches local URI protocols");
            var versions = Descendants(plainCard).OfType<Button>().Single(button => button.Name == "CanvasVersions");
            var menu = (MenuFlyout)versions.Flyout;
            Check(menu.Items.Count == 2 && ((ToggleMenuFlyoutItem)menu.Items[0]).IsChecked,
                context + ": Canvas version menu defaults to the latest creation-based version");
            menu.ShowAt(versions); await Task.Delay(80);
            Invoke((MenuFlyoutItem)menu.Items[1]); menu.Hide(); await Task.Delay(60);
            Check(plain.Text == "# Original" && ((ToggleMenuFlyoutItem)menu.Items[1]).IsChecked
                && !((ToggleMenuFlyoutItem)menu.Items[0]).IsChecked,
                context + ": existing version menu switches reconstructed plain-text history");
            var mdCard = cards.Single(card => AutomationProperties.GetName(card) == "notes");
            Check(Descendants(mdCard).OfType<ChatMarkdown>().Single().Text == "# Markdown"
                && Descendants(mdCard).OfType<TextBlock>().Single(text => text.Name == "CanvasPlainText").Visibility == Visibility.Collapsed,
                context + ": Markdown Canvas keeps the existing native Markdown renderer");
            Check(PortableConversations.Write(fixture) == before && PortableConversations.Write(Field<Conversation>(shell, "current")) == saved,
                context + ": Canvas viewing and version selection do not alter native results or conversation history");
        }
        finally { body.Children.Clear(); Call(shell, "RefreshConversationActivity"); }
    }
}
