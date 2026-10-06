using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace AIHappey.Desktop.Core;

internal sealed class ConversationSearchDialog : ContentDialog
{
    private readonly IReadOnlyList<Conversation> conversations;
    private readonly Grid layout = new() { RowSpacing = 12 };
    private readonly TextBox query = new() { Name = "ConversationSearchQuery", PlaceholderText = "Search conversations…", CornerRadius = new CornerRadius(8) };
    private readonly StackPanel results = new() { Name = "ConversationSearchResults", Spacing = 8 };
    private readonly TextBlock status = new() { Name = "ConversationSearchStatus", TextWrapping = TextWrapping.Wrap };
    private readonly ScrollViewer viewer;
    private CancellationTokenSource? pending;
    private bool closed;
    public Conversation? SelectedConversation { get; private set; }

    public ConversationSearchDialog(IReadOnlyList<Conversation> conversations)
    {
        this.conversations = conversations;
        Name = "ConversationSearchDialog"; Title = "Search chats"; CloseButtonText = "Close";
        DefaultButton = ContentDialogButton.None;
        HorizontalAlignment = HorizontalAlignment.Center; VerticalAlignment = VerticalAlignment.Center;
        Resources["ContentDialogMaxWidth"] = 760d; Resources["ContentDialogMinWidth"] = 0d;
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto }); layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        layout.Children.Add(query); Grid.SetRow(status, 1); layout.Children.Add(status);
        viewer = new ScrollViewer { Content = results, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        Grid.SetRow(viewer, 2); layout.Children.Add(viewer); Content = layout;
        ControlAppearance.Native(query); ToolbarControls.Label(query, "Search conversations");
        AutomationProperties.SetLiveSetting(status, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        query.TextChanged += async (_, _) => await SearchAsync();
        Opened += (_, _) => { SizeToRoot(); XamlRoot.Changed += RootChanged; query.Focus(FocusState.Programmatic); };
        Closing += (_, _) => { closed = true; pending?.Cancel(); };
        Closed += (_, _) => XamlRoot.Changed -= RootChanged;
        Render(ConversationSearch.Find(conversations, ""), "");
    }

    private void RootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => SizeToRoot();
    private void SizeToRoot()
    {
        layout.Width = Math.Max(0, Math.Min(680, XamlRoot.Size.Width - 96));
        layout.Height = Math.Max(0, Math.Min(540, XamlRoot.Size.Height - 220));
        MaxWidth = Math.Max(0, Math.Min(760, XamlRoot.Size.Width - 32));
    }

    private async Task SearchAsync()
    {
        pending?.Cancel();
        using var cancellation = new CancellationTokenSource(); pending = cancellation;
        var text = query.Text.Trim();
        results.Children.Clear(); status.Text = text.Length == 0 ? "Recent chats" : "Searching…";
        try
        {
            if (text.Length != 0) await Task.Delay(200, cancellation.Token);
            var hits = ConversationSearch.Find(conversations, text, cancellation.Token);
            if (!closed && pending == cancellation) { Render(hits, text); viewer.ChangeView(null, 0, null, true); }
        }
        catch (OperationCanceledException) { /* Replaced query/closed modal never renders stale results. */ }
        finally { if (pending == cancellation) pending = null; }
    }

    private void Render(IReadOnlyList<ConversationSearchHit> hits, string text)
    {
        results.Children.Clear();
        status.Text = hits.Count == 0 ? text.Length == 0 ? "No recent chats." : "No results."
            : text.Length == 0 ? "Recent chats" : $"{hits.Count}{(hits.Count == 50 ? "+" : "")} {(hits.Count == 1 ? "conversation" : "conversations")}";
        foreach (var hit in hits)
        {
            var conversation = hit.Conversation;
            var content = new StackPanel { Spacing = 8 };
            content.Children.Add(new TextBlock { Text = conversation.Title, FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            content.Children.Add(new TextBlock { Text = $"{conversation.Messages.Count} {(conversation.Messages.Count == 1 ? "message" : "messages")} · {conversation.Updated.ToLocalTime():g}", FontSize = 12, TextWrapping = TextWrapping.Wrap });
            if (hit.Snippet is not null) content.Children.Add(new TextBlock { Text = hit.Snippet, TextWrapping = TextWrapping.Wrap, MaxLines = 3, TextTrimming = TextTrimming.CharacterEllipsis });
            var open = new Button { Name = "OpenSearchConversation", Tag = conversation.Id, Content = new FontIcon { Glyph = "\uE8F2", FontSize = 18 }, Width = 36, Height = 36, Padding = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Left };
            ToolbarControls.Subtle(open); ToolbarControls.Label(open, "Open conversation: " + conversation.Title);
            open.Click += (_, _) => { SelectedConversation = conversation; Hide(); };
            var footer = new Border { Child = open, Padding = new Thickness(0, 8, 0, 0), BorderThickness = new Thickness(0, 1, 0, 0) };
            ControlAppearance.Separator(footer); content.Children.Add(footer);
            var card = new Border { Name = "ConversationSearchCard", Child = content, Padding = new Thickness(16), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1) };
            ControlAppearance.Apply(card, (_, _) => { }, palette => { card.Background = new SolidColorBrush(palette.Panel); card.BorderBrush = new SolidColorBrush(palette.Stroke); });
            results.Children.Add(card);
        }
    }
}
