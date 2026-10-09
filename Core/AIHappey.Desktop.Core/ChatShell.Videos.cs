using System.Text.Json;
using System.Text.Json.Nodes;
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
    private readonly VideosPage videosPage = new() { Visibility = Visibility.Collapsed };
    private readonly AutoSuggestBox videoTarget = new() { Name = "VideoModelPicker", PlaceholderText = DesktopResources.Get("SelectModel"), MinWidth = 120, MaxWidth = 420, Height = 40, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly StackPanel videoModelToolbar = new() { Name = "VideoModelToolbar", Orientation = Orientation.Horizontal, Spacing = 8, Visibility = Visibility.Collapsed };
    private readonly Button videoFavorite = new() { Name = "VideoModelFavorite", Content = new SymbolIcon(Symbol.OutlineStar), Width = 40, Height = 40, Padding = new Thickness(0) };
    private IReadOnlyList<ChatTarget> videoModels = [];
    private string? videoPartition;
    private VideoJobCoordinator? videoJobs;
    private Task videoSubmission = Task.CompletedTask;
    private readonly CancellationTokenSource videoWorkLifetime = new();
    private bool videoSubmitting, videoRefreshing;
    private readonly HashSet<string> videoNotices = [];
    private VideoSettingsDialog? videoSettingsDialog;
    private VideoPreviewDialog? videoPreviewDialog;
    private UrlAttachmentDialog? videoLinkDialog;
    private ContentDialog? videoDeleteDialog;
    private VideoInputStore VideoInputs => new(Path.Combine(session.DataDirectory, "video-inputs", ImageLibraryStore.Partition(session)));
    private bool CanUseVideos => initialized && !busy && !closing && !downloading && !historyDialogOpen && catalogDialog is null && activePage == DesktopPage.Videos;
    private ChatTarget? SelectedVideoModel => videoModels.FirstOrDefault(m => m.Id == videoTarget.Text.Trim());
    private void PrepareVideos()
    {
        ToolbarControls.Outline(videoTarget); ToolbarControls.Label(videoTarget, DesktopResources.Get("SelectModel")); ToolbarControls.Subtle(videoFavorite);
        videoModelToolbar.Children.Add(videoTarget); videoModelToolbar.Children.Add(videoFavorite);
        videoTarget.QueryIcon = new FontIcon { Glyph = "\uE70D", FontSize = 12 };
        videoTarget.TextChanged += (_, args) => { if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput) VideoModelSuggestions(videoTarget.Text); videosPage.SetModelAvailable(SelectedVideoModel is not null); UpdateVideoFavorite(); };
        videoTarget.GotFocus += (_, _) => { VideoModelSuggestions(); videoTarget.IsSuggestionListOpen = true; };
        videoTarget.SuggestionChosen += (_, args) => videoTarget.Text = ((ChatTarget)args.SelectedItem).Id;
        videoTarget.QuerySubmitted += (_, args) => { if (args.ChosenSuggestion is ChatTarget model) videoTarget.Text = model.Id; else { VideoModelSuggestions(videoTarget.Text); videoTarget.IsSuggestionListOpen = true; } };
        videoFavorite.Click += async (_, _) =>
        {
            if (!CanUseVideos || SelectedVideoModel is not { } model) return;
            await RunAsync(async ct =>
            {
                var partition = session.HistoryPartition; var next = new HashSet<string>(modelFavorites, StringComparer.Ordinal);
                var key = ModelOverviewCatalog.FavoriteKey(model); if (!next.Remove(key)) next.Add(key);
                await modelFavoritesStore.SaveAsync(partition, next, ct); if (closing || partition != session.HistoryPartition) return;
                modelFavorites = next; modelFavoritesPartition = partition; modelsOverview.SetFavorites(next); UpdateVideoFavorite();
            });
        };
        videosPage.SendRequested = () => videoSubmission = SendVideosAsync(); videosPage.PickRequested = PickVideoAttachmentsAsync;
        videosPage.LinkRequested = AddVideoLinkAsync; videosPage.AttachmentDataRequested = AdmitVideoDataAsync;
        videosPage.SettingsRequested = EditVideoSettingsAsync; videosPage.FolderRequested = OpenVideosFolderAsync; videosPage.PreviewRequested = PreviewVideoAsync;
    }
    private void VideoModelSuggestions(string query = "") => videoTarget.ItemsSource = videoModels.Where(m => string.IsNullOrWhiteSpace(query)
        || m.Id.Contains(query, StringComparison.OrdinalIgnoreCase) || m.Label.Contains(query, StringComparison.OrdinalIgnoreCase)).Take(100).ToArray();
    private void UpdateVideoFavorite()
    {
        var selected = SelectedVideoModel; var favorite = selected is not null && modelFavorites.Contains(ModelOverviewCatalog.FavoriteKey(selected));
        videoFavorite.Content = new SymbolIcon(favorite ? Symbol.SolidStar : Symbol.OutlineStar);
        ToolbarControls.Label(videoFavorite, DesktopResources.Get(favorite ? "RemoveFavorite" : "AddFavorite")); videoFavorite.IsEnabled = !busy && selected is not null;
    }
    private async Task StartVideoJobsAsync(CancellationToken ct)
    {
        if (closing) return;
        videoJobs ??= new(Path.Combine(session.DataDirectory, "video-jobs"), new DesktopVideoClient(client, http), contextHttp,
            () => ImageLibraryStore.Partition(session), () => session.Settings.Videos.PollingIntervalSeconds);
        videoJobs.Changed -= VideoJobsChanged; videoJobs.Changed += VideoJobsChanged;
        await videoJobs.StartAsync(ct);
    }
    private async Task StopVideoJobsAsync()
    {
        if (videoJobs is not { } coordinator) return; coordinator.Changed -= VideoJobsChanged;
        await coordinator.DisposeAsync(); videoJobs = null;
    }
    private void VideoJobsChanged() => DispatcherQueue.TryEnqueue(async () =>
    {
        if (closing || activePage != DesktopPage.Videos || videoRefreshing) return;
        videoRefreshing = true;
        try { await ReloadVideoLibraryAsync(CancellationToken.None); }
        catch (Exception) { if (!closing) videosPage.Notice(DesktopResources.Get("VideoStorageRetry"), InfoBarSeverity.Warning); }
        finally { videoRefreshing = false; }
    });
    private void InvalidateVideos()
    {
        videoModels = []; videoTarget.Text = ""; videoTarget.ItemsSource = null; videoPartition = null; videoNotices.Clear();
        videosPage.ClearDraft(); videosPage.ClearNotices(); videosPage.SetItems([], []); videosPage.SetModelAvailable(false);
    }
    private async Task LoadVideosAsync(CancellationToken ct, bool useCache = false)
    {
        var partition = ImageLibraryStore.Partition(session); if (videoPartition != partition) { InvalidateVideos(); videoPartition = partition; }
        await StartVideoJobsAsync(ct); await ReloadVideoLibraryAsync(ct);
        var available = useCache && aiModelTargets is not null ? aiModelTargets : await client.ListAsync(ServiceKind.Ai, ct);
        ct.ThrowIfCancellationRequested(); if (closing || partition != ImageLibraryStore.Partition(session)) return;
        aiModelTargets = available; videoModels = AiModelCatalog.OfType(available, "video");
        if (SelectedVideoModel is null) videoTarget.Text = videoModels.FirstOrDefault(m => m.Id == session.Settings.AiModels.VideoModel)?.Id ?? videoModels.FirstOrDefault()?.Id ?? "";
        if (modelFavoritesPartition != session.HistoryPartition) { modelFavorites = await modelFavoritesStore.LoadAsync(session.HistoryPartition, ct); modelFavoritesPartition = session.HistoryPartition; }
        VideoModelSuggestions(); videosPage.SetModelAvailable(SelectedVideoModel is not null); UpdateVideoFavorite();
    }
    private async Task ReloadVideoLibraryAsync(CancellationToken ct)
    {
        var partition = ImageLibraryStore.Partition(session); var root = session.Settings.Videos.EffectiveRoot;
        var saved = await new VideoLibraryStore(root, partition).ListAsync(ct);
        var jobs = videoJobs is null ? [] : await videoJobs.SnapshotAsync(ct);
        if (closing || partition != ImageLibraryStore.Partition(session) || root != session.Settings.Videos.EffectiveRoot) return;
        videosPage.SetItems(saved, jobs);
        foreach (var job in jobs)
        {
            foreach (var warning in job.Warnings) if (videoNotices.Add(job.Id + ":" + warning)) videosPage.Notice(warning, InfoBarSeverity.Warning);
            if (job.Error is { } error && videoNotices.Add(job.Id + ":" + error)) videosPage.Notice(error, job.State is "error" or "uncertain" ? InfoBarSeverity.Error : InfoBarSeverity.Warning);
        }
    }
    private async Task SendVideosAsync()
    {
        if (!CanUseVideos || videoSubmitting || SelectedVideoModel is not { } model || string.IsNullOrWhiteSpace(videosPage.PromptText)) return;
        var prompt = videosPage.PromptText; var attachments = videosPage.Attachments; var preferences = session.Settings.Videos.Clone();
        var partition = ImageLibraryStore.Partition(session); var inputs = VideoInputs; videoSubmitting = true; videosPage.SetSubmitting(true); videosPage.ClearNotices();
        try
        {
            var ct = videoWorkLifetime.Token; preferences.Validate();
            var references = new List<ComposerAttachment>(); foreach (var file in preferences.InputReferences) references.Add(await inputs.ReadAsync(file, ct));
            references.AddRange(attachments.Where(f => f.IsLink));
            var first = preferences.FirstFrame is null ? null : await inputs.ReadAsync(preferences.FirstFrame, ct);
            var last = preferences.LastFrame is null ? null : await inputs.ReadAsync(preferences.LastFrame, ct);
            var requests = preferences.Requests(model, prompt, attachments.FirstOrDefault(f => !f.IsLink), references, first, last);
            if (closing || partition != ImageLibraryStore.Partition(session)) return;
            await StartVideoJobsAsync(ct); var coordinator = videoJobs!;
            await coordinator.SubmitAsync(requests, preferences.EffectiveRoot, ct);
            if (!closing && partition == ImageLibraryStore.Partition(session)) videosPage.ClearAttachments();
            // Polling never owns shell busy state, and continues when this page is hidden.
        }
        catch (OperationCanceledException) { if (!closing) videosPage.Notice(DesktopResources.Get("OperationCanceled"), InfoBarSeverity.Warning); }
        catch (Exception error) { if (!closing && partition == ImageLibraryStore.Partition(session)) videosPage.Notice(error is GatewayException or InvalidOperationException ? error.Message : DesktopResources.Get("VideoGenerationFailed"), InfoBarSeverity.Error); }
        finally
        {
            videoSubmitting = false; if (!closing) { videosPage.SetSubmitting(false); VideoJobsChanged(); }
        }
    }
    private async Task PickVideoAttachmentsAsync()
    {
        if (!CanUseVideos || videoSubmitting) return; var partition = ImageLibraryStore.Partition(session);
        await RunAsync(async ct =>
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.VideosLibrary };
            foreach (var extension in VideoAttachments.Extensions) picker.FileTypeFilter.Add(extension); InitializeImagePicker(picker);
            var files = await picker.PickMultipleFilesAsync().AsTask(ct); await AdmitVideoFilesAsync(files, partition, ct);
        });
        if (!closing) videosPage.FocusPrompt();
    }
    private async Task AdmitVideoFilesAsync(IEnumerable<IStorageItem> items, string partition, CancellationToken ct)
    {
        var rejected = new List<string>(); var admitted = false;
        foreach (var item in items)
        {
            if (item is not StorageFile file) { rejected.Add(item.Name); continue; }
            try
            {
                var size = await file.GetBasicPropertiesAsync().AsTask(ct); ComposerAttachments.ValidateSize((long)size.Size);
                await using var stream = await file.OpenStreamForReadAsync(); var input = await ComposerAttachments.ReadAsync(file.Name, file.ContentType, stream, ct);
                if (!VideoAttachments.IsInput(input.MediaType)) throw new InvalidOperationException(); VideoAttachments.Validate(input);
                if (!closing && partition == ImageLibraryStore.Partition(session)) { videosPage.AdmitAttachment(input); admitted = true; }
            }
            catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException) { rejected.Add(file.Name); }
            if (admitted) break; // Browser composer accepts the first valid local image/video only.
        }
        if (rejected.Count > 0 && !closing) videosPage.Notice(DesktopResources.Format("RejectedFiles", string.Join(", ", rejected)), InfoBarSeverity.Warning);
    }
    private async Task AdmitVideoDataAsync(DataPackageView data)
    {
        if (!CanUseVideos || videoSubmitting) return; var partition = ImageLibraryStore.Partition(session);
        await RunAsync(async ct =>
        {
            if (data.Contains(StandardDataFormats.StorageItems)) await AdmitVideoFilesAsync(await data.GetStorageItemsAsync().AsTask(ct), partition, ct);
            else if (data.Contains(StandardDataFormats.Bitmap))
            {
                var reference = await data.GetBitmapAsync().AsTask(ct); using var source = await reference.OpenReadAsync().AsTask(ct);
                var decoder = await BitmapDecoder.CreateAsync(source).AsTask(ct);
                if ((long)decoder.PixelWidth * decoder.PixelHeight > 32_000_000) throw new InvalidOperationException(DesktopResources.Get("AttachmentLimit"));
                using var bitmap = await decoder.GetSoftwareBitmapAsync().AsTask(ct); using var buffer = new InMemoryRandomAccessStream();
                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, buffer).AsTask(ct); encoder.SetSoftwareBitmap(bitmap); await encoder.FlushAsync().AsTask(ct);
                using var stream = buffer.AsStreamForRead(); var file = await ComposerAttachments.ReadAsync("clipboard.png", "image/png", stream, ct);
                if (!closing && partition == ImageLibraryStore.Partition(session)) videosPage.AdmitAttachment(file);
            }
        });
        if (!closing) videosPage.FocusPrompt();
    }
    private async Task AddVideoLinkAsync()
    {
        if (!CanUseVideos || videoSubmitting) return; var partition = ImageLibraryStore.Partition(session);
        var dialog = new UrlAttachmentDialog(async (value, ct) =>
        { var type = await UrlAttachments.ResolveMediaTypeAsync(value, contextHttp, ct); return type is not null && VideoAttachments.IsInput(type) ? type : null; },
            ["image/png", "image/jpeg", "image/webp", "video/mp4", "video/webm", "video/quicktime"]) { XamlRoot = XamlRoot };
        SystemAppearance.PrepareDialog(dialog); videoLinkDialog = dialog; historyDialogOpen = true;
        try
        {
            if (await dialog.ShowAsync() == ContentDialogResult.Primary && !closing && partition == ImageLibraryStore.Partition(session) && dialog.Attachment is { } file)
                try { videosPage.AdmitAttachment(file); } catch (InvalidOperationException error) { videosPage.Notice(error.Message, InfoBarSeverity.Warning); }
        }
        finally { videoLinkDialog = null; historyDialogOpen = false; if (!closing) videosPage.FocusPrompt(); }
    }
    private async Task EditVideoSettingsAsync()
    {
        if (!CanUseVideos || videoSubmitting) return; var partition = ImageLibraryStore.Partition(session);
        var dialog = new VideoSettingsDialog(session.Settings.Videos, VideoInputs) { XamlRoot = XamlRoot };
        dialog.SaveAsync = async next =>
        { if (closing || partition != ImageLibraryStore.Partition(session)) return; var settings = session.Settings.Clone(); settings.Videos = next; await SettingsStore.SaveAsync(session.DataDirectory, settings); session.Settings = settings; };
        SystemAppearance.PrepareDialog(dialog); videoSettingsDialog = dialog; historyDialogOpen = true;
        try { await dialog.ShowAsync(); } finally { videoSettingsDialog = null; historyDialogOpen = false; }
    }
    private async Task OpenVideosFolderAsync()
    {
        if (!CanUseVideos) return;
        await RunAsync(async ct =>
        {
            var store = new VideoLibraryStore(session.Settings.Videos.EffectiveRoot, ImageLibraryStore.Partition(session)); VideoDisk.Prepare(store.Folder);
            if (!await Launcher.LaunchFolderAsync(await StorageFolder.GetFolderFromPathAsync(store.Folder).AsTask(ct)).AsTask(ct)) throw new InvalidOperationException(DesktopResources.Get("VideoFolderFailed"));
        });
    }
    private async Task PreviewVideoAsync(LibraryVideo item)
    {
        if (!CanUseVideos) return; var partition = ImageLibraryStore.Partition(session); var store = new VideoLibraryStore(session.Settings.Videos.EffectiveRoot, partition);
        var dialog = new VideoPreviewDialog(item) { XamlRoot = XamlRoot }; SystemAppearance.PrepareDialog(dialog); videoPreviewDialog = dialog; historyDialogOpen = true;
        try { await dialog.ShowAsync(); } finally { dialog.ReleasePlayer(); videoPreviewDialog = null; historyDialogOpen = false; }
        if (closing || partition != ImageLibraryStore.Partition(session)) return;
        if (dialog.Action == VideoPreviewAction.Delete)
        {
            var confirmation = new ContentDialog { XamlRoot = XamlRoot, Title = DesktopResources.Get("VideoDeleteTitle"), Content = DesktopResources.Get("VideoDeleteHint"), PrimaryButtonText = DesktopResources.Get("Delete"), CloseButtonText = DesktopResources.Get("Cancel") };
            SystemAppearance.PrepareDialog(confirmation); videoDeleteDialog = confirmation; historyDialogOpen = true; ContentDialogResult result;
            try { result = await confirmation.ShowAsync(); } finally { videoDeleteDialog = null; historyDialogOpen = false; }
            if (result == ContentDialogResult.Primary && !closing) await RunAsync(async ct => { await store.DeleteAsync(item); await ReloadVideoLibraryAsync(ct); });
        }
        else if (dialog.Action == VideoPreviewAction.Save)
            await RunAsync(async ct =>
            {
                var picker = new FileSavePicker { SuggestedFileName = "video-" + item.Generation.CreatedAt.ToString("yyyyMMdd-HHmmss") };
                picker.FileTypeChoices.Add(DesktopResources.Get("Videos"), new List<string> { VideoAttachments.Extension(item.Output.MediaType) }); InitializeImagePicker(picker);
                var destination = await picker.PickSaveFileAsync().AsTask(ct); if (destination is null || closing || string.Equals(destination.Path, item.Path, StringComparison.OrdinalIgnoreCase)) return;
                VideoDisk.CheckPath(item.Path); await using var source = File.OpenRead(item.Path); await using var output = await destination.OpenStreamForWriteAsync(); output.SetLength(0); await source.CopyToAsync(output, ct);
            });
    }
}
