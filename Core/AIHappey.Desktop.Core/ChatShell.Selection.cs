using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Vercel.Models;

namespace AIHappey.Desktop.Core;

public sealed partial class ChatShell
{
    private readonly Dictionary<ServiceKind, ChatSelection> rememberedSelections = [];
    private void ReconcileRememberedSelections()
    {
        if (session.Settings.SideInferenceAgentNames.Get(AppAgentRole.SelectModel).Length == 0
            && rememberedSelections.TryGetValue(ServiceKind.Ai, out var models))
        {
            var available = PickerModels(aiModelTargets ?? []).Where(m => session.Settings.AiModels.AllowsChat(m.ModelType ?? "language")).ToArray();
            models.UseManualSelection(available, false, fallback: AiModelCatalog.NewChatModel(available, session.Settings.AiModels));
        }
        if (session.Settings.SideInferenceAgentNames.Get(AppAgentRole.SelectFavorite).Length == 0
            && rememberedSelections.TryGetValue(ServiceKind.Agents, out var agents)) agents.FavoriteRouter = false;
    }
    private void RememberSelection()
    {
        var saved = new ChatSelection { FavoriteRouter = target.Selection.FavoriteRouter };
        saved.Replace(target.Selection.Ids); rememberedSelections[Service] = saved;
    }
    private HashSet<string> SelectionFavorites(bool agentMode) => new((agentMode ? targets.Where(t =>
        favorites.Contains($"Agent:{(t.LocalAgentName is not null ? "Local" : "Backend")}:{t.LocalAgentName ?? t.RemoteAgentId}"))
        : (aiModelTargets ?? targets).Where(t => modelFavorites.Contains(ModelOverviewCatalog.FavoriteKey(t)))).Select(t => t.Id), StringComparer.Ordinal);
    private bool AgentVisible(ChatTarget target, IReadOnlySet<string> favoriteIds) => target.RemoteAgentId is not null || favoriteIds.Contains(target.Id)
        || localAgents.FirstOrDefault(a => a.Name == target.LocalAgentName)?.Models.Any(model =>
            DesktopAgent.Text(model["type"]) != "decision" && ProviderPreferences.IsEnabled(session.Settings, "language",
                ChatPreferences.ResolveProvider(ServiceKind.Ai, DesktopAgent.Text(model["id"])) ?? "")) == true;
    private async Task LoadSelectionCatalogsAsync(CancellationToken ct)
    {
        var partition = session.HistoryPartition;
        if (localAgentPartition != session.AgentPartition) await LoadLocalAgentsAsync(ct);
        if (modelFavoritesPartition != partition)
        {
            var saved = await modelFavoritesStore.LoadAsync(partition, ct);
            ct.ThrowIfCancellationRequested(); if (partition != session.HistoryPartition || closing) throw new OperationCanceledException(ct);
            modelFavorites = saved; modelFavoritesPartition = partition;
        }
        if (catalogPartition != partition)
        {
            var saved = await catalogFavorites.LoadAsync(partition, ct);
            ct.ThrowIfCancellationRequested(); if (partition != session.HistoryPartition || closing) throw new OperationCanceledException(ct);
            favorites = saved; catalogPartition = partition;
        }
        if (Service == ServiceKind.Agents && aiModelTargets is null)
        {
            // Agent discovery/manual execution remains useful when the optional AI routing service is unavailable.
            try { aiModelTargets = await client.ListAsync(ServiceKind.Ai, ct); }
            catch (Exception error) when (error is not OperationCanceledException) { aiModelTargets = []; }
        }
    }
    private string SelectionSignature() => JsonSerializer.Serialize(new
    {
        partition = session.HistoryPartition, agentsPartition = session.AgentPartition, service = Service,
        ids = target.Selection.Ids, router = target.Selection.FavoriteRouter, settings = session.Settings,
        targets, models = aiModelTargets, agents = localAgents.Select(a => a.Definition), favorites, modelFavorites,
    }, PortableConversations.Json);
    private async Task<string[]> ResolveTurnSelectionAsync(List<UIMessage> messages, string[] selected, bool router, CancellationToken ct)
    {
        var agentMode = Service == ServiceKind.Agents;
        var routing = agentMode ? router : selected.Length > 1;
        if (!routing)
        {
            if (selected.Length == 0 || selected.Any(id => !targets.Any(t => t.Id == id))) throw new GatewayException(DesktopResources.Get("SelectTarget"));
            if (!agentMode && selected.Any(id => !targets.Any(t => t.Id == id && session.Settings.AiModels.AllowsChat(t.ModelType ?? "language"))))
                throw new GatewayException(DesktopResources.Get("SelectTarget"));
            return selected;
        }
        var assignments = session.Settings.SideInferenceAgentNames;
        var agent = DecisionRouting.Agent(assignments, localAgents, agentMode);
        var model = DecisionRouting.Model(agent, aiModelTargets ?? []) ?? throw new GatewayException(DesktopResources.Get("RoutingModelRequired"));
        var favoriteIds = SelectionFavorites(agentMode);
        var candidates = agentMode ? targets.Where(t => favoriteIds.Contains(t.Id) && AgentVisible(t, favoriteIds)).ToArray()
            : selected.Select(id => targets.FirstOrDefault(t => t.Id == id && session.Settings.AiModels.AllowsChat(t.ModelType ?? "language"))
                ?? throw new GatewayException(DesktopResources.Get("SelectTarget"))).ToArray();
        var descriptors = candidates.Select(t => agentMode
            ? DecisionRouting.AgentCandidate(t, localAgents.FirstOrDefault(a => a.Name == t.LocalAgentName), aiModelTargets ?? [])
            : DecisionRouting.ModelCandidate(t)).ToArray();
        var body = DecisionRouting.Request(model, DecisionRouting.Instructions(agent, agentMode), messages, descriptors);
        var signature = SelectionSignature();
        bool Current() => !closing && signature == SelectionSignature();
        var winner = await new DesktopDecisionClient(client, http).DecideAsync(body, Current, ct);
        ct.ThrowIfCancellationRequested();
        if (!Current()) throw new GatewayException(DesktopResources.Get("RoutingContextChanged"));
        return [winner];
    }
    private void SaveConversationSelection(string[] ids, bool router)
    {
        current.Metadata["desktopSelection"] = new { ids, favoriteRouter = router };
        RememberSelection();
    }
    private void RestoreConversationSelection()
    {
        if (!current.Metadata.TryGetValue("desktopSelection", out var value)) return;
        var saved = JsonSerializer.SerializeToElement(value, PortableConversations.Json);
        if (!saved.TryGetProperty("ids", out var ids) || ids.ValueKind != JsonValueKind.Array) return;
        target.SetSelection(ids.EnumerateArray().Where(id => id.ValueKind == JsonValueKind.String).Select(id => id.GetString()!),
            Service == ServiceKind.Agents && saved.TryGetProperty("favoriteRouter", out var router) && router.ValueKind == JsonValueKind.True);
        UpdateTargetSuggestions();
    }
}
