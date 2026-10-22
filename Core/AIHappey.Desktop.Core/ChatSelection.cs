using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Vercel.Models;

namespace AIHappey.Desktop.Core;

/// <summary>Identity and ordering are independent of display names, filtered rows and routing winners.</summary>
public sealed class ChatSelection
{
    private readonly List<string> ids = [];
    public IReadOnlyList<string> Ids => ids.ToArray();
    public bool FavoriteRouter { get; set; }
    public void Replace(IEnumerable<string> values)
    {
        ids.Clear(); ids.AddRange(values.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal));
    }
    public void Toggle(string id, bool multiple, bool allowEmpty)
    {
        if (FavoriteRouter) { FavoriteRouter = false; Replace([id]); return; }
        if (!multiple) { Replace([id]); return; }
        if (!ids.Contains(id)) ids.Add(id);
        else if (allowEmpty || ids.Count > 1) ids.Remove(id);
    }
    public static string Display(ChatTarget item) => string.IsNullOrWhiteSpace(item.Label)
        ? ModelOverviewCatalog.DisplayId(item) : item.Label;
}

public sealed class AgentWorkflowPreferences
{
    public static IReadOnlyList<string> Types { get; } = ["concurrent", "sequential", "groupchat", "handoff", "decisions", "magentic"];
    public string Type { get; set; } = "sequential";
    public string? DecisionModel { get; set; }
    public int MaximumIterationCount { get; set; } = 5;
    public JsonArray Handoffs { get; set; } = [];
    public string? ManagerAgentKey { get; set; }
    public int? MaxRounds { get; set; }
    public int? MaxResets { get; set; }
    public int? MaxStalls { get; set; }
    public string? ResponseLanguage { get; set; }
    public AgentWorkflowPreferences Clone() => new() { Type = Type, DecisionModel = DecisionModel,
        MaximumIterationCount = MaximumIterationCount, Handoffs = (JsonArray)Handoffs.DeepClone(), ManagerAgentKey = ManagerAgentKey,
        MaxRounds = MaxRounds, MaxResets = MaxResets, MaxStalls = MaxStalls, ResponseLanguage = ResponseLanguage };
    public void Validate(IReadOnlyList<ChatTarget> models)
    {
        if (!Types.Contains(Type) || MaximumIterationCount is < 1 or > 20 || MaxRounds < 0 || MaxResets < 0 || MaxStalls < 0)
            throw new InvalidOperationException(DesktopResources.Get("WorkflowInvalid"));
        if (Type == "decisions" && !models.Any(m => m.Id == DecisionModel && DecisionRouting.IsDecisionModel(m)))
            throw new InvalidOperationException(DesktopResources.Get("RoutingModelRequired"));
    }
    public JsonObject Metadata()
    {
        var magentic = new JsonObject();
        if (MaxRounds is { } rounds) magentic["maxRounds"] = rounds;
        if (MaxResets is { } resets) magentic["maxResets"] = resets;
        if (MaxStalls is { } stalls) magentic["maxStalls"] = stalls;
        if (!string.IsNullOrWhiteSpace(ResponseLanguage)) magentic["responseLanguage"] = ResponseLanguage;
        var result = new JsonObject { ["groupchat"] = new JsonObject { ["maximumIterationCount"] = MaximumIterationCount },
            ["handoff"] = new JsonObject { ["handoffs"] = Handoffs.DeepClone() }, ["magentic"] = magentic };
        if (Type == "decisions") result["decisions"] = new JsonObject { ["model"] = DecisionModel };
        return result;
    }
}

public static class DesktopAgentRequests
{
    public static JsonObject Build(IEnumerable<string> selected, IReadOnlyList<ChatTarget> targets,
        IReadOnlyList<DesktopAgent> localAgents, AgentWorkflowPreferences workflow)
    {
        var keys = selected.Distinct(StringComparer.Ordinal).ToArray();
        var entries = keys.Select(key => targets.FirstOrDefault(t => t.Id == DesktopAgentTargets.Restore(key, targets))
            ?? throw new InvalidOperationException(DesktopResources.Get("AgentUnavailable"))).ToList();
        if (entries.Count == 0) throw new InvalidOperationException(DesktopResources.Get("SelectTarget"));
        if (workflow.Type == "magentic" && workflow.ManagerAgentKey is { } manager)
        {
            var index = entries.FindIndex(t => t.Id == manager);
            if (index > 0) { var entry = entries[index]; entries.RemoveAt(index); entries.Insert(0, entry); }
        }
        var mixedMagentic = workflow.Type == "magentic" && entries.Any(t => t.LocalAgentName is not null)
            && entries.Any(t => t.RemoteAgentId is not null);
        var agents = new JsonArray(); var models = new JsonArray();
        foreach (var entry in entries)
        {
            if (entry.LocalAgentName is { } name)
            {
                var agent = localAgents.FirstOrDefault(a => a.Name == name)
                    ?? throw new InvalidOperationException(DesktopResources.Get("AgentUnavailable"));
                agent.Validate(); agents.Add(agent.Definition.DeepClone());
            }
            else if (mixedMagentic)
            {
                if (entry.ModelMetadata is not { ValueKind: JsonValueKind.Object } definition)
                    throw new InvalidOperationException(DesktopResources.Get("WorkflowRemoteDefinitionRequired"));
                agents.Add(JsonNode.Parse(definition.GetRawText()));
            }
            else models.Add(entry.RemoteAgentId ?? throw new InvalidOperationException(DesktopResources.Get("AgentUnavailable")));
        }
        return new JsonObject { ["agents"] = agents, ["models"] = models, ["workflowType"] = workflow.Type,
            ["workflowMetadata"] = workflow.Metadata() };
    }
}
