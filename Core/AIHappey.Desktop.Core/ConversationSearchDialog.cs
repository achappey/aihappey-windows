using FluentIcons.Common;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AIHappey.Desktop.Core;

internal sealed class ConversationSearchDialog : ContentDialog, IResponsiveDialog
{
    private readonly IReadOnlyList<Conversation> conversations;
    private readonly Func<string, CancellationToken, Task<IReadOnlyList<ConversationSearchHit>>>? search;
    private readonly Grid layout = new() { RowSpacing = 12 };
    private readonly TextBox query = new() { Name = "ConversationSearchQuery", PlaceholderText = DesktopResources.Get("SearchConversationsPlaceholder"), CornerRadius = new CornerRadius(8) };
    private readonly StackPanel results = new() { Name = "ConversationSearchResults", Spacing = 8 };
    private readonly TextBlock status = new() { Name = "ConversationSearchStatus", TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Top, Visibility = Visibility.Collapsed };
    private readonly ScrollViewer viewer;
    private CancellationTokenSource? pending;
    private bool closed;
    public Conversation? SelectedConversation { get; private set; }

    public ConversationSearchDialog(IReadOnlyList<Conversation> conversations,
        Func<string, CancellationToken, Task<IReadOnlyList<ConversationSearchHit>>>? search = null)
    {
        this.conversations = conversations;
        this.search = search;
        Name = "ConversationSearchDialog"; Title = DesktopResources.Get("SearchChats"); CloseButtonText = DesktopResources.Get("Close");
        DefaultButton = ContentDialogButton.None;
        HorizontalAlignment = HorizontalAlignment.Center; VerticalAlignment = VerticalAlignment.Center;
        Resources["ContentDialogMaxWidth"] = 760d; Resources["ContentDialogMinWidth"] = 0d;
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        layout.Children.Add(query);
        viewer = new ScrollViewer { Content = results, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        Grid.SetRow(viewer, 1); layout.Children.Add(viewer);
        Grid.SetRow(status, 1); layout.Children.Add(status); Content = layout;
        ControlAppearance.Native(query); ToolbarControls.Label(query, DesktopResources.Get("SearchConversations"));
        AutomationProperties.SetLiveSetting(status, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        query.TextChanged += async (_, _) => await SearchAsync();
        Opened += (_, _) => { SizeToRoot(); XamlRoot.Changed += RootChanged; query.Focus(FocusState.Programmatic); };
        Closing += (_, _) => { closed = true; pending?.Cancel(); };
        Closed += (_, _) => XamlRoot.Changed -= RootChanged;
        Render(ConversationSearch.Find(conversations, ""), "");
    }

    private void RootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => SizeToRoot();
    void IResponsiveDialog.SizeToRoot() => SizeToRoot();
    private void SizeToRoot()
    {
        layout.Width = Math.Max(0, Math.Min(680, XamlRoot.Size.Width - 96));
        layout.Height = Math.Max(0, Math.Min(540, XamlRoot.Size.Height - 220));
    }

    private async Task SearchAsync()
    {
        pending?.Cancel();
        using var cancellation = new CancellationTokenSource(); pending = cancellation;
        var text = query.Text.Trim();
        results.Children.Clear(); SetStatus(DesktopResources.Get("Searching"));
        try
        {
            if (text.Length != 0) await Task.Delay(200, cancellation.Token);
            var hits = search is null ? ConversationSearch.Find(conversations, text, cancellation.Token) : await search(text, cancellation.Token);
            if (!closed && pending == cancellation) { Render(hits, text); viewer.ChangeView(null, 0, null, true); }
        }
        catch (OperationCanceledException) { /* Replaced query/closed modal never renders stale results. */ }
        catch (Exception) { if (!closed && pending == cancellation) SetStatus(DesktopResources.Get("OperationFailed")); }
        finally { if (pending == cancellation) pending = null; }
    }

    private void SetStatus(string text)
    {
        status.Text = text;
        status.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Render(IReadOnlyList<ConversationSearchHit> hits, string text)
    {
        results.Children.Clear();
        SetStatus(hits.Count == 0 ? DesktopResources.Get(text.Length == 0 ? "NoRecentChats" : "NoResults") : "");
        var now = DateTimeOffset.UtcNow;
        foreach (var hit in hits)
        {
            var conversation = hit.Conversation;
            var grid = new Grid();
            grid.RowDefinitions.Add(new() { Height = GridLength.Auto }); grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
            var content = new StackPanel { Spacing = 8, Margin = new Thickness(16) };
            content.Children.Add(new TextBlock { Text = conversation.Title, FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            var count = conversation.IsSummary ? conversation.MessageCount : conversation.Messages.Count;
            var badges = new MessageFooterPanel();
            badges.Children.Add(Badge("ConversationSearchMessageCount", DesktopResources.Format(count == 1 ? "MessageCountOne" : "MessageCount", count)));
            badges.Children.Add(Badge("ConversationSearchUpdated", RelativeTime.Format(conversation.Updated, now, DesktopResources.Get)));
            content.Children.Add(badges);
            if (hit.Snippet is not null) content.Children.Add(new TextBlock { Text = hit.Snippet, TextWrapping = TextWrapping.Wrap, MaxLines = 3, TextTrimming = TextTrimming.CharacterEllipsis });
            grid.Children.Add(content);
            var open = new Button { Name = "OpenSearchConversation", Tag = conversation.Id, Content = DesktopIcons.Create(Icon.Chat, 18), Width = 36, Height = 36, Padding = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Left };
            NativeCardSurface.Action(open); ToolbarControls.Label(open, DesktopResources.Format("OpenConversation", conversation.Title));
            open.Click += (_, _) => { SelectedConversation = conversation; Hide(); };
            var footer = new Border { Name = "ConversationSearchFooter", Child = open, Padding = new Thickness(12, 8, 12, 8), BorderThickness = new Thickness(0, 1, 0, 0) };
            NativeCardSurface.Divider(footer); Grid.SetRow(footer, 1); grid.Children.Add(footer);
            var card = new Border { Name = "ConversationSearchCard", Child = grid };
            NativeCardSurface.Card(card); AutomationProperties.SetName(card, conversation.Title);
            results.Children.Add(card);
        }
    }

    private static Border Badge(string name, string text)
    {
        var label = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        NativeCardSurface.Secondary(label, true);
        var badge = new Border { Name = name, Child = label, HorizontalAlignment = HorizontalAlignment.Left };
        NativeCardSurface.Badge(badge); ToolbarControls.Label(badge, text); return badge;
    }
}
