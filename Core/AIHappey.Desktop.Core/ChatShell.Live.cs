using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Vercel.Models;
using Microsoft.UI.Xaml.Controls;

namespace AIHappey.Desktop.Core;

public sealed partial class ChatShell
{
    private sealed class LiveResponseBatch
    {
        public List<Task> Results { get; } = [];
        public bool Completed { get; set; }
        public bool Continued { get; set; }
    }
    private Task SendLiveAsync(RealtimeState state, string type, JsonObject? fields = null)
    {
        var message = fields is null ? new JsonObject() : (JsonObject)fields.DeepClone();
        message["type"] = type; message["event_id"] = Guid.NewGuid().ToString("N");
        return state.Connection!.SendAsync(message, state.Lifetime.Token);
    }
    private async Task AppendLiveAsync(RealtimeState state, string text, string? delegation, string channel = "commentary")
    {
        var content = new StringBuilder();
        // UTF-8 byte budget is a conservative upper bound on token count (500 per append).
        foreach (var rune in text.EnumerateRunes())
        {
            if (Encoding.UTF8.GetByteCount(content.ToString()) + rune.Utf8SequenceLength > 450)
            {
                await SendLiveAsync(state, "session." + channel + ".append", new() { ["content"] = content.ToString(), ["delegation_id"] = delegation }); content.Clear();
            }
            content.Append(rune.ToString());
        }
        await SendLiveAsync(state, "session." + channel + ".append", new() { ["content"] = content.ToString(), ["delegation_id"] = delegation });
    }
    private async Task SendLiveMessageAsync(RealtimeState state, UIMessage message)
    {
        state.LiveRevision++;
        state.Conversation.Messages.Add(new() { Message = message });
        await history.SaveAsync(state.Partition, state.Conversation);
        if (state.LiveResponses)
        {
            await SendLiveAsync(state, "response.item.create", new() { ["item"] = new JsonObject { ["type"] = "message", ["role"] = "user", ["content"] = RealtimeTranscript.InputContent(message, false) } });
            await SendLiveAsync(state, "response.create");
        }
        else QueueLiveClient(state, null, double.MaxValue);
    }
    private async Task HandleLiveEventAsync(RealtimeState state, JsonObject value)
    {
        var eventId = value["event_id"]?.ToString();
        if (eventId is not null && !state.LiveEvents.Add(eventId)) return;
        if (state.LiveEvents.Count > 20000) throw new InvalidOperationException("Live event limit reached; restart the session.");
        var type = value["type"]?.ToString();
        if (type == "error")
        {
            // Asynchronous configuration validation is nonfatal and does not roll settings back.
            Show(GatewayErrors.Display(value["error"]?["message"]?.ToString() ?? "Live command failed."), InfoBarSeverity.Error); return;
        }
        if (type == "session.input_transcript.delta") state.LiveRevision++;
        if (state.Transcript.Apply(value))
        {
            RenderTranscript();
            if (DateTimeOffset.UtcNow - state.LastSave > TimeSpan.FromSeconds(1) || type == "session.closed")
            { state.LastSave = DateTimeOffset.UtcNow; await history.SaveAsync(state.Partition, state.Conversation); }
        }
        if (type == "session.closed") { _ = StopRealtimeAsync(); return; }
        if (type == "session.delegation.created" && value["delegation"] is JsonObject delegation)
        {
            var id = delegation["id"]?.ToString();
            if (id is null || !state.Calls.Add("delegation:" + id)) return;
            if (delegation["response_id"] is { } responseId) state.LiveResponseIds[id] = responseId.ToString();
            if (!state.LiveResponses && delegation["target"]?.ToString() == "client") QueueLiveClient(state, id, value["offset_ms"]?.GetValue<double>() ?? 0);
        }
        if (type != "response.event" || !state.LiveResponses || value["event"] is not JsonObject nested) return;
        var delegationId = value["delegation_id"]?.ToString() ?? "";
        var nestedType = nested["type"]?.ToString();
        if (nestedType == "response.created" && nested["response"]?["id"] is { } created) state.LiveResponseIds[delegationId] = created.ToString();
        var response = nested["response"]?["id"]?.ToString() ?? nested["response_id"]?.ToString() ?? state.LiveResponseIds.GetValueOrDefault(delegationId);
        if (response is null) return;
        if (!state.LiveBatches.TryGetValue(response, out var batch)) state.LiveBatches[response] = batch = new();
        if (nestedType == "response.output_item.done" && nested["item"] is JsonObject item)
        {
            if (item["type"]?.ToString() == "function_call") { QueueRealtimeTool(state, item); batch.Results.Add(state.ToolQueue); }
            else if (item["type"]?.ToString() == "mcp_approval_request" && state.Calls.Add("approval:" + item["id"]))
                batch.Results.Add(ReturnLiveMcpApprovalAsync(state, item));
            else if (item["type"]?.ToString() is "custom_tool_call" or "computer_call" or "apply_patch_call"
                || item["type"]?.ToString() == "shell_call" && item["environment"]?["type"]?.ToString() == "local"
                || item["type"]?.ToString() == "tool_search_call" && item["execution"]?.ToString() == "client")
                throw new NotSupportedException("This client has no authorized executor for " + item["type"] + ".");
        }
        if (nestedType == "response.completed") batch.Completed = true;
        if (nestedType is "response.failed" or "response.incomplete") Show("The delegated response did not complete.", InfoBarSeverity.Error);
        if (batch.Completed && batch.Results.Count > 0 && !batch.Continued)
        {
            batch.Continued = true;
            // Do not block the event pump while tools or approval dialogs are active.
            _ = ContinueLiveResponseAsync(state, batch);
        }
    }
    private async Task ContinueLiveResponseAsync(RealtimeState state, LiveResponseBatch batch)
    {
        try { await Task.WhenAll(batch.Results); if (RealtimeCurrent(state)) await SendLiveAsync(state, "response.create"); }
        catch (OperationCanceledException) when (state.Lifetime.IsCancellationRequested) { }
        catch { if (RealtimeCurrent(state)) Show("Live tool continuation failed.", InfoBarSeverity.Error); }
    }
    private async Task ReturnLiveMcpApprovalAsync(RealtimeState state, JsonObject item)
    {
        var part = JsonSerializer.SerializeToElement(new JsonObject { ["input"] = item.DeepClone() });
        var decision = await ApproveToolAsync(new(item["id"]!.ToString(), item["id"]!.ToString(), item["name"]?.ToString() ?? "MCP", item["server_label"]?.ToString() ?? "MCP", part), state.DelegationLifetime.Token);
        if (RealtimeCurrent(state)) await SendLiveAsync(state, "response.item.create", new() { ["item"] = new JsonObject { ["type"] = "mcp_approval_response", ["approval_request_id"] = item["id"]!.DeepClone(), ["approve"] = decision.Approved } });
    }
    private void QueueLiveClient(RealtimeState state, string? delegation, double offset)
    {
        var revision = state.LiveRevision;
        var context = state.Conversation.Messages.Where(message => message.Message.Metadata?.GetValueOrDefault("end_ms") is not double end || end <= offset)
            .Where(PortableConversations.CanReplay).Select(message => JsonSerializer.Deserialize<UIMessage>(JsonSerializer.Serialize(message.Message, PortableConversations.Json), PortableConversations.Json)!).ToArray();
        state.ClientQueue = RunLiveClientAsync(state, state.ClientQueue, delegation, revision, context);
    }
    private async Task RunLiveClientAsync(RealtimeState state, Task previous, string? delegation, int revision, UIMessage[] context)
    {
        try
        {
            await AppendLiveAsync(state, "", delegation, "thinking");
            try { await previous; } catch { /* A failed prior task does not poison subsequent work. */ }
            if (!RealtimeCurrent(state) || revision != state.LiveRevision) return;
            var selected = state.Preferences.LiveClientTarget;
            if (string.IsNullOrWhiteSpace(selected)) throw new InvalidOperationException("Select a Live client agent or chat model in settings.");
            var service = state.Preferences.LiveClientAgent ? ServiceKind.Agents : ServiceKind.Ai;
            JsonObject? agentRequest = null;
            if (service == ServiceKind.Agents)
            {
                var available = await AgentTargetsAsync(state.DelegationLifetime.Token);
                if (!available.Any(target => target.Id == selected)) throw new InvalidOperationException("Selected client agent is unavailable.");
                var workflow = session.Settings.AgentWorkflow.Clone(); workflow.Type = "sequential";
                agentRequest = DesktopAgentRequests.Build([selected], available, localAgents, workflow);
                selected = available.First(target => target.Id == selected).RemoteAgentId ?? selected;
            }
            var work = new Conversation { Service = service, Target = selected };
            foreach (var message in context) work.Messages.Add(new() { Message = message });
            var output = new ConversationMessage { Message = new UIMessage { Id = Guid.NewGuid().ToString("N"), Role = Role.assistant, Metadata = new() { ["realtime"] = true, ["delegation_id"] = delegation ?? "", ["backend"] = true } }, Status = "streaming" };
            work.Messages.Add(output); state.Conversation.Messages.Add(output);
            var chat = session.Settings.Chat.Clone();
            await DesktopChatTurn.RunAsync(work, output,
                (messages, token) => client.StreamAsync(service, selected, work.Id, messages, token, chat, mcp: state.Tools, agentRequest: agentRequest),
                ApproveToolAsync, async (_, token) => { if (RealtimeCurrent(state)) { RenderTranscript(); await history.SaveAsync(state.Partition, state.Conversation); } },
                state.Tools, session.ActiveLanguage, state.DelegationLifetime.Token, session.ElicitAsync);
            if (!RealtimeCurrent(state) || revision != state.LiveRevision) return;
            if (output.Status != "complete") throw new InvalidOperationException("The delegated operation did not complete.");
            await AppendLiveAsync(state, output.Text, delegation);
        }
        catch (OperationCanceledException) when (state.Lifetime.IsCancellationRequested || state.DelegationLifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (RealtimeCurrent(state) && revision == state.LiveRevision)
            {
                Show(GatewayErrors.Display(error.Message), InfoBarSeverity.Error);
                try { await AppendLiveAsync(state, "The requested operation could not be verified. No successful outcome is confirmed.", delegation); } catch { }
            }
        }
    }
}
