using AIHappey.Vercel.Models;

namespace AIHappey.Desktop.Core;

/// <summary>Display-only model labels and provider identity; never rewrites portable message metadata.</summary>
public sealed record ChatMessagePresentation(string Model, CatalogProvider? Provider)
{
    public static ChatMessagePresentation Resolve(UIMessage message, ServiceKind service, string fallbackModel, bool showProviderLogo,
        IReadOnlyList<ChatTarget>? agentTargets = null)
    {
        var model = PortableConversations.MetadataString(message.Metadata, "model");
        if (string.IsNullOrWhiteSpace(model)) model = fallbackModel;
        ChatTarget? agent = null;
        var isAgent = service == ServiceKind.Agents && message.Role == Role.assistant;
        if (isAgent)
        {
            var candidates = (agentTargets ?? []).Where(t => t.RemoteAgentId is not null || t.LocalAgentName is not null).ToArray();
            agent = candidates.FirstOrDefault(t => t.Id == model || t.RemoteAgentId == model);
            if (agent is not null) model = agent.LocalAgentName ?? agent.Label;
            else
            {
                // Completed metadata is already a display name. Only strip our internal routing prefixes.
                model = DesktopAgentTargets.DisplayName(model);
                var matches = candidates.Where(t => (t.LocalAgentName ?? t.Label) == model).Take(2).ToArray();
                if (matches.Length == 1) agent = matches[0];
            }
        }
        var wireModel = model;
        if (!isAgent && message.Role == Role.assistant)
        {
            var known = agentTargets?.FirstOrDefault(t => t.Id == model);
            var providerKey = ChatPreferences.ResolveProvider(service, model);
            model = known?.Label ?? (providerKey is not null && model.StartsWith(providerKey + "/", StringComparison.OrdinalIgnoreCase)
                ? model[(providerKey.Length + 1)..] : model);
        }
        if (!showProviderLogo || message.Role != Role.assistant) return new(model, null);

        var key = PortableConversations.MetadataString(message.Metadata, "providerKey");
        if (string.IsNullOrWhiteSpace(key))
        {
            var agentModel = PortableConversations.MetadataString(message.Metadata, "agentModel");
            key = isAgent
                ? ChatPreferences.ResolveProvider(ServiceKind.Ai, string.IsNullOrWhiteSpace(agentModel) ? agent?.AgentModelId ?? "" : agentModel)
                : ChatPreferences.ResolveProvider(service, wireModel);
        }
        else key = key.Trim().ToLowerInvariant();
        // Match the browser: no catalog icon means no logo and no abbreviated author.
        // In particular, agent names containing '/' are not inferred as AI provider identities.
        if (key is null || !ProviderCatalog.All.TryGetValue(key, out var provider)
            || !provider.Icons.Any(icon => !string.IsNullOrWhiteSpace(icon.Src))) return new(model, null);

        var prefix = key + "/";
        var label = !isAgent && model.StartsWith(prefix, StringComparison.Ordinal) && model.Length > prefix.Length
            ? model[prefix.Length..] : model;
        return new(label, provider);
    }
}
