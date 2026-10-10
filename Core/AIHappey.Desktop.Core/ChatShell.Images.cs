using FluentIcons.Common;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.System;

namespace AIHappey.Desktop.Core;

public sealed partial class ChatShell
{
    private readonly ImagesPage imagesPage = new() { Visibility = Visibility.Collapsed };
    private readonly AutoSuggestBox imageTarget = new() { Name = "ImageModelPicker", PlaceholderText = DesktopResources.Get("SelectModel"),
        MinWidth = 120, MaxWidth = 420, Height = 40, Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Stretch };
    private IReadOnlyList<ChatTarget> imageModels = [];
    private IReadOnlyList<LibraryImage> libraryImages = [];
    private string? imagePartition;
    private ImageLibraryStore? imageLibrary;
    private ImageSettingsDialog? imageSettingsDialog;
    private ImagePreviewDialog? imagePreviewDialog;
    private UrlAttachmentDialog? imageLinkDialog;
    private int pendingImages;
    private string ImageMaskDirectory => Path.Combine(session.DataDirectory, "image-masks", ImageLibraryStore.Partition(session));
    private bool CanUseImages => initialized && !busy && !closing && !downloading && !historyDialogOpen && catalogDialog is null && activePage == DesktopPage.Images;
    private ChatTarget? SelectedImageModel => imageModels.FirstOrDefault(m => m.Id == imageTarget.Text.Trim());

