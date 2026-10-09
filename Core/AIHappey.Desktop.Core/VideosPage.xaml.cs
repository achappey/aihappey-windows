using System.Collections.ObjectModel;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.System;
using Windows.UI.Core;

namespace AIHappey.Desktop.Core;

public sealed partial class VideosPage : UserControl
{
    private readonly ObservableCollection<Border> cards = [];
    private readonly Dictionary<string, Border> byKey = [];
    private readonly List<ComposerAttachment> attachments = [];
    private bool editable = true, hasModel, submitting;
    public Func<Task>? SendRequested { get; set; }
    public Func<Task>? PickRequested { get; set; }
    public Func<Task>? LinkRequested { get; set; }
    public Func<Task>? SettingsRequested { get; set; }
    public Func<Task>? FolderRequested { get; set; }
    public Func<LibraryVideo, Task>? PreviewRequested { get; set; }
    public Func<DataPackageView, Task>? AttachmentDataRequested { get; set; }
    public string PromptText => Prompt.Text.Trim();
    public IReadOnlyList<ComposerAttachment> Attachments => attachments.ToArray();
    public VideosPage()
    {
        InitializeComponent(); Name = "VideosPage";
        Title.Text = DesktopResources.Get("Videos"); LibraryTitle.Text = DesktopResources.Get("MyVideos");
        Prompt.PlaceholderText = DesktopResources.Get("VideoPromptPlaceholder"); NoModels.Text = DesktopResources.Get("VideoNoModels");
        EmptyLibrary.Text = DesktopResources.Get("VideoEmptyLibrary"); OpenFolder.Content = DesktopResources.Get("VideoOpenFolder");
        AutomationProperties.SetHeadingLevel(Title, Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level1);
        AutomationProperties.SetHeadingLevel(LibraryTitle, Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level2);
        ToolbarControls.Label(Prompt, DesktopResources.Get("VideoPromptPlaceholder")); ToolbarControls.Label(Send, DesktopResources.Get("VideoGenerate"));
        ToolbarControls.Label(AddAttachment, DesktopResources.Get("Attachments")); ToolbarControls.Label(Settings, DesktopResources.Get("VideoSettings"));
        foreach (var control in new Control[] { Prompt, Send, OpenFolder }) ControlAppearance.Stock(control);
        ToolbarControls.Subtle(AddAttachment); ToolbarControls.Subtle(Settings);
        AddAttachment.Flyout = AttachmentMenu();
        Send.Click += async (_, _) => { if (Send.IsEnabled && SendRequested is not null) await SendRequested(); };
        Settings.Click += async (_, _) => { if (editable && !submitting && SettingsRequested is not null) await SettingsRequested(); };
        OpenFolder.Click += async (_, _) => { if (editable && FolderRequested is not null) await FolderRequested(); };
        Prompt.TextChanged += (_, _) => UpdateActions();
        Prompt.KeyDown += async (_, args) =>
        {
            if (args.Key != VirtualKey.Enter || Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down)) return;
            args.Handled = true; if (Send.IsEnabled && SendRequested is not null) await SendRequested();
        };
        Prompt.Paste += async (_, args) =>
        {
            if (!editable || submitting) return;
            try
            {
                var data = Clipboard.GetContent();
                if (!data.Contains(StandardDataFormats.Bitmap) && !data.Contains(StandardDataFormats.StorageItems)) return;
                args.Handled = true; if (AttachmentDataRequested is not null) await AttachmentDataRequested(data);
            }
            catch (Exception) { Notice(DesktopResources.Get("ClipboardUnavailable"), InfoBarSeverity.Warning); }
        };
        LibraryGrid.ItemsSource = cards;
        LibraryGrid.ItemClick += async (_, args) => { if (editable && args.ClickedItem is Border { Tag: LibraryVideo video } && PreviewRequested is not null) await PreviewRequested(video); };
        LibraryGrid.ContainerContentChanging += (_, args) =>
        { if (args.ItemContainer is GridViewItem container && args.Item is Border card) { AutomationProperties.SetName(container, AutomationProperties.GetName(card)); container.IsEnabled = card.Tag is LibraryVideo; } };
        Root.AddHandler(UIElement.DragEnterEvent, new DragEventHandler(DragOverFiles), true);
        Root.AddHandler(UIElement.DragOverEvent, new DragEventHandler(DragOverFiles), true);
        Root.AddHandler(UIElement.DropEvent, new DragEventHandler(DropFiles), true);
        Root.DragLeave += (_, _) => DropOutline.Visibility = Visibility.Collapsed; Prompt.AllowDrop = false;
        SizeChanged += (_, _) => ResizeGrid(); Loaded += (_, _) => ResizeGrid(); SetModelAvailable(false);
    }
    private MenuFlyout AttachmentMenu()
    {
        var menu = new MenuFlyout();
        foreach (var (label, icon, action) in new[] { ("Attachments", Symbol.Attach, PickRequested), ("Link", Symbol.Link, LinkRequested) })
        {
            // Resolve callbacks at click time; the shell wires them after page construction.
            var item = new MenuFlyoutItem { Text = DesktopResources.Get(label), Icon = new SymbolIcon(icon) }; ControlAppearance.Stock(item);
            item.Click += async (_, _) => { if (editable && !submitting && (label == "Link" ? LinkRequested : PickRequested) is { } callback) await callback(); }; menu.Items.Add(item);
        }
        return menu;
    }
    private void ResizeGrid()
    {
        var width = Math.Max(0, ActualWidth - 48); Header.Width = width; PromptPanel.Width = Math.Min(1056, width);
        var columns = Math.Clamp((int)(width / 300), 1, 3);
        if (LibraryGrid.ItemsPanelRoot is ItemsWrapGrid panel) { panel.MaximumRowsOrColumns = columns; panel.ItemWidth = Math.Max(48, width / columns); panel.ItemHeight = Math.Max(180, width / columns * .65); }
        foreach (var card in cards) { card.Width = Math.Max(40, width / columns - 8); card.Height = Math.Max(172, width / columns * .65 - 8); }
    }
    private void DragOverFiles(object sender, DragEventArgs args)
    {
        args.Handled = true; var accepts = editable && !submitting && args.DataView.Contains(StandardDataFormats.StorageItems) && args.AllowedOperations.HasFlag(DataPackageOperation.Copy);
        args.AcceptedOperation = accepts ? DataPackageOperation.Copy : DataPackageOperation.None;
        args.DragUIOverride.Caption = accepts ? DesktopResources.Get("VideoAttachFiles") : ""; DropOutline.Visibility = accepts ? Visibility.Visible : Visibility.Collapsed;
    }
    private async void DropFiles(object sender, DragEventArgs args)
    {
        args.Handled = true; DropOutline.Visibility = Visibility.Collapsed;
        if (!editable || submitting || !args.DataView.Contains(StandardDataFormats.StorageItems) || !args.AllowedOperations.HasFlag(DataPackageOperation.Copy)) { args.AcceptedOperation = DataPackageOperation.None; return; }
        var deferral = args.GetDeferral();
        try { args.AcceptedOperation = DataPackageOperation.Copy; if (AttachmentDataRequested is not null) await AttachmentDataRequested(args.DataView); }
        finally { deferral.Complete(); }
    }
    public void AdmitAttachment(ComposerAttachment file)
    {
        VideoAttachments.Validate(file);
        if (!file.IsLink) { if (!VideoAttachments.IsInput(file.MediaType)) throw new InvalidOperationException(DesktopResources.Get("VideoAttachmentRequired")); attachments.RemoveAll(a => !a.IsLink); }
        else attachments.RemoveAll(a => a.RemoteUrl == file.RemoteUrl);
        if (attachments.Count >= 20) throw new InvalidOperationException(DesktopResources.Get("ImageAttachmentTotalLimit"));
        attachments.Add(file); RenderTags();
    }
    private void RenderTags()
    {
        Tags.Children.Clear();
        foreach (var file in attachments)
        {
            var row = new Grid { ColumnSpacing = 8 }; row.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            row.Children.Add(new FontIcon { Glyph = file.IsLink ? "\uE71B" : "\uE714", FontSize = 14 });
            var text = new TextBlock { Text = file.Name, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center }; Grid.SetColumn(text, 1); row.Children.Add(text);
            var remove = new Button { Name = "VideoRemoveAttachment", Content = new FontIcon { Glyph = "\uE711", FontSize = 10 }, Width = 28, Height = 28, Padding = new Thickness(0), IsEnabled = editable && !submitting };
            ToolbarControls.Subtle(remove); ToolbarControls.Label(remove, DesktopResources.Format("RemoveContext", file.Name)); remove.Click += (_, _) => { if (editable && !submitting) { attachments.Remove(file); RenderTags(); } };
            Grid.SetColumn(remove, 2); row.Children.Add(remove);
            var badge = new Border { Name = "VideoAttachmentTag", Child = row, CornerRadius = new CornerRadius(16), Padding = new Thickness(12, 2, 4, 2) }; ControlAppearance.TokenBadge(badge); Tags.Children.Add(badge); ToolTipService.SetToolTip(badge, file.RemoteUrl ?? file.Name);
        }
        TagScroll.Visibility = attachments.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }
    public void ClearAttachments() { attachments.Clear(); RenderTags(); }
    public void ClearDraft() { Prompt.Text = ""; ClearAttachments(); }
    public void SetModelAvailable(bool value) { hasModel = value; NoModels.Visibility = value ? Visibility.Collapsed : Visibility.Visible; UpdateActions(); }
    public void SetBusy(bool value) { editable = !value; UpdateActions(); }
    public void SetSubmitting(bool value) { submitting = value; UpdateActions(); }
    private void UpdateActions()
    {
        Prompt.IsReadOnly = !editable || submitting; AddAttachment.IsEnabled = Settings.IsEnabled = editable && !submitting; OpenFolder.IsEnabled = editable;
        Send.IsEnabled = editable && !submitting && hasModel && !string.IsNullOrWhiteSpace(Prompt.Text); RenderTags();
    }
    public void SetItems(IReadOnlyList<LibraryVideo> videos, IReadOnlyList<VideoJob> jobs)
    {
        var ordered = new List<(string Key, Func<Border> Create)>();
        foreach (var job in jobs.Where(j => j.Pending))
            for (var i = 0; i < job.RequestedVideos; i++)
            { var key = job.Id + ":pending:" + i; ordered.Add((key, () => PendingCard(job))); }
        foreach (var video in videos) ordered.Add((video.Key, () => ResultCard(video)));
        var wanted = ordered.Select(i => i.Key).ToHashSet();
        foreach (var key in byKey.Keys.Where(k => !wanted.Contains(k)).ToArray()) { cards.Remove(byKey[key]); byKey.Remove(key); }
        for (var i = 0; i < ordered.Count; i++)
        {
            var (key, create) = ordered[i];
            if (!byKey.TryGetValue(key, out var card)) { card = create(); byKey.Add(key, card); cards.Insert(i, card); }
            else { var old = cards.IndexOf(card); if (old != i) cards.Move(old, i); }
        }
        EmptyLibrary.Visibility = cards.Count == 0 ? Visibility.Visible : Visibility.Collapsed; ResizeGrid(); RefreshShimmers();
    }
    private static Border Card(UIElement child)
    { var card = new Border { Name = "VideoCard", Child = child, Margin = new Thickness(4) }; NativeCardSurface.Card(card); return card; }
    private static Border PendingCard(VideoJob job)
    {
        var card = Card(new VideoShimmer(DesktopResources.Get("VideoProcessing") + "\n" + job.Model)); card.Tag = job.Id;
        AutomationProperties.SetName(card, DesktopResources.Get("VideoProcessing") + " · " + job.Model); return card;
    }
    private Border ResultCard(LibraryVideo item)
    {
        var grid = new Grid(); var image = new Image { Stretch = Stretch.UniformToFill }; grid.Children.Add(image);
        grid.Children.Add(new FontIcon { Glyph = "\uE768", FontSize = 36, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        var labels = new StackPanel { Spacing = 2, Margin = new Thickness(12) };
        labels.Children.Add(new TextBlock { Text = item.Model, TextTrimming = TextTrimming.CharacterEllipsis });
        if (item.Cost is { } cost) labels.Children.Add(new TextBlock { Text = cost.ToString("C4", CultureInfo.GetCultureInfo("en-US")), FontSize = 12 });
        var metadata = new Border { Child = labels, VerticalAlignment = VerticalAlignment.Bottom }; ControlAppearance.TokenBadge(metadata); grid.Children.Add(metadata);
        var card = Card(grid); card.Tag = item; AutomationProperties.SetName(card, item.Generation.Prompt + " · " + item.Model); ToolTipService.SetToolTip(card, item.Generation.Prompt);
        card.Loaded += async (_, _) =>
        {
            if (image.Source is not null) return;
            try
            {
                VideoDisk.CheckPath(item.Path); var file = await StorageFile.GetFileFromPathAsync(item.Path);
                using var thumbnail = await file.GetThumbnailAsync(ThumbnailMode.VideosView, 360, ThumbnailOptions.UseCurrentScale);
                if (thumbnail is not null && card.IsLoaded) { var bitmap = new BitmapImage(); await bitmap.SetSourceAsync(thumbnail); image.Source = bitmap; }
            }
            catch (Exception) { /* Thumbnail/codec unavailable: retain native play fallback. */ }
        };
        return card;
    }
    public void RefreshShimmers() { foreach (var card in cards) if (card.Child is VideoShimmer shimmer) shimmer.RefreshAnimation(); }
    public void ClearNotices() => Notices.Children.Clear();
    public void Notice(string message, InfoBarSeverity severity)
    { var bar = new InfoBar { IsOpen = true, IsClosable = true, Message = message, Severity = severity }; bar.Closed += (_, _) => Notices.Children.Remove(bar); Notices.Children.Add(bar); }
    public void FocusPrompt() => Prompt.Focus(FocusState.Programmatic);
}
