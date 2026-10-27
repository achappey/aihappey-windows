using System.Text.Json.Nodes;
using Microsoft.UI.Xaml.Controls;
using static AIHappey.Desktop.Core.ChatSettingsFields;

namespace AIHappey.Desktop.Core;

internal sealed partial class GoogleChatSettingsForm : NativeProviderChatSettingsForm
{
    private readonly GoogleChatConfig model;
    private static string G(string key) => "google." + key;
    public GoogleChatSettingsForm(ChatPreferences preferences) : base("Google")
    {
        model = new(preferences.ProviderMetadata.GetValueOrDefault("google") ?? GoogleChatConfig.Defaults());
        Reasoning(); Search(); Maps();
        Tool("url_context", G("url_context"), expandable: false);
        Tool("code_execution", "code_execution", expandable: false);
        ResponseFormats(); Speech(); Transcription(); Video(); Retrieval("parallel"); Retrieval("exa");
        Environment(); Safety(); Other();
        fields.Watch(() => { foreach (var child in childFields.Values.ToArray()) child.Refresh(); });
        fields.Refresh();
    }
    public override void Commit(ChatPreferences preferences) => preferences.ProviderMetadata["google"] = model.Commit();
    private Func<bool> On(string path) => () => model.Get(path) is not null;
    private StackPanel Tool(string name, string label, bool expandable = true) => Section(name, label,
        () => model.Enabled(name), value => model.ToggleTool(name, value), expandable);
    private void Select(Panel parent, string label, string path, string[] choices, Func<bool>? enabled = null,
        bool inherit = false, string fallback = "", Func<string, string>? translate = null, bool generation = false)
        => fields.Select(parent, label, () => model.String(path, fallback), choices,
            value => Write(path, JsonValue.Create(value), inherit && value.Length == 0, generation), enabled, inherit, translate);
    private void Write(string path, JsonNode? value, bool omit = false, bool generation = false)
    {
        if (generation) model.Generation(path["generation_config/".Length..], value, omit);
        else model.Set(path, value, omit);
    }
    private void Reasoning()
    {
        var body = Section("reasoning", "reasoning", () => model.ReasoningOn, model.Reasoning);
        var (left, right) = Pair(body);
        Select(left, "reasoningEffort", "generation_config/thinking_level", GoogleChatConfig.ThinkingLevels,
            () => model.ReasoningOn, fallback: "minimal", translate: L, generation: true);
        Select(right, "reasoningSummary", "generation_config/thinking_summaries", ["auto", "none"],
            () => model.ReasoningOn, fallback: "auto", translate: L, generation: true);
    }
    private void Search()
    {
        var body = Tool("google_search", "webSearch");
        foreach (var type in GoogleChatConfig.SearchTypes)
            fields.Switch(body, G(type), () => (model.Get("google_search/search_types") as JsonArray)?.Any(n => ProviderChatConfig.Text(n) == type) == true,
                value => model.ToggleArray("google_search/search_types", type, value), On("google_search"));
    }
    private void Maps()
    {
        var body = Tool("google_maps", G("googleMaps"));
        fields.Switch(body, G("enable_widget"), () => model.Boolean("google_maps/enable_widget"),
            value => model.Set("google_maps/enable_widget", JsonValue.Create(value)), On("google_maps"));
        var (left, right) = Pair(body);
        foreach (var (panel, key) in new[] { (left, "latitude"), (right, "longitude") })
            fields.Number(panel, key, () => model.NumberText("google_maps/" + key),
                value => model.Set("google_maps/" + key, value is null ? null : JsonValue.Create(value.Value), value is null), On("google_maps"));
    }
    private void Video()
    {
        const string path = "generation_config/video_config";
        var body = Section("video_config", G("videoConfig.title"), On(path), value => model.Generation("video_config", value ? new JsonObject() : null, !value));
        Select(body, G("videoConfig.task"), path + "/task", GoogleChatConfig.VideoTasks, On(path), inherit: true,
            translate: value => L(G("videoConfig.tasks." + value)), generation: true);
    }
    private void Retrieval(string provider)
    {
        var prefix = "interactions.retrieval.";
        var path = "retrieval/" + provider + "_ai_search_config/api_key";
        var body = Section("retrieval_" + provider, G(prefix + provider + ".title"), () => model.RetrievalOn(provider),
            value => model.Retrieval(provider, value, value ? model.String(path) : null));
        fields.Password(body, G(prefix + "apiKey"), () => model.String(path), value => model.Retrieval(provider, true, value), () => model.RetrievalOn(provider));
    }
    private void Other()
    {
        var body = Section("other", "other");
        Select(body, G("service_tier"), "service_tier", GoogleChatConfig.ServiceTiers, inherit: true,
            translate: value => L(G("serviceTiers." + value)));
        fields.Number(body, G("interactions.other.seed"), () => model.NumberText("generation_config/seed"),
            value => model.Generation("seed", value is null ? null : JsonValue.Create(value.Value), value is null), whole: true);
    }
}
