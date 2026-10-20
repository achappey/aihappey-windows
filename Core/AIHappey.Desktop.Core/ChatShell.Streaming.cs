using FluentIcons.Common;
using AIHappey.Vercel.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace AIHappey.Desktop.Core;

public sealed partial class ChatShell
{
    // Retained only during a stream (including its final status update), never across conversations.
    private Conversation? streamingConversation;
    private List<StreamingRow>? streamingRows;
    private readonly Dictionary<ConversationMessage, ProgressBar> streamingIndicators = [];

    private sealed class StreamingRow(TranscriptRow projected)
    {
        public TranscriptRow Projected = projected;
        public UIMessagePart? Displayed;
        public int Page;
        public readonly Grid Row = new();
        public readonly Border Card = new() { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1) };
        public readonly StackPanel Sections = new();
        public readonly StackPanel Content = new() { Spacing = 8 };
        public readonly Border Footer = new() { Name = "MessageFooter", Padding = new Thickness(12, 8, 12, 8), BorderThickness = new Thickness(0, 1, 0, 0) };
        public Border? Header;
        public bool? Activity;
        public string? ContentKey;
        public string? ContentType;
        public (ChatMessagePresentation Presentation, DateTimeOffset Timestamp, string? ActivityType)? HeaderKey;
        public (bool Activity, int Page, int Count, string? Tokens, string? Price, bool ShowTokens, string Key)? FooterKey;
        public IReadOnlyList<MessageSource> Sources = [];
        public IReadOnlyList<MessageAttachment> Attachments = [];
    }

    private void RenderTranscriptRows(IReadOnlyList<TranscriptRow> projectedMessages)
    {
        var incremental = streamingRows is not null && ReferenceEquals(streamingConversation, current);
        if (!incremental)
        {
            transcript.Children.Clear();
            messageTimes.Clear();
            streamingRows = null;
            streamingIndicators.Clear();
        }
        var previous = streamingRows ?? [];
        var available = previous.ToDictionary(view => view.Projected.Block.Key);
        var next = new List<StreamingRow>();
        var liveActivity = new HashSet<string>();
        foreach (var projected in projectedMessages)
        {
            var key = projected.Block.Key;
            if (!available.Remove(key, out var view) || !ReferenceEquals(view.Projected.Message, projected.Message))
            {
                // The waiting card becomes the first real block without unloading its progress bar.
                view = available.Values.FirstOrDefault(candidate => ReferenceEquals(candidate.Projected.Message, projected.Message)
                    && candidate.Projected.Block.Key.EndsWith(":pending", StringComparison.Ordinal));
                if (view is not null) available.Remove(view.Projected.Block.Key);
                else view = CreateStreamingRow(projected);
            }
            view.Projected = projected;
            UpdateStreamingRow(view);
            next.Add(view);
            if (projected.Block.Activity) liveActivity.Add(current.Id + ":" + key);
        }

        var liveRows = next.Select(view => (UIElement)view.Row).ToHashSet();
        foreach (var view in previous.Where(view => !liveRows.Contains(view.Row))) ForgetTranscriptHeader(view.Header);
        for (var index = transcript.Children.Count - 1; index >= 0; index--)
            if (!liveRows.Contains(transcript.Children[index])) transcript.Children.RemoveAt(index);
        for (var index = 0; index < next.Count; index++)
        {
            var row = next[index].Row;
            if (index < transcript.Children.Count && ReferenceEquals(transcript.Children[index], row)) continue;
            if (transcript.Children.Contains(row)) transcript.Children.Remove(row);
            transcript.Children.Insert(index, row);
        }

        var liveMessages = new HashSet<ConversationMessage>();
        foreach (var view in next)
        {
            var message = view.Projected.Message;
            if (message.Message.Role != Role.assistant || message.Status != "streaming") continue;
            // Only the final projected block of each message owns its indicator.
            if (next.Last(candidate => ReferenceEquals(candidate.Projected.Message, message)) != view) continue;
            liveMessages.Add(message);
            if (!streamingIndicators.TryGetValue(message, out var indicator))
            {
                indicator = new ProgressBar { Name = "AssistantStreamingProgress", IsIndeterminate = true, Height = 4 };
                AutomationProperties.SetName(indicator, DesktopResources.Get("Working"));
                streamingIndicators.Add(message, indicator);
            }
            if (!view.Content.Children.Contains(indicator))
            {
                // Reparent only when a new block appears, never for ordinary content chunks.
                if (VisualTreeHelper.GetParent(indicator) is Panel parent) parent.Children.Remove(indicator);
                view.Content.Children.Add(indicator);
            }
        }
        foreach (var message in streamingIndicators.Keys.Where(message => !liveMessages.Contains(message)).ToArray())
        {
            var indicator = streamingIndicators[message];
            if (VisualTreeHelper.GetParent(indicator) is Panel parent) parent.Children.Remove(indicator);
            streamingIndicators.Remove(message);
        }
        foreach (var key in activityPages.Keys.Where(key => !liveActivity.Contains(key)).ToArray()) activityPages.Remove(key);
        streamingRows = liveMessages.Count > 0 ? next : null;
        streamingConversation = liveMessages.Count > 0 ? current : null;
    }

    private StreamingRow CreateStreamingRow(TranscriptRow projected)
    {
        var view = new StreamingRow(projected);
        var user = projected.Message.Message.Role == Role.user;
        view.Card.HorizontalAlignment = user ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        view.Sections.Children.Add(new Border()); // Header slot, replaced before attachment.
        view.Sections.Children.Add(new Border { Name = "MessageBody", Child = view.Content, Padding = new Thickness(16) });
        view.Footer.Style = TranscriptStyle("TranscriptSeparatorStyle");
        view.Sections.Children.Add(view.Footer);
        view.Row.ColumnDefinitions.Add(new() { Width = new GridLength(user ? 1 : 4, GridUnitType.Star) });
        view.Row.ColumnDefinitions.Add(new() { Width = new GridLength(user ? 4 : 1, GridUnitType.Star) });
        Grid.SetColumn(view.Card, user ? 1 : 0);
        view.Row.Children.Add(view.Card);
        return view;
    }

    private void UpdateStreamingRow(StreamingRow view)
    {
        var projected = view.Projected;
        var message = projected.Message;
        var block = projected.Block;
        var user = message.Message.Role == Role.user;
        var key = current.Id + ":" + block.Key;
        view.Page = block.Activity && activityPages.TryGetValue(key, out var chosen)
            ? Math.Clamp(chosen, 0, block.Parts.Count - 1) : block.Parts.Count - 1;
        view.Displayed = view.Page >= 0 ? block.Parts[view.Page] : null;
        if (view.Activity != block.Activity)
        {
            view.Card.Name = block.Activity ? "ActivityCard" : "MessageCard";
            view.Card.Style = TranscriptStyle(user ? "TranscriptUserCardStyle" : "TranscriptCardStyle");
            // A pending placeholder can become an activity card. Ordinary chunks never enter here.
            if (view.Card.Child is Grid oldActivity) oldActivity.Children.Clear();
            view.Card.Child = null;
            if (block.Activity)
            {
                var activity = new Grid();
                activity.ColumnDefinitions.Add(new() { Width = new GridLength(4) });
                activity.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
                activity.Children.Add(new Border { Name = "ActivityAccent", Style = TranscriptStyle("TranscriptActivityAccentStyle"), CornerRadius = new CornerRadius(7, 0, 0, 7) });
                Grid.SetColumn(view.Sections, 1); activity.Children.Add(view.Sections); view.Card.Child = activity;
            }
            else { Grid.SetColumn(view.Sections, 0); view.Card.Child = view.Sections; }
            view.Activity = block.Activity;
        }
        var headerKey = (MessagePresentation(message),
            message.Timestamp, block.Activity ? view.Displayed?.Type : null);
        if (view.HeaderKey != headerKey)
        {
            ForgetTranscriptHeader(view.Header);
            view.Header = MessageHeader(message, user, block.Activity ? view.Displayed : null);
            view.Sections.Children[0] = view.Header;
            view.HeaderKey = headerKey;
        }
        UpdateStreamingContent(view);
        UpdateStreamingFooter(view);
    }

    private void ForgetTranscriptHeader(Border? header)
    {
        if (header is null) return;
        var labels = ControlAppearance.Descendants(header).OfType<TextBlock>().ToHashSet();
        messageTimes.RemoveAll(entry => labels.Contains(entry.Label));
    }

    private static void UpdateStreamingContent(StreamingRow view)
    {
        var part = view.Displayed;
        var block = view.Projected.Block;
        var waiting = view.Projected.Message.Status == "streaming" && block.Key.EndsWith(":pending", StringComparison.Ordinal);
        var type = part?.Type;
        var contentKey = part is null ? (waiting ? "" : block.Key.EndsWith(":details", StringComparison.Ordinal)
            ? DesktopResources.Get("AttachmentsSources") : DesktopResources.Get("Working"))
            : type is "text" or "reasoning" ? PortableConversations.Text(part) : PortableConversations.Element(part).GetRawText();
        if (view.ContentKey == contentKey && view.ContentType == type) return;
        if (part is not null && type is "text" or "reasoning" && view.ContentType == type
            && view.Content.Children.OfType<ChatMarkdown>().FirstOrDefault() is { } markdown)
        {
            if (markdown.Text != contentKey) markdown.Text = contentKey;
        }
        else
        {
            // Do not clear the content panel: the attached progress bar must keep animating.
            for (var index = view.Content.Children.Count - 1; index >= 0; index--)
                if (view.Content.Children[index] is not ProgressBar) view.Content.Children.RemoveAt(index);
            var content = new StackPanel();
            if (part is not null) RenderPart(content, part);
            else if (!waiting) content.Children.Add(SelectableText(contentKey));
            var children = content.Children.ToArray();
            content.Children.Clear();
            for (var index = 0; index < children.Length; index++) view.Content.Children.Insert(index, children[index]);
        }
        view.ContentKey = contentKey;
        view.ContentType = type;
    }

    private void UpdateStreamingFooter(StreamingRow view)
    {
        var projected = view.Projected;
        var message = projected.Message;
        var block = projected.Block;
        var user = message.Message.Role == Role.user;
        var (tokenCount, price) = !user && !block.Activity && message.Message.Metadata is not null
            ? MessageUsage.Read(message.Message.Metadata, current.Target, message.Timestamp) : (null, null);
        var footerKey = (block.Activity, view.Page, block.Parts.Count, tokenCount, price, session.Settings.Chat.ShowTokens, block.Key);
        if (view.FooterKey == footerKey && view.Sources.SequenceEqual(projected.Sources)
            && view.Attachments.SequenceEqual(projected.Attachments)) return;
        var footerActions = new MessageFooterPanel();
        var copy = ToolbarControls.CopyButton();
        copy.Click += (_, _) =>
        {
            try
            {
                var part = view.Displayed; // Resolve at click time, not from the chunk that created the button.
                var package = new DataPackage();
                package.SetText(part is null ? "" : part.Type is "text" or "reasoning" ? PortableConversations.Text(part) : PortableConversations.Element(part).GetRawText());
                Clipboard.SetContent(package);
            }
            catch { Show(DesktopResources.Get("ClipboardUnavailable"), InfoBarSeverity.Warning); }
        };
        footerActions.Children.Add(copy);
        if (block.Activity)
        {
            var key = current.Id + ":" + block.Key;
            var previous = ActivityButton(DesktopResources.Get("PreviousActivity"), Icon.ChevronLeft);
            var next = ActivityButton(DesktopResources.Get("NextActivity"), Icon.ChevronRight);
            previous.IsEnabled = view.Page > 0; next.IsEnabled = view.Page < block.Parts.Count - 1;
            previous.Click += (_, _) => { activityPages[key] = view.Page - 1; RenderTranscript(); };
            next.Click += (_, _) => { activityPages[key] = view.Page + 1; RenderTranscript(); };
            footerActions.Children.Add(previous);
            footerActions.Children.Add(new TextBlock { Name = "ActivityCount", Text = $"{view.Page + 1}/{block.Parts.Count}", VerticalAlignment = VerticalAlignment.Center });
            footerActions.Children.Add(next);
            var list = ActivityButton(DesktopResources.Get("ActivityList"), Icon.List);
            list.Click += (_, _) => ShowActivity(view.Projected.Block.Key, key, view.Page, list);
            footerActions.Children.Add(list);
        }
        if (session.Settings.Chat.ShowTokens && tokenCount is not null)
        {
            var usage = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            usage.Children.Add(DesktopIcons.Create(Icon.DataUsage, 14));
            usage.Children.Add(new TextBlock { Text = tokenCount, VerticalAlignment = VerticalAlignment.Center });
            var badge = new Border { Name = "TokenUsage", Child = usage, Padding = new Thickness(10, 4, 10, 4), CornerRadius = new CornerRadius(16), VerticalAlignment = VerticalAlignment.Center };
            ToolbarControls.Label(badge, DesktopResources.Format("TokenUsage", tokenCount));
            badge.Style = TranscriptStyle("TranscriptSurfaceStyle");
            footerActions.Children.Add(badge);
        }
        if (price is not null)
        {
            var pricing = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            pricing.Children.Add(DesktopIcons.Create(Icon.Money, 14));
            pricing.Children.Add(new TextBlock { Text = price, VerticalAlignment = VerticalAlignment.Center });
            var badge = new Border { Name = "MessagePrice", Child = pricing, Padding = new Thickness(10, 4, 10, 4), CornerRadius = new CornerRadius(16), VerticalAlignment = VerticalAlignment.Center };
            ToolbarControls.Label(badge, DesktopResources.Format("MessagePrice", price));
            AutomationProperties.SetHelpText(badge, DesktopResources.Get("MessagePriceWarning"));
            ToolTipService.SetToolTip(badge, new ToolTip { Content = DesktopResources.Get("MessagePriceWarning") });
            badge.Style = TranscriptStyle("TranscriptSurfaceStyle");
            footerActions.Children.Add(badge);
        }
        AddDetailsActions(footerActions, projected);
        view.Footer.Child = footerActions;
        view.FooterKey = footerKey;
        view.Sources = projected.Sources;
        view.Attachments = projected.Attachments;
    }
}
