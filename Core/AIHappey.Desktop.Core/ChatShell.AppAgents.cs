using System.Text.Json;
using AIHappey.Vercel.Models;

namespace AIHappey.Desktop.Core;

public sealed partial class ChatShell
{
    private IDesktopAppAgentClient appAgentClient;
    private readonly CancellationTokenSource appAgentLifetime = new();
    private readonly HashSet<Task> appAgentTasks = [];
    private CancellationTokenSource? welcomeRequest;
    private string? welcomeRequestKey;
    private bool appAgentsReady;

    private DesktopAgent? AssignedAppAgent(AppAgentRole role) => localAgentPartition == session.AgentPartition
        ? localAgents.FirstOrDefault(a => a.Name == session.Settings.SideInferenceAgentNames.Get(role))?.Clone() : null;

    private void RefreshWelcome()
    {
        if (closing || current.Messages.Count != 0)
        {
            welcomeRequest?.Cancel(); welcomeRequestKey = null;
            return;
        }
        if (!appAgentsReady) return;
        var agent = AssignedAppAgent(AppAgentRole.WelcomeMessage);
        var key = JsonSerializer.Serialize(new { current.Id, session.HistoryPartition, session.ActiveLanguage,
            user = session.Host.UserContext?.Name, assignment = session.Settings.SideInferenceAgentNames.Get(AppAgentRole.WelcomeMessage),
            definition = agent?.Definition.ToJsonString(), models = aiModelTargets?.Select(m => new { m.Id, m.ProviderKey }).ToArray() });
        if (key == welcomeRequestKey) return;
        welcomeRequest?.Cancel(); welcomeRequestKey = key;
        welcome.Text = DesktopResources.Get("Welcome");
        if (agent is null) return; // None/missing agents never cause discovery or inference.
        var lifetime = CancellationTokenSource.CreateLinkedTokenSource(appAgentLifetime.Token);
        lifetime.CancelAfter(TimeSpan.FromSeconds(30)); welcomeRequest = lifetime;
        var input = DesktopAppAgentClient.WelcomeInput(session.ActiveLanguage, session.Host.UserContext?.Name, DateTimeOffset.UtcNow);
        _ = TrackAppAgentAsync(async () =>
        {
            try
            {
                var models = aiModelTargets ?? await client.ListAsync(ServiceKind.Ai, lifetime.Token);
                var text = await appAgentClient.InvokeAsync(agent, models, input, DesktopResources.Get("Welcome"), lifetime.Token);
                if (!closing && !lifetime.IsCancellationRequested && key == welcomeRequestKey && current.Messages.Count == 0)
                    welcome.Text = text;
            }
            catch (Exception) { /* Keep the localized fallback; this is not a main-chat error. */ }
            finally { if (ReferenceEquals(welcomeRequest, lifetime)) welcomeRequest = null; lifetime.Dispose(); }
        });
    }

    private async Task TrackAppAgentAsync(Func<Task> action)
    {
        var task = action(); appAgentTasks.Add(task);
        try { await task; }
        catch (Exception) { System.Diagnostics.Debug.WriteLine("AIHappey app helper: using localized fallback."); }
        finally { appAgentTasks.Remove(task); }
    }

    private void StartConversationNaming(Conversation conversation, string partition, UIMessage firstMessage, string expectedTitle)
    {
        var agent = AssignedAppAgent(AppAgentRole.ConversationName);
        if (agent is null || closing || partition != session.HistoryPartition || conversation.Title != expectedTitle) return;
        var language = session.ActiveLanguage;
        var input = DesktopAppAgentClient.ConversationNameInput(string.Join("\n\n", firstMessage.Parts.Where(p => p.Type == "text")
            .Select(PortableConversations.Text)), language);
        var fallback = DesktopResources.Get("NewChat");
        _ = TrackAppAgentAsync(async () =>
        {
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(appAgentLifetime.Token);
            lifetime.CancelAfter(TimeSpan.FromSeconds(30));
            string name;
            try
            {
                var models = aiModelTargets ?? await client.ListAsync(ServiceKind.Ai, lifetime.Token);
                name = await appAgentClient.InvokeAsync(agent, models, input, fallback, lifetime.Token);
            }
            catch (Exception) { return; }
            if (string.IsNullOrWhiteSpace(name) || name == expectedTitle) return;
            // Serialize publication with UI operations and streaming checkpoints. The helper
            // itself never locks the composer while its network request is in flight.
            while (!closing && (busy || historyDialogOpen || catalogDialog is not null))
                await Task.Delay(20, appAgentLifetime.Token);
            if (closing || partition != session.HistoryPartition) return;
            await RunAsync(async ct =>
            {
                var stored = await history.GetAsync(partition, conversation.Id, ct);
                // Re-read the latest document, not the first-turn snapshot. Never recreate a
                // deleted chat or overwrite a user rename or another turn's saved messages.
                if (closing || partition != session.HistoryPartition || stored is null || stored.Title != expectedTitle
                    || current.Id == conversation.Id && current.Title != expectedTitle) return;
                stored.Title = name;
                await history.SaveAsync(partition, stored, ct);
                if (current.Id == conversation.Id) current.Title = name;
                await LoadHistoryAsync(ct);
            });
        });
    }
}
