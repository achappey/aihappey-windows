using System.Text.Json;
using System.Text.Json.Nodes;
using System.Runtime.CompilerServices;
using AIHappey.Vercel.Models;
using FluentIcons.Common;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage.Streams;

namespace AIHappey.Desktop.Core;

public sealed partial class ChatShell
{
    private readonly ToggleButton realtimeModelMode = ToolbarControls.CreateModeButton(DesktopIcons.Create(Icon.Brain), DesktopResources.Get("Models"));
    private readonly ToggleButton realtimeAgentsMode = ToolbarControls.CreateModeButton(DesktopIcons.Create(Icon.Bot), DesktopResources.Get("Agents"));
    private readonly ChatTargetPicker realtimeAgent = new() { Name = "RealtimeAgentPicker", AllowEmptySelection = true, Height = 40,
        PlaceholderText = DesktopResources.Get("AgentRealtimeSelect"), HorizontalAlignment = HorizontalAlignment.Stretch };
    private IReadOnlyList<ChatTarget> realtimeAgents = [];
    private bool realtimeAgentMode;
    private sealed class AgentRealtimeState(ChatTarget target, DesktopAgent agent, JsonObject request, ChatPreferences preferences)
    {
        public ChatTarget Target { get; } = target;
        public DesktopAgent Agent { get; } = agent;
        public JsonObject Request { get; } = request;
        public ChatPreferences Preferences { get; } = preferences;
        public IRealtimeTranscriptionSession? Connection;
        public AgentRealtimeTurns? Turns;
        public ConversationMessage? Draft;
        public CancellationTokenSource? Run;
        public Task Queue = Task.CompletedTask;
        public MediaPlayer? Player;
    }
    private ChatTarget? SelectedRealtimeAgent => realtimeAgents.FirstOrDefault(item => realtimeAgent.Selection.Ids.Contains(item.Id));
    private static DesktopAgent? RealtimeAgentDefinition(ChatTarget target)
    {
        if (target.ModelMetadata is not { ValueKind: JsonValueKind.Object } metadata) return null;
        if (metadata.TryGetProperty("agent", out var embedded) && embedded.ValueKind == JsonValueKind.Object) metadata = embedded;
        return new DesktopAgent(JsonNode.Parse(metadata.GetRawText())!.AsObject());
    }
    private bool RealtimeAgentSupported(ChatTarget target)
    {
        var id = DesktopAgent.Text(RealtimeAgentDefinition(target)?.TranscriptionModel?["id"]);
        return (aiModelTargets ?? []).Any(model => model.Id == id && model.ModelType == "transcription" && RealtimeTranscriptionConfiguration.Supports(model));
    }
    private void PrepareAgentRealtimeHeader()
    {
        realtimeModelMode.Name = "RealtimeModelMode"; realtimeAgentsMode.Name = "RealtimeAgentsMode";
        realtimeHeader.ColumnDefinitions.Insert(0, new() { Width = GridLength.Auto });
        realtimeHeader.ColumnDefinitions.Insert(0, new() { Width = GridLength.Auto });
        Grid.SetColumn(realtimeTarget, 2); Grid.SetColumn(realtimeFavorite, 3);
        Grid.SetColumn(realtimeAgentsMode, 1); Grid.SetColumn(realtimeAgent, 2); Grid.SetColumnSpan(realtimeAgent, 2);
        realtimeHeader.Children.Add(realtimeModelMode); realtimeHeader.Children.Add(realtimeAgentsMode); realtimeHeader.Children.Add(realtimeAgent);
        ToolbarControls.Label(realtimeAgent, DesktopResources.Get("AgentRealtimeSelect"));
        realtimeModelMode.Click += (_, _) => SetAgentRealtimeMode(false);
        realtimeAgentsMode.Click += (_, _) => SetAgentRealtimeMode(true);
        realtimeAgent.SelectionChanged += UpdateRealtimeControls;
    }
    private void SetAgentRealtimeMode(bool value)
    {
        if (realtime is null && !busy) realtimeAgentMode = value;
        UpdateRealtimeControls();
    }
    private void UpdateAgentRealtimeControls()
    {
        realtimeModelMode.IsChecked = !realtimeAgentMode; realtimeAgentsMode.IsChecked = realtimeAgentMode;
        realtimeModelMode.IsEnabled = realtimeAgentsMode.IsEnabled = realtimeAgent.IsEnabled = realtime is null && !busy;
        realtimeTarget.Visibility = realtimeFavorite.Visibility = realtimeAgentMode ? Visibility.Collapsed : Visibility.Visible;
        realtimeAgent.Visibility = realtimeAgentMode ? Visibility.Visible : Visibility.Collapsed;
    }
    private async Task LoadRealtimeAgentsAsync(CancellationToken ct)
    {
        await LoadSelectionCatalogsAsync(ct);
        var partition = session.HistoryPartition;
        IReadOnlyList<ChatTarget> remote = [];
        try { remote = await client.ListAsync(ServiceKind.Agents, ct); }
        catch (Exception error) when (error is not OperationCanceledException) { /* Local agents still work without remote discovery. */ }
        ct.ThrowIfCancellationRequested(); if (closing || partition != session.HistoryPartition) return;
        var local = DesktopAgentTargets.Project(localAgents.Select(agent => agent.CatalogItem()));
        realtimeAgents = local.Concat(remote.Select(model => model with { Id = "remote:" + model.Id, RemoteAgentId = model.Id }))
            .Where(target => RealtimeAgentDefinition(target)?.TranscriptionModel is { } transcription
                && !string.IsNullOrWhiteSpace(DesktopAgent.Text(transcription["id"]))).ToArray();
        realtimeAgent.Configure(true, false, new HashSet<string>(realtimeAgents.Where(item => favorites.Contains(
            $"Agent:{(item.LocalAgentName is null ? "Backend" : "Local")}:{item.LocalAgentName ?? item.RemoteAgentId}")).Select(item => item.Id)), false, "");
        realtimeAgent.ItemsSource = realtimeAgents;
    }
    private ChatPreferences AgentAudioHeaders(JsonObject model)
    {
        var preferences = session.Settings.Chat.Clone(); var provider = DesktopAgent.Provider(DesktopAgent.Text(model["id"]));
        if (model["providerHeaders"] is JsonObject headers)
            preferences.ProviderHeaders[provider] = headers.Where(pair => pair.Value is JsonValue).ToDictionary(pair => pair.Key, pair => DesktopAgent.Text(pair.Value), StringComparer.OrdinalIgnoreCase);
        return preferences;
    }
    private async Task SendAgentRealtimeAsync(McpSelectedPrompt? selectedPrompt)
    {
        if (busy || realtimeSending || closing || !initialized || historyDialogOpen) return;
        realtimeSending = true; UpdateRealtimeControls();
        try
        {
            await StopRealtimeTranscriptionAsync(); await realtimeCleanup; await dictationCleanup;
            var partition = session.HistoryPartition;
            if (SelectedRealtimeAgent is not { } selected || !RealtimeAgentSupported(selected))
                throw new GatewayException(DesktopResources.Get("AgentRealtimeUnsupported"));
            var prepared = selectedPrompt is not null || !string.IsNullOrWhiteSpace(input.Text) || contextAttachments.Count > 0 || selectedResources.Count > 0
                ? await ComposerAttachments.PrepareAsync(selectedPrompt is null ? input.Text.Trim() : "", contextAttachments.ToArray(), ServiceKind.Agents,
                    session.Settings.ConvertAttachmentsToText, documentExtractor, CancellationToken.None, selectedResources.ToArray(), selectedPrompt?.Parts) : null;
            if (closing || partition != session.HistoryPartition || activePage != DesktopPage.Realtime) return;
            if (realtime is null)
            {
                await RunAsync(async ct =>
                {
                    var agent = RealtimeAgentDefinition(selected)!.Clone(); var preferences = session.Settings.Chat.Clone();
                    if (preferences.PluginEnabled(DesktopLocalTools.SkillSearch)) await LoadRuntimeSkillsAsync(ct);
                    var snapshot = CaptureSkillRuntime(preferences, agentToolsOnly: true);
                    var request = DesktopAgentRequests.Build([selected.Id], realtimeAgents, localAgents, new AgentWorkflowPreferences { Type = "sequential" });
                    var voice = new AgentRealtimeState(selected, agent, request, preferences);
                    var state = new RealtimeState(current, partition, agent.ModelId, snapshot, session.Settings.Realtime.Clone()) { AgentVoice = voice };
                    realtime = state; UpdateRealtimeControls();
                    void Dispatch(Action action) => DispatcherQueue.TryEnqueue(() => { if (RealtimeCurrent(state) && realtimeMute.IsChecked != true) action(); });
                    voice.Turns = new(DesktopAgent.Provider(DesktopAgent.Text(agent.TranscriptionModel!["id"])),
                        () => Dispatch(() => CancelAgentRealtimeRun(state)),
                        text => Dispatch(() => PreviewAgentRealtime(state, text)),
                        text => Dispatch(() => QueueAgentRealtimeTurn(state, NewAgentRealtimeUser(voice, text))));
                    var transcription = agent.TranscriptionModel!;
                    voice.Connection = new RealtimeTranscriptionSession(DesktopAgent.Text(transcription["id"]),
                        new() { ProviderOptions = (JsonObject)(transcription["providerMetadata"] as JsonObject ?? new()).DeepClone() },
                        (payload, token) => new DesktopRealtimeTokenClient(client, http).GetAsync(payload, AgentAudioHeaders(transcription),
                            DesktopAgent.Provider(DesktopAgent.Text(transcription["id"])), token),
                        onEvent: value => voice.Turns.Apply(value));
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, state.Lifetime.Token);
                    try
                    {
                        await voice.Connection.StartAsync(linked.Token); linked.Token.ThrowIfCancellationRequested();
                        if (!RealtimeCurrent(state)) throw new OperationCanceledException(linked.Token);
                        current.Service = ServiceKind.Agents; current.Target = selected.Id; current.Metadata["realtime"] = true; current.Metadata["realtimeAgent"] = selected.Id;
                        if (current.Messages.Count == 0) current.Title = DesktopResources.Get("RealtimeConversation");
                        await voice.Connection.SetMutedAsync(realtimeMute.IsChecked == true);
                        state.Connected = true; state.Pump = PumpAgentRealtimeAsync(state); UpdateRealtimeControls();
                    }
                    catch { await StopRealtimeAsync(); throw; }
                });
            }
            if (realtime is { Connected: true, AgentVoice: not null } active && RealtimeCurrent(active) && prepared is not null)
            {
                QueueAgentRealtimeTurn(active, prepared.Message);
                if (selectedPrompt is null) input.Text = ""; ResetContext();
                if (prepared.Warnings.Count > 0) Show(string.Join("\n", prepared.Warnings), InfoBarSeverity.Warning);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { Show(RealtimeErrorMessage(error), InfoBarSeverity.Error); }
        finally { realtimeSending = false; UpdateRealtimeControls(); }
    }
    private async Task PumpAgentRealtimeAsync(RealtimeState state)
    {
        try { await foreach (var _ in state.AgentVoice!.Connection!.Updates.ReadAllAsync(state.Lifetime.Token)) { } }
        catch (OperationCanceledException) when (state.Lifetime.IsCancellationRequested) { }
        catch (Exception error) { if (RealtimeCurrent(state)) Show(RealtimeErrorMessage(error), InfoBarSeverity.Error); }
        finally { if (!state.Stopping && ReferenceEquals(realtime, state)) _ = StopRealtimeAsync(); }
    }
    private static UIMessage NewAgentRealtimeUser(AgentRealtimeState voice, string text) => new()
    {
        Id = voice.Draft?.Message.Id ?? Guid.NewGuid().ToString("N"), Role = Role.user,
        Parts = [PortableConversations.Part(new JsonObject { ["type"] = "text", ["text"] = text })],
        Metadata = new() { ["realtime"] = true, ["input"] = "audio", ["realtimeAgent"] = voice.Target.Id }
    };
    private void PreviewAgentRealtime(RealtimeState state, string text)
    {
        var voice = state.AgentVoice!;
        voice.Draft = new() { Message = NewAgentRealtimeUser(voice, text) }; RenderTranscript();
    }
    private void CancelAgentRealtimeRun(RealtimeState state)
    {
        var voice = state.AgentVoice!;
        voice.Run?.Cancel(); voice.Player?.Pause();
    }
    private void QueueAgentRealtimeTurn(RealtimeState state, UIMessage message)
    {
        if (!RealtimeCurrent(state)) return;
        var voice = state.AgentVoice!; CancelAgentRealtimeRun(state); voice.Draft = null;
        message = new UIMessage { Id = message.Id, Role = message.Role, Parts = message.Parts,
            Metadata = new(message.Metadata ?? []) { ["realtime"] = true, ["realtimeAgent"] = voice.Target.Id } };
        var run = CancellationTokenSource.CreateLinkedTokenSource(state.Lifetime.Token); voice.Run = run;
        voice.Queue = RunAgentRealtimeTurnAsync(state, voice.Queue, message, run);
    }
    private async Task RunAgentRealtimeTurnAsync(RealtimeState state, Task previous, UIMessage user, CancellationTokenSource run)
    {
        var voice = state.AgentVoice!; var ct = run.Token;
        ConversationMessage? output = null;
        StreamingAgentSpeech? streamedSpeech = null;
        try
        {
            await previous; ct.ThrowIfCancellationRequested(); if (!RealtimeCurrent(state)) return;
            state.Conversation.Messages.Add(new() { Message = user });
            output = new() { Status = "streaming", Message = new UIMessage { Id = Guid.NewGuid().ToString("N"), Role = Role.assistant,
                Metadata = new() { ["model"] = voice.Target.Label, ["agentModel"] = voice.Agent.ModelId, ["providerKey"] = DesktopAgent.Provider(voice.Agent.ModelId),
                    ["timestamp"] = DateTimeOffset.UtcNow.ToString("O"), ["realtime"] = true, ["realtimeAgent"] = voice.Target.Id } } };
            state.Conversation.Messages.Add(output); activeMcpTurn = state.Tools; activeConversationTools = state.Tools.ConversationTools;
            followBottom = true; RenderTranscript(); await SaveTurnHistoryAsync(state.Partition, state.Conversation, ct);
            var scope = transientConversationData.SetScope(state.Partition, state.Conversation.Id);
            var render = DateTimeOffset.MinValue; var save = render;
            if (voice.Agent.SpeechModel is { } selectedSpeech && (aiModelTargets ?? []).FirstOrDefault(target => target.Id == DesktopAgent.Text(selectedSpeech["id"])) is { } speechTarget
                && (StreamingSpeechProtocol.Supports(speechTarget) || OpenAiRealtimeSpeech.Supports(speechTarget)))
            {
                streamedSpeech = new((started, token) => OpenAgentStreamingSpeechAsync(selectedSpeech, started, token),
                    (text, token) => PlayAgentRealtimeSpeechAsync(state, selectedSpeech, text, token),
                    _ => DispatcherQueue.TryEnqueue(() => { if (RealtimeCurrent(state) && !ct.IsCancellationRequested) Show(DesktopResources.Get("AgentRealtimeSpeechFailed"), InfoBarSeverity.Warning); }), ct,
                    allowLegacyFallback: !OpenAiRealtimeSpeech.IsModel(DesktopAgent.Text(selectedSpeech["id"])));
            }
            var responseIndex = 0;
            async IAsyncEnumerable<StreamEvent> AgentStream(List<UIMessage> messages, [EnumeratorCancellation] CancellationToken token)
            {
                var index = responseIndex++;
                await foreach (var item in client.StreamAsync(ServiceKind.Agents, voice.Target.RemoteAgentId ?? voice.Target.Id, state.Conversation.Id,
                    messages, token, voice.Preferences, voice.Target.ProviderKey, mcp: state.Tools, agentRequest: voice.Request))
                {
                    streamedSpeech?.Observe(index, item); yield return item;
                }
                streamedSpeech?.CompleteResponse(index);
            }
            await DesktopChatTurn.RunAsync(state.Conversation, output,
                AgentStream, ApproveToolAsync,
                async (force, token) =>
                {
                    token.ThrowIfCancellationRequested(); if (!RealtimeCurrent(state) || !ReferenceEquals(voice.Run, run)) throw new OperationCanceledException(token);
                    var now = DateTimeOffset.UtcNow;
                    if (force || now - render > TimeSpan.FromMilliseconds(100)) { RenderTranscript(); render = now; }
                    if (force || now - save > TimeSpan.FromSeconds(1)) { await SaveTurnHistoryAsync(state.Partition, state.Conversation, token); save = now; }
                }, state.Tools, session.ActiveLanguage, ct, session.ElicitAsync, item => CaptureConversationData(scope, item, output));
            ct.ThrowIfCancellationRequested();
            if (streamedSpeech is not null) await streamedSpeech.DrainAsync();
            else if (output.Status == "complete" && RealtimeCurrent(state) && voice.Agent.SpeechModel is { } speech)
            {
                var text = AgentRealtimeResponse.FinalText(output.Message.Parts);
                if (!string.IsNullOrWhiteSpace(text)) await PlayAgentRealtimeSpeechAsync(state, speech, text, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (RealtimeCurrent(state) && !ct.IsCancellationRequested) Show(RealtimeErrorMessage(error), InfoBarSeverity.Error); }
        finally
        {
            if (streamedSpeech is not null) await streamedSpeech.DisposeAsync();
            if (output is not null)
            {
                if (output.Status == "streaming") output.Status = "interrupted";
                try { await SaveTurnHistoryAsync(state.Partition, state.Conversation); }
                catch { if (RealtimeCurrent(state)) Show(DesktopResources.Get("HistorySaveFailed"), InfoBarSeverity.Error); }
            }
            if (ReferenceEquals(voice.Run, run)) voice.Run = null;
            activeMcpTurn = null; activeConversationTools = null;
            if (RealtimeCurrent(state)) RenderTranscript(); run.Dispose();
        }
    }
    private async Task<IStreamingSpeechSession> OpenAgentStreamingSpeechAsync(JsonObject model, Action started, CancellationToken ct)
    {
        var id = DesktopAgent.Text(model["id"]); var provider = DesktopAgent.Provider(id);
        var options = model["providerMetadata"]?[provider] as JsonObject ?? new();
        var openai = OpenAiRealtimeSpeech.IsModel(id);
        var config = openai ? OpenAiRealtimeSpeech.Session(id, options) : null;
        var protocol = openai ? null : StreamingSpeechProtocol.Create(id, options);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var token = await new DesktopRealtimeTokenClient(client, http).GetAsync(new() { ["model"] = id,
            ["providerOptions"] = new JsonObject { [provider] = config is null ? new JsonObject() : new JsonObject { ["session"] = config.DeepClone() } } }, AgentAudioHeaders(model), provider, timeout.Token);
        var output = new StreamingSpeechOutput(protocol?.SampleRate ?? OpenAiRealtimeSpeech.SampleRate, started);
        try
        {
            IStreamingSpeechSession session = config is not null
                ? await OpenAiRealtimeSpeechSession.OpenAsync(config, token, output.Push, ct, output.Dispose)
                : await StreamingSpeechSession.OpenAsync(protocol!, token, output.Push, ct);
            ct.ThrowIfCancellationRequested();
            return new PlayedStreamingSpeechSession(session, output, ct);
        }
        catch { output.Dispose(); throw; }
    }
    private async Task PlayAgentRealtimeSpeechAsync(RealtimeState state, JsonObject model, string text, CancellationToken ct)
    {
        var voice = state.AgentVoice!;
        try
        {
            var preferences = new SpeechPreferences { ProviderOptions = (model["providerMetadata"] as JsonObject ?? new()).Where(pair => pair.Value is JsonObject)
                .ToDictionary(pair => pair.Key, pair => (JsonObject)pair.Value!.DeepClone(), StringComparer.OrdinalIgnoreCase) };
            var id = DesktopAgent.Text(model["id"]);
            if (OpenAiRealtimeSpeech.IsModel(id)) throw new InvalidOperationException("OpenAI Realtime speech is unavailable; refresh the model catalog.");
            var result = await new DesktopSpeechClient(client, http).GenerateAsync(new(id, id) { ModelType = "speech" }, text, preferences, ct, AgentAudioHeaders(model));
            ct.ThrowIfCancellationRequested(); if (!RealtimeCurrent(state)) return;
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream.GetOutputStreamAt(0))) { writer.WriteBytes(result.Audio); await writer.StoreAsync(); }
            stream.Seek(0);
            using var source = MediaSource.CreateFromStream(stream, result.MediaType);
            using var player = new MediaPlayer { AutoPlay = false }; voice.Player = player;
            var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            player.MediaEnded += (_, _) => finished.TrySetResult();
            player.MediaFailed += (_, _) => finished.TrySetException(new GatewayException(DesktopResources.Get("SpeechPlaybackFailed")));
            try { player.Source = source; player.Play(); await finished.Task.WaitAsync(ct); }
            finally { player.Pause(); player.Source = null; if (ReferenceEquals(voice.Player, player)) voice.Player = null; }
        }
        catch (OperationCanceledException) { throw; }
        catch { if (RealtimeCurrent(state) && !ct.IsCancellationRequested) Show(DesktopResources.Get("AgentRealtimeSpeechFailed"), InfoBarSeverity.Warning); }
    }
    private async Task FinishAgentRealtimeStopAsync(RealtimeState state)
    {
        await Task.Yield();
        var voice = state.AgentVoice!; voice.Turns?.Dispose(); CancelAgentRealtimeRun(state); state.Lifetime.Cancel(); voice.Draft = null;
        try { if (voice.Connection is not null) await voice.Connection.DisposeAsync(); await state.Pump; await voice.Queue; }
        catch { /* Teardown must continue after transport/device loss. */ }
        activeMcpTurn = null; activeSharedFileTools?.Dispose(); activeSharedFileTools = null;
        try { if (state.Conversation.Messages.Count > 0) await history.SaveAsync(state.Partition, state.Conversation); state.HistorySaved = true; }
        catch { if (!closing) Show(DesktopResources.Get("HistorySaveFailed"), InfoBarSeverity.Error); }
        if (!closing && state.Partition == session.HistoryPartition)
        {
            realtimeMute.IsChecked = false; UpdateRealtimeControls();
            if (activePage == DesktopPage.Realtime) RenderTranscript();
            await LoadHistoryAsync(CancellationToken.None);
        }
    }
}
