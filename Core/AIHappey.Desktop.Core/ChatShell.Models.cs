namespace AIHappey.Desktop.Core;

public sealed partial class ChatShell
{
    private IReadOnlyList<ChatTarget>? aiModelTargets;

    private void UpdateTargetSuggestions(string query = "")
    {
        target.ItemsSource = (Service == ServiceKind.Ai
            ? AiModelCatalog.ChatSuggestions(targets, session.Settings.AiModels, query)
            : targets.Where(x => string.IsNullOrWhiteSpace(query) || x.Label.Contains(query, StringComparison.OrdinalIgnoreCase)
                || x.Id.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray()).Take(100).ToArray();
    }

    private void SelectNewChatModel()
    {
        if (Service != ServiceKind.Ai) return;
        target.Text = AiModelCatalog.NewChatModel(targets, session.Settings.AiModels);
        current.Target = target.Text;
        UpdateTargetSuggestions();
    }
}
