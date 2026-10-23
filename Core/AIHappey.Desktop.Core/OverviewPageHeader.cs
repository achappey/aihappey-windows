using FluentIcons.Common;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AIHappey.Desktop.Core;

/// <summary>Centered overview heading with native, accessible links on its right.</summary>
internal sealed class OverviewPageHeader : Grid
{
    public OverviewPageHeader(TextBlock title, string? officialUrl = null, string? docsUrl = null)
    {
        Name = "OverviewPageHeader";
        ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new() { Height = GridLength.Auto });
        RowDefinitions.Add(new() { Height = GridLength.Auto });
        title.HorizontalAlignment = HorizontalAlignment.Center;
        title.TextAlignment = TextAlignment.Center;
        title.TextWrapping = TextWrapping.Wrap;
        AutomationProperties.SetHeadingLevel(title, Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level1);
        SetColumn(title, 1); Children.Add(title);
        var links = new StackPanel { Name = "OverviewHeaderLinks", Orientation = Orientation.Horizontal, Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        AddLink(docsUrl, "OverviewDocumentation", Icon.BookOpen);
        AddLink(officialUrl, "OverviewOfficialWebsite", Icon.Globe);
        SetColumn(links, 2); Children.Add(links);
        SizeChanged += (_, _) =>
        {
            // Keep the title truly centered; move links below it only when they cannot fit.
            var compact = links.Children.Count > 0 && ActualWidth < 500;
            SetColumn(title, compact ? 0 : 1); SetColumnSpan(title, compact ? 3 : 1);
            SetRow(links, compact ? 1 : 0);
            title.MaxWidth = Math.Max(0, compact ? ActualWidth : ActualWidth - 2 * (links.DesiredSize.Width + 8));
        };
        void AddLink(string? url, string labelKey, Icon icon)
        {
            if (url is null) return;
            var link = new HyperlinkButton { Name = labelKey, NavigateUri = new Uri(url), Content = DesktopIcons.Create(icon, 20),
                Width = 36, Height = 36, Padding = new Thickness(0) };
            ControlAppearance.Stock(link); ToolbarControls.Label(link, DesktopResources.Get(labelKey)); links.Children.Add(link);
        }
    }
}
