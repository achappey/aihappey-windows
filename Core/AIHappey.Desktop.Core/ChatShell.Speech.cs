using FluentIcons.Common;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;

namespace AIHappey.Desktop.Core;

public sealed partial class ChatShell
{
    private readonly SpeechPage speechPage = new() { Visibility = Visibility.Collapsed };
    private readonly AutoSuggestBox speechTarget = new() { Name = "SpeechModelPicker", PlaceholderText = DesktopResources.Get("SelectModel"), MinWidth = 120, MaxWidth = 420, Height = 40, Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Stretch };
    private IReadOnlyList<ChatTarget> speechModels = []; private IReadOnlyList<LibrarySpeech> librarySpeech = []; private string? speechPartition; private SpeechLibraryStore? speechLibrary;
    private SpeechSettingsDialog? speechSettingsDialog; private SpeechDetailsDialog? speechDetailsDialog; private int pendingSpeech;
    private string SpeechRoot => Path.Combine(session.DataDirectory, "speech");
    private bool CanUseSpeech => initialized && !busy && !closing && !downloading && !historyDialogOpen && catalogDialog is null && activePage == DesktopPage.Speech;
    private ChatTarget? SelectedSpeechModel => speechModels.FirstOrDefault(m => m.Id == speechTarget.Text.Trim());
    private void PrepareSpeech()
    {
        ToolbarControls.Outline(speechTarget); ToolbarControls.Label(speechTarget, DesktopResources.Get("SelectModel")); speechTarget.QueryIcon = DesktopIcons.Create(Icon.ChevronDown, 12);
        speechTarget.TextChanged += (_, args) => { if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput) SpeechModelSuggestions(speechTarget.Text); speechPage.SetModelAvailable(SelectedSpeechModel is not null); };
        speechTarget.GotFocus += (_, _) => { SpeechModelSuggestions(); speechTarget.IsSuggestionListOpen = true; }; speechTarget.SuggestionChosen += (_, args) => speechTarget.Text = ((ChatTarget)args.SelectedItem).Id;
        speechTarget.QuerySubmitted += (_, args) => { if (args.ChosenSuggestion is ChatTarget model) speechTarget.Text = model.Id; else { SpeechModelSuggestions(speechTarget.Text); speechTarget.IsSuggestionListOpen = true; } };
        speechPage.SendRequested = SendSpeechAsync; speechPage.StopRequested = () => operation?.Cancel(); speechPage.ImportRequested = PickSpeechDocumentsAsync; speechPage.FilesDropped = DropSpeechDocumentsAsync;
        speechPage.SettingsRequested = EditSpeechSettingsAsync; speechPage.FolderRequested = OpenSpeechFolderAsync; speechPage.PlayRequested = ViewSpeechAsync; speechPage.DetailsRequested = ViewSpeechAsync; speechPage.DownloadRequested = DownloadSpeechAsync; speechPage.DeleteRequested = DeleteSpeechAsync;
        speechPage.DictationRequested = () =>
        {
            if (CanUseSpeech && !speechPage.StartDictation())
                speechPage.Notice(DesktopResources.Get("VoiceTypingFailed"), InfoBarSeverity.Warning);
            return Task.CompletedTask;
        };
    }
    private void SpeechModelSuggestions(string query = "") => speechTarget.ItemsSource = speechModels.Where(m => string.IsNullOrWhiteSpace(query) || m.Id.Contains(query, StringComparison.OrdinalIgnoreCase) || m.Label.Contains(query, StringComparison.OrdinalIgnoreCase)).Take(100).ToArray();
    private void InvalidateSpeech() { speechModels = []; speechTarget.Text = ""; speechTarget.ItemsSource = null; librarySpeech = []; speechLibrary = null; speechPartition = null; pendingSpeech = 0; speechPage.ClearNotices(); speechPage.SetItems([]); speechPage.SetModelAvailable(false); }
    private async Task LoadSpeechAsync(CancellationToken ct, bool useCache = false)
    {
        var partition = SpeechLibraryStore.Partition(session); if (speechPartition != partition) { InvalidateSpeech(); speechPartition = partition; }
        speechLibrary = new(SpeechRoot, partition); librarySpeech = await speechLibrary.ListAsync(ct); speechPage.SetItems(librarySpeech, pendingSpeech);
        var available = useCache && aiModelTargets is not null ? aiModelTargets : await client.ListAsync(ServiceKind.Ai, ct); ct.ThrowIfCancellationRequested(); if (closing || partition != SpeechLibraryStore.Partition(session)) return;
        aiModelTargets = available; speechModels = AiModelCatalog.OfType(available, "speech");
        if (SelectedSpeechModel is null) speechTarget.Text = speechModels.FirstOrDefault(m => m.Id == session.Settings.AiModels.SpeechModel)?.Id ?? speechModels.FirstOrDefault(m => m.Id == "openai/gpt-4o-mini-tts")?.Id ?? speechModels.FirstOrDefault()?.Id ?? "";
        SpeechModelSuggestions(); speechPage.SetModelAvailable(SelectedSpeechModel is not null);
    }
    private async Task SendSpeechAsync()
    {
        if (!CanUseSpeech || SelectedSpeechModel is not { } model || string.IsNullOrWhiteSpace(speechPage.PromptText)) return; var text = speechPage.PromptText; var preferences = session.Settings.Speech.Clone(); var partition = SpeechLibraryStore.Partition(session); var store = new SpeechLibraryStore(SpeechRoot, partition); speechPage.ClearNotices();
        await RunAsync(async ct => { pendingSpeech = 1; speechPage.SetItems(librarySpeech, 1); try { var result = await new DesktopSpeechClient(client, http).GenerateAsync(model, text, preferences, ct); await store.SaveAsync(result); librarySpeech = await store.ListAsync(); if (!closing && partition == SpeechLibraryStore.Partition(session)) { foreach (var warning in result.Warnings) speechPage.Notice(warning, InfoBarSeverity.Warning); } } finally { pendingSpeech = 0; if (!closing && partition == SpeechLibraryStore.Partition(session)) speechPage.SetItems(librarySpeech); } }, inference: true);
    }
    private async Task PickSpeechDocumentsAsync()
    {
        if (!CanUseSpeech) return; IReadOnlyList<StorageFile> files = []; await RunAsync(async ct => { var picker = new FileOpenPicker(); picker.FileTypeFilter.Add("*"); InitializeImagePicker(picker); files = await picker.PickMultipleFilesAsync().AsTask(ct); await ExtractSpeechDocumentsAsync(files, ct); });
    }
    private async Task DropSpeechDocumentsAsync(DataPackageView data) { if (!CanUseSpeech || !data.Contains(StandardDataFormats.StorageItems)) return; await RunAsync(async ct => await ExtractSpeechDocumentsAsync((await data.GetStorageItemsAsync().AsTask(ct)).OfType<StorageFile>(), ct)); }
    private async Task ExtractSpeechDocumentsAsync(IEnumerable<StorageFile> files, CancellationToken ct)
    {
        var parts = new List<string>(); var failed = new List<string>(); var extraction = new DocumentTextExtraction();
        foreach (var file in files) try { var properties = await file.GetBasicPropertiesAsync().AsTask(ct); ComposerAttachments.ValidateSize((long)properties.Size); await using var stream = await file.OpenStreamForReadAsync(); var source = await ComposerAttachments.ReadAsync(file.Name, file.ContentType, stream, ct); var text = await extraction.ExtractAsync(file.Name, source.MediaType, source.Content, ct); if (text is null) failed.Add(file.Name); else parts.Add(text.Trim()); } catch (Exception) { failed.Add(file.Name); }
        if (parts.Count > 0) speechPage.ReplacePrompt(string.Join("\n\n", parts)); if (failed.Count > 0) speechPage.Notice(DesktopResources.Format("SpeechImportFailed", string.Join(", ", failed)), InfoBarSeverity.Warning);
    }
    private async Task EditSpeechSettingsAsync()
    {
        if (!CanUseSpeech) return; var partition = SpeechLibraryStore.Partition(session); var model = SelectedSpeechModel; var provider = model is null ? null : ChatPreferences.ResolveProvider(ServiceKind.Ai, model.Id, model.ProviderKey); var dialog = new SpeechSettingsDialog(session.Settings.Speech, provider) { XamlRoot = XamlRoot };
        dialog.SaveAsync = async next => { if (closing || partition != SpeechLibraryStore.Partition(session)) return; var settings = session.Settings.Clone(); settings.Speech = next; await SettingsStore.SaveAsync(session.DataDirectory, settings); session.Settings = settings; };
        SystemAppearance.PrepareDialog(dialog); speechSettingsDialog = dialog; historyDialogOpen = true; try { await dialog.ShowAsync(); } finally { speechSettingsDialog = null; historyDialogOpen = false; }
    }
    private async Task OpenSpeechFolderAsync() { if (!CanUseSpeech) return; await RunAsync(async ct => { var store = new SpeechLibraryStore(SpeechRoot, SpeechLibraryStore.Partition(session)); Directory.CreateDirectory(store.Folder); if (!await Launcher.LaunchFolderAsync(await StorageFolder.GetFolderFromPathAsync(store.Folder).AsTask(ct)).AsTask(ct)) throw new InvalidOperationException(DesktopResources.Get("ImageFolderFailed")); }); }
    private async Task ViewSpeechAsync(LibrarySpeech item) { if (!CanUseSpeech) return; var dialog = new SpeechDetailsDialog(item) { XamlRoot = XamlRoot }; SystemAppearance.PrepareDialog(dialog); speechDetailsDialog = dialog; historyDialogOpen = true; try { await dialog.ShowAsync(); } finally { speechDetailsDialog = null; historyDialogOpen = false; } if (dialog.Action == SpeechDetailAction.Download && !closing) await DownloadSpeechAsync(item); }
    private async Task DownloadSpeechAsync(LibrarySpeech item) { if (!CanUseSpeech) return; await RunAsync(async ct => { var picker = new FileSavePicker { SuggestedFileName = Path.GetFileNameWithoutExtension(item.Item.File) + "-" + item.Item.CreatedAt.ToString("yyyyMMdd-HHmmss") }; picker.FileTypeChoices.Add(DesktopResources.Get("Audio"), [SpeechAudio.Extension(item.Item.MediaType, item.Item.Format)]); InitializeImagePicker(picker); var file = await picker.PickSaveFileAsync().AsTask(ct); if (file is null || string.Equals(file.Path, item.Path, StringComparison.OrdinalIgnoreCase)) return; await using var input = File.OpenRead(item.Path); await using var output = await file.OpenStreamForWriteAsync(); output.SetLength(0); await input.CopyToAsync(output, ct); }); }
    private async Task DeleteSpeechAsync(LibrarySpeech item) { if (!CanUseSpeech || speechLibrary is null) return; var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = DesktopResources.Get("SpeechDeleteTitle"), Content = DesktopResources.Get("SpeechDeleteHint"), PrimaryButtonText = DesktopResources.Get("Delete"), CloseButtonText = DesktopResources.Get("Cancel") }; SystemAppearance.PrepareDialog(dialog); historyDialogOpen = true; ContentDialogResult result; try { result = await dialog.ShowAsync(); } finally { historyDialogOpen = false; } if (result != ContentDialogResult.Primary || closing) return; await RunAsync(async ct => { await speechLibrary.DeleteAsync(item); librarySpeech = await speechLibrary.ListAsync(ct); speechPage.SetItems(librarySpeech); }); }
}
