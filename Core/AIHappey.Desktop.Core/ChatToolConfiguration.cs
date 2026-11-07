using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AIHappey.Desktop.Core;

/// <summary>Request-only options, with the browser's wire names and entry-level override semantics.</summary>
public sealed class ChatToolRequestOptions
{
    [JsonPropertyName("allowed_callers")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? AllowedCallers { get; set; }
    [JsonPropertyName("defer_loading")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? DeferLoading { get; set; }
    [JsonIgnore] public bool IsEmpty => AllowedCallers is null && DeferLoading is null;
    public ChatToolRequestOptions Clone() => new() { AllowedCallers = AllowedCallers?.Where(IsCaller).ToList(), DeferLoading = DeferLoading };
    public static bool IsCaller(string caller) => caller is "direct" or "programmatic";
    public static ChatToolRequestOptions Read(JsonElement? value)
    {
        var result = new ChatToolRequestOptions();
        if (value is not { ValueKind: JsonValueKind.Object } options) return result;
        if (options.TryGetProperty("allowed_callers", out var callers) && callers.ValueKind == JsonValueKind.Array)
            result.AllowedCallers = callers.EnumerateArray().Where(c => c.ValueKind == JsonValueKind.String)
                .Select(c => c.GetString()!).Where(IsCaller).ToList();
        if (options.TryGetProperty("defer_loading", out var deferred) && deferred.ValueKind is JsonValueKind.True or JsonValueKind.False)
            result.DeferLoading = deferred.GetBoolean();
        return result;
    }
}

// Source metadata is a sidecar: neither persisted nor injected into MCP tool declarations/context.
public sealed record ChatToolSource(string Kind, string Id, string Name, string? Description = null,
    ChatToolRequestOptions? RequestOptions = null, bool Namespace = false)
{
    public static ChatToolSource Local { get; } = new("local", "local", "Local tools");
    public static ChatToolSource ForLocal(string name)
    {
        var plugin = DesktopLocalTools.Plugins.FirstOrDefault(p => p.Tools.Any(t => CatalogProjection.Text(t, "name") == name));
        return plugin is null ? Local : new("plugin", plugin.Id, plugin.Id);
    }
    public string NamespaceName => ChatToolConfiguration.NamespaceName(Kind is "mcp" or "plugin" ? Name : "local");
}

/// <summary>Native counterpart of browser toolRequestConfig.ts. No provider capability policy or execution changes.</summary>
public static class ChatToolConfiguration
{
    public static readonly IReadOnlyList<string> Callers = Array.AsReadOnly(new[] { "direct", "programmatic" });
    public static ChatToolRequestOptions Effective(ChatPreferences preferences, ChatToolOption tool) =>
        preferences.ToolRequestConfig.TryGetValue(tool.Name, out var options) ? options : tool.Source?.RequestOptions ?? new();

    public static void SetCallers(ChatPreferences preferences, ChatToolOption tool, IEnumerable<string> callers, bool bulk = false)
    {
        // Per-tool caller edits start from the explicit override; bulk edits start from effective source defaults.
        var next = (bulk ? Effective(preferences, tool) : preferences.ToolRequestConfig.GetValueOrDefault(tool.Name) ?? new()).Clone();
        next.AllowedCallers = callers.Where(ChatToolRequestOptions.IsCaller).ToList();
        if (next.AllowedCallers.Count == 0) next.AllowedCallers = null;
        Store(preferences, tool.Name, next);
    }

    public static void SetDeferred(ChatPreferences preferences, ChatToolOption tool, bool value, bool bulk = false)
    {
        var next = Effective(preferences, tool).Clone();
        // The browser keeps explicit false for an individual tool, but clears the bulk property when switched off.
        next.DeferLoading = bulk && !value ? null : value;
        Store(preferences, tool.Name, next);
    }

    private static void Store(ChatPreferences preferences, string name, ChatToolRequestOptions options)
    {
        if (options.IsEmpty) preferences.ToolRequestConfig.Remove(name);
        else preferences.ToolRequestConfig[name] = options;
    }

    public static string NamespaceName(string value)
    {
        var name = Regex.Replace(value.Trim().ToLowerInvariant(), "[^a-z0-9_-]+", "_").Trim('_', '-');
        return name.Length == 0 ? "tools" : name[..Math.Min(64, name.Length)];
    }

    public static JsonArray Shape(IEnumerable<JsonElement> tools, ChatPreferences preferences, Func<string, ChatToolSource> sourceForTool)
    {
        var ordinary = new JsonArray();
        var groups = new Dictionary<string, (ChatToolSource Source, JsonArray Tools)>(StringComparer.Ordinal);
        foreach (var definition in tools)
        {
            var tool = JsonNode.Parse(definition.GetRawText())!.AsObject();
            var name = CatalogProjection.Text(definition, "name")!;
            var source = sourceForTool(name);
            var options = Effective(preferences, new(name, name, source));
            tool.Remove("allowed_callers"); tool.Remove("defer_loading");
            tool.Remove("source"); tool.Remove("requestOptions");
            var callers = options.AllowedCallers?.Where(ChatToolRequestOptions.IsCaller).ToArray() ?? [];
            if (callers.Length > 0) tool["allowed_callers"] = JsonSerializer.SerializeToNode(callers);
            if (options.DeferLoading == true) tool["defer_loading"] = true;
            if (!preferences.UseToolNamespaces && !source.Namespace) { ordinary.Add(tool); continue; }

            var kind = source.Kind is "mcp" or "plugin" ? source.Kind : "local";
            var key = kind + ":" + (kind == "local" ? "local" : source.Id);
            if (!groups.TryGetValue(key, out var group)) groups.Add(key, group = (source, new()));
            var function = new JsonObject { ["type"] = "function", ["name"] = name };
            var description = new[] { tool["description"], tool["title"], (tool["annotations"] as JsonObject)?["title"] }
                .OfType<JsonValue>().Select(v => v.TryGetValue<string>(out var text) ? text.Trim() : "").FirstOrDefault(text => text.Length > 0);
            if (description is not null) function["description"] = description;
            function["parameters"] = (tool["inputSchema"] as JsonObject ?? tool["parameters"] as JsonObject)?.DeepClone()
                ?? new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() };
            foreach (var property in new[] { "allowed_callers", "defer_loading" })
                if (tool[property] is { } value) function[property] = value.DeepClone();
            group.Tools.Add(function);
        }
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (source, functions) in groups.Values)
        {
            var local = source.Kind is not ("mcp" or "plugin");
            var stem = NamespaceName(local ? "local" : source.Name);
            var name = stem; var suffix = 2;
            while (!used.Add(name)) name = stem[..Math.Min(60, stem.Length)] + "_" + suffix++;
            ordinary.Add(new JsonObject { ["type"] = "namespace", ["name"] = name,
                ["description"] = !string.IsNullOrEmpty(source.Description) ? source.Description : local
                    ? "Locally available application tools." : $"Tools provided by {source.Name}.", ["tools"] = functions });
        }
        return ordinary;
    }

