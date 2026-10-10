namespace AIHappey.Desktop.Core;

public sealed partial class ChatShell
{
    private LocalConversationTools? activeConversationTools;
    private void RegisterLocalPlugins(McpTurnSnapshot snapshot, ChatPreferences preferences, string partition)
    {
        if (preferences.PluginEnabled(DesktopLocalTools.Conversations))
        {
            var tools = new LocalConversationTools(history, partition, documentExtractor,
                () => !closing && session.HistoryPartition == partition,
                async (_, ct) => await LoadHistoryAsync(ct));
            snapshot.ConversationTools = tools;
            DesktopLocalTools.Register(snapshot, DesktopLocalTools.Conversations, tools.CallAsync);
        }
        if (preferences.PluginEnabled(DesktopLocalTools.ArtificialIntelligence))
        {
            var tools = new LocalArtificialIntelligenceTools(aiModelTargets ?? []);
            DesktopLocalTools.Register(snapshot, DesktopLocalTools.ArtificialIntelligence, tools.CallAsync);
        }
    }
    private Task SaveTurnHistoryAsync(string partition, Conversation conversation, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return activeConversationTools is { } tools ? tools.SaveAsync(conversation, ct) : history.SaveAsync(partition, conversation, ct);
    }
    private bool NeedsSkillCatalog(ChatPreferences preferences) => preferences.PluginEnabled(DesktopLocalTools.SkillSearch)
        || preferences.EnabledSkillIds.Any(id => !id.StartsWith("mcp:", StringComparison.Ordinal));
}
