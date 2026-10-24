using FluentIcons.Common;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace AIHappey.Desktop.Core;

public sealed partial class ChatShell
{
    private readonly ConversationCanvasSelection canvasSelection = new();

    private UIElement CanvasActivityList(IReadOnlyList<CanvasDocument> documents)
    {
        canvasSelection.Synchronize(documents);
        var panel = new StackPanel { Name = "ConversationCanvasList", Spacing = 12, Padding = new Thickness(12) };
        if (documents.Count == 0)
            panel.Children.Add(new TextBlock { Name = "NoCanvasActivities", Text = DesktopResources.Get("NoCanvasActivities"),
                TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
        foreach (var document in documents) panel.Children.Add(CanvasActivityCard(document));
        return panel;
    }

    private Border CanvasActivityCard(CanvasDocument document)
    {
        var currentVersion = canvasSelection.Current(document);
        var body = new StackPanel { Spacing = 10 };
        var header = new Grid { ColumnSpacing = 8 };
        header.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var labels = new StackPanel { Spacing = 2 };
        var title = new TextBlock { Name = "CanvasDocumentTitle", Text = document.Title, TextWrapping = TextWrapping.Wrap,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
        ToolTipService.SetToolTip(title, document.Uri); labels.Children.Add(title);
        var timestamp = new TextBlock { Name = "CanvasVersionTimestamp", Text = ConversationCanvasProjection.FormatTimestamp(currentVersion.Timestamp), TextWrapping = TextWrapping.Wrap };
        NativeCardSurface.Secondary(timestamp, metadata: true); labels.Children.Add(timestamp); header.Children.Add(labels);
        var versions = new Button { Name = "CanvasVersions", Content = DesktopIcons.Create(Icon.MoreHorizontal, 18),
            Width = 32, Height = 32, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Top };
        NativeCardSurface.Action(versions); ToolbarControls.Label(versions, DesktopResources.Format("CanvasVersions", document.Title));
        var menu = new MenuFlyout(); versions.Flyout = menu;
        var markdown = new ChatMarkdown { Name = "CanvasMarkdown", Text = currentVersion.Text };
        foreach (var version in document.Versions)
        {
            var option = new ToggleMenuFlyoutItem { Name = "CanvasVersion", Text = ConversationCanvasProjection.FormatTimestamp(version.Timestamp),
                IsChecked = version == currentVersion, Tag = version };
            option.Click += (_, _) =>
            {
                currentVersion = version; canvasSelection.Select(document, version);
                timestamp.Text = ConversationCanvasProjection.FormatTimestamp(version.Timestamp); markdown.Text = version.Text;
                foreach (var entry in menu.Items.OfType<ToggleMenuFlyoutItem>()) entry.IsChecked = Equals(entry.Tag, version);
            };
            menu.Items.Add(option);
        }
        Grid.SetColumn(versions, 1); header.Children.Add(versions); body.Children.Add(header); body.Children.Add(markdown);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var copy = new Button { Name = "CopyCanvasMarkdown", Content = DesktopIcons.Create(Icon.Copy, 16), Width = 32, Height = 32, Padding = new Thickness(0) };
        NativeCardSurface.Action(copy); ToolbarControls.Label(copy, DesktopResources.Format("CopyCanvasMarkdown", document.Title));
        copy.Click += (_, _) =>
        {
            try { Clipboard.SetContent(CanvasClipboard.Create(currentVersion.Text)); }
            catch { Show(DesktopResources.Get("ClipboardUnavailable"), InfoBarSeverity.Warning); }
        };
        actions.Children.Add(copy);
        var externalUri = ConversationCanvasProjection.ExternalUri(document.Uri);
        var open = new Button { Name = "OpenCanvasLink", Content = DesktopIcons.Create(Icon.Open, 16), Width = 32, Height = 32,
            Padding = new Thickness(0), IsEnabled = externalUri is not null };
        NativeCardSurface.Action(open); ToolbarControls.Label(open, DesktopResources.Format(externalUri is null ? "CanvasLinkUnavailable" : "OpenCanvasLink", document.Title));
        open.Click += async (_, _) =>
        {
            if (externalUri is null) return;
            try { if (!await Launcher.LaunchUriAsync(externalUri)) Show(DesktopResources.Get("SourceOpenFailed"), InfoBarSeverity.Warning); }
            catch { Show(DesktopResources.Get("SourceOpenFailed"), InfoBarSeverity.Warning); }
        };
        actions.Children.Add(open); body.Children.Add(actions);
        var card = new Border { Name = "ConversationCanvasCard", Tag = document.Uri, Child = body };
        NativeCardSurface.Card(card); AutomationProperties.SetName(card, document.Title); return card;
    }
}
