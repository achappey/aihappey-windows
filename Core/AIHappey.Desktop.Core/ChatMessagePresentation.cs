using AIHappey.Vercel.Models;

namespace AIHappey.Desktop.Core;

/// <summary>Display-only model labels and provider identity; never rewrites portable message metadata.</summary>
public sealed record ChatMessagePresentation(string Model, CatalogProvider? Provider)
{
    public static ChatMessagePresentation Resolve(UIMessage message, ServiceKind service, string fallbackModel, bool showProviderLogo)
    {
        var model = PortableConversations.MetadataString(message.Metadata, "model");
        if (string.IsNullOrWhiteSpace(model)) model = fallbackModel;
        if (!showProviderLogo || message.Role != Role.assistant) return new(model, null);

        var key = PortableConversations.MetadataString(message.Metadata, "providerKey");
        if (string.IsNullOrWhiteSpace(key)) key = ChatPreferences.ResolveProvider(service, model);
        else key = key.Trim().ToLowerInvariant();
        // Match the browser: no catalog icon means no logo and no abbreviated author.
        // In particular, agent names containing '/' are not inferred as AI provider identities.
        if (key is null || !ProviderCatalog.All.TryGetValue(key, out var provider)
            || !provider.Icons.Any(icon => !string.IsNullOrWhiteSpace(icon.Src))) return new(model, null);

        var prefix = key + "/";
        var label = model.StartsWith(prefix, StringComparison.Ordinal) && model.Length > prefix.Length
            ? model[prefix.Length..] : model;
        return new(label, provider);
    }
}
