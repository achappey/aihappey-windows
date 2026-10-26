using System.Diagnostics;
using System.Globalization;
using FluentIcons.Common;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AIHappey.Desktop.Core;

public sealed partial class ChatShell
{
    private sealed record ComposerDictationTarget(DesktopPage Page, TextBox Input, Button Button, string Draft, int Start, int Length, Conversation Conversation);
    private sealed record DictationState(IDictationRecording Recorder, ChatTarget Model, TranscriptionPreferences Preferences, string Partition, ComposerDictationTarget? Composer);
    private Func<Action<Exception>, IDictationRecording> createDictationRecorder = failed => new DictationRecorder(failed);
    private DictationState? dictation;
    private ContentDialog? dictateWarning;
    private readonly DispatcherTimer dictationTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly Stopwatch dictationClock = new();
    private bool dictationChanging;
    private int dictationEpoch;
    private Task dictationCleanup = Task.CompletedTask;
    private ComposerDictationTarget? composerDictation;
    private CancellationTokenSource? composerTranscription;
    private Func<TextBox, bool> startWindowsDictation = WindowsVoiceTyping.TryStart;

    private ChatTarget? DefaultDictationModel => ComposerDictation.DefaultModel(session.Settings, aiModelTargets ?? []);

    private void UpdateDictationAvailability()
    {
        var visible = session.Settings.Chat.Dictate == "windows" || session.Settings.Chat.Dictate == "transcriptions" && DefaultDictationModel is not null;
        if (composerDictation is not null && (!visible || session.Settings.Chat.Dictate != "transcriptions"
            || dictation is { } state && DefaultDictationModel?.Id != state.Model.Id)) DiscardDictation();
        dictate.Visibility = visible && activePage == DesktopPage.Chat ? Visibility.Visible : Visibility.Collapsed;
        foreach (var button in new[] { imagesPage.DictationButton, videosPage.DictationButton, speechPage.DictationButton })
            button.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private (TextBox Input, Button Button) DictationControls(DesktopPage page) => page switch
    {
        DesktopPage.Images => (imagesPage.DictationInput, imagesPage.DictationButton),
        DesktopPage.Videos => (videosPage.DictationInput, videosPage.DictationButton),
        DesktopPage.Speech => (speechPage.DictationInput, speechPage.DictationButton),
        _ => (input, dictate)
    };

    private void DictationNotice(ComposerDictationTarget? target, string message, InfoBarSeverity severity)
    {
        switch (target?.Page)
        {
            case DesktopPage.Chat: Show(message, severity); break;
            case DesktopPage.Images: imagesPage.Notice(message, severity); break;
            case DesktopPage.Videos: videosPage.Notice(message, severity); break;
            case DesktopPage.Speech: speechPage.Notice(message, severity); break;
            default: transcriptionsPage.Notice(message, severity); break;
        }
    }

    private void SetDictationRecording(bool recording, bool changing = false, string? elapsed = null)
    {
        if (composerDictation is not { } target) { transcriptionsPage.SetRecording(recording, changing, elapsed); return; }
        switch (target.Page)
        {
            case DesktopPage.Chat: input.IsReadOnly = recording || changing || busy; send.IsEnabled = !recording && !changing && !busy; break;
            case DesktopPage.Images: imagesPage.SetDictating(recording || changing); break;
            case DesktopPage.Videos: videosPage.SetDictating(recording || changing); break;
            case DesktopPage.Speech: speechPage.SetDictating(recording || changing); break;
        }
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        content.Children.Add(DesktopIcons.Create(changing ? Icon.ArrowSync : Icon.Stop));
        if (recording && !changing) content.Children.Add(new TextBlock { Text = elapsed ?? "0:00", VerticalAlignment = VerticalAlignment.Center });
        target.Button.Content = recording || changing ? content : DesktopIcons.Create(Icon.Mic);
        target.Button.Width = recording && !changing ? double.NaN : 40;
        target.Button.IsEnabled = changing && composerTranscription is not null || !changing && !busy;
        ToolbarControls.Label(target.Button, DesktopResources.Get(recording ? "TranscriptionStopRecording" : changing ? "Stop" : "SpeechDictate"));
    }

    private async Task ToggleComposerDictationAsync(DesktopPage page)
    {
        if (composerTranscription is not null && composerDictation?.Page == page) { composerTranscription.Cancel(); return; }
        if (!initialized || busy || closing || downloading || historyDialogOpen || catalogDialog is not null || activePage != page || dictationChanging) return;
        if (dictation is { Composer: not null } state && state.Composer.Page == page) { await FinishDictationAsync(state); return; }
        if (dictation is not null || composerDictation is not null || page == DesktopPage.Videos && videoSubmitting) return;
        var (text, button) = DictationControls(page);
        if (!text.IsEnabled || text.IsReadOnly) return;
        if (session.Settings.Chat.Dictate == "none") return;
        if (session.Settings.Chat.Dictate == "windows")
        {
            if (!startWindowsDictation(text)) DictationNotice(new(page, text, button, text.Text, text.SelectionStart, text.SelectionLength, current), DesktopResources.Get("VoiceTypingFailed"), InfoBarSeverity.Warning);
            return;
        }
        if (DefaultDictationModel is not { } model) return;
        await StartDictationAsync(model, new(page, text, button, text.Text, text.SelectionStart, text.SelectionLength, current));
    }

    private void PrepareDictation()
    {
        transcriptionsPage.DictationRequested = ToggleTranscriptionDictationAsync;
        foreach (var text in new[] { input, imagesPage.DictationInput, videosPage.DictationInput, speechPage.DictationInput })
            text.TextChanged += (_, _) =>
            {
                if (composerDictation is { } target && ReferenceEquals(target.Input, text) && text.Text != target.Draft) DiscardDictation();
            };
        UpdateDictationAvailability();
        dictationTimer.Tick += (_, _) =>
        {
            if (dictation is not null && !dictationChanging)
            {
                var seconds = (int)dictationClock.Elapsed.TotalSeconds;
                SetDictationRecording(true, elapsed: $"{seconds / 60}:{(seconds % 60).ToString("00", CultureInfo.CurrentCulture)}");
            }
        };
        Unloaded += (_, _) => DiscardDictation();
    }

    private async Task ToggleTranscriptionDictationAsync()
    {
        if (dictationChanging || closing) return;
        if (dictation is { } current) { await FinishDictationAsync(current); return; }
        if (!CanUseTranscriptions || SelectedTranscriptionModel is not { } model) return;
        await StartDictationAsync(model, null);
    }

    private async Task StartDictationAsync(ChatTarget model, ComposerDictationTarget? target)
    {
        var epoch = ++dictationEpoch;
        var partition = TranscriptionLibraryStore.Partition(session);
        var preferences = session.Settings.Transcriptions.Clone();
        composerDictation = target;
        dictationChanging = true; SetDictationRecording(false, changing: true);
        if (target is null) transcriptionsPage.ClearNotices();
        bool Current() => DictationCurrent(epoch, partition, target, model);
        IDictationRecording? recorder = null;
        try
        {
            if (target is null && !preferences.SkipDictateWarning)
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
                DictationNotice(target, error is InvalidOperationException ? error.Message : DesktopResources.Get("TranscriptionRecordingFailed"), InfoBarSeverity.Error);
            }));
            state = new(recorder, model, preferences, partition, target); dictation = state;
            recorder.Start();
            dictationClock.Restart(); dictationChanging = false; dictationTimer.Start();
            if (target is null) transcriptionTarget.IsEnabled = false;
            SetDictationRecording(true);
            recorder = null; // The active state now owns cleanup.
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (Current())
            {
                DiscardDictation();
                DictationNotice(target, DesktopResources.Get("TranscriptionRecordingFailed"), InfoBarSeverity.Error);
            }
        }
        finally
        {
            if (recorder is not null) await ReleaseDictationAsync(recorder);
            if (epoch == dictationEpoch && dictation is null) DiscardDictation();
        }
    }

    private async Task FinishDictationAsync(DictationState state)
    {
        var epoch = dictationEpoch;
        dictationChanging = true; dictationTimer.Stop();
        SetDictationRecording(true, changing: true);
        try
        {
            var audio = await state.Recorder.StopAsync();
            await ReleaseDictationAsync(state.Recorder);
            if (!DictationCurrent(epoch, state.Partition, state.Composer, state.Model) || !ReferenceEquals(dictation, state)) return;
            dictation = null; dictationChanging = false; dictationClock.Reset();
            if (state.Composer is { } target)
            {
                await RunAsync(async ct =>
                {
                    using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    composerTranscription = lifetime;
                    try
                    {
                        SetDictationRecording(false, changing: true);
                        var result = await new DesktopTranscriptionClient(client, http).GenerateAsync(state.Model, audio, state.Preferences, lifetime.Token);
                        lifetime.Token.ThrowIfCancellationRequested();
                        if (!DictationCurrent(epoch, state.Partition, target, state.Model)) return;
                        // Clear ownership before changing text so the draft-change guard does not discard this insertion.
                        SetDictationRecording(false); composerDictation = null;
                        target.Input.Text = ComposerDictation.InsertText(target.Draft, target.Start, target.Length, result.Response["text"]!.GetValue<string>(), out var caret);
                        target.Input.Select(caret, 0); target.Input.Focus(FocusState.Programmatic);
                        if (result.Response["warnings"] is System.Text.Json.Nodes.JsonArray warnings)
                            foreach (var warning in warnings) DictationNotice(target, warning?.ToJsonString() ?? "", InfoBarSeverity.Warning);
                    }
                    finally { if (ReferenceEquals(composerTranscription, lifetime)) composerTranscription = null; }
                }, inference: true);
            }
            else
            {
                transcriptionTarget.IsEnabled = !busy; transcriptionsPage.SetRecording(false);
                await TranscribeSourcesAsync([(audio.Name, _ => Task.FromResult(audio))], state.Model, state.Preferences, state.Partition);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (!closing && epoch == dictationEpoch && state.Partition == TranscriptionLibraryStore.Partition(session))
                DictationNotice(state.Composer, error is InvalidOperationException ? error.Message : DesktopResources.Get("TranscriptionRecordingFailed"), InfoBarSeverity.Error);
        }
        finally { if (epoch == dictationEpoch) DiscardDictation(); }
    }

    private bool DictationCurrent(int epoch, string partition, ComposerDictationTarget? target, ChatTarget model) =>
        !closing && epoch == dictationEpoch && partition == TranscriptionLibraryStore.Partition(session)
        && activePage == (target?.Page ?? DesktopPage.Transcriptions)
        && (target is null || ReferenceEquals(composerDictation, target) && target.Input.Text == target.Draft
            && (target.Page != DesktopPage.Chat || ReferenceEquals(current, target.Conversation))
            && session.Settings.Chat.Dictate == "transcriptions" && DefaultDictationModel?.Id == model.Id);

    private void DiscardDictation()
    {
        ++dictationEpoch;
        composerTranscription?.Cancel();
        var previous = dictation; dictation = null; dictationChanging = false;
        dictateWarning?.Hide(); dictationTimer.Stop(); dictationClock.Reset();
        transcriptionsPage.SetRecording(false); transcriptionTarget.IsEnabled = !busy;
        if (composerDictation is not null) { SetDictationRecording(false); composerDictation = null; }
        if (previous is not null) dictationCleanup = Task.WhenAll(dictationCleanup, ReleaseDictationAsync(previous.Recorder));
    }

    private static async Task ReleaseDictationAsync(IDictationRecording recorder)
    {
        // Cleanup must finish on navigation/shutdown even after a device has disappeared.
        try { await recorder.DisposeAsync(); }
        catch (Exception error) when (error is not OutOfMemoryException) { }
    }
}
