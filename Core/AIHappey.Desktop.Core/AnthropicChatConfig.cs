using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AIHappey.Desktop.Core;

/// <summary>Browser-compatible Anthropic editing and canonical native Messages API JSON.</summary>
public sealed partial class AnthropicChatConfig : ProviderChatConfig
{
    public const string ContextBeta = "context-management-2025-06-27";
    public const string AdvisorBeta = "advisor-tool-2026-03-01";
    public const string CompactionBeta = "compact-2026-09-04";
    public static readonly string[] ToolTypes = ["advisor", "bash", "code_execution", "memory", "text_editor", "web_fetch", "web_search", "tool_search_tool_bm25", "tool_search_tool_regex"];
    public static readonly string[] AllowedCallers = ["direct", "code_execution_20260521", "code_execution_20260120", "code_execution_20250825"];
    public static readonly string[] Efforts = ["low", "medium", "high", "xhigh", "max"];
    public static readonly string[] AdvisorModels = ["claude-sonnet-5-5", "claude-opus-5-5", "claude-fable-5-1", "claude-opus-5", "claude-opus-4-8", "claude-opus-4-7", "claude-opus-4-6", "claude-sonnet-4-6"];
    public static readonly string[] BuiltInSkills = ["pptx", "xlsx", "docx", "pdf"];
    public static readonly string[] ContextTypes = ["clear_tool_uses_20250919", "clear_thinking_20251015", "compact_20260112"];
    public static readonly string[] Betas = ["mcp-client-2025-11-20", "interleaved-thinking-2025-05-14", ContextBeta,
        "model-context-window-exceeded-2025-08-26", "fast-mode-2026-02-01", "mid-conversation-tool-changes-2026-07-01",
        "managed-agents-2026-04-01", "task-budgets-2026-03-13", "server-side-fallback-2026-07-01", AdvisorBeta,
        "cache-diagnosis-2026-04-07", "thinking-display-updates-2026-08-18", "mid-conversation-system-clear-at-2026-08-21",
        "mid-conversation-output-config-2026-07-01", "thinking-binding-controls-2026-08-01", CompactionBeta,
        "inline-tools-2026-09-15", "mcp-client-2026-09-15"];
    public Dictionary<string, string> Headers { get; }
    private bool autoContextBeta, autoAdvisorBeta;
    public AnthropicChatConfig(JsonObject config, Dictionary<string, string>? headers = null) : base(Resolve(config))
    {
        Headers = new(headers ?? [], StringComparer.OrdinalIgnoreCase);
        // Old browser preferences stored the beta in the body. It is always a header now.
        if (!Headers.ContainsKey("anthropic-beta") && config["anthropic-beta"] is { } legacy)
            Headers["anthropic-beta"] = string.Join(",", ParseBetas(legacy));
        Root.Remove("anthropic-beta");
    }
    public static string[] Versions(string tool) => tool switch
    {
        "advisor" => ["advisor_20260301"], "bash" => ["bash_20250124"],
        "code_execution" => ["code_execution_20260521", "code_execution_20260120", "code_execution_20250825"],
        "memory" => ["memory_20250818"], "text_editor" => ["text_editor_20250728", "text_editor_20250429", "text_editor_20250124"],
        "web_fetch" => ["web_fetch_20260318", "web_fetch_20260309", "web_fetch_20260209", "web_fetch_20250910"],
        "web_search" => ["web_search_20260318", "web_search_20260209", "web_search_20250305"],
        "tool_search_tool_bm25" => ["tool_search_tool_bm25_20251119", "tool_search_tool_bm25"],
        "tool_search_tool_regex" => ["tool_search_tool_regex_20251119", "tool_search_tool_regex"], _ => []
    };
    public static JsonObject Defaults() => JsonNode.Parse("""
        {"service_tier":"auto","cache_control":{"type":"ephemeral"},"tools":[
        {"name":"web_search","type":"web_search_20260318","max_uses":5,"allowed_callers":["direct"]},
        {"name":"web_fetch","type":"web_fetch_20260318","max_uses":5,"allowed_callers":["direct"],"citations":{"enabled":true}},
        {"name":"code_execution","type":"code_execution_20260521","allowed_callers":["direct"]}],
        "max_tokens":64000,"thinking":{"type":"adaptive"},"output_config":{"effort":"medium"}}
        """)!.AsObject();
    public static JsonObject SectionDefault(string section)
    {
        if (ToolTypes.Contains(section))
        {
            var tool = new JsonObject { ["name"] = ToolName(section, Versions(section)[0]), ["type"] = Versions(section)[0] };
            if (section is not ("advisor" or "tool_search_tool_bm25" or "tool_search_tool_regex")) tool["allowed_callers"] = Strings(["direct"]);
            if (section is "web_fetch" or "web_search") tool["max_uses"] = 5;
            if (section == "web_fetch") tool["citations"] = new JsonObject { ["enabled"] = true };
            if (section == "advisor") tool["model"] = AdvisorModels[0];
            return tool;
        }
        return JsonNode.Parse(section switch
        {
            "cache_control" => """{"type":"ephemeral"}""",
            "compaction" => """{"type":"summarize"}""",
            "thinking" => """{"type":"enabled","budget_tokens":4096,"display":"summarized"}""",
            "output_config" => """{"effort":"medium"}""", _ => "{}"
        })!.AsObject();
    }
    public static string ToolName(string section, string version) => section == "text_editor"
        ? version == "text_editor_20250124" ? "str_replace_editor" : "str_replace_based_edit_tool" : section;
    private static bool Matches(string? candidate, string type) => candidate == type || candidate is not null
        && candidate.StartsWith(type + "_", StringComparison.Ordinal) && candidate.Length == type.Length + 9 && candidate[(type.Length + 1)..].All(char.IsAsciiDigit);
    private static string? ManagedName(JsonObject tool) => ToolTypes.FirstOrDefault(type => Matches(Text(tool["type"]), type) || Text(tool["name"]) == type);
    public static JsonObject Resolve(JsonObject config)
    {
        var next = (JsonObject)config.DeepClone(); var tools = (config["tools"] as JsonArray)?.OfType<JsonObject>().ToArray() ?? [];
        foreach (var type in ToolTypes)
        {
            var tool = tools.FirstOrDefault(t => Text(t["type"]) == type) ?? tools.FirstOrDefault(t => Matches(Text(t["type"]), type)) ?? config[type] as JsonObject;
            next[type] = tool?.DeepClone();
            if (next[type] is JsonObject resolved) resolved["type"] ??= type;
        }
        return next;
    }
    private static JsonObject CleanTool(JsonObject tool)
    {
        var next = (JsonObject)tool.DeepClone(); var type = Text(next["type"]) ?? "";
        if (type.StartsWith("web_fetch_", StringComparison.Ordinal) || type.StartsWith("web_search_", StringComparison.Ordinal))
        {
            foreach (var key in new[] { "allowed_domains", "blocked_domains" }) if (next[key] is null) next.Remove(key);
            if (type.StartsWith("web_search_", StringComparison.Ordinal) && next["user_location"] is null) next.Remove("user_location");
        }
        if (type is not ("web_fetch_20260318" or "web_search_20260318")) next.Remove("response_inclusion");
        if (type is not ("web_fetch_20260318" or "web_fetch_20260309")) next.Remove("use_cache");
        return next;
    }
    public static JsonObject Canonical(JsonObject config, bool preferRoot = false)
    {
        var next = (JsonObject)config.DeepClone(); var result = new JsonArray(); var seen = new HashSet<string>();
        if (config["tools"] is JsonArray tools)
            foreach (var node in tools)
            {
                if (node is null) continue;
                if (node is JsonObject tool && ManagedName(tool) is { } name)
                {
                    if (!seen.Add(name)) continue;
                    var selected = preferRoot && config.ContainsKey(name) ? config[name] as JsonObject : tool;
                    if (selected is not null) result.Add(CleanTool(selected));
                }
                else result.Add(node.DeepClone());
            }
        foreach (var name in ToolTypes)
        {
            if (!seen.Contains(name) && config[name] is JsonObject tool) result.Add(CleanTool(tool));
            next.Remove(name);
        }
        next.Remove("tools"); if (result.Count > 0) next["tools"] = result;
        next.Remove("anthropic-beta"); return next;
    }
    public void Toggle(string section, bool enabled) => Set(section, enabled ? SectionDefault(section) : null, !ToolTypes.Contains(section) && !enabled);
    public void Version(string section, string version) => Change(() =>
    {
        SetRaw(section + "/type", JsonValue.Create(version)); SetRaw(section + "/name", JsonValue.Create(ToolName(section, version)));
        if (section == "text_editor" && version != "text_editor_20250728") SetRaw(section + "/max_characters", null, true);
    });
    public void ThinkingMode(string type) => Change(() =>
    {
        var binding = Get("thinking/block_binding")?.DeepClone(); var display = String("thinking/display", "summarized");
        var thinking = type == "enabled" ? SectionDefault("thinking") : new JsonObject { ["type"] = type };
        if (type is "enabled" or "adaptive")
        {
            thinking["display"] = type == "enabled" ? "summarized" : display;
            if (binding is not null) thinking["block_binding"] = binding;
        }
        SetRaw("thinking", thinking);
    });
    public void PrefixMismatch(string value) => Change(() =>
    {
        SetRaw("thinking/block_binding/prefix_mismatch_behavior", JsonValue.Create(value), value.Length == 0);
        if (Get("thinking/block_binding") is JsonObject binding && binding.Count == 0) SetRaw("thinking/block_binding", null, true);
    });
    public bool HasContextEdits => (Get("context_management/edits") as JsonArray)?.Count > 0;
    private static bool HasEdits(JsonObject root) => ((root["context_management"] as JsonObject)?["edits"] as JsonArray)?.Count > 0;
    private static bool HasCompaction(JsonObject root) => Text((root["compaction"] as JsonObject)?["type"]) == "summarize";
    public string[] BetaValues => Headers.TryGetValue("anthropic-beta", out var value) ? Split(value).Distinct(StringComparer.Ordinal).ToArray() : [];
    private static string[] ParseBetas(JsonNode value) => value is JsonArray a
        ? a.Select(Text).Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!.Trim()).Distinct(StringComparer.Ordinal).ToArray()
        : Split(Text(value) ?? "").Distinct(StringComparer.Ordinal).ToArray();
    public void Beta(string flag, bool enabled) => Change(() =>
    {
        var values = BetaValues.ToList(); if (enabled) { if (!values.Contains(flag)) values.Add(flag); } else values.RemoveAll(v => v == flag);
        StoreBetas(values);
    });
    private void StoreBetas(IEnumerable<string> values)
    {
        Headers.Remove("anthropic-beta"); var list = values.Distinct(StringComparer.Ordinal).ToArray();
        if (list.Length > 0) Headers["anthropic-beta"] = string.Join(",", list);
    }
    protected override void Change(Action change)
    {
        var previous = (JsonObject)Root.DeepClone(); var previousBetas = BetaValues;
        change(); Normalize(previous, previousBetas);
    }
    private void Normalize(JsonObject previous, string[] previousBetas)
    {
        var requested = BetaValues; var next = requested.ToList(); var betaListChanged = !previousBetas.SequenceEqual(requested);
        void RequiredBeta(string beta, bool wasEnabled, bool enabled, ref bool owned)
        {
            if (enabled)
            {
                if (!next.Contains(beta)) { next.Add(beta); owned = true; }
                else if (owned && wasEnabled && betaListChanged) owned = false;
            }
            else if (wasEnabled && owned && next.Contains(beta)) { next.Remove(beta); owned = false; }
        }
        RequiredBeta(ContextBeta, HasEdits(previous), HasContextEdits, ref autoContextBeta);
        RequiredBeta(AdvisorBeta, previous["advisor"] is JsonObject, Enabled("advisor"), ref autoAdvisorBeta);
        var wasCompact = HasCompaction(previous); var requestedCompact = HasCompaction(Root);
        var configChanged = wasCompact != requestedCompact;
        var betaChanged = previousBetas.Contains(CompactionBeta) != requested.Contains(CompactionBeta);
        var compact = !HasContextEdits && (configChanged ? requestedCompact : betaChanged ? requested.Contains(CompactionBeta) : requestedCompact || requested.Contains(CompactionBeta));
        SetRaw("compaction", new JsonObject { ["type"] = "summarize" }, !compact);
        if (compact && !next.Contains(CompactionBeta)) next.Add(CompactionBeta);
        if (!HasContextEdits) SetRaw("context_management", null, true);
        if (Root.ContainsKey("container")) SetRaw("container", NormalizeContainer(Root["container"]), NormalizeContainer(Root["container"]) is null);
        StoreBetas(next);
        Root = Resolve(Canonical(Root, true));
    }
    public JsonObject Commit()
    {
        // No synthetic defaults are inserted merely by opening a saved form.
        foreach (var key in Headers.Where(p => string.IsNullOrWhiteSpace(p.Value)).Select(p => p.Key).ToArray()) Headers.Remove(key);
        return Canonical(Root, true);
    }
    public static JsonObject? NormalizeContainer(JsonNode? value)
    {
        if (value is null || value.ToJsonString() == "false") return null;
        var container = value as JsonObject ?? new JsonObject { ["id"] = Text(value) };
        var next = new JsonObject(); var id = Text(container["id"])?.Trim(); if (!string.IsNullOrEmpty(id)) next["id"] = id;
        var skills = new JsonArray(); var seen = new HashSet<string>();
        if (container["skills"] is JsonArray list)
            foreach (var skill in list.OfType<JsonObject>())
            {
                var skillId = Text(skill["skill_id"])?.Trim(); var type = Text(skill["type"])?.Trim();
                if (string.IsNullOrEmpty(skillId) || type is not ("anthropic" or "custom") || !seen.Add(type + ":" + skillId)) continue;
                var copy = (JsonObject)skill.DeepClone(); copy["skill_id"] = skillId; copy["type"] = type;
                var version = Text(copy["version"])?.Trim(); if (string.IsNullOrEmpty(version)) copy.Remove("version"); else copy["version"] = version;
                skills.Add(copy);
            }
        if (skills.Count > 0) next["skills"] = skills; return next;
    }
    public string ContainerId => Text(NormalizeContainer(Get("container"))?["id"]) ?? "";
    public string[] ContainerSkills(string type) => (NormalizeContainer(Get("container"))?["skills"] as JsonArray)?.OfType<JsonObject>()
        .Where(s => Text(s["type"]) == type).Select(s => Text(s["skill_id"])!).ToArray() ?? [];
    public void SetContainerId(string value)
    {
        var container = NormalizeContainer(Get("container")) ?? new(); container["id"] = value; Set("container", container);
    }
    public void SetContainerSkills(IEnumerable<string> custom, IEnumerable<string> builtIn)
    {
        var skills = new JsonArray();
        foreach (var id in custom.Select(v => v.Trim()).Where(v => v.Length > 0).Distinct()) skills.Add(new JsonObject { ["skill_id"] = id, ["type"] = "custom" });
        foreach (var id in BuiltInSkills.Where(builtIn.Contains)) skills.Add(new JsonObject { ["skill_id"] = id, ["type"] = "anthropic", ["version"] = "latest" });
        var container = NormalizeContainer(Get("container")) ?? new(); container.Remove("skills"); if (skills.Count > 0) container["skills"] = skills; Set("container", container);
    }
    public static JsonObject ContextDefault(string type) => JsonNode.Parse(type switch
    {
        "clear_thinking_20251015" => """{"type":"clear_thinking_20251015","keep":{"type":"thinking_turns","value":1}}""",
        "compact_20260112" => """{"type":"compact_20260112","trigger":{"type":"input_tokens","value":150000}}""",
        _ => """{"type":"clear_tool_uses_20250919","trigger":{"type":"input_tokens","value":10000}}"""
    })!.AsObject();
    public static bool TryUsdCents(string value, out string? cents)
    {
        cents = null; var match = Regex.Match(value.Trim(), @"\A([0-9]+)(?:\.([0-9]{1,2}))?\z", RegexOptions.CultureInvariant);
        if (!match.Success) return false;
        var amount = BigInteger.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) * 100
            + BigInteger.Parse(match.Groups[2].Value.PadRight(2, '0'), CultureInfo.InvariantCulture);
        if (amount <= 0) return false; cents = amount.ToString(CultureInfo.InvariantCulture); return true;
    }
    public static string FormatUsd(string? cents)
    {
        if (cents is null || !Regex.IsMatch(cents, @"\A[1-9][0-9]*\z", RegexOptions.CultureInvariant)) return "";
        var amount = BigInteger.Parse(cents, CultureInfo.InvariantCulture);
        return (amount / 100).ToString(CultureInfo.InvariantCulture) + "." + (amount % 100).ToString("D2", CultureInfo.InvariantCulture);
    }
    public void SessionBudget(string usd)
    {
        if (!TryUsdCents(usd, out var amount)) { Set("budget", null, true); return; }
        Set("budget", new JsonObject { ["type"] = "limit", ["max_list_cost"] = new JsonObject { ["amount"] = amount, ["currency"] = "USD" } });
    }
}
