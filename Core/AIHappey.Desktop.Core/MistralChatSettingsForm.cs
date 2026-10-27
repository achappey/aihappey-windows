using System.Text.Json.Nodes;
using static AIHappey.Desktop.Core.ChatSettingsFields;

namespace AIHappey.Desktop.Core;

internal sealed class MistralChatSettingsForm : NativeProviderChatSettingsForm
{
    private readonly MistralChatConfig model;
    public MistralChatSettingsForm(ChatPreferences preferences) : base("Mistral")
    {
        model = new(preferences.ProviderMetadata.GetValueOrDefault("mistral") ?? MistralChatConfig.Defaults());
        var body = Section("web_search", "webSearch", () => model.SearchOn, model.Search);
        fields.Switch(body, "mistral.webSearchPremium", () => model.Enabled("web_search_premium"), model.Premium, () => model.SearchOn);
        Section("image_generation", "image_generation", () => model.Enabled("image_generation"), value => model.ToggleTool("image_generation", value), expandable: false);
        Section("code_interpreter", "code_execution", () => model.Enabled("code_interpreter"), value => model.ToggleTool("code_interpreter", value), expandable: false);
        body = Section("document_library", "openai.file_search", () => model.Enabled("document_library"), value => model.ToggleTool("document_library", value));
        fields.Text(body, "openai.vector_store_ids", () => model.Joined("document_library/library_ids"),
            value => model.Set("document_library/library_ids", ProviderChatConfig.Strings(ProviderChatConfig.Split(value))),
            () => model.Enabled("document_library")).PlaceholderText = "xxx, zzz";
        body = Section("reasoning", "reasoning", () => model.ReasoningOn, model.Reasoning);
        fields.Select(body, "reasoningEffort", () => model.String("completion_args/reasoning_effort", "high"), MistralChatConfig.Efforts,
            value => model.Completion("reasoning_effort", JsonValue.Create(value)), () => model.ReasoningOn, label: L);
        body = Section("other", "other");
        Number(body, "seed", "random_seed", whole: true);
        var (left, right) = Pair(body);
        Number(left, "presencePenalty", "presence_penalty", -2, 2);
        Number(right, "frequencyPenalty", "frequency_penalty", -2, 2);
        fields.Refresh();
    }
    private void Number(Microsoft.UI.Xaml.Controls.Panel body, string label, string key,
        double min = double.NegativeInfinity, double max = double.PositiveInfinity, bool whole = false)
        => fields.Number(body, label, () => model.NumberText("completion_args/" + key),
            value => model.Completion(key, value is null ? null : JsonValue.Create(value.Value)), min: min, max: max, whole: whole);
    public override void Commit(ChatPreferences preferences) => preferences.ProviderMetadata["mistral"] = model.Commit();
}
