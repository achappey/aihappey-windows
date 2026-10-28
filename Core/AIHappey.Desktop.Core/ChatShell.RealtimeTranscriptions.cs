using Microsoft.UI.Xaml.Controls;

namespace AIHappey.Desktop.Core;

public sealed partial class ChatShell
{
    private sealed class TranscriptionRealtimeState(string partition, TranscriptionLibraryStore store, IRealtimeTranscriptionSession connection, string model)
    {
        public string Partition { get; } = partition;
        public TranscriptionLibraryStore Store { get; } = store;
        public IRealtimeTranscriptionSession Connection { get; } = connection;
        public CancellationTokenSource Startup { get; } = new();
        public Task Start { get; set; } = Task.CompletedTask;
        public Task Pump { get; set; } = Task.CompletedTask;
        public Task? Stop { get; set; }
        public bool Connected, Stopping;
        public string? Error;
        public StoredTranscription Item { get; } = new() { Id = Guid.NewGuid().ToString("N"), CreatedAt = DateTimeOffset.UtcNow,
            Filename = "realtime-" + DateTimeOffset.Now.ToString("yyyy-MM-dd-HHmmss") + ".txt", Model = model, Kind = "realtime" };
    }
    private TranscriptionRealtimeState? transcriptionRealtime;
    private Task transcriptionRealtimeCleanup = Task.CompletedTask;
    private Func<string, RealtimeTranscriptionPreferences, Func<System.Text.Json.Nodes.JsonObject, CancellationToken, Task<string>>, IRealtimeTranscriptionSession>
        createRealtimeTranscription = (model, preferences, token) => new RealtimeTranscriptionSession(model, preferences, token);

