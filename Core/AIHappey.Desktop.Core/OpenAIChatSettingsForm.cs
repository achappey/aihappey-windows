using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using static AIHappey.Desktop.Core.ChatSettingsFields;

namespace AIHappey.Desktop.Core;

/// <summary>Native equivalent of OpenAIChatConfigForm and its chat cards. Provider fields
/// remain raw OpenAI JSON; this class is an editor, not an SDK/request translation layer.</summary>
internal sealed class OpenAIChatSettingsForm : IChatProviderForm
{
    private readonly OpenAIChatConfig model;
    private readonly ChatSettingsFields fields = new();
    private readonly StackPanel cards = new() { Spacing = 18 };
    private readonly Dictionary<string, ChatSettingsFields> childFields = [];
    public FrameworkElement View => cards;
    public bool IsValid => fields.IsValid && childFields.Values.All(f => f.IsValid);
    public OpenAIChatSettingsForm(ChatPreferences preferences)
    {
        model = new(preferences.ProviderMetadata.GetValueOrDefault("openai") ?? new JsonObject(), preferences.ProviderHeaders.GetValueOrDefault("openai"));
        Reasoning(); WebSearch(); ImageGeneration(); ToolSearch(); Programmatic(); CodeInterpreter(); Shell(); FileSearch();
        ContextManagement(); Moderation(); PromptCache(); MultiAgent(); AgentSession(); EnvironmentSession(); Other();
        fields.Refresh();
    }
    public void Commit(ChatPreferences preferences)
    {
        preferences.ProviderMetadata["openai"] = OpenAIChatConfig.Canonical(model.Root);
        if (model.Headers.Count == 0) preferences.ProviderHeaders.Remove("openai");
        else preferences.ProviderHeaders["openai"] = new(model.Headers, StringComparer.OrdinalIgnoreCase);
    }
    private StackPanel Section(string name, string label, bool toggle = true)
    {
        Card(cards, "OpenAI_" + name, label, out var body, out var heading);
        if (toggle) fields.HeaderSwitch(heading, label, () => model.Enabled(name), on => model.Toggle(name, on));
        return body;
    }
    private Func<bool> On(string section) => () => model.Enabled(section);
    private void Write(string path, JsonNode? value, bool omit = false)
    {
        model.Set(path, value, omit);
        if (path.StartsWith("web_search/", StringComparison.Ordinal)) model.NormalizeWebSearch();
    }
    private void Select(Panel body, string key, string path, string[] options, string section, string fallback = "", bool inherit = false, Func<bool>? enabled = null)
        => fields.Select(body, key, () => model.String(path, fallback), options, value => Write(path, JsonValue.Create(value), inherit && value.Length == 0), enabled ?? On(section), inherit);
    private void Text(Panel body, string key, string path, string section, string fallback = "", bool optional = false, bool multiline = false, Func<bool>? enabled = null)
        => fields.Text(body, key, () => model.String(path, fallback), value => Write(path, JsonValue.Create(optional ? value.Trim() : value), optional && string.IsNullOrWhiteSpace(value)), enabled ?? On(section), multiline);
    private void Switch(Panel body, string key, string path, string section, bool fallback = false, Func<bool>? enabled = null)
        => fields.Switch(body, key, () => model.Boolean(path, fallback), value => Write(path, JsonValue.Create(value)), enabled ?? On(section));
    private void Include(Panel body, string key, string flag, string section)
        => fields.Switch(body, key, () => model.Includes(flag), value => model.Include(flag, value), On(section));
    private void List(Panel body, string key, string path, string section, bool optional = false, bool unique = false, Func<bool>? enabled = null)
        => fields.Text(body, key, () => model.Joined(path), value => { var values = OpenAIChatConfig.Split(value, unique); Write(path, OpenAIChatConfig.Strings(values), optional && values.Length == 0); }, enabled ?? On(section), true);
    private void Integer(Panel body, string key, string path, string section, int? fallback = null, int min = 1, int max = int.MaxValue, bool optional = true, Func<bool>? enabled = null)
        => fields.Integer(body, key, () => model.Get(path)?.ToJsonString() ?? fallback?.ToString(CultureInfo.InvariantCulture) ?? "", value => Write(path, value is null ? null : JsonValue.Create(value.Value), value is null), enabled ?? On(section), min, max, optional);
    private void Slider(Panel body, string key, string path, string section, int min, int max, int fallback)
        => fields.Slider(body, key, () => model.Number(path, fallback), min, max, value => Write(path, JsonValue.Create((int)value)), On(section));

