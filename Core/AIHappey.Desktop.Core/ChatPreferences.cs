using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AIHappey.Vercel.Models;

namespace AIHappey.Desktop.Core;

/// <summary>App-wide inference preferences, not conversation data or host credentials.
/// Provider objects retain their native wire shape, including fields unknown to this UI.</summary>
public sealed class ChatPreferences
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxOutputTokens { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SystemInstructions { get; set; }
    private List<string> enabledSkillIds = [];
    public List<string> EnabledSkillIds
    {
        get => enabledSkillIds;
        set => enabledSkillIds = value?.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToList() ?? [];
    }
    public Dictionary<string, JsonObject> ProviderMetadata { get; set; } = new()
    {
        ["openai"] = OpenAIChatConfig.Defaults()
    };
    public Dictionary<string, Dictionary<string, string>> ProviderHeaders { get; set; } = [];

    public ChatPreferences Clone() => new()
    {
        MaxOutputTokens = MaxOutputTokens,
        SystemInstructions = SystemInstructions,
        EnabledSkillIds = EnabledSkillIds.ToList(),
        ProviderMetadata = ProviderMetadata.ToDictionary(p => p.Key, p => (JsonObject)p.Value.DeepClone()),
        ProviderHeaders = ProviderHeaders.ToDictionary(p => p.Key, p => new Dictionary<string, string>(p.Value, StringComparer.OrdinalIgnoreCase))
    };

    public static bool TryTokenLimit(string text, out int? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(text)) return true;
        if (!int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed < 1) return false;
        value = parsed;
        return true;
    }

    public static string? ResolveProvider(ServiceKind service, string modelId, string? providerKey = null)
    {
        if (service != ServiceKind.Ai) return null;
        if (!string.IsNullOrWhiteSpace(providerKey)) return providerKey.Trim().ToLowerInvariant();
        var slash = modelId.IndexOf('/');
        return slash > 0 ? modelId[..slash].Trim().ToLowerInvariant() : null;
    }

    public JsonObject RequestBody(string target, string conversationId, List<UIMessage> messages, string? providerKey, McpTurnSnapshot? mcp = null)
    {
        if (MaxOutputTokens is < 1) throw new InvalidOperationException(DesktopResources.Get("ChatTokensInvalid"));
        var request = new ChatRequest { Id = conversationId, Model = target, Messages = messages, MaxOutputTokens = MaxOutputTokens };
        if (providerKey is not null && ProviderMetadata.TryGetValue(providerKey, out var config))
        {
            var native = providerKey == "openai" ? OpenAIChatConfig.Canonical(config) : config.DeepClone();
            request.ProviderMetadata = new() { [providerKey] = JsonSerializer.SerializeToElement(native) };
        }
        var body = JsonSerializer.SerializeToNode(request, PortableConversations.Json)!.AsObject();
        if (MaxOutputTokens is null) body.Remove("maxOutputTokens");
        if (request.ProviderMetadata is null) body.Remove("providerMetadata");
        if (mcp is not null) body["tools"] = new JsonArray(mcp.Tools.Select(t => JsonNode.Parse(t.GetRawText())).ToArray());
        return body;
    }

    public void ApplyHeaders(HttpRequestMessage request, string? providerKey)
    {
        if (providerKey is null || !ProviderHeaders.TryGetValue(providerKey, out var headers)) return;
        foreach (var (name, value) in headers)
        {
            // Match the gateway's provider passthrough policy. Never permit transport, bearer,
            // API-key or host policy headers to be replaced by persisted provider preferences.
            var providerPrefix = providerKey + "-";
            var xPrefix = "x-" + providerKey + "-";
            var allowed = name.Equals("OpenAI-Beta", StringComparison.OrdinalIgnoreCase)
                || name.Equals("HTTP-Referer", StringComparison.OrdinalIgnoreCase) || name.Equals("X-Title", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith(providerPrefix, StringComparison.OrdinalIgnoreCase) && name.Length > providerPrefix.Length
                || name.StartsWith(xPrefix, StringComparison.OrdinalIgnoreCase) && name.Length > xPrefix.Length
                    && !name.Equals(xPrefix + "key", StringComparison.OrdinalIgnoreCase);
            if (!allowed || name.Any(char.IsControl) || value.Any(char.IsControl) || string.IsNullOrWhiteSpace(value) || request.Headers.Contains(name)) continue;
            request.Headers.TryAddWithoutValidation(name, value.Trim());
        }
    }
}

