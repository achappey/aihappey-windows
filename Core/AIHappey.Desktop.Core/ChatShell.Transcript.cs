using AIHappey.Vercel.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AIHappey.Desktop.Core;

public sealed partial class ChatShell
{
    private readonly DispatcherTimer relativeTimeTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly List<(TextBlock Label, DateTimeOffset Timestamp)> messageTimes = [];

    private void PrepareTranscript()
    {
        Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("ms-appx:///AIHappey.Desktop.Core/TranscriptStyles.xaml")
        });
        relativeTimeTimer.Tick += (_, _) => RefreshMessageTimes();
        Loaded += (_, _) => { RefreshMessageTimes(); relativeTimeTimer.Start(); };
        Unloaded += (_, _) => relativeTimeTimer.Stop();
    }

    private Style TranscriptStyle(string key) => (Style)Resources[key];

    private void RefreshMessageTimes()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (label, timestamp) in messageTimes)
            label.Text = RelativeTime.Format(timestamp, now, DesktopResources.Get);
    }

    private Border MessageHeader(ConversationMessage message, bool user, UIMessagePart? activity)
    {
        var items = new MessageFooterPanel(); // Reuse the existing wrapping, vertically centered layout.
        if (!user)
        {
            items.Children.Add(new TextBlock
            {
                Name = "MessageModel", Text = PortableConversations.MetadataString(message.Message.Metadata, "model") ?? current.Target,
                TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center
            });
            var badge = new Border
            {
                Name = "AiGeneratedBadge", Style = TranscriptStyle("TranscriptBadgeStyle"),
                Child = new TextBlock { Text = DesktopResources.Get("GeneratedByAi"), FontSize = 12, TextWrapping = TextWrapping.Wrap }
            };
            ToolbarControls.Label(badge, DesktopResources.Get("GeneratedByAi"));
            AutomationProperties.SetHelpText(badge, DesktopResources.Get("GeneratedByAiWarning"));
            ToolTipService.SetToolTip(badge, new ToolTip { Content = DesktopResources.Get("GeneratedByAiWarning") });
            items.Children.Add(badge);
        }
        var time = new TextBlock
        {
            Name = "MessageTime", Style = TranscriptStyle("TranscriptMetadataStyle"),
            Text = RelativeTime.Format(message.Timestamp, DateTimeOffset.UtcNow, DesktopResources.Get)
        };
        ToolTipService.SetToolTip(time, new ToolTip { Content = message.Timestamp.ToLocalTime().ToString("f") });
        messageTimes.Add((time, message.Timestamp));
        items.Children.Add(time);
        var layout = new Grid { ColumnSpacing = 12 };
        layout.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        layout.Children.Add(items);
        if (activity is not null)
        {
            IconElement icon = activity.Type == "reasoning" ? ToolbarControls.BrainIcon() : new FontIcon { Glyph = "\uE90F", FontSize = 20 };
            icon.Name = activity.Type == "reasoning" ? "ReasoningActivityIcon" : "ToolActivityIcon";
            icon.VerticalAlignment = VerticalAlignment.Top;
            ToolbarControls.Label(icon, DesktopResources.Get(activity.Type == "reasoning" ? "Reasoning" : "ToolActivity"));
            Grid.SetColumn(icon, 1); layout.Children.Add(icon);
        }
        return new Border
        {
            Name = "MessageHeader", Child = layout, Padding = new Thickness(16, 12, 16, 12),
            BorderThickness = new Thickness(0, 0, 0, 1), Style = TranscriptStyle("TranscriptSeparatorStyle")
        };
    }
}
