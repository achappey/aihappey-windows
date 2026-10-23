using System.Text.Json.Nodes;
using Microsoft.UI.Xaml.Controls;
using static AIHappey.Desktop.Core.ChatSettingsFields;

namespace AIHappey.Desktop.Core;

/// <summary>Native counterpart of every Anthropic chat card. No provider request wrapping.</summary>
internal sealed partial class AnthropicChatSettingsForm : NativeProviderChatSettingsForm
{
    private readonly AnthropicChatConfig model;
    public AnthropicChatSettingsForm(ChatPreferences preferences) : base("Anthropic")
    {
        model = new(preferences.ProviderMetadata.GetValueOrDefault("anthropic") ?? AnthropicChatConfig.Defaults(), preferences.ProviderHeaders.GetValueOrDefault("anthropic"));
        NativeSettingsSurface.Card(cards, "Anthropic_tokens", L("maxOutputTokens"), out var tokens);
        Number(tokens, "maxOutputTokens", "max_tokens", min: 1, optional: false, whole: true);
        Cache();
        Section("compaction", A("compaction.title"), () => model.String("compaction/type") == "summarize",
            value => model.Toggle("compaction", value), expandable: false, toggleEnabled: () => !model.HasContextEdits);
        Thinking(); Output(); Container();
        Tool("bash", "bash"); WebSearch(); WebFetch(); Tool("code_execution", "code_execution");
        Tool("text_editor", A("textEditor")); Tool("memory", A("memory")); Advisor();
        Tool("tool_search_tool_bm25", A("toolSearchBm25")); Tool("tool_search_tool_regex", A("toolSearchRegex"));
        ContextManagement(); Session(); Speed(); Other(); Beta();
        fields.Refresh();
    }
    public override void Commit(ChatPreferences preferences)
    {
        preferences.ProviderMetadata["anthropic"] = model.Commit();
        if (model.Headers.Count == 0) preferences.ProviderHeaders.Remove("anthropic");
        else preferences.ProviderHeaders["anthropic"] = new(model.Headers, StringComparer.OrdinalIgnoreCase);
    }
    private static string A(string key) => "anthropic." + key;
    private Func<bool> On(string section) => () => model.Enabled(section);
    private StackPanel Feature(string section, string key) => Section(section, key, () => model.Enabled(section), value => model.Toggle(section, value));
    private void Number(Panel body, string key, string path, Func<bool>? enabled = null, double min = double.NegativeInfinity, bool optional = true, bool whole = false)
        => fields.Number(body, key, () => model.NumberText(path), value => model.Set(path, value is null ? null : JsonValue.Create(value.Value), value is null), enabled, min: min, optional: optional, whole: whole);
    private void Select(Panel body, string key, string path, string[] options, Func<bool>? enabled = null, string fallback = "", bool inherit = false, string? optionLabels = null)
        => fields.Select(body, key, () => model.String(path, fallback), options, value => model.Set(path, JsonValue.Create(value), inherit && value.Length == 0), enabled, inherit,
            optionLabels is null ? null : value => L(optionLabels + "." + value));
    private void Text(Panel body, string key, string path, Func<bool>? enabled = null, bool optional = false)
        => fields.Text(body, key, () => model.String(path), value => model.Set(path, JsonValue.Create(optional ? value.Trim() : value), optional && string.IsNullOrWhiteSpace(value)), enabled);
    private void List(Panel body, string key, string path, Func<bool> enabled)
        => fields.Text(body, key, () => model.Joined(path), value => model.StringList(path, value), enabled);
    private void OptionalSwitch(Panel body, string key, string path, Func<bool> enabled)
        => fields.Switch(body, key, () => model.Boolean(path), value => model.Set(path, value ? JsonValue.Create(true) : null, !value), enabled);
    private void Cache()
    {
        var body = Feature("cache_control", A("cache.title"));
        fields.Select(body, A("cache.ttl"), () => model.String("cache_control/ttl") == "1h" ? "1h" : "5m", ["5m", "1h"],
            value => model.Set("cache_control", value == "1h" ? JsonNode.Parse("""{"type":"ephemeral","ttl":"1h"}""") : AnthropicChatConfig.SectionDefault("cache_control")),
            On("cache_control"), label: value => L(A("cache.ttlOptions." + value)));
    }
    private void Thinking()
    {
        var body = Feature("thinking", "reasoning");
        fields.Select(body, "mode", () => model.String("thinking/type", "enabled"), ["enabled", "disabled", "between_tools", "adaptive"],
            model.ThinkingMode, On("thinking"), label: value => L(A("thinkingModes." + value)));
        bool BindingOn() => model.Enabled("thinking") && model.String("thinking/type", "enabled") is "enabled" or "adaptive";
        var binding = Column(body); fields.Visible(binding, () => model.String("thinking/type", "enabled") is "enabled" or "adaptive");
        var (left, right) = Pair(binding);
        Select(left, A("thinkingDisplay"), "thinking/display", ["summarized", "omitted", "updates"], BindingOn, "summarized", optionLabels: A("thinkingDisplayModes"));
        fields.Select(right, A("prefixMismatchBehavior"), () => model.String("thinking/block_binding/prefix_mismatch_behavior"), ["error", "drop_block"],
            model.PrefixMismatch, BindingOn, true, value => L(A("prefixMismatchBehaviorOptions." + value)));
        var budget = Column(body); fields.Visible(budget, () => model.String("thinking/type", "enabled") == "enabled");
        Number(budget, "budget", "thinking/budget_tokens", () => model.Enabled("thinking") && model.String("thinking/type", "enabled") == "enabled", 1024);
    }
    private void Output()
    {
        var body = Feature("output_config", A("outputConfig.title")); var (left, right) = Pair(body);
        fields.Slider(left, A("outputConfig.effort"), () => Math.Max(0, Array.IndexOf(AnthropicChatConfig.Efforts, model.String("output_config/effort", "medium"))),
            0, AnthropicChatConfig.Efforts.Length - 1, value => model.Set("output_config/effort", JsonValue.Create(AnthropicChatConfig.Efforts[(int)value])),
            On("output_config"), value => L(AnthropicChatConfig.Efforts[(int)value]));
        fields.Number(right, A("outputConfig.taskBudget"), () => model.NumberText("output_config/task_budget/total"), value => model.Set("output_config/task_budget",
            value is null ? null : new JsonObject { ["type"] = "tokens", ["total"] = value.Value }, value is null), On("output_config"), min: 20000, whole: true);
    }
    private void Container()
    {
        var body = Section("container", A("containerSettings.title"), () => model.Get("container") is not null,
            value => model.Set("container", value ? AnthropicChatConfig.NormalizeContainer(model.Get("container")) ?? new JsonObject() : null, !value));
        bool Active() => model.Get("container") is not null;
        fields.Text(body, A("containerSettings.id"), () => model.ContainerId, model.SetContainerId, Active).PlaceholderText = L(A("containerSettings.idPlaceholder"));
        fields.Text(body, A("containerSettings.customSkillIds"), () => string.Join(", ", model.ContainerSkills("custom")),
            value => model.SetContainerSkills(ProviderChatConfig.Split(value), model.ContainerSkills("anthropic")), Active).PlaceholderText = L(A("containerSettings.customSkillIdsPlaceholder"));
        foreach (var skill in AnthropicChatConfig.BuiltInSkills.OrderBy(id => L(A("containerSettings.skills." + id))))
            fields.Switch(body, A("containerSettings.skills." + skill), () => model.ContainerSkills("anthropic").Contains(skill), value =>
            {
                var enabled = model.ContainerSkills("anthropic").ToList();
                if (value) enabled.Add(skill); else enabled.Remove(skill);
                model.SetContainerSkills(model.ContainerSkills("custom"), enabled);
            }, Active);
    }
    private void Session()
    {
        var enabled = model.Get("budget") is not null;
        var body = Section("budget", A("session.title"), () => enabled, value =>
        {
            enabled = value; if (!value) model.Set("budget", null, true);
        });
        fields.Text(body, A("session.maxListCostUsd"), () => AnthropicChatConfig.FormatUsd(model.String("budget/max_list_cost/amount")),
            model.SessionBudget, () => enabled, validate: value => AnthropicChatConfig.TryUsdCents(value, out _), trackValidation: true).PlaceholderText = "0.00 USD";
    }
    private void Speed()
    {
        bool Active() => model.String("speed") is "standard" or "fast";
        var body = Section("speed", A("other.speed.title"), Active, value => model.Set("speed", JsonValue.Create("standard"), !value));
        Select(body, A("other.speed.title"), "speed", ["standard", "fast"], Active, "standard", optionLabels: A("other.speed.options"));
    }
    private void Other()
    {
        var body = Section("other", "other");
        Select(body, A("other.serviceTier.title"), "service_tier", ["auto", "standard_only"], inherit: true, optionLabels: A("other.serviceTier.options"));
        Select(body, A("other.inferenceGeo.title"), "inference_geo", ["global", "us"], inherit: true, optionLabels: A("other.inferenceGeo.options"));
    }
    private void Beta()
    {
        var body = Section("beta", A("betaHeader"));
        var (left, right) = Pair(body); var index = 0;
        // Keep unknown saved flags visible and editable, without changing their wire values.
        foreach (var flag in AnthropicChatConfig.Betas.Concat(model.BetaValues).Distinct().Order(StringComparer.Ordinal))
            fields.Switch(index++ % 2 == 0 ? left : right, A("betaHeader"), () => model.BetaValues.Contains(flag), value => model.Beta(flag, value),
                () => flag != AnthropicChatConfig.CompactionBeta || !model.HasContextEdits, label: flag);
    }
}
