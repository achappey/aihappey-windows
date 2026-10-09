using System.Collections.ObjectModel;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.System;
using Windows.UI.Core;

namespace AIHappey.Desktop.Core;

public sealed class ImageTile
{
    public LibraryImage? Item { get; }
    public BitmapImage? Thumbnail { get; }
    public bool Pending => Item is null;
    public Visibility PendingVisibility => Pending ? Visibility.Visible : Visibility.Collapsed;
    public Visibility MetadataVisibility => Pending ? Visibility.Collapsed : Visibility.Visible;
    public string Model => Item?.Model ?? "";
    public string Cost => Item?.Cost is { } cost ? cost.ToString("C4", CultureInfo.GetCultureInfo("en-US")) : "";
    public string Description => Item is null ? DesktopResources.Get("Working") : Item.Generation.Prompt + " · " + Model;
    public ImageTile(LibraryImage? item)
    {
        Item = item;
        if (item is not null) Thumbnail = new BitmapImage { DecodePixelWidth = 320, UriSource = new Uri(item.Path) };
    }
}

public sealed partial class ImagesPage : UserControl
{
    private readonly ObservableCollection<ImageTile> tiles = [];
    private readonly List<ComposerAttachment> attachments = [];
    private bool editable = true, hasModel;
    public Func<Task>? SendRequested { get; set; }
    public Action? StopRequested { get; set; }
    public Func<Task>? PickRequested { get; set; }
    public Func<Task>? LinkRequested { get; set; }
    public Func<Task>? SettingsRequested { get; set; }
    public Func<Task>? FolderRequested { get; set; }
    public Func<LibraryImage, Task>? PreviewRequested { get; set; }
    public Func<DataPackageView, Task>? AttachmentDataRequested { get; set; }
    public string PromptText => Prompt.Text.Trim();
    public IReadOnlyList<ComposerAttachment> Attachments => attachments.ToArray();
    public ImagesPage()
    {
        InitializeComponent();
        Name = "ImagesPage"; Title.Text = DesktopResources.Get("Images"); LibraryTitle.Text = DesktopResources.Get("MyImages");
        Prompt.PlaceholderText = DesktopResources.Get("ImagePromptPlaceholder"); Stop.Content = DesktopResources.Get("Stop");
        NoModels.Text = DesktopResources.Get("ImageNoModels"); EmptyLibrary.Text = DesktopResources.Get("ImageEmptyLibrary"); OpenFolder.Content = DesktopResources.Get("ImageOpenFolder");
        AutomationProperties.SetHeadingLevel(Title, Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level1);
        AutomationProperties.SetHeadingLevel(LibraryTitle, Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level2);
        ToolbarControls.Label(Prompt, DesktopResources.Get("ImagePromptPlaceholder"));
        ToolbarControls.Label(Send, DesktopResources.Get("ImageGenerate")); ToolbarControls.Label(Settings, DesktopResources.Get("ImageSettings"));
        ToolbarControls.Label(AddAttachment, DesktopResources.Get("Attachments"));
        foreach (var control in new Control[] { Prompt, Send, Stop, OpenFolder }) ControlAppearance.Stock(control);
        ToolbarControls.Subtle(AddAttachment); ToolbarControls.Subtle(Settings);
        AddAttachment.Flyout = AttachmentMenu(false); Prompt.ContextFlyout = AttachmentMenu(true);
        Send.Click += async (_, _) => { if (editable && hasModel && SendRequested is not null) await SendRequested(); };
        Stop.Click += (_, _) => StopRequested?.Invoke();
        Settings.Click += async (_, _) => { if (SettingsRequested is not null) await SettingsRequested(); };
        OpenFolder.Click += async (_, _) => { if (FolderRequested is not null) await FolderRequested(); };
        Prompt.TextChanged += (_, _) => UpdateSend();
        Prompt.KeyDown += async (_, args) =>
        {
            if (args.Key != VirtualKey.Enter || Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down)) return;
            args.Handled = true; if (Send.IsEnabled && SendRequested is not null) await SendRequested();
        };
        Prompt.Paste += async (_, args) =>
        {
            if (!editable) return;
            try
            {
                var data = Clipboard.GetContent();
                if (!data.Contains(StandardDataFormats.Bitmap) && !data.Contains(StandardDataFormats.StorageItems)) return;
                args.Handled = true; if (AttachmentDataRequested is not null) await AttachmentDataRequested(data);
            }
            catch (Exception) { Notice(DesktopResources.Get("ClipboardUnavailable"), InfoBarSeverity.Warning); }
        };
        LibraryGrid.ItemsSource = tiles;
        LibraryGrid.ItemClick += async (_, args) => { if (editable && args.ClickedItem is ImageTile { Item: { } image } && PreviewRequested is not null) await PreviewRequested(image); };
        LibraryGrid.ContainerContentChanging += (_, args) =>
        {
            if (args.ItemContainer is GridViewItem container && args.Item is ImageTile tile)
            { AutomationProperties.SetName(container, tile.Description); container.IsEnabled = !tile.Pending; }
        };
        Root.AddHandler(UIElement.DragEnterEvent, new DragEventHandler(DragOverFiles), true);
        Root.AddHandler(UIElement.DragOverEvent, new DragEventHandler(DragOverFiles), true);
        Root.AddHandler(UIElement.DropEvent, new DragEventHandler(DropFiles), true);
        Root.DragLeave += (_, args) => { var point = args.GetPosition(Root); if (point.X <= 0 || point.Y <= 0 || point.X >= Root.ActualWidth || point.Y >= Root.ActualHeight) DropOutline.Visibility = Visibility.Collapsed; };
        Prompt.AllowDrop = false;
        SizeChanged += (_, _) => ResizeGrid(); Loaded += (_, _) => ResizeGrid();
        SetModelAvailable(false);
    }
    private MenuFlyout AttachmentMenu(bool context)
    {
        var menu = new MenuFlyout();
        void Item(string label, string glyph, Func<Task> callback)
        {
            var item = new MenuFlyoutItem { Text = DesktopResources.Get(label), Icon = new FontIcon { Glyph = glyph } };
            ControlAppearance.Stock(item); item.Click += async (_, _) => { if (editable) await callback(); }; menu.Items.Add(item);
        }
        if (context)
        {
            Item("ImageCopy", "\uE8C8", () => { var data = new DataPackage(); data.SetText(Prompt.SelectedText); Clipboard.SetContent(data); return Task.CompletedTask; });
            Item("ImageCut", "\uE8C6", () => { var data = new DataPackage(); data.SetText(Prompt.SelectedText); Clipboard.SetContent(data); Prompt.SelectedText = ""; return Task.CompletedTask; });
            Item("ImagePaste", "\uE77F", async () =>
            {
                try
                {
                    var data = Clipboard.GetContent();
                    if ((data.Contains(StandardDataFormats.Bitmap) || data.Contains(StandardDataFormats.StorageItems)) && AttachmentDataRequested is not null) await AttachmentDataRequested(data);
                    else if (data.Contains(StandardDataFormats.Text)) Prompt.SelectedText = await data.GetTextAsync();
                }
                catch (Exception) { Notice(DesktopResources.Get("ClipboardUnavailable"), InfoBarSeverity.Warning); }
            });
            Item("ImageSelectAll", "\uE8B3", () => { Prompt.SelectAll(); return Task.CompletedTask; }); menu.Items.Add(new MenuFlyoutSeparator());
        }
        Item("Attachments", "\uE723", async () => { if (PickRequested is not null) await PickRequested(); });
        Item("Link", "\uE71B", async () => { if (LinkRequested is not null) await LinkRequested(); });
        return menu;
    }
    private void ResizeGrid()
    {
        var width = Math.Max(0, ActualWidth - 48); Header.Width = width; PromptPanel.Width = Math.Min(1056, width);
        if (LibraryGrid.ItemsPanelRoot is ItemsWrapGrid panel)
        {
            var columns = Math.Clamp((int)(width / 180), 1, 5); panel.MaximumRowsOrColumns = columns;
            panel.ItemWidth = panel.ItemHeight = Math.Max(48, width / columns);
        }
    }
    private void DragOverFiles(object sender, DragEventArgs args)
    {
        args.Handled = true;
        var accepts = editable && args.DataView.Contains(StandardDataFormats.StorageItems) && args.AllowedOperations.HasFlag(DataPackageOperation.Copy);
        args.AcceptedOperation = accepts ? DataPackageOperation.Copy : DataPackageOperation.None;
        args.DragUIOverride.Caption = accepts ? DesktopResources.Get("ImageAttachFiles") : "";
        DropOutline.Visibility = accepts ? Visibility.Visible : Visibility.Collapsed;
    }
    private async void DropFiles(object sender, DragEventArgs args)
    {
        args.Handled = true; DropOutline.Visibility = Visibility.Collapsed;
        if (!editable || !args.DataView.Contains(StandardDataFormats.StorageItems) || !args.AllowedOperations.HasFlag(DataPackageOperation.Copy)) { args.AcceptedOperation = DataPackageOperation.None; return; }
        var deferral = args.GetDeferral();
        try { args.AcceptedOperation = DataPackageOperation.Copy; if (AttachmentDataRequested is not null) await AttachmentDataRequested(args.DataView); }
        finally { deferral.Complete(); }
    }
    public void AdmitAttachment(ComposerAttachment file)
    {
        ImageAttachments.Validate(file);
        if (file.IsLink) attachments.RemoveAll(a => a.RemoteUrl == file.RemoteUrl);
        if (attachments.Count >= 20 || attachments.Sum(a => (long)a.Content.Length) + file.Content.Length > 100L * 1024 * 1024)
            throw new InvalidOperationException(DesktopResources.Get("ImageAttachmentTotalLimit"));
        attachments.Add(file); RenderTags();
    }
    private void RenderTags()
    {
        Tags.Children.Clear();
        foreach (var file in attachments)
        {
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            row.Children.Add(new FontIcon { Glyph = file.IsLink ? "\uE71B" : "\uEB9F", FontSize = 14 });
            var text = new TextBlock { Text = file.Name, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center }; Grid.SetColumn(text, 1); row.Children.Add(text);
            var remove = new Button { Name = "ImageRemoveAttachment", Content = new FontIcon { Glyph = "\uE711", FontSize = 10 }, IsEnabled = editable, Width = 28, Height = 28, Padding = new Thickness(0) };
            ToolbarControls.Subtle(remove); ToolbarControls.Label(remove, DesktopResources.Format("RemoveContext", file.Name));
            remove.Click += (_, _) => { if (editable) { attachments.Remove(file); RenderTags(); } };
            Grid.SetColumn(remove, 2); row.Children.Add(remove);
            var badge = new Border { Name = "ImageAttachmentTag", Child = row, CornerRadius = new CornerRadius(16), Padding = new Thickness(12, 2, 4, 2) }; ControlAppearance.TokenBadge(badge); Tags.Children.Add(badge);
            ToolTipService.SetToolTip(badge, file.RemoteUrl ?? file.Name);
        }
        TagScroll.Visibility = attachments.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }
    public void ClearDraft() { Prompt.Text = ""; attachments.Clear(); RenderTags(); }
    public void SetModelAvailable(bool value) { hasModel = value; NoModels.Visibility = value ? Visibility.Collapsed : Visibility.Visible; UpdateSend(); }
    public void SetBusy(bool value, bool inference)
    {
        editable = !value; Prompt.IsReadOnly = value; AddAttachment.IsEnabled = Settings.IsEnabled = OpenFolder.IsEnabled = !value;
        Stop.Visibility = value && inference ? Visibility.Visible : Visibility.Collapsed; Send.Visibility = value && inference ? Visibility.Collapsed : Visibility.Visible;
        DropOutline.Visibility = Visibility.Collapsed; RenderTags(); UpdateSend();
    }
    private void UpdateSend() => Send.IsEnabled = editable && hasModel && !string.IsNullOrWhiteSpace(Prompt.Text);
    public void SetItems(IReadOnlyList<LibraryImage> images, int pending = 0)
    {
        tiles.Clear(); for (var i = 0; i < pending; i++) tiles.Add(new(null)); foreach (var image in images) tiles.Add(new(image));
        EmptyLibrary.Visibility = tiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed; ResizeGrid();
    }
    public void ClearNotices() => Notices.Children.Clear();
    public void Notice(string message, InfoBarSeverity severity)
    {
        var bar = new InfoBar { IsOpen = true, IsClosable = true, Message = message, Severity = severity };
        bar.Closed += (_, _) => Notices.Children.Remove(bar); Notices.Children.Add(bar);
    }
    public void FocusPrompt() => Prompt.Focus(FocusState.Programmatic);
}