    private void Reasoning()
    {
        var body = Section("reasoning", "reasoning"); var (left, right) = Pair(body);
        fields.Slider(left, "reasoningEffort", () => Math.Max(0, Array.IndexOf(OpenAIChatConfig.Efforts, model.String("reasoning/effort", "none"))), 0, 6,
            value => Write("reasoning/effort", JsonValue.Create(OpenAIChatConfig.Efforts[(int)value])), On("reasoning"), value => OpenAIChatConfig.Efforts[(int)value]);
        Select(right, "reasoningSummary", "reasoning/summary", ["auto", "concise", "detailed"], "reasoning");
        (left, right) = Pair(body);
        Select(left, "reasoningContext", "reasoning/context", ["auto", "current_turn", "all_turns"], "reasoning");
        Select(right, "reasoningMode", "reasoning/mode", ["standard", "pro"], "reasoning");
        Include(body, "openai.encryptedContent", "reasoning.encrypted_content", "reasoning");
    }
    private void WebSearch()
    {
        var body = Section("web_search", "webSearch"); var (left, right) = Pair(body);
        string[] sizes = ["low", "medium", "high"];
        fields.Slider(left, "searchContextSize", () => Math.Max(0, Array.IndexOf(sizes, model.String("web_search/search_context_size", "medium"))), 0, 2,
            value => Write("web_search/search_context_size", JsonValue.Create(sizes[(int)value])), On("web_search"), value => sizes[(int)value]);
        Select(right, "openai.returnTokenBudget", "web_search/return_token_budget", ["default", "unlimited"], "web_search", "default");
        // The browser uses add/remove domain chips; the list is canonicalized on every change.
        var domains = new StackPanel { Spacing = 8 }; body.Children.Add(domains);
        var draft = new TextBox { Header = L("openai.allowedDomains"), PlaceholderText = "pubmed.ncbi.nlm.nih.gov" }; ControlAppearance.Stock(draft); domains.Children.Add(draft);
        var entries = new StackPanel { Spacing = 6 }; domains.Children.Add(entries);
        void RenderDomains()
        {
            entries.Children.Clear();
            if (model.Get("web_search/filters/allowed_domains") is not JsonArray values) return;
            foreach (var value in values.Select(OpenAIChatConfig.Text).Where(v => v is not null).Cast<string>())
            {
                var row = new StackPanel { Spacing = 4 }; entries.Children.Add(row);
                row.Children.Add(new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap });
                var remove = Button(row, "delete", () => fields.Changed(() =>
                {
                    var next = ((JsonArray)model.Get("web_search/filters/allowed_domains")!).Select(OpenAIChatConfig.Text).Where(v => v != value).Cast<string>();
                    Write("web_search/filters/allowed_domains", OpenAIChatConfig.Strings(next)); RenderDomains();
                }));
                remove.IsEnabled = model.Enabled("web_search");
            }
        }
        var add = Button(domains, "add", () => fields.Changed(() =>
        {
            if (string.IsNullOrWhiteSpace(draft.Text)) return;
            var current = model.Get("web_search/filters/allowed_domains") is JsonArray a ? a.Select(OpenAIChatConfig.Text).Where(v => v is not null).Cast<string>() : [];
            Write("web_search/filters/allowed_domains", OpenAIChatConfig.Strings(current.Append(draft.Text.Trim()))); draft.Text = ""; RenderDomains();
        }));
        fields.Watch(() => { draft.IsEnabled = add.IsEnabled = model.Enabled("web_search"); foreach (var row in entries.Children.OfType<StackPanel>()) foreach (var button in row.Children.OfType<Button>()) button.IsEnabled = model.Enabled("web_search"); });
        RenderDomains();
        (left, right) = Pair(body);
        foreach (var (panel, key, placeholder) in new[] { (left, "country", "NL"), (right, "region", "Noord-Holland"), (left, "city", "Amsterdam"), (right, "timezone", "Europe/Amsterdam") })
        {
            var box = fields.Text(panel, key, () => model.String("web_search/user_location/" + key), value => model.WebLocation(key, value), On("web_search")); box.PlaceholderText = placeholder;
        }
        (left, right) = Pair(body);
        fields.Switch(left, "openai.externalWebAccess", () => model.Boolean("web_search/external_web_access", true), value => Write("web_search/external_web_access", value ? null : JsonValue.Create(false), value), On("web_search"));
        Include(right, "openai.includeSources", "web_search_call.action.sources", "web_search");
        Include(left, "openai.includeSearchResults", "web_search_call.results", "web_search");
        (left, right) = Pair(body);
        foreach (var (panel, type) in new[] { (left, "text"), (right, "image") })
            fields.Switch(panel, "openai.searchContentTypes." + type,
                () => (model.Get("web_search/search_content_types") as JsonArray)?.Any(n => OpenAIChatConfig.Text(n) == type) == true,
                enabled =>
                {
                    model.ToggleArray("web_search/search_content_types", type, enabled);
                    if (type == "image" && enabled && model.Get("web_search/image_settings") is null) model.Set("web_search/image_settings", JsonNode.Parse("""{"max_results":3,"caption":true}"""));
                    model.NormalizeWebSearch();
                }, () => model.Enabled("web_search") && model.Includes("web_search_call.results"));
        (left, right) = Pair(body);
        bool ImageOn() => model.Enabled("web_search") && model.Includes("web_search_call.results") && (model.Get("web_search/search_content_types") as JsonArray)?.Any(n => OpenAIChatConfig.Text(n) == "image") == true;
        Integer(left, "openai.imageSettings.maxResults", "web_search/image_settings/max_results", "web_search", enabled: ImageOn);
        fields.Switch(right, "openai.imageSettings.caption", () => model.Boolean("web_search/image_settings/caption"), value => Write("web_search/image_settings/caption", value ? JsonValue.Create(true) : null, !value), ImageOn);
    }
    private void ImageGeneration()
    {
        var body = Section("image_generation", "image_generation"); var (left, right) = Pair(body);
        fields.Select(left, "model", () => model.String("image_generation/model", "gpt-image-1.5"),
            ["chatgpt-image-latest", "gpt-image-2.5-sunburst", "gpt-image-2.5-flare", "gpt-image-2", "gpt-image-1.5", "gpt-image-1", "gpt-image-1-mini"], model.ImageModel, On("image_generation"));
        Slider(right, "partial_images", "image_generation/partial_images", "image_generation", 0, 3, 0);
        (left, right) = Pair(body);
        Select(left, "quality", "image_generation/quality", ["auto", "low", "medium", "high", "xhigh", "max"], "image_generation");
        Select(right, "input_fidelity", "image_generation/input_fidelity", ["low", "high"], "image_generation", enabled: () => model.Enabled("image_generation") && !model.String("image_generation/model").StartsWith("gpt-image-2", StringComparison.Ordinal));
        (left, right) = Pair(body);
        Select(left, "openai.action", "image_generation/action", ["auto", "generate", "edit"], "image_generation", "auto");
        Select(right, "moderation", "image_generation/moderation", ["auto", "low"], "image_generation", "auto");
        (left, right) = Pair(body);
        Select(left, "background", "image_generation/background", ["auto", "transparent", "opaque"], "image_generation");
        Select(right, "size", "image_generation/size", ["auto", "1024x1024", "1024x1536", "1536x1024"], "image_generation");
        Slider(body, "openai.output_compression", "image_generation/output_compression", "image_generation", 0, 100, 100);
    }
    private void ToolSearch()
    {
        var body = Section("tool_search", "openai.toolSearch.title");
        fields.Watch(() => { if (model.Enabled("tool_search") && model.Get("tool_search/type") is null) model.Set("tool_search/type", JsonValue.Create("tool_search")); });
        fields.Select(body, "openai.toolSearch.executionMode", () => model.String("tool_search/execution") == "client" ? "client" : "hosted", ["hosted", "client"], value =>
            model.Set("tool_search", value == "client" ? JsonNode.Parse("""
                {"type":"tool_search","execution":"client","description":"Search the available tool catalog for tools that satisfy a goal.","parameters":{"type":"object","properties":{"goal":{"type":"string","description":"A concise description of the capability or task to find tools for."}},"required":["goal"],"additionalProperties":false}}
                """) : new JsonObject { ["type"] = "tool_search" }), On("tool_search"));
    }
    private void Programmatic()
    {
        Section("programmatic_tool_calling", "openai.programmaticToolCalling.title");
        fields.Watch(() => { if (model.Enabled("programmatic_tool_calling")) model.Set("programmatic_tool_calling/type", JsonValue.Create("programmatic_tool_calling")); });
    }
    private void AllowedCallers(Panel body, string section)
    {
        var (left, right) = Pair(body);
        foreach (var (panel, caller) in new[] { (left, "direct"), (right, "programmatic") })
            fields.Switch(panel, "openai.programmaticToolCalling.allowedCallersOptions." + caller,
                () => (model.Get(section + "/allowed_callers") as JsonArray)?.Any(n => OpenAIChatConfig.Text(n) == caller) == true,
                value => model.ToggleArray(section + "/allowed_callers", caller, value), () => model.Enabled(section) && model.Enabled("programmatic_tool_calling"));
    }
    private void CodeInterpreter()
    {
        var body = Section("code_interpreter", "code_execution"); AllowedCallers(body, "code_interpreter");
        fields.Text(body, "openai.container", () => OpenAIChatConfig.Text(model.Get("code_interpreter/container")) ?? "", value =>
            model.Set("code_interpreter/container", value.Trim().Length == 0 ? new JsonObject { ["type"] = "auto" } : JsonValue.Create(value.Trim())), On("code_interpreter"));
        var automatic = Column(body); fields.Visible(automatic, () => OpenAIChatConfig.Text(model.Get("code_interpreter/container")) is null);
        Container(automatic, "code_interpreter/container", "code_interpreter");
        Include(body, "openai.includeOutputs", "code_interpreter_call.outputs", "code_interpreter");
    }
    private void Shell()
    {
        var body = Section("shell", "openai.shell"); AllowedCallers(body, "shell");
        fields.Select(body, "openai.shellEnvironment", () => model.String("shell/environment/type", "container_auto"), ["container_auto", "container_reference", "local"], model.ShellEnvironment, On("shell"));
        var automatic = Column(body); fields.Visible(automatic, () => model.String("shell/environment/type", "container_auto") == "container_auto");
        Container(automatic, "shell/environment", "shell");
        var reference = Column(body); fields.Visible(reference, () => model.String("shell/environment/type") == "container_reference");
        Text(reference, "openai.container", "shell/environment/container_id", "shell");
        // Skill management is intentionally outside this version. Existing inline/reference
        // entries are retained exactly, without downloads, extraction or local execution.
        var skills = Column(body); fields.Visible(skills, () => model.String("shell/environment/type", "container_auto") != "container_reference");
        skills.Children.Add(new TextBlock { Text = DesktopResources.Get("ChatShellSkillsDeferred"), TextWrapping = TextWrapping.Wrap });
        var list = new StackPanel { Spacing = 6 }; skills.Children.Add(list);
        string? rendered = null;
        fields.Watch(() =>
        {
            var signature = (model.Get("shell/environment/skills")?.ToJsonString() ?? "") + model.Enabled("shell");
            if (signature == rendered) return; rendered = signature; list.Children.Clear();
            if (model.Get("shell/environment/skills") is not JsonArray values) return;
            for (var i = 0; i < values.Count; i++)
            {
                var index = i; var row = new StackPanel { Spacing = 4 }; list.Children.Add(row);
                row.Children.Add(new TextBlock { Text = OpenAIChatConfig.Text(values[i]?["name"]) ?? OpenAIChatConfig.Text(values[i]?["skill_id"]) ?? L("openai.shellSkillId"), TextWrapping = TextWrapping.Wrap });
                Button(row, "delete", () => fields.Changed(() => ((JsonArray)model.Get("shell/environment/skills")!).RemoveAt(index))).IsEnabled = model.Enabled("shell");
            }
        });
    }
    private void Container(Panel body, string path, string section)
    {
        bool Active() => model.Enabled(section) && (section == "shell" ? model.String(path + "/type", "container_auto") == "container_auto" : OpenAIChatConfig.Text(model.Get(path)) is null);
        Select(body, "openai.shellMemoryLimit", path + "/memory_limit", ["1g", "4g", "16g", "64g"], section, inherit: true, enabled: Active);
        fields.Select(body, "openai.shellNetworkPolicy", () => model.String(path + "/network_policy/type", "disabled"), ["disabled", "allowlist"], value => model.Set(path + "/network_policy", value == "allowlist"
            ? new JsonObject { ["type"] = "allowlist", ["allowed_domains"] = new JsonArray(), ["domain_secrets"] = new JsonArray() } : new JsonObject { ["type"] = "disabled" }), Active);
        var allowlist = Column(body); fields.Visible(allowlist, () => Active() && model.String(path + "/network_policy/type") == "allowlist");
        List(allowlist, "openai.shellAllowedDomains", path + "/network_policy/allowed_domains", section, enabled: () => Active() && model.String(path + "/network_policy/type") == "allowlist");
        var secrets = new StackPanel { Spacing = 12 }; allowlist.Children.Add(secrets);
        string? rendered = null;
        void RenderSecrets()
        {
            var values = model.Get(path + "/network_policy/domain_secrets") as JsonArray ?? new();
            var signature = values.Count + ":" + Active() + ":" + model.String(path + "/network_policy/type");
            if (signature == rendered) return; rendered = signature; secrets.Children.Clear();
            var local = new ChatSettingsFields(); childFields[path] = local;
            for (var i = 0; i < values.Count; i++)
            {
                var index = i; var row = new StackPanel { Spacing = 8 }; secrets.Children.Add(row);
                foreach (var key in new[] { "domain", "name", "value" })
                    local.Text(row, "openai.shellSecret" + char.ToUpperInvariant(key[0]) + key[1..],
                        () => OpenAIChatConfig.Text(((JsonArray?)model.Get(path + "/network_policy/domain_secrets"))?[index]?[key]) ?? "",
                        value => ((JsonArray)model.Get(path + "/network_policy/domain_secrets")!)[index]![key] = value,
                        () => Active() && model.String(path + "/network_policy/type") == "allowlist");
                Button(row, "delete", () => fields.Changed(() => { ((JsonArray)model.Get(path + "/network_policy/domain_secrets")!).RemoveAt(index); RenderSecrets(); }));
            }
        }
        Button(allowlist, "openai.shellAddDomainSecret", () => fields.Changed(() =>
        {
            var values = (JsonArray?)(model.Get(path + "/network_policy/domain_secrets")?.DeepClone()) ?? new();
            values.Add(new JsonObject { ["domain"] = "", ["name"] = "", ["value"] = "" }); model.Set(path + "/network_policy/domain_secrets", values); RenderSecrets();
        }));
        fields.Watch(() => { RenderSecrets(); if (childFields.TryGetValue(path, out var local)) local.Refresh(); });
    }
    private void FileSearch()
    {
        var body = Section("file_search", "openai.file_search");
        Slider(body, "openai.max_num_results", "file_search/max_num_results", "file_search", 1, 50, 10);
        List(body, "openai.vector_store_ids", "file_search/vector_store_ids", "file_search");
        Include(body, "openai.includeSearchResults", "file_search_call.results", "file_search");
    }
    private void ContextManagement()
    {
        var body = Section("context_management", "openai.contextManagement.title", false);
        body.Children.Add(new TextBlock { Text = L("openai.contextManagement.description"), TextWrapping = TextWrapping.Wrap });
        var entries = new StackPanel { Spacing = 12 }; body.Children.Add(entries);
        void Render()
        {
            entries.Children.Clear(); childFields.Remove("context_management");
            var values = model.Get("context_management") as JsonArray ?? new();
            if (values.Count == 0) entries.Children.Add(new TextBlock { Text = L("openai.contextManagement.empty"), TextWrapping = TextWrapping.Wrap });
            for (var i = 0; i < values.Count; i++)
            {
                var index = i; var row = new StackPanel { Spacing = 8 }; entries.Children.Add(row);
                row.Children.Add(new TextBlock { Text = (OpenAIChatConfig.Text(values[i]?["type"]) ?? "compaction") + " · " + (values[i]?["compact_threshold"]?.ToJsonString() ?? "1000"), TextWrapping = TextWrapping.Wrap });
                var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 }; row.Children.Add(actions);
                void Move(int destination)
                {
                    var next = (JsonArray)values.DeepClone(); var entry = next[index]!.DeepClone(); next.RemoveAt(index); next.Insert(destination, entry); model.Set("context_management", next); Render();
                }
                Button(actions, "up", () => Move(index - 1)).IsEnabled = index > 0;
                Button(actions, "down", () => Move(index + 1)).IsEnabled = index < values.Count - 1;
                Button(actions, "delete", () => { var next = (JsonArray)values.DeepClone(); next.RemoveAt(index); model.Set("context_management", next, next.Count == 0); Render(); });
                Button(actions, "edit", () =>
                {
                    Render(); var currentRow = (StackPanel)entries.Children[index];
                    var editor = new StackPanel { Spacing = 8 }; currentRow.Children.Add(editor);
                    var local = new ChatSettingsFields(); childFields["context_management"] = local;
                    var type = OpenAIChatConfig.Text(values[index]?["type"]) ?? "compaction";
                    int threshold = values[index]?["compact_threshold"]?.GetValue<int>() ?? 1000;
                    local.Select(editor, "openai.contextManagement.type", () => type, ["compaction"], value => type = value);
                    var input = local.Integer(editor, "openai.contextManagement.compactThreshold", () => threshold.ToString(), value => threshold = value!.Value, min: 1000, optional: false);
                    var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 }; editor.Children.Add(buttons);
                    Button(buttons, "cancel", Render);
                    Button(buttons, "save", () =>
                    {
                        if (!local.IsValid) { input.Focus(FocusState.Programmatic); return; }
                        var next = (JsonArray)values.DeepClone(); next[index] = new JsonObject { ["type"] = type, ["compact_threshold"] = threshold }; model.Set("context_management", next); Render();
                    });
                });
            }
        }
        Button(body, "openai.contextManagement.add", () =>
        {
            var next = (JsonArray?)(model.Get("context_management")?.DeepClone()) ?? new(); next.Add(new JsonObject { ["type"] = "compaction", ["compact_threshold"] = 1000 }); model.Set("context_management", next); Render();
        });
        Render();
    }
    private void Moderation()
    {
        var body = Section("moderation", "openai.moderation.title");
        Text(body, "openai.moderation.model", "moderation/model", "moderation", "omni-moderation-latest");
        var (left, right) = Pair(body);
        Select(left, "openai.moderation.inputMode", "moderation/policy/input/mode", ["score", "block"], "moderation", "score");
        Select(right, "openai.moderation.outputMode", "moderation/policy/output/mode", ["score", "block"], "moderation", "score");
    }
    private void PromptCache()
    {
        var body = Section("prompt_cache_options", "openai.promptCacheOptions.title"); var (left, right) = Pair(body);
        Select(left, "openai.promptCacheOptions.mode", "prompt_cache_options/mode", ["implicit", "explicit"], "prompt_cache_options", "implicit");
        Select(right, "openai.promptCacheOptions.ttl", "prompt_cache_options/ttl", ["30m"], "prompt_cache_options", "30m");
    }
    private void MultiAgent()
    {
        var body = Section("multi_agent", "openai.multiAgent.title");
        Integer(body, "openai.multiAgent.maxConcurrentSubagents", "multi_agent/max_concurrent_subagents", "multi_agent", 3, optional: false);
    }
    private void AgentSession()
    {
        var body = Section("agent", "openai.sessionAgent.title");
        Text(body, "openai.sessionAgent.model", "agent/model", "agent", optional: true);
        Text(body, "openai.sessionAgent.instructions", "agent/instructions", "agent", optional: true, multiline: true);
        var (left, right) = Pair(body);
        Select(left, "openai.sessionAgent.effort", "agent/reasoning/effort", OpenAIChatConfig.Efforts, "agent", inherit: true);
        Select(right, "openai.sessionAgent.summary", "agent/reasoning/summary", ["concise", "detailed", "auto"], "agent", inherit: true);
        Select(body, "openai.sessionAgent.serviceTier", "agent/service_tier", ["auto", "default", "flex", "priority", "fast", "ultrafast"], "agent", inherit: true);
        (left, right) = Pair(body);
        Select(left, "openai.sessionAgent.verbosity", "agent/text/verbosity", ["low", "medium", "high"], "agent", inherit: true);
        fields.Select(right, "openai.sessionAgent.format", () => model.String("agent/text/format/type"), ["text", "json_schema"], value =>
            model.Set("agent/text/format", value.Length == 0 ? null : value == "text" ? new JsonObject { ["type"] = value } : new JsonObject { ["type"] = value, ["schema"] = new JsonObject() }, value.Length == 0), On("agent"), true);
        var schema = Column(body); fields.Visible(schema, () => model.Enabled("agent") && model.String("agent/text/format/type") == "json_schema");
        fields.Json(schema, "openai.sessionAgent.schema", () => model.Get("agent/text/format/schema"), value => model.Set("agent/text/format/schema", value), () => model.Enabled("agent") && model.String("agent/text/format/type") == "json_schema");
        fields.Switch(body, "openai.sessionAgent.multiAgent", () => model.Boolean("agent/multi_agent/enabled"), value => model.Set("agent/multi_agent", value ? new JsonObject { ["enabled"] = true } : null, !value), On("agent"));
        var subagents = Column(body); fields.Visible(subagents, () => model.Boolean("agent/multi_agent/enabled"));
        Integer(subagents, "openai.sessionAgent.maxSubagents", "agent/multi_agent/max_concurrent_subagents", "agent", enabled: () => model.Enabled("agent") && model.Boolean("agent/multi_agent/enabled"));
        body.Children.Add(new TextBlock { Text = L("openai.sessionAgent.tools"), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        foreach (var type in new[] { "computer_use", "web_search", "programmatic_tool_calling", "tool_search" })
            fields.Switch(body, "openai.sessionAgent." + type, () => model.AgentTool(type) is not null, value => model.AgentTool(type, value), On("agent"));
        var computer = Column(body); fields.Visible(computer, () => model.AgentTool("computer_use") is not null);
        fields.Switch(computer, "openai.sessionAgent.screenshots", () => model.AgentTool("computer_use")?["include_screenshots"]?.GetValue<bool>() == true,
            value => model.AgentToolField("computer_use", "include_screenshots", JsonValue.Create(value)), () => model.Enabled("agent") && model.AgentTool("computer_use") is not null);
        var programmatic = Column(body); fields.Visible(programmatic, () => model.AgentTool("programmatic_tool_calling") is not null);
        fields.Switch(programmatic, "openai.sessionAgent.programmaticEnabled", () => model.AgentTool("programmatic_tool_calling")?["enabled"]?.ToJsonString() != "false",
            value => model.AgentToolField("programmatic_tool_calling", "enabled", JsonValue.Create(value)), () => model.Enabled("agent") && model.AgentTool("programmatic_tool_calling") is not null);
        var search = Column(body); fields.Visible(search, () => model.AgentTool("web_search") is not null);
        bool SearchOn() => model.Enabled("agent") && model.AgentTool("web_search") is not null;
        (left, right) = Pair(search);
        fields.Select(left, "openai.sessionAgent.searchMode", () => OpenAIChatConfig.Text(model.AgentTool("web_search")?["mode"]) ?? "", ["disabled", "cached", "live"], value => model.AgentToolField("web_search", "mode", JsonValue.Create(value), value.Length == 0), SearchOn, true);
        fields.Select(right, "openai.sessionAgent.searchContext", () => OpenAIChatConfig.Text(model.AgentTool("web_search")?["context_size"]) ?? "", ["low", "medium", "high"], value => model.AgentToolField("web_search", "context_size", JsonValue.Create(value), value.Length == 0), SearchOn, true);
        fields.Text(search, "openai.sessionAgent.allowedDomains", () => model.AgentTool("web_search")?["allowed_domains"] is JsonArray a ? string.Join("\n", a.Select(OpenAIChatConfig.Text)) : "", value =>
        {
            var domains = OpenAIChatConfig.Split(value, true); model.AgentToolField("web_search", "allowed_domains", OpenAIChatConfig.Strings(domains), domains.Length == 0);
        }, SearchOn, true);
        (left, right) = Pair(search);
        foreach (var (panel, key) in new[] { (left, "country"), (right, "region"), (left, "city"), (right, "timezone") })
            fields.Text(panel, "openai.sessionAgent." + key, () => OpenAIChatConfig.Text(model.AgentTool("web_search")?["location"]?[key]) ?? "", value =>
            {
                var location = model.AgentTool("web_search")?["location"]?.DeepClone() as JsonObject ?? new();
                if (string.IsNullOrWhiteSpace(value)) location.Remove(key); else location[key] = value.Trim();
                model.AgentToolField("web_search", "location", location);
            }, SearchOn);
    }
    private void EnvironmentSession()
    {
        var body = Section("environment", "openai.sessionEnvironment.title");
        fields.Select(body, "openai.sessionEnvironment.type", () => model.String("environment/type", "openai_hosted"), ["none", "openai_hosted"], value =>
        { if (model.String("environment/type") != value) model.Set("environment", new JsonObject { ["type"] = value }); }, On("environment"));
        var hosted = Column(body); fields.Visible(hosted, () => model.String("environment/type") == "openai_hosted");
        bool Hosted() => model.Enabled("environment") && model.String("environment/type") == "openai_hosted";
        Text(hosted, "openai.sessionEnvironment.templateId", "environment/environment_template_id", "environment", optional: true, enabled: Hosted);
        Select(hosted, "openai.sessionEnvironment.containerSize", "environment/container_size", ["small", "medium", "large"], "environment", inherit: true, enabled: Hosted);
        Switch(hosted, "openai.sessionEnvironment.desktop", "environment/desktop/enabled", "environment", enabled: Hosted);
        fields.Select(hosted, "openai.sessionEnvironment.network", () => model.String("environment/network/access"), ["enabled", "disabled", "restricted"], value =>
        {
            if (value.Length == 0) model.Set("environment/network", null, true);
            else if (value == "restricted" && model.String("environment/network/access") == "restricted") { }
            else model.Set("environment/network", new JsonObject { ["access"] = value });
        }, Hosted, true);
        var network = Column(hosted); fields.Visible(network, () => Hosted() && model.String("environment/network/access") == "restricted");
        network.Children.Add(new TextBlock { Text = L("openai.sessionEnvironment.domainHelp"), TextWrapping = TextWrapping.Wrap });
        List(network, "openai.sessionEnvironment.allowedDomains", "environment/network/allowed_domains", "environment", true, true,
            () => Hosted() && model.String("environment/network/access") == "restricted" && (model.Get("environment/network/blocked_domains") as JsonArray)?.Count is not > 0);
        List(network, "openai.sessionEnvironment.blockedDomains", "environment/network/blocked_domains", "environment", true, true,
            () => Hosted() && model.String("environment/network/access") == "restricted" && (model.Get("environment/network/allowed_domains") as JsonArray)?.Count is not > 0);
    }
    private void Other()
    {
        var body = Section("other", "other", false);
        fields.Select(body, "openai.imageInputDetail.title", () => model.String("inputImageDetail", "auto"), ["auto", "low", "high", "original"], value => model.Set("inputImageDetail", JsonValue.Create(value)));
        fields.Select(body, "openai.serviceTier.title", () => model.String("service_tier", "auto"), ["auto", "default", "flex", "scale", "fast", "ultrafast"], value => model.Set("service_tier", JsonValue.Create(value)));
        fields.Switch(body, "parallelToolCalls", () => model.Boolean("parallel_tool_calls"), value => model.Set("parallel_tool_calls", JsonValue.Create(value)));
    }
}
