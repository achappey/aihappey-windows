using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;

namespace AIHappey.Desktop.Core;

public sealed partial class ChatShell
{
    private readonly TranscriptionsPage transcriptionsPage = new() { Visibility = Visibility.Collapsed };
    private readonly AutoSuggestBox transcriptionTarget = new() { Name = "TranscriptionModelPicker", PlaceholderText = DesktopResources.Get("SelectModel"), MinWidth = 120, MaxWidth = 420, Height = 40, Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Stretch };
    private IReadOnlyList<ChatTarget> transcriptionModels = [];
    private IReadOnlyList<LibraryTranscription> libraryTranscriptions = [];
    private string? transcriptionPartition;
    private TranscriptionLibraryStore? transcriptionLibrary;
    private TranscriptionSettingsDialog? transcriptionSettingsDialog;
    private TranscriptionDetailsDialog? transcriptionDetailsDialog;
    private ContentDialog? transcriptionDeleteDialog;
    private int pendingTranscriptions;
    private string TranscriptionRoot => Path.Combine(session.DataDirectory, "transcriptions");
    private bool CanUseTranscriptions => initialized && !busy && !closing && !downloading && !historyDialogOpen && catalogDialog is null && activePage == DesktopPage.Transcriptions;
    private ChatTarget? SelectedTranscriptionModel => transcriptionModels.FirstOrDefault(m => m.Id == transcriptionTarget.Text.Trim());
    private void PrepareTranscriptions()
    {
        ToolbarControls.Outline(transcriptionTarget); ToolbarControls.Label(transcriptionTarget, DesktopResources.Get("SelectModel")); transcriptionTarget.QueryIcon = new FontIcon { Glyph = "\uE70D", FontSize = 12 };
        transcriptionTarget.TextChanged += (_, args) => { if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput) TranscriptionModelSuggestions(transcriptionTarget.Text); transcriptionsPage.SetModelAvailable(SelectedTranscriptionModel is not null); };
        transcriptionTarget.GotFocus += (_, _) => { TranscriptionModelSuggestions(); transcriptionTarget.IsSuggestionListOpen = true; };
        transcriptionTarget.SuggestionChosen += (_, args) => transcriptionTarget.Text = ((ChatTarget)args.SelectedItem).Id;
        transcriptionTarget.QuerySubmitted += (_, args) => { if (args.ChosenSuggestion is ChatTarget model) transcriptionTarget.Text = model.Id; else { TranscriptionModelSuggestions(transcriptionTarget.Text); transcriptionTarget.IsSuggestionListOpen = true; } };
        transcriptionsPage.PickRequested = PickTranscriptionFilesAsync; transcriptionsPage.SettingsRequested = EditTranscriptionSettingsAsync;
        transcriptionsPage.StopRequested = () => operation?.Cancel(); transcriptionsPage.FilesDropped = DropTranscriptionFilesAsync;
        transcriptionsPage.FolderRequested = OpenTranscriptionsFolderAsync; transcriptionsPage.ViewRequested = ViewTranscriptionAsync; transcriptionsPage.DeleteRequested = DeleteTranscriptionAsync;
    }
    private void TranscriptionModelSuggestions(string query = "") => transcriptionTarget.ItemsSource = transcriptionModels.Where(m => string.IsNullOrWhiteSpace(query) || m.Id.Contains(query, StringComparison.OrdinalIgnoreCase) || m.Label.Contains(query, StringComparison.OrdinalIgnoreCase)).Take(100).ToArray();
    private void InvalidateTranscriptions()
    {
        transcriptionModels = []; transcriptionTarget.Text = ""; transcriptionTarget.ItemsSource = null; libraryTranscriptions = []; transcriptionLibrary = null; transcriptionPartition = null; pendingTranscriptions = 0;
        transcriptionsPage.ClearNotices(); transcriptionsPage.SetItems([]); transcriptionsPage.SetModelAvailable(false);
    }
    private async Task LoadTranscriptionsAsync(CancellationToken ct, bool useCache = false)
    {
        var partition = TranscriptionLibraryStore.Partition(session);
        if (transcriptionPartition != partition) { InvalidateTranscriptions(); transcriptionPartition = partition; }
        var store = new TranscriptionLibraryStore(TranscriptionRoot, partition);
        var saved = await store.ListAsync(ct);
        if (closing || partition != TranscriptionLibraryStore.Partition(session)) return;
        transcriptionLibrary = store; libraryTranscriptions = saved; transcriptionsPage.SetItems(saved, pendingTranscriptions);
        var available = useCache && aiModelTargets is not null ? aiModelTargets : await client.ListAsync(ServiceKind.Ai, ct);
        ct.ThrowIfCancellationRequested(); if (closing || partition != TranscriptionLibraryStore.Partition(session)) return;
        aiModelTargets = available; transcriptionModels = AiModelCatalog.OfType(available, "transcription");
        if (SelectedTranscriptionModel is null) transcriptionTarget.Text = transcriptionModels.FirstOrDefault(m => m.Id == session.Settings.AiModels.TranscriptionModel)?.Id
            ?? transcriptionModels.FirstOrDefault(m => m.Id == "openai/gpt-transcribe")?.Id ?? transcriptionModels.FirstOrDefault()?.Id ?? "";
        TranscriptionModelSuggestions(); transcriptionsPage.SetModelAvailable(SelectedTranscriptionModel is not null);
    }
    private async Task PickTranscriptionFilesAsync()
    {
        if (!CanUseTranscriptions) return;
        var partition = TranscriptionLibraryStore.Partition(session); IReadOnlyList<StorageFile> files = [];
        await RunAsync(async ct =>
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.MusicLibrary }; foreach (var extension in TranscriptionFiles.Extensions) picker.FileTypeFilter.Add(extension); InitializeImagePicker(picker);
            files = await picker.PickMultipleFilesAsync().AsTask(ct);
        });
        if (CanUseTranscriptions && partition == TranscriptionLibraryStore.Partition(session)) await TranscribeStorageFilesAsync(files);
    }
    private async Task DropTranscriptionFilesAsync(DataPackageView data)
    {
        if (!CanUseTranscriptions || !data.Contains(StandardDataFormats.StorageItems)) return;
        var partition = TranscriptionLibraryStore.Partition(session); IReadOnlyList<IStorageItem> items = [];
        await RunAsync(async ct => items = await data.GetStorageItemsAsync().AsTask(ct));
        if (CanUseTranscriptions && partition == TranscriptionLibraryStore.Partition(session)) await TranscribeStorageFilesAsync(items);
    }
    private async Task TranscribeStorageFilesAsync(IEnumerable<IStorageItem> items)
    {
        if (!CanUseTranscriptions || SelectedTranscriptionModel is not { } model) return;
        var snapshot = items.ToArray(); if (snapshot.Length == 0) return;
        var preferences = session.Settings.Transcriptions.Clone(); var partition = TranscriptionLibraryStore.Partition(session); var store = new TranscriptionLibraryStore(TranscriptionRoot, partition);
        transcriptionsPage.ClearNotices();
        await RunAsync(async ct =>
        {
            pendingTranscriptions = snapshot.Length; transcriptionsPage.SetItems(libraryTranscriptions, pendingTranscriptions);
            try
            {
                foreach (var item in snapshot)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        if (item is not StorageFile file || TranscriptionFiles.MediaType(file.Name, file.ContentType) is not { } type) throw new InvalidOperationException(DesktopResources.Get("TranscriptionUnsupported"));
                        var properties = await file.GetBasicPropertiesAsync().AsTask(ct); ComposerAttachments.ValidateSize((long)properties.Size);
                        await using var input = await file.OpenStreamForReadAsync(); var source = await ComposerAttachments.ReadAsync(file.Name, type, input, ct); TranscriptionFiles.Validate(source);
                        var result = await new DesktopTranscriptionClient(client, http).GenerateAsync(model, source, preferences, ct);
                        // Commit completed results even if Stop was pressed just after the response arrived.
                        await store.SaveAsync(result, source); libraryTranscriptions = await store.ListAsync();
                        if (!closing && partition == TranscriptionLibraryStore.Partition(session) && result.Response["warnings"] is System.Text.Json.Nodes.JsonArray warnings)
                            foreach (var warning in warnings) transcriptionsPage.Notice(warning?.ToJsonString() ?? "", InfoBarSeverity.Warning);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception error) when (error is not OutOfMemoryException)
                    {
                        if (!closing && partition == TranscriptionLibraryStore.Partition(session)) transcriptionsPage.Notice(item.Name + ": " + error.Message, error is InvalidOperationException ? InfoBarSeverity.Warning : InfoBarSeverity.Error);
                    }
                    finally
                    {
                        pendingTranscriptions = Math.Max(0, pendingTranscriptions - 1);
                        if (!closing && partition == TranscriptionLibraryStore.Partition(session)) transcriptionsPage.SetItems(libraryTranscriptions, pendingTranscriptions);
                    }
                }
            }
            finally { pendingTranscriptions = 0; if (!closing && partition == TranscriptionLibraryStore.Partition(session)) transcriptionsPage.SetItems(libraryTranscriptions); }
        }, inference: true);
    }
    private async Task EditTranscriptionSettingsAsync()
    {
        if (!CanUseTranscriptions) return;
        var partition = TranscriptionLibraryStore.Partition(session);
        var providers = transcriptionModels.Select(m => m.ProviderKey ?? m.Id.Split('/')[0]).Prepend(SelectedTranscriptionModel?.ProviderKey ?? SelectedTranscriptionModel?.Id.Split('/')[0] ?? "openai");
        var dialog = new TranscriptionSettingsDialog(session.Settings.Transcriptions, providers) { XamlRoot = XamlRoot };
        dialog.SaveAsync = async next =>
        {
            if (closing || partition != TranscriptionLibraryStore.Partition(session)) return;
            var settings = session.Settings.Clone(); settings.Transcriptions = next; await SettingsStore.SaveAsync(session.DataDirectory, settings); session.Settings = settings;
        };
        SystemAppearance.PrepareDialog(dialog); transcriptionSettingsDialog = dialog; historyDialogOpen = true;
        try { await dialog.ShowAsync(); } finally { transcriptionSettingsDialog = null; historyDialogOpen = false; }
    }
    private async Task OpenTranscriptionsFolderAsync()
    {
        if (!CanUseTranscriptions) return;
        await RunAsync(async ct =>
        {
            var store = new TranscriptionLibraryStore(TranscriptionRoot, TranscriptionLibraryStore.Partition(session)); Directory.CreateDirectory(store.Folder);
            if (!await Launcher.LaunchFolderAsync(await StorageFolder.GetFolderFromPathAsync(store.Folder).AsTask(ct)).AsTask(ct)) throw new InvalidOperationException(DesktopResources.Get("ImageFolderFailed"));
        });
    }
    private async Task DeleteTranscriptionAsync(LibraryTranscription item)
    {
        if (!CanUseTranscriptions || transcriptionLibrary is null) return;
        var store = transcriptionLibrary; var partition = TranscriptionLibraryStore.Partition(session);
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = DesktopResources.Get("TranscriptionDeleteTitle"), Content = DesktopResources.Get("TranscriptionDeleteHint"), PrimaryButtonText = DesktopResources.Get("Delete"), CloseButtonText = DesktopResources.Get("Cancel") };
        SystemAppearance.PrepareDialog(dialog); transcriptionDeleteDialog = dialog; historyDialogOpen = true; ContentDialogResult choice;
        try { choice = await dialog.ShowAsync(); } finally { transcriptionDeleteDialog = null; historyDialogOpen = false; }
        if (choice != ContentDialogResult.Primary || closing || partition != TranscriptionLibraryStore.Partition(session)) return;
        await RunAsync(async ct => { await store.DeleteAsync(item); libraryTranscriptions = await store.ListAsync(ct); transcriptionsPage.SetItems(libraryTranscriptions); });
    }
    private async Task ViewTranscriptionAsync(LibraryTranscription item)
    {
        if (!CanUseTranscriptions) return;
        var partition = TranscriptionLibraryStore.Partition(session); var dialog = new TranscriptionDetailsDialog(item) { XamlRoot = XamlRoot };
        SystemAppearance.PrepareDialog(dialog); transcriptionDetailsDialog = dialog; historyDialogOpen = true;
        try { await dialog.ShowAsync(); } finally { transcriptionDetailsDialog = null; historyDialogOpen = false; }
        if (closing || partition != TranscriptionLibraryStore.Partition(session) || dialog.Action == TranscriptionDetailAction.None) return;
        await RunAsync(async ct =>
        {
            var text = dialog.Action == TranscriptionDetailAction.ExportText;
            var extension = text ? ".txt" : Path.GetExtension(item.Item.Filename); if (string.IsNullOrWhiteSpace(extension)) extension = ".bin";
            var picker = new FileSavePicker { SuggestedFileName = text ? TranscriptionFiles.TextFilename(item.Item.Filename) : item.Item.Filename };
            picker.FileTypeChoices.Add(DesktopResources.Get(text ? "Text" : "TranscriptionOriginalMedia"), new List<string> { extension }); InitializeImagePicker(picker);
            var file = await picker.PickSaveFileAsync().AsTask(ct); if (file is null || closing || partition != TranscriptionLibraryStore.Partition(session)) return;
            if (string.Equals(file.Path, item.Path, StringComparison.OrdinalIgnoreCase)) return;
            if (text) await FileIO.WriteTextAsync(file, TranscriptionFiles.ExportText(item.Item.Response)).AsTask(ct);
            else { await using var input = File.OpenRead(item.Path); await using var output = await file.OpenStreamForWriteAsync(); output.SetLength(0); await input.CopyToAsync(output, ct); }
        });
    }
}