/// <summary>Mutable draft view with browser-compatible tool aliases. Only Canonical is stored/sent.</summary>
public sealed class OpenAIChatConfig
{
    public static readonly string[] ToolTypes = ["web_search", "image_generation", "code_interpreter", "file_search", "shell", "programmatic_tool_calling", "tool_search"];
    public static readonly string[] Efforts = ["none", "minimal", "low", "medium", "high", "xhigh", "max"];
    public JsonObject Root { get; }
    public Dictionary<string, string> Headers { get; }
    public OpenAIChatConfig(JsonObject config, Dictionary<string, string>? headers = null)
    {
        Root = Resolve(config);
        Headers = new(headers ?? [], StringComparer.OrdinalIgnoreCase);
    }

    public static JsonObject Defaults() => JsonNode.Parse("""
        {"tools":[{"type":"web_search","user_location":null},
          {"type":"image_generation","model":"gpt-image-2.5-sunburst","partial_images":3,"quality":"auto","action":"auto","moderation":"auto","output_compression":100,"background":"auto","size":"auto"}],
         "include":["web_search_call.action.sources","reasoning.encrypted_content","code_interpreter_call.outputs","file_search_call.results"],
         "store":false,"service_tier":"auto","context_management":[{"type":"compaction","compact_threshold":512000}],
         "reasoning":{"effort":"medium","context":"auto","mode":"standard","summary":"auto"},"parallel_tool_calls":true}
        """)!.AsObject();

    public static JsonObject SectionDefault(string section) => JsonNode.Parse(section switch
    {
        "reasoning" => """{"effort":"medium","context":"auto","mode":"standard","summary":"auto"}""",
        "web_search" => """{"search_context_size":"medium"}""",
        "image_generation" => """{"model":"gpt-image-1.5","action":"auto","size":"auto","quality":"auto","input_fidelity":"low","background":"auto","moderation":"auto","output_compression":100,"partial_images":3}""",
        "code_interpreter" => """{"container":{"type":"auto"}}""",
        "shell" => """{"type":"shell","environment":{"type":"container_auto"}}""",
        "file_search" => """{"max_num_results":10,"vector_store_ids":[]}""",
        "moderation" => """{"model":"omni-moderation-latest","policy":{"input":{"mode":"score"},"output":{"mode":"score"}}}""",
        "prompt_cache_options" => """{"mode":"implicit","ttl":"30m"}""",
        "multi_agent" => """{"enabled":true,"max_concurrent_subagents":3}""",
        "agent" => """{"tools":[]}""",
        "environment" => """{"type":"openai_hosted"}""",
        _ => "{}"
    })!.AsObject();

    private static bool Matches(string? candidate, string type) => candidate == type || candidate is not null
        && candidate.StartsWith(type + "_", StringComparison.Ordinal) && candidate.Length == type.Length + 9
        && candidate[(type.Length + 1)..].All(char.IsAsciiDigit);

    public static JsonObject Resolve(JsonObject config)
    {
        var next = (JsonObject)config.DeepClone();
        foreach (var type in ToolTypes)
        {
            var tools = config["tools"] as JsonArray;
            var tool = tools?.OfType<JsonObject>().FirstOrDefault(t => Text(t["type"]) == type)
                ?? tools?.OfType<JsonObject>().FirstOrDefault(t => Matches(Text(t["type"]), type)) ?? config[type] as JsonObject;
            if (tool is null) next.Remove(type);
            else { next[type] = tool.DeepClone(); next[type]!["type"] ??= type; }
        }
        return next;
    }

