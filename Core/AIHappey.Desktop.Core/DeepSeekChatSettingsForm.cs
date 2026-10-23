using System.Text.Json.Nodes;
using static AIHappey.Desktop.Core.ChatSettingsFields;

namespace AIHappey.Desktop.Core;

internal sealed class DeepSeekChatSettingsForm : NativeProviderChatSettingsForm
{
    private readonly DeepSeekChatConfig model;
    public DeepSeekChatSettingsForm(ChatPreferences preferences) : base("DeepSeek")
    {
        model = new(preferences.ProviderMetadata.GetValueOrDefault("deepseek") ?? new JsonObject());
        var body = Section("reasoning", "reasoning", () => model.ReasoningOn, model.ToggleReasoning);
        fields.Select(body, "reasoningEffort", () => model.Effort, DeepSeekChatConfig.Efforts,
            value => model.Set("reasoning/effort", JsonValue.Create(value)), () => model.ReasoningOn, label: L);
        Section("web_search", "webSearch", () => model.WebSearchOn, model.WebSearch, expandable: false);
        fields.Refresh();
    }
    public override void Commit(ChatPreferences preferences) => preferences.ProviderMetadata["deepseek"] = (JsonObject)model.Root.DeepClone();
}