    private bool TranscriptionRealtimeCurrent(TranscriptionRealtimeState state) => !closing && ReferenceEquals(transcriptionRealtime, state)
        && state.Partition == TranscriptionLibraryStore.Partition(session) && activePage == DesktopPage.Transcriptions;
    private void UpdateTranscriptionRealtimeAvailability()
    {
        transcriptionTarget.IsEnabled = !busy && transcriptionRealtime is null && transcriptionRealtimeCleanup.IsCompleted;
        transcriptionsPage.SetRealtimeAvailable(RealtimeTranscriptionConfiguration.Supports(SelectedTranscriptionModel)
            && initialized && !closing && realtime is null && transcriptionRealtimeCleanup.IsCompleted);
    }
    private async Task ToggleRealtimeTranscriptionAsync()
    {
        if (transcriptionRealtime is not null) { await StopRealtimeTranscriptionAsync(); return; }
        if (!CanUseTranscriptions || !RealtimeTranscriptionConfiguration.Supports(SelectedTranscriptionModel) || realtime is not null) return;
        await transcriptionRealtimeCleanup; await dictationCleanup; await realtimeCleanup;
        if (!CanUseTranscriptions || SelectedTranscriptionModel is not { } model || !RealtimeTranscriptionConfiguration.Supports(model)) return;
        transcriptionsPage.ClearNotices();
        var partition = TranscriptionLibraryStore.Partition(session); var chat = session.Settings.Chat.Clone();
        var provider = RealtimeConfiguration.Provider(model.Id);
        var tokens = new DesktopRealtimeTokenClient(client, http);
        TranscriptionRealtimeState state;
        try
        {
            var connection = createRealtimeTranscription(model.Id, session.Settings.Transcriptions.Realtime.Clone(),
                (payload, ct) => tokens.GetAsync(payload, chat, provider, ct));
            state = new(partition, new(TranscriptionRoot, partition), connection, model.Id);
        }
        catch { transcriptionsPage.Notice(DesktopResources.Get("TranscriptionRealtimeFailed"), InfoBarSeverity.Error); return; }
        transcriptionRealtime = state; UpdateTranscriptionRealtimeAvailability(); transcriptionsPage.SetRealtime("starting");
        state.Pump = PumpRealtimeTranscriptionAsync(state);
        try
        {
            state.Start = state.Connection.StartAsync(state.Startup.Token); await state.Start;
            if (state.Stopping || !TranscriptionRealtimeCurrent(state)) { await StopRealtimeTranscriptionAsync(); return; }
            state.Connected = true; transcriptionsPage.SetRealtime("connected", state.Connection.Snapshot.DisplayText);
        }
        catch (OperationCanceledException) when (state.Startup.IsCancellationRequested) { }
        catch (Exception error) { state.Error = TranscriptionRealtimeError(error); }
        finally { if (!state.Connected && ReferenceEquals(transcriptionRealtime, state)) await StopRealtimeTranscriptionAsync(); }
    }
    private static string TranscriptionRealtimeError(Exception error) => error is RealtimeFailureException safe
        ? safe.Message : DesktopResources.Get("TranscriptionRealtimeFailed");
    private async Task PumpRealtimeTranscriptionAsync(TranscriptionRealtimeState state)
    {
        string lastFinal = "";
        try
        {
            await foreach (var snapshot in state.Connection.Updates.ReadAllAsync())
            {
                if (TranscriptionRealtimeCurrent(state)) transcriptionsPage.SetRealtime(state.Stopping ? "stopping" : state.Connected ? "connected" : "starting", snapshot.DisplayText);
                var signature = string.Join("|", snapshot.Segments.Select(s => $"{s.Id}:{s.Text}:{s.StartSecond}:{s.EndSecond}"));
                if (snapshot.HasConfirmedText && signature != lastFinal)
                {
                    await state.Store.SaveRealtimeAsync(state.Item, snapshot, "active"); lastFinal = signature;
                }
            }
            if (!state.Stopping) state.Error ??= DesktopResources.Get("TranscriptionRealtimeFailed");
        }
        catch (Exception error) { state.Error = error is IOException or UnauthorizedAccessException ? DesktopResources.Get("TranscriptionRealtimeSaveFailed") : TranscriptionRealtimeError(error); }
        finally { if (!state.Stopping && ReferenceEquals(transcriptionRealtime, state)) _ = StopRealtimeTranscriptionAsync(); }
    }
    private Task StopRealtimeTranscriptionAsync()
    {
        var state = transcriptionRealtime;
        if (state is null) return transcriptionRealtimeCleanup;
        if (state.Stop is not null) return state.Stop;
        state.Stopping = true;
        if (!state.Connected) state.Startup.Cancel();
        if (TranscriptionRealtimeCurrent(state)) transcriptionsPage.SetRealtime("stopping", state.Connection.Snapshot.DisplayText);
        return transcriptionRealtimeCleanup = state.Stop = FinishRealtimeTranscriptionAsync(state);
    }
    private async Task FinishRealtimeTranscriptionAsync(TranscriptionRealtimeState state)
    {
        // Break synchronous callback reentrancy before publishing the shared cleanup task.
        await Task.Yield();
        try
        {
            await state.Connection.StopAsync(); await state.Pump;
            await state.Store.SaveRealtimeAsync(state.Item, state.Connection.Snapshot, state.Error is null ? "completed" : "error");
        }
        catch { state.Error = DesktopResources.Get("TranscriptionRealtimeSaveFailed"); }
        finally
        {
            state.Startup.Dispose();
            if (ReferenceEquals(transcriptionRealtime, state)) transcriptionRealtime = null;
            if (!closing && state.Partition == TranscriptionLibraryStore.Partition(session))
            {
                if (activePage == DesktopPage.Transcriptions)
                {
                    transcriptionsPage.SetRealtime(state.Error is null ? "completed" : "error", state.Connection.Snapshot.Text);
                    if (state.Error is not null) transcriptionsPage.Notice(state.Error, InfoBarSeverity.Error);
                }
                try { libraryTranscriptions = await state.Store.ListAsync(); transcriptionsPage.SetItems(libraryTranscriptions); }
                catch { transcriptionsPage.Notice(DesktopResources.Get("TranscriptionRealtimeSaveFailed"), InfoBarSeverity.Error); }
            }
            // Availability should not depend on the cleanup task reporting completion while inside its own finally.
            if (ReferenceEquals(transcriptionRealtime, null))
            {
                transcriptionTarget.IsEnabled = !busy && !closing;
                transcriptionsPage.SetRealtimeAvailable(initialized && !closing && RealtimeTranscriptionConfiguration.Supports(SelectedTranscriptionModel));
            }
        }
    }
}
