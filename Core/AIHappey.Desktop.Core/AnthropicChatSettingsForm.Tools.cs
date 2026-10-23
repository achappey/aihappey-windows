using System.Text.Json.Nodes;
using Microsoft.UI.Xaml.Controls;
using static AIHappey.Desktop.Core.ChatSettingsFields;

namespace AIHappey.Desktop.Core;

internal sealed partial class AnthropicChatSettingsForm
{
    private StackPanel Tool(string section, string label)
    {
        var body = Feature(section, label); SharedToolFields(body, section);
        if (section is "bash" or "memory" or "text_editor")
            fields.OptionalJson(body, A("inputExamples"), () => model.Get(section + "/input_examples"),
                (value, omit) => model.Set(section + "/input_examples", value, omit), On(section));
        if (section == "text_editor")
        {
            var details = Column(body); fields.Visible(details, () => model.String("text_editor/type", AnthropicChatConfig.Versions(section)[0]) == "text_editor_20250728");
            Number(details, A("maxCharacters"), "text_editor/max_characters", () => model.Enabled(section) && model.String(section + "/type") == "text_editor_20250728");
        }
        return body;
    }
    private void SharedToolFields(Panel body, string section)
    {
        fields.Select(body, A("version"), () => model.String(section + "/type", AnthropicChatConfig.Versions(section)[0]), AnthropicChatConfig.Versions(section),
            value => model.Version(section, value), On(section));
        var callers = Column(body);
        callers.Children.Add(new TextBlock { Text = L(A("allowedCallers")), TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap });
        // Native switches replace the web multiselect; existing unknown caller values survive.
        foreach (var caller in AnthropicChatConfig.AllowedCallers.Concat((model.Get(section + "/allowed_callers") as JsonArray)?.Select(ProviderChatConfig.Text).OfType<string>() ?? []).Distinct())
            fields.Switch(callers, A("allowedCallers"), () => (model.Get(section + "/allowed_callers") as JsonArray)?.Any(n => ProviderChatConfig.Text(n) == caller) == true,
                value => model.ToggleArray(section + "/allowed_callers", caller, value), On(section), label: caller);
        var (left, right) = Pair(body);
        fields.Switch(left, A("cacheControl"), () => model.Enabled(section + "/cache_control"),
            value => model.Set(section + "/cache_control", value ? CacheControl(model.String(section + "/cache_control/ttl")) : null, !value), On(section));
        OptionalSwitch(right, A("deferLoading"), section + "/defer_loading", On(section));
        OptionalSwitch(body, A("strictValidation"), section + "/strict", On(section));
        ToolCache(body, section, "cache_control");
    }
    private static JsonObject CacheControl(string ttl)
    {
        var cache = new JsonObject { ["type"] = "ephemeral" }; if (ttl.Length > 0) cache["ttl"] = ttl; return cache;
    }
    private void ToolCache(Panel body, string section, string field)
    {
        var details = Column(body); fields.Visible(details, () => model.Enabled(section + "/" + field));
        bool Active() => model.Enabled(section) && model.Enabled(section + "/" + field);
        var (left, right) = Pair(details);
        fields.Select(left, A("cacheControlType"), () => model.String(section + "/" + field + "/type", "ephemeral"), ["ephemeral"],
            _ => model.Set(section + "/" + field, CacheControl(model.String(section + "/" + field + "/ttl"))), Active, label: _ => L(A("ephemeral")));
        fields.Select(right, A("cacheDuration"), () => model.String(section + "/" + field + "/ttl"), ["5m", "1h"],
            value => model.Set(section + "/" + field, CacheControl(value)), Active, true);
    }
    private void Advisor()
    {
        var body = Feature("advisor", A("advisor.title"));
        Select(body, A("advisor.model"), "advisor/model", AnthropicChatConfig.AdvisorModels, On("advisor"), AnthropicChatConfig.AdvisorModels[0]);
        var (left, right) = Pair(body);
        Number(left, A("maxUses"), "advisor/max_uses", On("advisor"), 1, whole: true);
        Number(right, A("advisor.maxTokens"), "advisor/max_tokens", On("advisor"), 1024, whole: true);
        fields.Switch(body, A("advisor.caching"), () => model.Enabled("advisor/caching"),
            value => model.Set("advisor/caching", value ? CacheControl(model.String("advisor/caching/ttl")) : null, !value), On("advisor"));
        ToolCache(body, "advisor", "caching");
    }
    private void SearchOptions(Panel body, string section)
    {
        Number(body, A("maxUses"), section + "/max_uses", On(section));
        var inclusion = Column(body); fields.Visible(inclusion, () => model.String(section + "/type", AnthropicChatConfig.Versions(section)[0]) == section + "_20260318");
        Select(inclusion, A("responseInclusion"), section + "/response_inclusion", ["excluded"],
            () => model.Enabled(section) && model.String(section + "/type") == section + "_20260318", inherit: true);
        var (left, right) = Pair(body);
        List(left, A("allowedDomains"), section + "/allowed_domains", On(section));
        List(right, A("blockedDomains"), section + "/blocked_domains", On(section));
    }
    private void WebSearch()
    {
        var body = Feature("web_search", "webSearch"); SharedToolFields(body, "web_search"); SearchOptions(body, "web_search");
        fields.Switch(body, A("userLocation"), () => model.Enabled("web_search/user_location"), value => model.Set("web_search/user_location", value
            ? JsonNode.Parse("""{"type":"approximate","timezone":"","country":"","region":"","city":""}""") : null, !value), On("web_search"));
        var location = Column(body); fields.Visible(location, () => model.Enabled("web_search/user_location"));
        bool Active() => model.Enabled("web_search") && model.Enabled("web_search/user_location");
        fields.Select(location, A("locationType"), () => model.String("web_search/user_location/type", "approximate"), ["approximate"],
            value => model.Set("web_search/user_location/type", JsonValue.Create(value)), Active, label: _ => L(A("locationApproximate")));
        var (left, right) = Pair(location);
        foreach (var (panel, key) in new[] { (left, "country"), (right, "region"), (left, "city"), (right, "timezone") })
            Text(panel, key, "web_search/user_location/" + key, Active);
    }
    private void WebFetch()
    {
        var body = Feature("web_fetch", "webFetch"); SharedToolFields(body, "web_fetch");
        Number(body, A("maxContentTokens"), "web_fetch/max_content_tokens", On("web_fetch"));
        SearchOptions(body, "web_fetch");
        fields.Switch(body, A("citations"), () => model.Boolean("web_fetch/citations/enabled"),
            value => model.Set("web_fetch/citations", value ? new JsonObject { ["enabled"] = true } : null, !value), On("web_fetch"));
        var cache = Column(body);
        bool SupportsCache() => model.String("web_fetch/type", AnthropicChatConfig.Versions("web_fetch")[0]) is "web_fetch_20260318" or "web_fetch_20260309";
        fields.Visible(cache, SupportsCache);
        fields.Switch(cache, A("useCache"), () => model.Boolean("web_fetch/use_cache", true),
            value => model.Set("web_fetch/use_cache", JsonValue.Create(value)), () => model.Enabled("web_fetch") && SupportsCache());
    }
}
