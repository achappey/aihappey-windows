using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AIHappey.Desktop.Core;

/// <summary>Native resource metadata, shared by recycled picker rows and template arguments.</summary>
public sealed class McpResourceView : UserControl
{
    public McpResourceView()
    {
        IsTabStop = false;
        DataContextChanged += (_, _) => Content = DataContext is McpResourceEntry entry ? Details(entry, true) : null;
    }

    internal static StackPanel Details(McpResourceEntry entry, bool showTitle)
    {
        var panel = new StackPanel { Spacing = 6, HorizontalAlignment = HorizontalAlignment.Stretch };
        if (showTitle) panel.Children.Add(new TextBlock { Text = entry.Name, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap });
        var badges = new MessageFooterPanel { Name = "McpResourceMetadata" };
        foreach (var text in new[] { entry.ResourceType, entry.ServerName, entry.MimeType }.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            var badge = new Border { Name = "McpResourceMetadataBadge", Padding = new Thickness(10, 4, 10, 4), CornerRadius = new CornerRadius(16),
                Child = new TextBlock { Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap } };
            ControlAppearance.TokenBadge(badge); badges.Children.Add(badge);
        }
        panel.Children.Add(badges);
        if (!string.IsNullOrWhiteSpace(entry.Description)) panel.Children.Add(new TextBlock { Text = entry.Description, TextWrapping = TextWrapping.Wrap });
        return panel;
    }
}
