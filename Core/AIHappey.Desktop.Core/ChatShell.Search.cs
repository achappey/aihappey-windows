using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AIHappey.Desktop.Core;

public sealed partial class ChatShell
{
    private readonly Button searchChats = new() { Name = "SearchChats", HorizontalAlignment = HorizontalAlignment.Stretch,
        HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(12, 10, 12, 10), CornerRadius = new CornerRadius(6) };
    private ConversationSearchDialog? searchDialog;

    private void PrepareSearchChats()
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        content.Children.Add(new SymbolIcon(Symbol.Find)); content.Children.Add(new TextBlock { Text = DesktopResources.Get("SearchChats"), VerticalAlignment = VerticalAlignment.Center });
        searchChats.Content = content; ToolbarControls.Subtle(searchChats); ToolbarControls.Label(searchChats, DesktopResources.Get("SearchChats"));
        searchChats.Click += async (_, _) => await SearchConversationsAsync();
    }

    private async Task SearchConversationsAsync()
    {
        if (busy || closing || downloading || historyDialogOpen || catalogDialog is not null || searchDialog is not null) return;
        var partition = session.HistoryPartition;
        // Replace the history-list copy with the live chat so the latest text/count/timestamp is searchable.
        var snapshot = conversations.Where(conversation => conversation.Id != current.Id).ToList();
        if (current.Messages.Count > 0 || conversations.Any(conversation => conversation.Id == current.Id)) snapshot.Add(current);
        var dialog = new ConversationSearchDialog(snapshot) { XamlRoot = XamlRoot };
        SystemAppearance.PrepareDialog(dialog); searchDialog = dialog; historyDialogOpen = true;
        try { await dialog.ShowAsync(); }
        finally
        {
            searchDialog = null; historyDialogOpen = false;
            if (!closing && searchChats.IsLoaded) searchChats.Focus(FocusState.Programmatic);
        }
        if (!closing && !busy && session.HistoryPartition == partition && dialog.SelectedConversation is { } selected)
            await OpenConversationAsync(selected);
    }

    private async Task OpenConversationAsync(Conversation selected)
    {
        if (busy || closing) return;
        var same = current.Id == selected.Id;
        if (!same) { current = selected; input.Text = ""; ResetContext(); }
        ShowPage(DesktopPage.Chat); UpdateMode(current.Service); target.Text = current.Target;
        suppress = true;
        try { chats.SelectedItem = conversations.FirstOrDefault(conversation => conversation.Id == current.Id); }
        finally { suppress = false; }
        RenderTranscript();
        await RunAsync(DiscoverAsync);
        if (!closing) input.Focus(FocusState.Programmatic);
    }
}
