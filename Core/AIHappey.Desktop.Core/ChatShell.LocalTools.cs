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
        if (preferences.PluginEnabled(DesktopLocalTools.WindowsSearch))
        {
            var tools = new LocalWindowsSearchTools(isCurrent: () => !closing && session.HistoryPartition == partition);
            DesktopLocalTools.Register(snapshot, DesktopLocalTools.WindowsSearch, tools.CallAsync);
        }
        if (preferences.PluginEnabled(DesktopLocalTools.Files))
        {
            activeSharedFileTools?.Dispose();
            var filesPartition = session.FilesPartition;
            var tools = new LocalSharedFileTools(sharedFileStore, filesPartition, documentExtractor,
                () => !closing && session.HistoryPartition == partition && session.FilesPartition == filesPartition,
                async ct => { if (!closing && session.FilesPartition == filesPartition && activePage == DesktopPage.Files) await LoadFilesAsync(ct); });
            activeSharedFileTools = tools;
            DesktopLocalTools.Register(snapshot, DesktopLocalTools.Files, tools.CallAsync);
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
