using System.Text.Json.Nodes;
using Microsoft.UI.Xaml.Controls;
using static AIHappey.Desktop.Core.ChatSettingsFields;

namespace AIHappey.Desktop.Core;

internal sealed partial class GoogleChatSettingsForm
{
    private void Environment()
    {
        var sources = (JsonArray?)model.Get("environment/sources")?.DeepClone() ?? new();
        var rules = (JsonArray?)model.Get("environment/network/allowlist")?.DeepClone() ?? new();
        var mode = model.NetworkMode;
        var id = model.EnvironmentId;
        Action renderSources = () => { }, renderRules = () => { };
        void Save(bool? enabled = null) => model.Set("environment",
            GoogleChatConfig.BuildEnvironment(enabled ?? model.EnvironmentOn, id, sources, mode, rules, model.Get("environment") as JsonObject),
            enabled == false);
        bool Editable() => model.EnvironmentOn && string.IsNullOrWhiteSpace(id);
        var body = Section("environment", G("environment.title"), () => model.EnvironmentOn, value =>
        {
            if (!value) { sources = new(); rules = new(); mode = "default"; id = ""; }
            Save(value); renderSources(); renderRules();
        });
        fields.Text(body, G("environment.environmentId"), () => id, value =>
        {
            id = value; sources = new(); rules = new(); mode = "default";
            Save(); renderSources(); renderRules();
        }, () => model.EnvironmentOn).PlaceholderText = "env_abc123";
        renderSources = Rows("environment_sources", body, () => (JsonArray)sources.DeepClone(), values => { sources = values; Save(); }, Editable,
            (row, local, draft, save, render) =>
            {
                var (left, right) = Pair(row);
                local.Select(left, G("environment.sourceType"), () => draft.String("type", "repository"), GoogleChatConfig.SourceTypes,
                    value =>
                    {
                        draft.Set("type", JsonValue.Create(value));
                        draft.Set(value == "inline" ? "source" : "content", null, true);
                        save(); render();
                    }, Editable, label: value => L(G("environment.sourceTypes." + value)));
                RowText(right, local, draft, G("environment.target"), "target", save, Editable);
                if (draft.String("type") == "inline")
                    local.Text(row, G("environment.inlineContent"), () => draft.String("content"),
                        value => { draft.Set("content", JsonValue.Create(value)); save(); }, Editable, multiline: true);
                else RowText(row, local, draft, G("environment.source"), "source", save, Editable);
            }, GoogleChatConfig.SourceDefault, G("environment.addSource"));
        fields.Select(body, G("environment.networkMode"), () => mode, GoogleChatConfig.NetworkModes, value =>
        {
            mode = value;
            if (mode == "allowlist" && rules.Count == 0) rules.Add(GoogleChatConfig.AllowlistDefault());
            Save(); renderRules();
        }, Editable, label: value => L(G("environment.networkModes." + value)));
        var ruleBody = Column(body); fields.Visible(ruleBody, () => mode == "allowlist");
        ruleBody.Children.Add(new TextBlock { Text = L(G("environment.allowlistHelp")), TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap });
        bool RulesEnabled() => Editable() && mode == "allowlist";
        renderRules = Rows("environment_rules", ruleBody, () => (JsonArray)rules.DeepClone(), values => { rules = values; Save(); }, RulesEnabled,
            (row, local, draft, save, _) =>
            {
                RowText(row, local, draft, G("environment.domain"), "domain", save, RulesEnabled);
                local.Text(row, G("environment.transform"), () => draft.Get("transform")?.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) ?? "",
                    value => { draft.Set("transform", string.IsNullOrWhiteSpace(value) ? null : JsonNode.Parse(value), string.IsNullOrWhiteSpace(value)); save(); }, RulesEnabled,
                    multiline: true, validate: value =>
                    {
                        if (string.IsNullOrWhiteSpace(value)) return true;
                        try { return JsonNode.Parse(value) is JsonObject; } catch (System.Text.Json.JsonException) { return false; }
                    }, trackValidation: true);
            }, GoogleChatConfig.AllowlistDefault, G("environment.addAllowlistRule"));
    }
}
