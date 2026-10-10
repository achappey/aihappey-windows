namespace AIHappey.Desktop.Core;

public enum AppAgentRole { WelcomeMessage, ConversationName, ExplainToolCall, ToolSearch, ResourceSearch }

/// <summary>Browser-compatible name assignments. Missing settings seed defaults; an empty
/// string is an explicit None and must never be replaced with a default during loading.</summary>
public sealed class AppAgentPreferences
{
    public string WelcomeMessageAgent { get; set; } = "WelcomeMessageAgent";
    public string ConversationNameAgent { get; set; } = "ConversationNameAgent";
    public string ExplainToolCallAgent { get; set; } = "ExplainToolcallAgent";
    public string ToolSearchAgent { get; set; } = "ToolSearchAgent";
    public string ResourceSearchAgent { get; set; } = "ResourceSearchAgent";
    public AppAgentPreferences Clone() => (AppAgentPreferences)MemberwiseClone();
    public string Get(AppAgentRole role) => (role switch
    {
        AppAgentRole.WelcomeMessage => WelcomeMessageAgent,
        AppAgentRole.ConversationName => ConversationNameAgent,
        AppAgentRole.ExplainToolCall => ExplainToolCallAgent,
        AppAgentRole.ToolSearch => ToolSearchAgent,
        AppAgentRole.ResourceSearch => ResourceSearchAgent,
        _ => throw new ArgumentOutOfRangeException(nameof(role))
    }) ?? "";
    public void Set(AppAgentRole role, string? name)
    {
        name ??= "";
        switch (role)
        {
            case AppAgentRole.WelcomeMessage: WelcomeMessageAgent = name; break;
            case AppAgentRole.ConversationName: ConversationNameAgent = name; break;
            case AppAgentRole.ExplainToolCall: ExplainToolCallAgent = name; break;
            case AppAgentRole.ToolSearch: ToolSearchAgent = name; break;
            case AppAgentRole.ResourceSearch: ResourceSearchAgent = name; break;
            default: throw new ArgumentOutOfRangeException(nameof(role));
        }
    }
}