    public static void ApplyToRequest(JsonObject body, IEnumerable<JsonElement> tools, ChatPreferences preferences,
        string? providerKey, Func<string, ChatToolSource> sourceForTool)
    {
        var shaped = Shape(tools, preferences, sourceForTool);
        var namespaces = shaped.OfType<JsonObject>().Where(t => t["type"]?.ToString() == "namespace").ToArray();
        body["tools"] = new JsonArray(shaped.Where(t => t is not JsonObject obj || obj["type"]?.ToString() != "namespace").Select(t => t?.DeepClone()).ToArray());
        if (namespaces.Length == 0) return;
        var key = providerKey ?? "";
        var metadata = body["providerMetadata"] as JsonObject ?? new();
        var config = metadata[key] as JsonObject ?? new();
        var names = namespaces.Select(t => t["name"]!.ToString()).ToHashSet(StringComparer.Ordinal);
        var existing = config["tools"] as JsonArray ?? [];
        config["tools"] = new JsonArray(existing.Where(t => t is not JsonObject obj || obj["type"]?.ToString() != "namespace" || !names.Contains(obj["name"]?.ToString() ?? ""))
            .Select(t => t?.DeepClone()).Concat(namespaces.Select(t => t.DeepClone())).ToArray());
        metadata[key] = config; body["providerMetadata"] = metadata;
    }
}
