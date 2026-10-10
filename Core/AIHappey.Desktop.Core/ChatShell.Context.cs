using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace AIHappey.Desktop.Core;

public sealed partial class ChatShell
{
    private readonly Button addContext = new() { Name = "AddContext", Content = new SymbolIcon(Symbol.Add), Width = 40, Height = 40, Padding = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Left };
    private readonly MessageFooterPanel contextTags = new() { Name = "ContextTags" };
    private readonly ScrollViewer contextTagScroll = new() { Name = "ContextTagScroll", MaxHeight = 128, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Disabled, Visibility = Visibility.Collapsed };
    private readonly List<ComposerAttachment> contextAttachments = [];
    private readonly DocumentTextExtraction documentExtractor = new();
    // Never share this client with the authenticated gateway client, including cookies or redirects.
    private readonly HttpClient contextHttp = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = Timeout.InfiniteTimeSpan };
    private UrlAttachmentDialog? linkDialog;
    private int contextVersion;

    private void PrepareContext()
    {
        ToolbarControls.Subtle(addContext); ToolbarControls.Label(addContext, DesktopResources.Get("AddContext"));
        contextTagScroll.Content = contextTags;
        var menu = new MenuFlyout { Placement = FlyoutPlacementMode.TopEdgeAlignedLeft };
        var files = new MenuFlyoutItem { Text = DesktopResources.Get("Attachments"), Icon = new FontIcon { Glyph = "\uE723" } };
        var link = new MenuFlyoutItem { Text = DesktopResources.Get("Link"), Icon = new FontIcon { Glyph = "\uE71B" } };
        foreach (var item in new[] { files, link, selectResources, selectPrompts, manageMcp }) { ControlAppearance.Native(item); menu.Items.Add(item); }
        files.Click += async (_, _) => await PickAttachmentsAsync();
        link.Click += async (_, _) => await AddLinkAsync();
        selectResources.Click += async (_, _) => await SelectResourcesAsync();
        selectPrompts.Click += async (_, _) => await SelectPromptsAsync();
        menu.Opening += (_, _) =>
        {
            UpdateResourceMenu();
            UpdatePromptMenu();
            var palette = ControlAppearance.Palette(this);
            var style = new Style(typeof(MenuFlyoutPresenter));
            style.Setters.Add(new Setter(FrameworkElement.RequestedThemeProperty, ActualTheme));
            style.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(palette.Panel)));
            style.Setters.Add(new Setter(Control.ForegroundProperty, new SolidColorBrush(palette.Text)));
            style.Setters.Add(new Setter(Control.BorderBrushProperty, new SolidColorBrush(palette.Stroke)));
            style.Setters.Add(new Setter(Control.CornerRadiusProperty, new CornerRadius(8)));
            menu.MenuFlyoutPresenterStyle = style;
            foreach (var item in menu.Items.OfType<MenuFlyoutItem>()) item.RequestedTheme = ActualTheme;
        };
        menu.Opened += (_, _) => { foreach (var item in menu.Items.OfType<MenuFlyoutItem>()) ControlAppearance.Refresh(item); };
        ActualThemeChanged += (_, _) => menu.Hide();
        addContext.Flyout = menu;
    }

    private void ResetContext()
    {
        ResetFileDrop();
        contextVersion++; contextAttachments.Clear(); selectedResources.Clear();
        linkDialog?.Hide(); resourcesDialog?.Hide(); promptsDialog?.Hide(); RenderContextTags();
    }

    private void AddContextAttachment(ComposerAttachment attachment)
    {
        // Same URL is one context tag; local files with the same name may be different contents.
        if (attachment.IsLink) contextAttachments.RemoveAll(file => file.RemoteUrl == attachment.RemoteUrl);
        contextAttachments.Add(attachment); RenderContextTags();
    }

    private void RenderContextTags()
    {
        contextTags.Children.Clear();
        foreach (var file in contextAttachments)
        {
            var content = new Grid { ColumnSpacing = 6, MaxWidth = 320 };
            content.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            content.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            content.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            content.Children.Add(new FontIcon { Glyph = file.IsLink ? "\uE71B" : "\uE723", FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
            var label = new TextBlock { Text = file.Name, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(label, 1); content.Children.Add(label);
            var remove = new Button { Name = "RemoveContext", Content = new FontIcon { Glyph = "\uE711", FontSize = 10 }, Width = 24, Height = 24, Padding = new Thickness(0), IsEnabled = !busy };
            ToolbarControls.Subtle(remove); ToolbarControls.Label(remove, DesktopResources.Format("RemoveContext", file.Name));
            remove.Click += (_, _) =>
            {
                if (busy || closing) return;
                contextAttachments.Remove(file); RenderContextTags(); input.Focus(FocusState.Programmatic);
            };
            Grid.SetColumn(remove, 2); content.Children.Add(remove);
            var tag = new Border { Name = "ContextTag", Child = content, Padding = new Thickness(10, 2, 4, 2), CornerRadius = new CornerRadius(16) };
            ControlAppearance.TokenBadge(tag); ToolbarControls.Label(tag, file.Name + " · " + file.MediaType + (file.IsLink ? "\n" + file.RemoteUrl : ""));
            contextTags.Children.Add(tag);
        }
        RenderResourceTags();
        RenderSkillTags();
        contextTagScroll.Visibility = contextTags.Children.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private async Task PickAttachmentsAsync()
    {
        if (!CanAddContext) return;
        var version = contextVersion;
        var partition = session.HistoryPartition;
        await RunAsync(async ct =>
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, Microsoft.UI.Win32Interop.GetWindowFromWindowId(XamlRoot.ContentIslandEnvironment.AppWindowId));
            var selected = await picker.PickMultipleFilesAsync().AsTask(ct);
            await AdmitStorageItemsAsync(selected, version, partition, ct);
        });
        if (!closing) input.Focus(FocusState.Programmatic);
    }

    private bool IsCurrentContext(int version, string partition) => !closing && version == contextVersion
        && partition == session.HistoryPartition && activePage == DesktopPage.Chat;

    private async Task AdmitStorageItemsAsync(IEnumerable<IStorageItem> items, int version, string partition, CancellationToken ct)
    {
        var snapshot = items.ToArray();
        var rejected = snapshot.Where(item => item is not StorageFile).Select(item => item.Name).ToList();
        rejected.AddRange(await ComposerAttachments.AdmitAsync(snapshot.OfType<StorageFile>(), file => file.Name,
            async (file, token) =>
            {
                var properties = await file.GetBasicPropertiesAsync().AsTask(token);
                ComposerAttachments.ValidateSize((long)properties.Size);
                await using var stream = await file.OpenStreamForReadAsync();
                return await ComposerAttachments.ReadAsync(file.Name, file.ContentType, stream, token);
            }, AddContextAttachment, () => IsCurrentContext(version, partition), ct));
        if (IsCurrentContext(version, partition) && rejected.Count > 0)
            Show(DesktopResources.Format("RejectedFiles", string.Join(", ", rejected)), InfoBarSeverity.Warning);
    }

    private async Task AddLinkAsync()
    {
        if (busy || closing || historyDialogOpen || catalogDialog is not null || linkDialog is not null) return;
        var version = contextVersion; var partition = session.HistoryPartition;
        var dialog = new UrlAttachmentDialog((value, ct) => UrlAttachments.ResolveMediaTypeAsync(value, contextHttp, ct)) { XamlRoot = XamlRoot };
        SystemAppearance.PrepareDialog(dialog); linkDialog = dialog; historyDialogOpen = true;
        try
        {
            if (await dialog.ShowAsync() == ContentDialogResult.Primary && !closing && version == contextVersion
                && partition == session.HistoryPartition && dialog.Attachment is { } file) AddContextAttachment(file);
        }
        finally { linkDialog = null; historyDialogOpen = false; if (!closing) input.Focus(FocusState.Programmatic); }
    }
}