    private void PrepareImages()
    {
        ToolbarControls.Outline(imageTarget); ToolbarControls.Label(imageTarget, DesktopResources.Get("SelectModel"));
        imageTarget.QueryIcon = DesktopIcons.Create(Icon.ChevronDown, 12);
        imageTarget.TextChanged += (_, args) => { if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput) ImageModelSuggestions(imageTarget.Text); imagesPage.SetModelAvailable(SelectedImageModel is not null); };
        imageTarget.GotFocus += (_, _) => { ImageModelSuggestions(); imageTarget.IsSuggestionListOpen = true; };
        imageTarget.SuggestionChosen += (_, args) => { imageTarget.Text = ((ChatTarget)args.SelectedItem).Id; imagesPage.SetModelAvailable(SelectedImageModel is not null); };
        imageTarget.QuerySubmitted += (_, args) =>
        { if (args.ChosenSuggestion is ChatTarget model) imageTarget.Text = model.Id; else { ImageModelSuggestions(imageTarget.Text); imageTarget.IsSuggestionListOpen = true; } };
        imagesPage.SendRequested = SendImagesAsync;
        imagesPage.StopRequested = () => operation?.Cancel();
        imagesPage.PickRequested = PickImageAttachmentsAsync;
        imagesPage.LinkRequested = AddImageLinkAsync;
        imagesPage.SettingsRequested = EditImageSettingsAsync;
        imagesPage.FolderRequested = OpenImagesFolderAsync;
        imagesPage.PreviewRequested = PreviewImageAsync;
        imagesPage.AttachmentDataRequested = AdmitImageDataAsync;
    }
    private void ImageModelSuggestions(string query = "") => imageTarget.ItemsSource = imageModels.Where(m => string.IsNullOrWhiteSpace(query)
        || m.Id.Contains(query, StringComparison.OrdinalIgnoreCase) || m.Label.Contains(query, StringComparison.OrdinalIgnoreCase)).Take(100).ToArray();
    private void InvalidateImages()
    {
        imageModels = []; imageTarget.Text = ""; imageTarget.ItemsSource = null;
        libraryImages = []; imageLibrary = null; imagePartition = null; pendingImages = 0;
        imagesPage.ClearDraft(); imagesPage.ClearNotices(); imagesPage.SetItems([]); imagesPage.SetModelAvailable(false);
    }
    private async Task LoadImagesAsync(CancellationToken ct, bool useCache = false)
    {
        var partition = ImageLibraryStore.Partition(session);
        if (imagePartition != partition) { InvalidateImages(); imagePartition = partition; }
        // Load the on-disk library first; it remains usable if model discovery fails/offline.
        await ReloadImageLibraryAsync(ct);
        var available = useCache && aiModelTargets is not null ? aiModelTargets : await client.ListAsync(ServiceKind.Ai, ct);
        ct.ThrowIfCancellationRequested(); if (closing || partition != ImageLibraryStore.Partition(session)) return;
        aiModelTargets = available; imageModels = AiModelCatalog.OfType(available, "image");
        if (SelectedImageModel is null) imageTarget.Text = imageModels.FirstOrDefault(m => m.Id == session.Settings.AiModels.ImageModel)?.Id ?? imageModels.FirstOrDefault()?.Id ?? "";
        ImageModelSuggestions(); imagesPage.SetModelAvailable(SelectedImageModel is not null);
    }
    private async Task ReloadImageLibraryAsync(CancellationToken ct)
    {
        imageLibrary = new(session.Settings.Images.EffectiveRoot, ImageLibraryStore.Partition(session));
        libraryImages = await imageLibrary.ListAsync(ct);
        imagesPage.SetItems(libraryImages, pendingImages);
    }
    private async Task SendImagesAsync()
    {
        if (!CanUseImages || SelectedImageModel is not { } model || string.IsNullOrWhiteSpace(imagesPage.PromptText)) return;
        var prompt = imagesPage.PromptText; var files = imagesPage.Attachments; var preferences = session.Settings.Images.Clone();
        var partition = ImageLibraryStore.Partition(session); var store = new ImageLibraryStore(preferences.EffectiveRoot, partition);
        imagesPage.ClearNotices();
        await RunAsync(async ct =>
        {
            preferences.Validate(); Directory.CreateDirectory(store.Folder);
            ComposerAttachment? mask = null;
            if (preferences.MaskPath is { } path && Path.GetFullPath(path).StartsWith(Path.GetFullPath(ImageMaskDirectory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                await using var stream = File.OpenRead(path); mask = await ComposerAttachments.ReadAsync(Path.GetFileName(path), UrlAttachments.TypeFromPath(path), stream, ct); ImageAttachments.Validate(mask);
            }
            pendingImages = preferences.N; imagesPage.SetItems(libraryImages, pendingImages);
            try
            {
                await new DesktopImageClient(client, http).GenerateAsync(model, prompt, preferences, files, mask, async batch =>
                {
                    // Capture the store/settings before inference. Never save into a later account/root.
                    await store.SaveAsync(batch, files, mask);
                    pendingImages = Math.Max(0, pendingImages - (batch.Request.N ?? batch.Images.Count));
                    libraryImages = await store.ListAsync();
                    if (!closing && partition == ImageLibraryStore.Partition(session))
                    {
                        imagesPage.SetItems(libraryImages, pendingImages);
                        foreach (var warning in batch.Warnings) imagesPage.Notice(warning, InfoBarSeverity.Warning);
                    }
                }, ct);
            }
            finally { pendingImages = 0; if (!closing) imagesPage.SetItems(libraryImages); }
            // Like the browser image page, retain the prompt and inputs for refinement/reuse.
        }, inference: true);
    }
    private async Task PickImageAttachmentsAsync()
    {
        if (!CanUseImages) return;
        var partition = ImageLibraryStore.Partition(session);
        await RunAsync(async ct =>
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
            foreach (var extension in ImageAttachments.Extensions) picker.FileTypeFilter.Add(extension);
            InitializeImagePicker(picker);
            var files = await picker.PickMultipleFilesAsync().AsTask(ct); await AdmitImageFilesAsync(files, partition, ct);
        });
        if (!closing) imagesPage.FocusPrompt();
    }
    private async Task AdmitImageFilesAsync(IEnumerable<IStorageItem> items, string partition, CancellationToken ct)
    {
        var snapshot = items.ToArray(); var rejected = snapshot.Where(i => i is not StorageFile).Select(i => i.Name).ToList();
        rejected.AddRange(await ComposerAttachments.AdmitAsync(snapshot.OfType<StorageFile>(), f => f.Name, async (f, token) =>
        {
            var size = await f.GetBasicPropertiesAsync().AsTask(token); ComposerAttachments.ValidateSize((long)size.Size);
            await using var stream = await f.OpenStreamForReadAsync(); var file = await ComposerAttachments.ReadAsync(f.Name, f.ContentType, stream, token); ImageAttachments.Validate(file); return file;
        }, imagesPage.AdmitAttachment, () => !closing && activePage == DesktopPage.Images && partition == ImageLibraryStore.Partition(session), ct));
        if (rejected.Count > 0) imagesPage.Notice(DesktopResources.Format("RejectedFiles", string.Join(", ", rejected)), InfoBarSeverity.Warning);
    }
    private async Task AdmitImageDataAsync(DataPackageView data)
    {
        if (!CanUseImages) return;
        var partition = ImageLibraryStore.Partition(session);
        await RunAsync(async ct =>
        {
            if (data.Contains(StandardDataFormats.StorageItems)) await AdmitImageFilesAsync(await data.GetStorageItemsAsync().AsTask(ct), partition, ct);
            else if (data.Contains(StandardDataFormats.Bitmap))
            {
                var reference = await data.GetBitmapAsync().AsTask(ct); using var source = await reference.OpenReadAsync().AsTask(ct);
                var decoder = await BitmapDecoder.CreateAsync(source).AsTask(ct);
                if ((long)decoder.PixelWidth * decoder.PixelHeight > 32_000_000) throw new InvalidOperationException(DesktopResources.Get("AttachmentLimit"));
                using var bitmap = await decoder.GetSoftwareBitmapAsync().AsTask(ct); using var buffer = new InMemoryRandomAccessStream();
                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, buffer).AsTask(ct); encoder.SetSoftwareBitmap(bitmap); await encoder.FlushAsync().AsTask(ct);
                using var stream = buffer.AsStreamForRead(); var file = await ComposerAttachments.ReadAsync("clipboard.png", "image/png", stream, ct);
                if (!closing && partition == ImageLibraryStore.Partition(session)) imagesPage.AdmitAttachment(file);
            }
        });
        if (!closing) imagesPage.FocusPrompt();
    }
    private async Task AddImageLinkAsync()
    {
        if (!CanUseImages) return;
        var partition = ImageLibraryStore.Partition(session);
        var dialog = new UrlAttachmentDialog(async (value, ct) =>
        {
            var type = await UrlAttachments.ResolveMediaTypeAsync(value, contextHttp, ct);
            return type is not null && ImageAttachments.IsImage(type) ? type : null;
        }, ["image/png", "image/jpeg", "image/webp", "image/gif", "image/bmp", "image/tiff"]) { XamlRoot = XamlRoot };
        SystemAppearance.PrepareDialog(dialog); imageLinkDialog = dialog; historyDialogOpen = true;
        try
        {
            if (await dialog.ShowAsync() == ContentDialogResult.Primary && !closing && partition == ImageLibraryStore.Partition(session) && dialog.Attachment is { } file)
            {
                try { imagesPage.AdmitAttachment(file); }
                catch (InvalidOperationException error) { imagesPage.Notice(error.Message, InfoBarSeverity.Warning); }
            }
        }
        finally { imageLinkDialog = null; historyDialogOpen = false; if (!closing) imagesPage.FocusPrompt(); }
    }
    private async Task EditImageSettingsAsync()
    {
        if (!CanUseImages) return;
        var partition = ImageLibraryStore.Partition(session); var preferences = session.Settings.Images.Clone();
        if (preferences.MaskPath is { } path && !Path.GetFullPath(path).StartsWith(Path.GetFullPath(ImageMaskDirectory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) preferences.MaskPath = null;
        var dialog = new ImageSettingsDialog(preferences, SelectedImageModel?.ProviderKey, ImageMaskDirectory) { XamlRoot = XamlRoot };
        dialog.SaveAsync = async next =>
        {
            if (closing || partition != ImageLibraryStore.Partition(session)) return;
            var settings = session.Settings.Clone(); settings.Images = next; await SettingsStore.SaveAsync(session.DataDirectory, settings); session.Settings = settings;
        };
        SystemAppearance.PrepareDialog(dialog); imageSettingsDialog = dialog; historyDialogOpen = true;
        try { await dialog.ShowAsync(); }
        finally { imageSettingsDialog = null; historyDialogOpen = false; }
        if (!closing) await RunAsync(ct => LoadImagesAsync(ct, useCache: true));
    }
    private async Task OpenImagesFolderAsync()
    {
        if (!CanUseImages) return;
        await RunAsync(async ct =>
        {
            var store = new ImageLibraryStore(session.Settings.Images.EffectiveRoot, ImageLibraryStore.Partition(session));
            Directory.CreateDirectory(store.Folder); var folder = await StorageFolder.GetFolderFromPathAsync(store.Folder).AsTask(ct);
            if (!await Launcher.LaunchFolderAsync(folder).AsTask(ct)) throw new InvalidOperationException(DesktopResources.Get("ImageFolderFailed"));
        });
    }
    private async Task PreviewImageAsync(LibraryImage item)
    {
        if (!CanUseImages || imageLibrary is null) return;
        var store = imageLibrary; var partition = ImageLibraryStore.Partition(session);
        var dialog = new ImagePreviewDialog(item) { XamlRoot = XamlRoot }; SystemAppearance.PrepareDialog(dialog);
        imagePreviewDialog = dialog; historyDialogOpen = true;
        try { await dialog.ShowAsync(); }
        finally { imagePreviewDialog = null; historyDialogOpen = false; }
        if (closing || partition != ImageLibraryStore.Partition(session)) return;
        if (dialog.Action == ImagePreviewAction.Delete)
        {
            var confirmation = new ContentDialog { XamlRoot = XamlRoot, Title = DesktopResources.Get("ImageDeleteTitle"), Content = DesktopResources.Get("ImageDeleteHint"),
                PrimaryButtonText = DesktopResources.Get("Delete"), CloseButtonText = DesktopResources.Get("Cancel") };
            SystemAppearance.PrepareDialog(confirmation); historyDialogOpen = true;
            ContentDialogResult result;
            try { result = await confirmation.ShowAsync(); } finally { historyDialogOpen = false; }
            if (result != ContentDialogResult.Primary || closing) return;
            await RunAsync(async ct => { await store.DeleteAsync(item); libraryImages = await store.ListAsync(ct); imagesPage.SetItems(libraryImages); });
        }
        else if (dialog.Action == ImagePreviewAction.AddToPrompt)
        {
            await RunAsync(async ct => { await using var stream = File.OpenRead(item.Path); imagesPage.AdmitAttachment(await ComposerAttachments.ReadAsync(Path.GetFileName(item.Path), item.Output.MediaType, stream, ct)); });
            imagesPage.FocusPrompt();
        }
        else if (dialog.Action == ImagePreviewAction.Save)
            await RunAsync(async ct =>
            {
                var extension = ImageAttachments.Extension(item.Output.MediaType);
                var picker = new FileSavePicker { SuggestedFileName = "image-" + item.Generation.CreatedAt.ToString("yyyyMMdd-HHmmss") };
                picker.FileTypeChoices.Add(DesktopResources.Get("Images"), new List<string> { extension }); InitializeImagePicker(picker);
                var destination = await picker.PickSaveFileAsync().AsTask(ct); if (destination is null || closing) return;
                if (string.Equals(destination.Path, item.Path, StringComparison.OrdinalIgnoreCase)) return;
                await using var input = File.OpenRead(item.Path); await using var output = await destination.OpenStreamForWriteAsync(); output.SetLength(0); await input.CopyToAsync(output, ct);
            });
    }
    private void InitializeImagePicker(object picker) => WinRT.Interop.InitializeWithWindow.Initialize(picker, Microsoft.UI.Win32Interop.GetWindowFromWindowId(XamlRoot.ContentIslandEnvironment.AppWindowId));
}
