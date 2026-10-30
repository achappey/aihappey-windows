using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Vercel.Models;
using FluentIcons.Common;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace AIHappey.Desktop.Core;

public sealed partial class ChatShell
{
    private readonly Grid realtimeHeader = new() { Name = "RealtimeHeader", Visibility = Visibility.Collapsed };
    private readonly AutoSuggestBox realtimeTarget = new() { Name = "RealtimeModelPicker", MinWidth = 180, MaxWidth = 420, PlaceholderText = DesktopResources.Get("SelectModel") };
    private readonly Button realtimeFavorite = new() { Name = "RealtimeFavorite", Content = DesktopIcons.Create(Icon.Star), Width = 40, Height = 40 };
    private readonly StackPanel realtimeControls = new() { Name = "RealtimeControls", Orientation = Orientation.Horizontal, Spacing = 4, Visibility = Visibility.Collapsed };
    private readonly ToggleButton realtimeMute = ToolbarControls.CreateModeButton(DesktopIcons.Create(Icon.Mic), DesktopResources.Get("RealtimeMute"));
    private readonly ToggleButton realtimeCamera = ToolbarControls.CreateModeButton(DesktopIcons.Create(Icon.Video), DesktopResources.Get("RealtimeCamera"));
    private readonly ToggleButton realtimeScreen = ToolbarControls.CreateModeButton(DesktopIcons.Create(Icon.Desktop), DesktopResources.Get("RealtimeScreen"));
    private readonly Button realtimeStop = new() { Name = "RealtimeStop", Content = DesktopIcons.Create(Icon.Stop), Width = 40, Height = 40, Padding = new Thickness(0), CornerRadius = new CornerRadius(6), Visibility = Visibility.Collapsed };
    private string realtimeStatus = "", realtimeDevices = "";
    private readonly Image realtimePreview = new() { Name = "RealtimePreview", Height = 160, MaxWidth = 280, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Right, Visibility = Visibility.Collapsed };
    private IReadOnlyList<ChatTarget> realtimeModels = [];
    private RealtimeState? realtime;
    private RealtimeVideoCapture? realtimeCapture;
    private bool realtimeSending, realtimeVideoChanging;
    private Task realtimeCleanup = Task.CompletedTask;
    private sealed class RealtimeState(Conversation conversation, string partition, string model, McpTurnSnapshot tools, RealtimePreferences preferences)
    {
        public Conversation Conversation { get; } = conversation;
        public string Partition { get; } = partition;
        public string Model { get; } = model;
        public bool Google => RealtimeConfiguration.Provider(Model) == "google";
        public bool Live { get; init; }
        public bool LiveResponses => Preferences.ProviderOptions["openai"]?["session"]?["delegation"]?["type"]?.ToString() == "responses";
        public int LiveRevision { get; set; }
        public Task ClientQueue { get; set; } = Task.CompletedTask;
        public HashSet<string> LiveEvents { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> LiveResponseIds { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, LiveResponseBatch> LiveBatches { get; } = new(StringComparer.Ordinal);
        public McpTurnSnapshot Tools { get; } = tools;
        public RealtimePreferences Preferences { get; } = preferences;
        public RealtimeTranscript Transcript { get; } = new(conversation, model);
        public CancellationTokenSource Lifetime { get; } = new();
        public CancellationTokenSource DelegationLifetime { get; } = new();
        public bool Stopping { get; set; }
        public bool HistorySaved { get; set; }
        public IRealtimeSession? Connection { get; set; }
        public bool Connected { get; set; }
        public AgentRealtimeState? AgentVoice { get; init; }
        public bool Reconnecting { get; set; }
        public HashSet<string> Calls { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Executed { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Continued { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, CancellationTokenSource> CallCancellation { get; } = new(StringComparer.Ordinal);
        public Task ToolQueue { get; set; } = Task.CompletedTask;
        public Task Pump { get; set; } = Task.CompletedTask;
        public DateTimeOffset LastSave { get; set; }
        public DateTimeOffset LastRender { get; set; }
    }
    private bool RealtimeCurrent(RealtimeState state) => ReferenceEquals(realtime, state) && !closing && state.Partition == session.HistoryPartition && ReferenceEquals(current, state.Conversation) && !state.Lifetime.IsCancellationRequested;
    private void PrepareRealtime()
    {
        realtimeMute.Name = "RealtimeMute"; realtimeCamera.Name = "RealtimeCamera"; realtimeScreen.Name = "RealtimeScreen";
        foreach (var (control, key) in new (Control, string)[] { (realtimeTarget, "SelectModel"), (realtimeFavorite, "RealtimeFavorite"), (realtimeMute, "RealtimeMute"),
            (realtimeCamera, "RealtimeCamera"), (realtimeScreen, "RealtimeScreen"), (realtimeStop, "Stop") })
        { ControlAppearance.Native(control); ToolbarControls.Label(control, DesktopResources.Get(key)); }
        ToolbarControls.Subtle(realtimeStop);
        AutomationProperties.SetLiveSetting(realtimeMute, AutomationLiveSetting.Polite);
        foreach (var control in new UIElement[] { realtimeMute, realtimeCamera, realtimeScreen, realtimeStop }) realtimeControls.Children.Add(control);
        realtimeTarget.TextChanged += (_, args) =>
        {
            if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
                realtimeTarget.ItemsSource = PickerModels(realtimeModels).Where(m => m.Id.Contains(realtimeTarget.Text, StringComparison.OrdinalIgnoreCase) || m.Label.Contains(realtimeTarget.Text, StringComparison.OrdinalIgnoreCase)).ToArray();
            UpdateRealtimeControls();
        };
        realtimeTarget.GotFocus += (_, _) => { realtimeTarget.ItemsSource = PickerModels(realtimeModels); realtimeTarget.IsSuggestionListOpen = true; };
        realtimeTarget.SuggestionChosen += (_, args) => realtimeTarget.Text = ((ChatTarget)args.SelectedItem).Id;
        realtimeMute.Click += async (_, _) =>
        {
            UpdateRealtimeControls();
            if (realtime?.AgentVoice is { } voice)
            {
                voice.Turns?.SetMuted(realtimeMute.IsChecked == true); voice.Draft = null;
                await voice.Connection!.SetMutedAsync(realtimeMute.IsChecked == true); RenderTranscript(); return;
            }
            if (realtime?.Connection is { } connection) try { await connection.SetMutedAsync(realtimeMute.IsChecked == true); } catch { await RealtimeFailedAsync(); }
        };
        realtimeStop.Click += async (_, _) => await StopRealtimeAndOpenHistoryAsync();
        realtimeCamera.Click += async (_, _) => await ChangeRealtimeVideoAsync(false);
        realtimeScreen.Click += async (_, _) => await ChangeRealtimeVideoAsync(true);
        Unloaded += (_, _) => { welcomeSpeech.Cancel(); if (realtime is not null) realtimeCleanup = StopRealtimeAsync(); };
        Microsoft.Win32.SystemEvents.PowerModeChanged += RealtimePowerChanged;
    }
    private void RealtimePowerChanged(object sender, Microsoft.Win32.PowerModeChangedEventArgs args)
    {
        if (args.Mode == Microsoft.Win32.PowerModes.Suspend) DispatcherQueue.TryEnqueue(() => { welcomeSpeech.Cancel(); DiscardDictation(); transcriptionRealtimeCleanup = StopRealtimeTranscriptionAsync(); realtimeCleanup = StopRealtimeAsync(); });
    }
    private async Task LoadRealtimeAsync(CancellationToken ct, bool useCache = false)
    {
        var partition = session.HistoryPartition;
        var available = useCache && aiModelTargets is not null ? aiModelTargets : await client.ListAsync(ServiceKind.Ai, ct);
        if (closing || partition != session.HistoryPartition) return;
        aiModelTargets = available;
        await LoadModelFavoritesAsync(ct);
        await LoadRealtimeAgentsAsync(ct);
        realtimeModels = ProviderPreferences.Filter(AiModelCatalog.OfType(available, "audio"), session.Settings).Where(m => RealtimeConfiguration.Supported(m.Id)).ToArray();
        if (!realtimeModels.Any(m => m.Id == realtimeTarget.Text))
            realtimeTarget.Text = new[] { session.Settings.AiModels.AudioModel, session.Settings.Realtime.Model, "openai/gpt-realtime-2" }.FirstOrDefault(id => realtimeModels.Any(m => m.Id == id)) ?? realtimeModels.FirstOrDefault()?.Id ?? "";
        realtimeTarget.ItemsSource = PickerModels(realtimeModels); UpdateRealtimeControls();
        if (realtimeModels.Count == 0) Show(DesktopResources.Get("RealtimeNoModels"), InfoBarSeverity.Warning);
    }
    private void UpdateRealtimeControls()
    {
        var visible = activePage == DesktopPage.Realtime;
        realtimeHeader.Visibility = realtimeControls.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible)
        {
            realtimePreview.Visibility = Visibility.Collapsed;
            ((FluentIcons.WinUI.FluentIcon)send.Content).Icon = Icon.Send;
            AutomationProperties.SetHelpText(send, "");
            return;
        }
        var connected = realtime?.Connected == true;
        realtimeTarget.IsEnabled = realtime is null && !busy;
        UpdateAgentRealtimeControls();
        UpdateModelFavorites();
        realtimeMute.IsEnabled = connected;
        var google = !realtimeAgentMode && RealtimeConfiguration.Provider(realtime?.Model ?? realtimeTarget.Text) == "google";
        realtimeCamera.Visibility = realtimeScreen.Visibility = google ? Visibility.Visible : Visibility.Collapsed;
        realtimeCamera.IsEnabled = realtimeScreen.IsEnabled = connected && !realtimeVideoChanging;
        realtimeStop.IsEnabled = realtime is not null;
        var status = DesktopResources.Get(realtime is null ? "RealtimeReady" : realtime.Reconnecting ? "RealtimeReconnecting"
            : realtimeCapture is not null ? realtimeScreen.IsChecked == true ? "RealtimeSharingScreen" : "RealtimeSharingCamera"
            : connected ? realtimeMute.IsChecked == true ? "RealtimeMuted" : "RealtimeConnected" : "RealtimeConnecting");
        ((FluentIcons.WinUI.FluentIcon)realtimeMute.Content).Icon = realtimeMute.IsChecked == true ? Icon.MicOff : Icon.Mic;
        var muteLabel = DesktopResources.Get(realtimeMute.IsChecked == true ? "RealtimeUnmute" : "RealtimeMute");
        ToolbarControls.Label(realtimeMute, muteLabel);
        if (realtimeDevices.Length > 0) ToolTipService.SetToolTip(realtimeMute, new ToolTip { Content = muteLabel + "\n" + realtimeDevices });
        ToolbarControls.Label(realtimeCamera, DesktopResources.Get(realtimeCamera.IsChecked == true ? "RealtimeStopCamera" : "RealtimeCamera"));
        ToolbarControls.Label(realtimeScreen, DesktopResources.Get(realtimeScreen.IsChecked == true ? "RealtimeStopScreen" : "RealtimeScreen"));
        AutomationProperties.SetItemStatus(realtimeMute, status);
        AutomationProperties.SetHelpText(realtimeMute, realtimeDevices);
        AutomationProperties.SetHelpText(send, status);
        if (status != realtimeStatus)
        {
            realtimeStatus = status;
            FrameworkElementAutomationPeer.FromElement(realtimeMute)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }
        realtimeStop.Visibility = realtime is null ? Visibility.Collapsed : Visibility.Visible;
        // The session stop cancels connection startup as well; never show a second operation-stop button here.
        stop.Visibility = Visibility.Collapsed;
        input.PlaceholderText = DesktopResources.Get(connected ? "AskAnything" : "RealtimeStartHint");
        ((FluentIcons.WinUI.FluentIcon)send.Content).Icon = connected ? Icon.Send : Icon.Live;
        ToolbarControls.Label(send, DesktopResources.Get(connected ? "SendMessage" : "RealtimeStart"));
        send.IsEnabled = initialized && !busy && !realtimeSending && (realtime is null || connected)
            && (realtimeAgentMode ? SelectedRealtimeAgent is { } selected && RealtimeAgentSupported(selected) : realtimeModels.Any(m => m.Id == realtimeTarget.Text));
        dictate.Visibility = Visibility.Collapsed;
    }
    private async Task SendRealtimeAsync(McpSelectedPrompt? selectedPrompt = null)
    {
        welcomeSpeech.Cancel();
        if (realtimeAgentMode) { await SendAgentRealtimeAsync(selectedPrompt); return; }
        await StopRealtimeTranscriptionAsync();
        if (realtimeSending || busy || closing || historyDialogOpen || !initialized) return;
        realtimeSending = true; UpdateRealtimeControls();
        try
        {
            await realtimeCleanup;
            if (realtimeModels.FirstOrDefault(m => m.Id == realtimeTarget.Text) is not { } model) return;
            var partition = session.HistoryPartition;
            UIMessage? message = null;
            if (selectedPrompt is not null || !string.IsNullOrWhiteSpace(input.Text) || contextAttachments.Count > 0 || selectedResources.Count > 0)
            {
                var prepared = await ComposerAttachments.PrepareAsync(selectedPrompt is null ? input.Text.Trim() : "", contextAttachments.ToArray(), ServiceKind.Ai,
                    session.Settings.ConvertAttachmentsToText, documentExtractor, CancellationToken.None, selectedResources.ToArray(), selectedPrompt?.Parts);
                RealtimeTranscript.InputContent(prepared.Message, RealtimeConfiguration.Provider(model.Id) == "google");
                message = prepared.Message;
                if (RealtimeConfiguration.Provider(model.Id) is "smallestai" or "fishaudio" && message.Parts.Any(part => part.Type != "text"))
                    throw new NotSupportedException("This voice agent accepts text input only.");
                if (prepared.Warnings.Count > 0) Show(string.Join("\n", prepared.Warnings), InfoBarSeverity.Warning);
            }
            if (partition != session.HistoryPartition || closing || activePage != DesktopPage.Realtime) return;
            if (realtime is null)
            {
                await RunAsync(async ct =>
                {
                    var preferences = session.Settings.Realtime.Clone(); preferences.Model = model.Id; preferences.Validate();
                    var chat = session.Settings.Chat.Clone();
                    if (NeedsSkillCatalog(chat)) await LoadRuntimeSkillsAsync(ct);
                    var snapshot = CaptureSkillRuntime(chat);
                    var system = CaptureSystemContext(chat, snapshot);
                    var instructions = string.Join("\n\n", system.Parts.Where(p => p.Type == "text").Select(PortableConversations.Text));
                    var definitions = snapshot.Tools.Select(t => new JsonObject { ["name"] = CatalogProjection.Text(t, "name"), ["description"] = CatalogProjection.Text(t, "description"),
                        ["parameters"] = t.TryGetProperty("inputSchema", out var schema) ? JsonNode.Parse(schema.GetRawText()) : new JsonObject { ["type"] = "object" } }).ToArray();
                    var provider = RealtimeConfiguration.Provider(model.Id);
                    var chatDefaults = new JsonObject();
                    if (provider == "openai" && chat.ProviderMetadata.TryGetValue(provider, out var openai))
                        foreach (var field in new[] { "truncation", "parallel_tool_calls" }) if (openai[field] is { } value) chatDefaults[field] = value.DeepClone();
                    var config = RealtimeConfiguration.Session(model.Id, preferences, instructions, definitions, chat.ToolChoice, chatDefaults);
                    if (provider is not ("smallestai" or "fishaudio") && chat.MaxOutputTokens is { } maximum) config[provider == "openai" ? "max_output_tokens" : "maxOutputTokens"] = maximum;
                    var tokenClient = new DesktopRealtimeTokenClient(client, http);
                    var state = new RealtimeState(current, partition, model.Id, snapshot, preferences) { Live = RealtimeConfiguration.IsLive(model) };
                    realtime = state; UpdateRealtimeControls();
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, state.Lifetime.Token);
                    state.Connection = provider == "openai"
                        ? new OpenAiRealtimeSession(token => tokenClient.GetAsync(RealtimeConfiguration.TokenRequest(model.Id, preferences, config, DateTimeOffset.UtcNow), chat, provider, token),
                            RealtimeConfiguration.UsesSessions(model) ? (sdp, token) => tokenClient.CreateSessionAsync(model.Id, sdp,
                                state.Live ? RealtimeConfiguration.LiveSession(preferences, instructions)
                                    : new JsonObject { ["providerOptions"] = new JsonObject { ["openai"] = new JsonObject { ["session"] = config.DeepClone() } } }, chat, token) : null, state.Live)
                        : provider == "fishaudio" ? new FishAudioRealtimeSession(token => tokenClient.GetFishSessionAsync(RealtimeConfiguration.TokenRequest(model.Id, preferences, config, DateTimeOffset.UtcNow), chat, token),
                            chat.ToolChoice == "none" ? [] : definitions.Select(tool => tool["name"]!.ToString()))
                        : provider == "smallestai" ? new SmallestRealtimeSession(token => tokenClient.GetAsync(RealtimeConfiguration.TokenRequest(model.Id, preferences, config, DateTimeOffset.UtcNow), chat, provider, token))
                        : new GoogleRealtimeSession(RealtimeConfiguration.GoogleSetup(model.Id, config), (handle, token) =>
                        {
                            var resumed = (JsonObject)config.DeepClone();
                            if (handle is not null) resumed["sessionResumption"] = new JsonObject { ["handle"] = handle };
                            return tokenClient.GetAsync(RealtimeConfiguration.TokenRequest(model.Id, preferences, resumed, DateTimeOffset.UtcNow), chat, provider, token);
                        });
                    try
                    {
                        await state.Connection.StartAsync(linked.Token);
                        linked.Token.ThrowIfCancellationRequested();
                        if (!RealtimeCurrent(state)) throw new OperationCanceledException(linked.Token);
                        state.Connected = true; current.Service = ServiceKind.Ai; current.Target = model.Id; current.Metadata["realtime"] = true;
                        if (current.Messages.Count == 0) current.Title = DesktopResources.Get("RealtimeConversation");
                        await state.Connection.SetMutedAsync(realtimeMute.IsChecked == true);
                        state.Pump = PumpRealtimeAsync(state);
                        var next = session.Settings.Clone(); next.Realtime = preferences;
                        await SettingsStore.SaveAsync(session.DataDirectory, next); session.Settings = next;
                        Show(DesktopResources.Get("RealtimeHeadphones"), InfoBarSeverity.Informational);
                    }
                    catch (Exception error) { await StopRealtimeAsync(); throw new GatewayException(RealtimeErrorMessage(error)); }
                });
            }
            if (realtime is { Connected: true } active && RealtimeCurrent(active) && message is not null)
            {
                if (active.Live)
                {
                    await SendLiveMessageAsync(active, message);
                    if (selectedPrompt is null) input.Text = ""; ResetContext(); RenderTranscript();
                    return;
                }
                var content = RealtimeTranscript.InputContent(message, active.Google);
                await active.Connection!.SendAsync(active.Google
                    ? new JsonObject { ["clientContent"] = new JsonObject { ["turns"] = new JsonArray(new JsonObject { ["role"] = "user", ["parts"] = content }), ["turnComplete"] = true } }
                    : new JsonObject { ["type"] = "conversation.item.create", ["item"] = new JsonObject { ["type"] = "message", ["role"] = "user", ["content"] = content } }, active.Lifetime.Token);
                message = new UIMessage { Id = message.Id, Role = message.Role, Parts = message.Parts, Metadata = message.Metadata is null ? new() : new(message.Metadata) };
                message.Metadata!["realtime"] = true; message.Metadata["model"] = active.Model;
                // Atoms emits authoritative transcripts for text input too; don't persist a duplicate.
                if (RealtimeConfiguration.Provider(active.Model) != "smallestai") active.Conversation.Messages.Add(new() { Message = message });
                if (selectedPrompt is null) input.Text = ""; ResetContext(); RenderTranscript();
                await history.SaveAsync(active.Partition, active.Conversation);
                if (!active.Google) await active.Connection.SendAsync(new() { ["type"] = "response.create" }, active.Lifetime.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { Show(RealtimeErrorMessage(error), InfoBarSeverity.Error); }
        finally { realtimeSending = false; UpdateRealtimeControls(); }
    }
    private async Task PumpRealtimeAsync(RealtimeState state)
    {
        try
        {
            await foreach (var value in state.Connection!.Events.ReadAllAsync(state.Lifetime.Token))
            {
                if (state.Stopping)
                {
                    // Only final history events are accepted while the transport finalizes.
                    state.Transcript.Apply(value);
                    continue;
                }
                if (!RealtimeCurrent(state)) break;
                var type = value["type"]?.ToString();
                if (state.Live)
                {
                    await HandleLiveEventAsync(state, value);
                    continue;
                }
                if (type == "error") throw new RealtimeFailureException("provider-event", "The provider reported a realtime session error.");
                if (type == "audio.processing")
                {
                    var aec = value["echoCancellationAvailable"]?.GetValue<bool>() == true;
                    var reference = value["echoReferenceConfigured"]?.GetValue<bool>() == true;
                    var devices = DesktopResources.Format("RealtimeDevices", value["inputDevice"]?.ToString() ?? "", value["outputDevice"]?.ToString() ?? "");
                    realtimeDevices = devices + "\n" + DesktopResources.Get(aec ? "RealtimeAecAvailable" : "RealtimeAecUnavailable")
                        + (reference ? "\n" + DesktopResources.Get("RealtimeAecReference") : "");
                    AutomationProperties.SetHelpText(realtimeMute, realtimeDevices);
                    ToolTipService.SetToolTip(realtimeMute, new ToolTip { Content = AutomationProperties.GetName(realtimeMute) + "\n" + realtimeDevices });
                    if (!aec) Show(DesktopResources.Get("RealtimeAecUnavailable"), InfoBarSeverity.Warning);
                    continue;
                }
                if (type is "session.reconnecting" or "session.resumed")
                {
                    state.Reconnecting = type == "session.reconnecting"; state.Connected = !state.Reconnecting;
                    UpdateRealtimeControls(); continue;
                }
                if (type == "fishaudio.session.closed")
                {
                    state.Conversation.Metadata["realtimeEndReason"] = value["reason"]?.ToString() ?? "connection_lost";
                    break;
                }
                if (type == "session.error" && RealtimeConfiguration.Provider(state.Model) == "fishaudio")
                {
                    Show("Fish Audio reported a session error (" + value["code"]?.ToString() + ").", InfoBarSeverity.Warning);
                    continue;
                }
                var changed = state.Transcript.Apply(value);
                if (type == "response.output_item.done" && value["item"] is JsonObject item && item["type"]?.ToString() == "function_call") QueueRealtimeTool(state, item);
                if (type == "tool.cancelled" && value["call_id"] is { } cancelled && state.CallCancellation.TryGetValue(cancelled.ToString(), out var cancellation)) cancellation.Cancel();
                if (type == "response.done" && value["response"] is JsonObject response)
                {
                    var calls = (response["output"] as JsonArray ?? new()).OfType<JsonObject>().Where(i => i["type"]?.ToString() == "function_call").ToArray();
                    foreach (var call in calls) QueueRealtimeTool(state, call);
                    if (!state.Google && calls.Length > 0 && state.Continued.Add(response["id"]?.ToString() ?? "")) state.ToolQueue = ContinueRealtimeAsync(state, state.ToolQueue);
                }
                if (changed)
                {
                    if (DateTimeOffset.UtcNow - state.LastRender > TimeSpan.FromMilliseconds(100) || type?.EndsWith("done", StringComparison.Ordinal) == true)
                    { state.LastRender = DateTimeOffset.UtcNow; RenderTranscript(); }
                    if (DateTimeOffset.UtcNow - state.LastSave > TimeSpan.FromSeconds(1) || type == "response.done")
                    { state.LastSave = DateTimeOffset.UtcNow; await history.SaveAsync(state.Partition, state.Conversation); }
                }
            }
        }
        catch (OperationCanceledException) when (state.Lifetime.IsCancellationRequested) { }
        catch (Exception error) { if (RealtimeCurrent(state)) Show(RealtimeErrorMessage(error), InfoBarSeverity.Error); }
        finally { if (ReferenceEquals(realtime, state)) _ = StopRealtimeAsync(); }
    }
    private async Task ContinueRealtimeAsync(RealtimeState state, Task previous)
    {
        await previous;
        if (RealtimeCurrent(state)) try { await state.Connection!.SendAsync(new() { ["type"] = "response.create" }, state.Lifetime.Token); } catch { await RealtimeFailedAsync(); }
    }
    private void QueueRealtimeTool(RealtimeState state, JsonObject call)
    {
        var id = call["call_id"]?.ToString();
        if (string.IsNullOrEmpty(id) || !state.Calls.Add(id)) return;
        if (state.Calls.Count > 1000) { state.Lifetime.Cancel(); return; }
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(state.Lifetime.Token, state.DelegationLifetime.Token);
        state.CallCancellation[id] = cancellation;
        state.ToolQueue = ExecuteRealtimeToolAsync(state, state.ToolQueue, (JsonObject)call.DeepClone(), cancellation.Token);
    }
    private async Task ExecuteRealtimeToolAsync(RealtimeState state, Task previous, JsonObject call, CancellationToken ct)
    {
        await previous;
        if (!RealtimeCurrent(state) || ct.IsCancellationRequested) return;
        var id = call["call_id"]!.ToString(); var name = call["name"]?.ToString() ?? "";
        try
        {
            var arguments = JsonNode.Parse(call["arguments"]?.ToString() ?? "{}") as JsonObject ?? throw new JsonException();
            var output = state.Transcript.Draft("tool:" + id, Role.assistant);
            output.Message.Parts.Add(PortableConversations.Part(new JsonObject { ["type"] = "tool-" + name, ["toolCallId"] = id,
                ["state"] = "approval-requested", ["input"] = arguments, ["approval"] = new JsonObject { ["id"] = id } }));
            // Match desktop semantics: execute client tools, then review the result before releasing it to the provider.
            await DesktopMcpToolExecution.ExecutePendingAsync(output, state.Tools, state.Executed, session.ActiveLanguage, ct, session.ElicitAsync);
            if (!RealtimeCurrent(state)) return;
            RenderTranscript();
            foreach (var pending in DesktopToolApprovals.Pending(output.Message))
                DesktopToolApprovals.Respond(output.Message, pending, await ApproveToolAsync(pending, ct));
            ct.ThrowIfCancellationRequested();
            var part = PortableConversations.Node(output.Message.Parts.Last());
            var approved = part["approval"]?["approved"]?.GetValue<bool>() == true;
            var result = approved ? part["output"]?.DeepClone() ?? new JsonObject { ["isError"] = true, ["error"] = part["errorText"]?.DeepClone() }
                : new JsonObject { ["isError"] = true, ["error"] = "Tool result not approved." };
            output.Status = "complete";
            await state.Connection!.SendAsync(state.Google
                ? new JsonObject { ["toolResponse"] = new JsonObject { ["functionResponses"] = new JsonArray(new JsonObject { ["id"] = id, ["name"] = name, ["response"] = new JsonObject { ["result"] = result } }) } }
                : new JsonObject { ["type"] = state.Live ? "response.item.create" : "conversation.item.create", ["item"] = new JsonObject { ["type"] = "function_call_output", ["call_id"] = id, ["output"] = result.ToJsonString(),
                    ["caller"] = call["caller"]?.DeepClone() } }, ct);
            await history.SaveAsync(state.Partition, state.Conversation); if (RealtimeCurrent(state)) RenderTranscript();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch { if (RealtimeCurrent(state)) await RealtimeFailedAsync(); }
    }
    private async Task RealtimeFailedAsync() { Show(DesktopResources.Get("RealtimeFailed"), InfoBarSeverity.Error); await StopRealtimeAsync(); }
    private Task StopRealtimeAsync()
    {
        var state = realtime; realtime = null;
        if (state is not null) { state.Stopping = true; state.DelegationLifetime.Cancel(); }
        realtimeDevices = "";
        UpdateRealtimeControls();
        // Detach synchronously, and make the next Start wait for all previous device teardown.
        if (state is null && realtimeCapture is null) return realtimeCleanup;
        return realtimeCleanup = FinishRealtimeStopAsync(state);
    }
    private async Task FinishRealtimeStopAsync(RealtimeState? state)
    {
        if (state?.AgentVoice is not null) { await FinishAgentRealtimeStopAsync(state); return; }
        await StopRealtimeVideoAsync();
        if (state?.Connection is { } connection)
        {
            try { await connection.DisposeAsync(); } catch { /* Best effort after device/network loss. */ }
            // Dispose completes the event stream. Drain final transcript/usage before saving.
            try { await state.Pump.WaitAsync(TimeSpan.FromSeconds(2)); } catch { /* Bounded shutdown after a failed transport. */ }
            state.Lifetime.Cancel();
            state.Transcript.Stop();
            try { if (state.Conversation.Messages.Count > 0) await history.SaveAsync(state.Partition, state.Conversation); state.HistorySaved = true; }
            catch { if (!closing) Show(DesktopResources.Get("HistorySaveFailed"), InfoBarSeverity.Error); }
        }
        if (!closing)
        {
            realtimeMute.IsChecked = false; UpdateRealtimeControls();
            if (activePage == DesktopPage.Realtime) RenderTranscript();
            if (state is not null && state.Partition == session.HistoryPartition) await LoadHistoryAsync(CancellationToken.None);
        }
    }
    private async Task StopRealtimeAndOpenHistoryAsync()
    {
        var state = realtime;
        await StopRealtimeAsync();
        if (state is { HistorySaved: true } && !closing && state.Partition == session.HistoryPartition && ReferenceEquals(current, state.Conversation))
            await OpenConversationAsync(state.Conversation);
    }
    private static string RealtimeErrorMessage(Exception error)
    {
        if (error is RealtimeFailureException safe) return DesktopResources.Get("RealtimeFailed") + "\n" + safe;
        if (error is GatewayException gateway) return GatewayErrors.Display(gateway.Message) ?? DesktopResources.Get("RealtimeFailed");
        return DesktopResources.Get("RealtimeFailed");
    }
    private async Task ChangeRealtimeVideoAsync(bool screen)
    {
        if (realtimeVideoChanging || realtime is not { Connected: true, Google: true } state) return;
        var requested = screen ? realtimeScreen.IsChecked == true : realtimeCamera.IsChecked == true;
        realtimeVideoChanging = true; UpdateRealtimeControls();
        try
        {
            await StopRealtimeVideoAsync();
            if (!requested || !RealtimeCurrent(state)) return;
            var capture = new RealtimeVideoCapture(); realtimeCapture = capture;
            var started = await capture.StartAsync(screen, Microsoft.UI.Win32Interop.GetWindowFromWindowId(XamlRoot.ContentIslandEnvironment.AppWindowId), state.Preferences,
                async (jpeg, ct) =>
                {
                    if (!RealtimeCurrent(state)) throw new OperationCanceledException(ct);
                    await state.Connection!.SendVideoAsync(jpeg, ct);
                    DispatcherQueue.TryEnqueue(async () =>
                    {
                        if (!RealtimeCurrent(state) || !ReferenceEquals(realtimeCapture, capture)) return;
                        try
                        {
                            using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
                            using var writer = new Windows.Storage.Streams.DataWriter(stream); writer.WriteBytes(jpeg); await writer.StoreAsync(); stream.Seek(0);
                            var image = new BitmapImage(); await image.SetSourceAsync(stream);
                            if (ReferenceEquals(realtimeCapture, capture)) { realtimePreview.Source = image; realtimePreview.Visibility = Visibility.Visible; }
                        }
                        catch { /* Preview failure must not leak or keep a device alive. */ }
                    });
                }, () => DispatcherQueue.TryEnqueue(async () =>
                {
                    if (!ReferenceEquals(realtimeCapture, capture)) return;
                    await StopRealtimeVideoAsync();
                    if (RealtimeCurrent(state)) Show(DesktopResources.Get("RealtimeVideoEnded"), InfoBarSeverity.Warning);
                }), state.Lifetime.Token);
            if (!started || !RealtimeCurrent(state)) { await StopRealtimeVideoAsync(); return; }
            realtimeScreen.IsChecked = screen; realtimeCamera.IsChecked = !screen;
        }
        catch (OperationCanceledException) { await StopRealtimeVideoAsync(); }
        catch { await StopRealtimeVideoAsync(); Show(DesktopResources.Get("RealtimeVideoFailed"), InfoBarSeverity.Warning); }
        finally { realtimeVideoChanging = false; UpdateRealtimeControls(); }
    }
    private async Task StopRealtimeVideoAsync()
    {
        var capture = realtimeCapture; realtimeCapture = null;
        realtimeCamera.IsChecked = realtimeScreen.IsChecked = false;
        realtimePreview.Source = null; realtimePreview.Visibility = Visibility.Collapsed;
        if (capture is not null) await capture.DisposeAsync();
    }
}
