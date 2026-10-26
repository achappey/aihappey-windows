namespace AIHappey.Desktop.Core;

/// <summary>Sidebar presentation only: pins never modify portable documents or activity timestamps.</summary>
public static class ConversationHistory
{
    public static IReadOnlyList<Conversation> Order(IEnumerable<Conversation> conversations, IReadOnlySet<string> pins) =>
        conversations.OrderByDescending(chat => pins.Contains(chat.Id)).ThenByDescending(chat => chat.Updated).ToArray();
}
