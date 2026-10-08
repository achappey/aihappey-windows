using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Pickers;
using Windows.System;

namespace AIHappey.Desktop.Core;

public sealed partial class ChatShell
{
    private readonly SplitView details = new() { Name = "DetailsSplit", PanePlacement = SplitViewPanePlacement.Right,
        DisplayMode = SplitViewDisplayMode.Overlay, IsPaneOpen = false, OpenPaneLength = 440, CompactPaneLength = 0 };
    private readonly StackPanel detailsBody = new() { Name = "DetailsBody", Spacing = 12, Padding = new Thickness(16) };
    private readonly TextBlock detailsTitle = new() { Text = DesktopResources.Get("Details"), FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    private string? detailsConversation;
    private string? detailsBlock;
    private string? detailsKind;
    private string? sourceHost;
    private Button? detailsOwner;
    private bool downloading;
    private readonly CancellationTokenSource downloadLifetime = new();

    private UIElement BuildDetailsLayout(UIElement content)
    {
        var panel = new Grid();
        panel.RowDefinitions.Add(new() { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        var header = new Grid { Padding = new Thickness(16, 12, 12, 12), ColumnSpacing = 8 };
        header.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        header.Children.Add(detailsTitle);
        var close = ActivityButton(DesktopResources.Get("CloseDetails"), "\uE711"); close.Name = "CloseDetails";
        close.Click += (_, _) => details.IsPaneOpen = false;
        Grid.SetColumn(close, 1); header.Children.Add(close); panel.Children.Add(header);
        var viewer = new ScrollViewer { Content = detailsBody, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        Grid.SetRow(viewer, 1); panel.Children.Add(viewer);
        ControlAppearance.Apply(panel, (_, _) => { }, palette =>
        { panel.Background = new SolidColorBrush(palette.Panel); details.PaneBackground = new SolidColorBrush(palette.Panel); });
        panel.KeyDown += (_, args) => { if (args.Key == VirtualKey.Escape) { details.IsPaneOpen = false; args.Handled = true; } };
        details.PaneClosed += (_, _) =>
        {
            detailsKind = detailsBlock = detailsConversation = null;
            if (detailsOwner?.IsLoaded == true) detailsOwner.Focus(FocusState.Programmatic);
            detailsOwner = null; detailsBody.Children.Clear();
        };
        details.SizeChanged += (_, args) => details.OpenPaneLength = Math.Min(480, Math.Max(0, args.NewSize.Width));
        details.Pane = panel; details.Content = content; return details;
    }

    private void OpenDetails(string kind, string blockKey, Button owner, string? host = null)
    {
        detailsConversation = current.Id; detailsBlock = blockKey; detailsKind = kind; sourceHost = host; detailsOwner = owner;
        details.IsPaneOpen = true; RefreshDetails();
        DispatcherQueue.TryEnqueue(() =>
        {
            if (details.IsPaneOpen && details.Pane is FrameworkElement panel)
                ControlAppearance.Descendants(panel).OfType<Button>().FirstOrDefault(button => button.Name == "CloseDetails")?.Focus(FocusState.Programmatic);
        });
    }

    private void ShowActivity(string blockKey, string pageKey, int selected, Button owner)
    {
        activityPages[pageKey] = selected;
        OpenDetails("activity", blockKey, owner);
    }

    private void AddDetailsActions(Panel footer, TranscriptRow row)
    {
        if (row.Sources.Count > 0)
        {
            var domains = row.Sources.Where(source => source.Host is not null).GroupBy(source => source.Host!)
                .OrderByDescending(group => group.Count()).ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase).ToArray();
            var icons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
            foreach (var group in domains.Take(5))
            {
                var button = ActivityButton($"Sources from {group.Key} ({group.Count()})", "\uE774");
                button.Name = "SourceFavicon"; button.Width = 28;
                var icon = new Grid { Width = 20, Height = 20 };
                var hostLabel = group.Key.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? group.Key[4..] : group.Key;
                icon.Children.Add(new TextBlock { Text = hostLabel[..1].ToUpperInvariant(), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
                if (!AppContext.TryGetSwitch("AIHappey.Desktop.DisableRemoteImages", out var disabled) || !disabled)
                {
                    var origin = new Uri(group.First().Url!).GetLeftPart(UriPartial.Authority);
                    var image = new Image { Width = 20, Height = 20, Source = new BitmapImage(new Uri("https://t0.gstatic.com/faviconV2?client=SOCIAL&type=FAVICON&fallback_opts=TYPE,SIZE,URL&url=" + Uri.EscapeDataString(origin) + "&size=64")) };
                    image.ImageFailed += (_, _) => image.Visibility = Visibility.Collapsed;
                    icon.Children.Add(image);
                }
                button.Content = icon; button.Click += (_, _) => OpenDetails("sources", row.Block.Key, button, group.Key); icons.Children.Add(button);
            }
            if (domains.Length > 5)
            {
                var overflow = FooterButton("More sources", $"+{domains.Length - 5}"); overflow.Name = "SourcesOverflow";
                overflow.Click += (_, _) => OpenDetails("sources", row.Block.Key, overflow); icons.Children.Add(overflow);
            }
            if (icons.Children.Count > 0) footer.Children.Add(icons);
            var sources = FooterButton("Show sources", $"{row.Sources.Count} {(row.Sources.Count == 1 ? "source" : "sources")}"); sources.Name = "SourcesButton";
            sources.Click += (_, _) => OpenDetails("sources", row.Block.Key, sources); footer.Children.Add(sources);
        }
        if (row.Attachments.Count > 0)
        {
            var files = FooterButton("Show attachments", row.Attachments.Count.ToString(), "\uE723"); files.Name = "AttachmentsButton";
            files.Click += (_, _) => OpenDetails("attachments", row.Block.Key, files); footer.Children.Add(files);
        }
    }

    private static Button FooterButton(string label, string text, string? glyph = null)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        if (glyph is not null) content.Children.Add(new FontIcon { Glyph = glyph, FontSize = 14 });
        content.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        var button = new Button { Content = content, Padding = new Thickness(8, 4, 8, 4), MinHeight = 32 };
        ToolbarControls.Subtle(button); ToolbarControls.Label(button, label); return button;
    }

    private void RefreshDetails()
    {
        if (!details.IsPaneOpen) return;
        var row = detailsConversation == current.Id ? MessageDetails.Project(current.Messages).FirstOrDefault(row => row.Block.Key == detailsBlock) : null;
        if (row is null) { details.IsPaneOpen = false; return; }
        detailsBody.Children.Clear();
        if (detailsKind == "activity")
        {
            detailsTitle.Text = DesktopResources.Get("Activity");
            var key = current.Id + ":" + row.Block.Key;
            var selected = activityPages.TryGetValue(key, out var page) ? Math.Clamp(page, 0, row.Block.Parts.Count - 1) : row.Block.Parts.Count - 1;
            for (var index = 0; index < row.Block.Parts.Count; index++)
            {
                var part = row.Block.Parts[index]; var chosen = index;
                var title = part.Type == "reasoning" ? DesktopResources.Get("Reasoning") : PortableConversations.ToolName(part);
                var button = FooterButton(DesktopResources.Format("ShowActivity", index + 1, title), $"{index + 1}. {title}"); button.Name = "ActivityListItem";
                button.HorizontalAlignment = HorizontalAlignment.Stretch; button.HorizontalContentAlignment = HorizontalAlignment.Left;
                if (index == selected) ControlAppearance.Native(button);
                button.Click += (_, _) => { activityPages[key] = chosen; RenderTranscript(); };
                detailsBody.Children.Add(button);
            }
            return;
        }
        if (detailsKind == "sources")
        {
            detailsTitle.Text = DesktopResources.Get("Sources");
            var filters = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            var all = FooterButton(DesktopResources.Get("AllSources"), $"All ({row.Sources.Count})");
            all.Click += (_, _) => { sourceHost = null; RefreshDetails(); }; filters.Children.Add(all);
            foreach (var group in row.Sources.Where(source => source.Host is not null).GroupBy(source => source.Host!))
            {
                var filter = FooterButton(group.Key, $"{group.Key} ({group.Count()})");
                filter.Click += (_, _) => { sourceHost = group.Key; RefreshDetails(); }; filters.Children.Add(filter);
            }
            detailsBody.Children.Add(new ScrollViewer { Content = filters, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollMode = ScrollMode.Enabled, VerticalScrollMode = ScrollMode.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled });
            foreach (var source in row.Sources.Where(source => sourceHost is null || source.Host == sourceHost))
            {
                var card = DetailsCard(source.Title);
                if (source.Host is not null) card.Children.Add(SelectableText(source.Host));
                if (source.Url is not null) card.Children.Add(SelectableText(source.Url));
                if (source.Filename is not null) card.Children.Add(SelectableText(source.Filename));
                var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                var copy = ActivityButton(DesktopResources.Get("CopySource"), "\uE8C8"); copy.Click += (_, _) => CopyDetails(source.Url ?? source.Title); actions.Children.Add(copy);
                if (AttachmentDownloads.RemoteUri(source.Url) is { } uri)
                {
                    var open = ActivityButton(DesktopResources.Get("OpenSource"), "\uE8A7");
                    open.Click += async (_, _) => { try { await Launcher.LaunchUriAsync(uri); } catch { Show(DesktopResources.Get("SourceOpenFailed"), InfoBarSeverity.Warning); } }; actions.Children.Add(open);
                }
                card.Children.Add(actions);
            }
            return;
        }
        detailsTitle.Text = DesktopResources.Get("Attachments");
        foreach (var file in row.Attachments)
        {
            var card = DetailsCard(file.Name); card.Children.Add(SelectableText(file.MediaType));
            if (file.ResourceUri is not null) card.Children.Add(SelectableText(file.ResourceUri));
            var download = FooterButton(DesktopResources.Format("DownloadFile", file.Name), DesktopResources.Get("Download"), "\uE896"); download.Name = "DownloadAttachment";
            download.IsEnabled = !downloading && AttachmentDownloads.CanDownload(file);
            download.Click += async (_, _) => await DownloadAttachmentAsync(file); card.Children.Add(download);
            if (!AttachmentDownloads.CanDownload(file)) card.Children.Add(SelectableText(DesktopResources.Get("BrowserResourceHint")));
        }
    }

    private StackPanel DetailsCard(string title)
    {
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(new TextBlock { Text = title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        var border = new Border { Child = content, Padding = new Thickness(12), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1) };
        ControlAppearance.MessageCard(border, false); detailsBody.Children.Add(border); return content;
    }

    private void CopyDetails(string value)
    {
        try { var package = new Windows.ApplicationModel.DataTransfer.DataPackage(); package.SetText(value); Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package); }
        catch { Show(DesktopResources.Get("ClipboardUnavailable"), InfoBarSeverity.Warning); }
    }

    private async Task DownloadAttachmentAsync(MessageAttachment attachment)
    {
        if (downloading || closing) return;
        downloading = true; RefreshDetails();
        try
        {
            var name = AttachmentDownloads.SafeName(attachment.Name, attachment.MediaType);
            var picker = new FileSavePicker { SuggestedFileName = Path.GetFileNameWithoutExtension(name) };
            picker.FileTypeChoices.Add(attachment.MediaType, new List<string> { Path.GetExtension(name) });
            WinRT.Interop.InitializeWithWindow.Initialize(picker, Microsoft.UI.Win32Interop.GetWindowFromWindowId(XamlRoot.ContentIslandEnvironment.AppWindowId));
            var destination = await picker.PickSaveFileAsync();
            if (destination is null) return;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(downloadLifetime.Token);
            timeout.CancelAfter(TimeSpan.FromMinutes(2));
            using var downloadHttp = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseDefaultCredentials = false });
            // Stage in memory so invalid/failed network data never overwrites the selected file.
            using var buffer = new MemoryStream();
            await AttachmentDownloads.WriteAsync(attachment, buffer, downloadHttp, timeout.Token);
            await using var output = await destination.OpenStreamForWriteAsync();
            output.SetLength(0); buffer.Position = 0; await buffer.CopyToAsync(output, timeout.Token);
            Show(DesktopResources.Get("AttachmentDownloaded"), InfoBarSeverity.Success);
        }
        catch (OperationCanceledException) { if (!closing) Show(DesktopResources.Get("DownloadCanceled"), InfoBarSeverity.Warning); }
        catch (Exception error) { Show(error is InvalidOperationException ? error.Message : DesktopResources.Get("DownloadFailed"), InfoBarSeverity.Warning); }
        finally { downloading = false; if (!closing) RefreshDetails(); }
    }
}