    public static JsonObject Canonical(JsonObject config)
    {
        var view = Resolve(config);
        // Explicit aliases (including a disabled/null alias) override the canonical array,
        // just like buildCanonicalProviderToolsConfig in the browser.
        foreach (var type in ToolTypes)
            if (config.ContainsKey(type)) view[type] = config[type]?.DeepClone();
        var tools = new JsonArray();
        if (config["tools"] is JsonArray existing)
            foreach (var tool in existing.OfType<JsonObject>())
                if (Text(tool["type"]) is { Length: > 0 } candidate && !ToolTypes.Any(type => Matches(candidate, type))) tools.Add(tool.DeepClone());
        foreach (var type in ToolTypes)
        {
            if (view[type] is JsonObject tool) { var copy = (JsonObject)tool.DeepClone(); copy["type"] ??= type; tools.Add(copy); }
            view.Remove(type);
        }
        view.Remove("tools");
        if (tools.Count > 0) view["tools"] = tools;
        view.Remove("instructions"); // The browser chat form intentionally excludes top-level instructions.
        return view;
    }

    public JsonNode? Get(string path)
    {
        JsonNode? node = Root;
        foreach (var key in path.Split('/')) node = node is JsonObject obj ? obj[key] : null;
        return node;
    }
    public string String(string path, string fallback = "") => Text(Get(path)) ?? fallback;
    public bool Boolean(string path, bool fallback = false) => Get(path) is JsonValue v && v.TryGetValue<bool>(out var result) ? result : fallback;
    public double Number(string path, double fallback = 0) => Get(path) is JsonValue v && v.TryGetValue<double>(out var result) ? result : fallback;
    public bool Enabled(string section) => Get(section) is JsonObject;
    public static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    public static JsonArray Strings(IEnumerable<string> values) => new(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
    public static string[] Split(string value, bool unique = false)
    {
        var values = value.Split([',', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries).Select(v => v.Trim()).Where(v => v.Length > 0);
        return (unique ? values.Distinct(StringComparer.Ordinal) : values).ToArray();
    }
    public string Joined(string path) => Get(path) is JsonArray a ? string.Join(", ", a.Select(Text).Where(v => v is not null)) : "";

    public void Set(string path, JsonNode? value, bool omit = false)
    {
        var keys = path.Split('/');
        var obj = Root;
        foreach (var key in keys[..^1])
        {
            if (obj[key] is not JsonObject child) { child = new(); obj[key] = child; }
            obj = child;
        }
        if (omit) obj.Remove(keys[^1]); else obj[keys[^1]] = value?.DeepClone();
    }
    public void Optional(string path, string value) => Set(path, JsonValue.Create(value.Trim()), string.IsNullOrWhiteSpace(value));
    public void Toggle(string section, bool enabled)
    {
        // Keep an explicit null alias so Canonical cannot resurrect a disabled tool from tools[].
        Set(section, enabled ? SectionDefault(section) : null, !ToolTypes.Contains(section) && !enabled);
        if (enabled && section is "code_interpreter" or "shell" && Enabled("programmatic_tool_calling")) Set(section + "/allowed_callers", Strings(["direct", "programmatic"]));
        if (section == "programmatic_tool_calling")
            foreach (var tool in new[] { "shell", "code_interpreter" })
                if (Enabled(tool)) Set(tool + "/allowed_callers", enabled ? Strings(["direct", "programmatic"]) : null, !enabled);
        if (section == "multi_agent") SetBeta("responses_multi_agent=v1", enabled);
    }
    public void SetBeta(string flag, bool enabled)
    {
        var flags = Headers.TryGetValue("OpenAI-Beta", out var value) ? Split(value, true).ToList() : [];
        flags.RemoveAll(v => v == flag);
        if (enabled) flags.Add(flag);
        Headers.Remove("OpenAI-Beta");
        if (flags.Count > 0) Headers["OpenAI-Beta"] = string.Join(",", flags);
    }
    public bool Includes(string flag) => Get("include") is JsonArray values && values.Any(v => Text(v) == flag);
    public void Include(string flag, bool enabled) => ToggleArray("include", flag, enabled);
    public void ToggleArray(string path, string flag, bool enabled)
    {
        var values = Get(path) is JsonArray list ? list.Select(Text).Where(v => v is not null).Cast<string>().ToList() : [];
        values.RemoveAll(v => v == flag);
        if (enabled) values.Add(flag);
        Set(path, Strings(values.Distinct()), values.Count == 0);
    }
    public void NormalizeWebSearch()
    {
        if (Get("web_search") is not JsonObject web) return;
        if (Text(web["return_token_budget"]) != "unlimited") web.Remove("return_token_budget");
        if (web["external_web_access"]?.ToJsonString() != "false") web.Remove("external_web_access");
        if (web["filters"] is JsonObject filters)
        {
            var domains = filters["allowed_domains"] is JsonArray a ? a.Select(Text).Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!.Trim()).Distinct().ToArray() : [];
            if (domains.Length == 0) web.Remove("filters"); else filters["allowed_domains"] = Strings(domains);
        }
        var types = web["search_content_types"] is JsonArray content ? new[] { "text", "image" }.Where(v => content.Any(n => Text(n) == v)).ToArray() : [];
        if (types.Length == 0) web.Remove("search_content_types"); else web["search_content_types"] = Strings(types);
        if (!types.Contains("image")) web.Remove("image_settings");
        else if (web["image_settings"] is JsonObject images)
        {
            var normalized = new JsonObject();
            if (images["max_results"] is JsonValue n && n.TryGetValue<double>(out var max) && max > 0) normalized["max_results"] = (int)max;
            if (images["caption"]?.ToJsonString() == "true") normalized["caption"] = true;
            if (normalized.Count == 0) web.Remove("image_settings"); else web["image_settings"] = normalized;
        }
    }
    public void WebLocation(string field, string value)
    {
        Set("web_search/user_location/" + field, JsonValue.Create(value));
        if (Get("web_search/user_location") is JsonObject location)
        {
            if (new[] { "country", "region", "city", "timezone" }.All(k => string.IsNullOrEmpty(Text(location[k])))) Set("web_search/user_location", null, true);
            else location["type"] = "approximate";
        }
        NormalizeWebSearch();
    }
    public void ImageModel(string value)
    {
        Set("image_generation/model", JsonValue.Create(value));
        if (value.StartsWith("gpt-image-2", StringComparison.Ordinal)) Set("image_generation/input_fidelity", null, true);
    }
    public void ShellEnvironment(string type)
    {
        var old = Get("shell/environment") as JsonObject;
        var next = new JsonObject { ["type"] = type };
        if (type == "container_reference") next["container_id"] = Text(old?["type"]) == type ? Text(old?["container_id"]) ?? "" : "";
        else
        {
            next["skills"] = old?["skills"]?.DeepClone() ?? new JsonArray();
            if (type == "container_auto" && Text(old?["type"]) == type)
                foreach (var key in new[] { "memory_limit", "network_policy" }) if (old!.ContainsKey(key)) next[key] = old[key]?.DeepClone();
        }
        Set("shell/environment", next);
    }
    public void AgentTool(string type, bool enabled)
    {
        var tools = Get("agent/tools") as JsonArray ?? new();
        var next = new JsonArray(tools.OfType<JsonObject>().Where(t => Text(t["type"]) != type).Select(t => (JsonNode?)t.DeepClone()).ToArray());
        if (enabled) next.Add(new JsonObject { ["type"] = type });
        Set("agent/tools", next);
    }
    public JsonObject? AgentTool(string type) => (Get("agent/tools") as JsonArray)?.OfType<JsonObject>().FirstOrDefault(t => Text(t["type"]) == type);
    public void AgentToolField(string type, string field, JsonNode? value, bool omit = false)
    {
        if (AgentTool(type) is not { } tool) return;
        if (omit) tool.Remove(field); else tool[field] = value?.DeepClone();
    }
}
