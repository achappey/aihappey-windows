using System.Diagnostics;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AIHappey.Desktop.Core;

public sealed partial class ChatShell
{
    private sealed record DictationState(IDictationRecording Recorder, ChatTarget Model, TranscriptionPreferences Preferences, string Partition);
    private Func<Action<Exception>, IDictationRecording> createDictationRecorder = failed => new DictationRecorder(failed);
    private DictationState? dictation;
    private ContentDialog? dictateWarning;
    private readonly DispatcherTimer dictationTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly Stopwatch dictationClock = new();
    private bool dictationChanging;
    private int dictationEpoch;
    private Task dictationCleanup = Task.CompletedTask;

    private void PrepareDictation()
    {
        transcriptionsPage.DictationRequested = ToggleTranscriptionDictationAsync;
        dictationTimer.Tick += (_, _) =>
        {
            if (dictation is not null && !dictationChanging)
            {
                var seconds = (int)dictationClock.Elapsed.TotalSeconds;
                transcriptionsPage.SetRecording(true, elapsed: $"{seconds / 60}:{(seconds % 60).ToString("00", CultureInfo.CurrentCulture)}");
            }
        };
        Unloaded += (_, _) => DiscardDictation();
    }

    private async Task ToggleTranscriptionDictationAsync()
    {
        if (dictationChanging || closing) return;
        if (dictation is { } current) { await FinishDictationAsync(current); return; }
        if (!CanUseTranscriptions || SelectedTranscriptionModel is not { } model) return;
        var epoch = ++dictationEpoch;
        var partition = TranscriptionLibraryStore.Partition(session);
        var preferences = session.Settings.Transcriptions.Clone();
        dictationChanging = true; transcriptionsPage.SetRecording(false, changing: true);
        transcriptionsPage.ClearNotices();
        bool Current() => !closing && epoch == dictationEpoch && activePage == DesktopPage.Transcriptions && partition == TranscriptionLibraryStore.Partition(session);
        IDictationRecording? recorder = null;
        try
        {
            if (!preferences.SkipDictateWarning)
            {
                var skip = new CheckBox { Name = "DictateDontShowAgain", Content = DesktopResources.Get("TranscriptionDictateWarningDontShowAgain") };
                ControlAppearance.Stock(skip);
                var content = new StackPanel { Spacing = 12 };
                content.Children.Add(new TextBlock { Text = DesktopResources.Get("TranscriptionDictateWarningMessage"), TextWrapping = TextWrapping.Wrap }); content.Children.Add(skip);
                var dialog = new ContentDialog
                {
                    Name = "DictateWarningDialog", XamlRoot = XamlRoot, Title = DesktopResources.Get("TranscriptionDictateWarningTitle"), Content = content,
                    PrimaryButtonText = DesktopResources.Get("TranscriptionDictateWarningContinue"), CloseButtonText = DesktopResources.Get("Cancel"), DefaultButton = ContentDialogButton.Primary
                };
                SystemAppearance.PrepareDialog(dialog); dictateWarning = dialog; historyDialogOpen = true;
                ContentDialogResult choice;
                try { choice = await dialog.ShowAsync(); }
                finally { if (ReferenceEquals(dictateWarning, dialog)) dictateWarning = null; historyDialogOpen = false; }
                if (choice != ContentDialogResult.Primary || !Current()) return;
                if (skip.IsChecked == true)
                {
                    var settings = session.Settings.Clone(); settings.Transcriptions.SkipDictateWarning = true;
                    await SettingsStore.SaveAsync(session.DataDirectory, settings);
                    if (!Current()) return;
                    session.Settings = settings;
                }
            }
            await dictationCleanup;
            if (!Current() || busy || downloading) return;
            DictationState? state = null;
            recorder = createDictationRecorder(error => DispatcherQueue.TryEnqueue(() =>
            {
                if (state is null || !ReferenceEquals(dictation, state) || !Current()) return;
                DiscardDictation();
                transcriptionsPage.Notice(error is InvalidOperationException ? error.Message : DesktopResources.Get("TranscriptionRecordingFailed"), InfoBarSeverity.Error);
            }));
            state = new(recorder, model, preferences, partition); dictation = state;
            recorder.Start();
            dictationClock.Restart(); dictationChanging = false; dictationTimer.Start();
            transcriptionTarget.IsEnabled = false; transcriptionsPage.SetRecording(true);
            recorder = null; // The active state now owns cleanup.
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (Current())
            {
                DiscardDictation();
                transcriptionsPage.Notice(DesktopResources.Get("TranscriptionRecordingFailed"), InfoBarSeverity.Error);
            }
        }
        finally
        {
            if (recorder is not null) await ReleaseDictationAsync(recorder);
            if (Current() && dictation is null) { dictationChanging = false; transcriptionsPage.SetRecording(false); }
        }
    }

    private async Task FinishDictationAsync(DictationState state)
    {
        var epoch = dictationEpoch;
        dictationChanging = true; dictationTimer.Stop();
        transcriptionsPage.SetRecording(true, changing: true);
        try
        {
            var audio = await state.Recorder.StopAsync();
            await ReleaseDictationAsync(state.Recorder);
            if (closing || epoch != dictationEpoch || !ReferenceEquals(dictation, state)
                || activePage != DesktopPage.Transcriptions || state.Partition != TranscriptionLibraryStore.Partition(session)) return;
            dictation = null; dictationChanging = false; dictationClock.Reset();
            transcriptionTarget.IsEnabled = !busy; transcriptionsPage.SetRecording(false);
            await TranscribeSourcesAsync([(audio.Name, _ => Task.FromResult(audio))], state.Model, state.Preferences, state.Partition);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (!closing && epoch == dictationEpoch && state.Partition == TranscriptionLibraryStore.Partition(session))
                transcriptionsPage.Notice(error is InvalidOperationException ? error.Message : DesktopResources.Get("TranscriptionRecordingFailed"), InfoBarSeverity.Error);
        }
        finally { if (epoch == dictationEpoch && ReferenceEquals(dictation, state)) DiscardDictation(); }
    }

    private void DiscardDictation()
    {
        ++dictationEpoch;
        var previous = dictation; dictation = null; dictationChanging = false;
        dictateWarning?.Hide(); dictationTimer.Stop(); dictationClock.Reset();
        transcriptionsPage.SetRecording(false); transcriptionTarget.IsEnabled = !busy;
        if (previous is not null) dictationCleanup = Task.WhenAll(dictationCleanup, ReleaseDictationAsync(previous.Recorder));
    }

    private static async Task ReleaseDictationAsync(IDictationRecording recorder)
    {
        // Cleanup must finish on navigation/shutdown even after a device has disappeared.
        try { await recorder.DisposeAsync(); }
        catch (Exception error) when (error is not OutOfMemoryException) { }
    }
}
