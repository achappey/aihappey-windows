using System.Text.Json.Nodes;

namespace AIHappey.Desktop.Core;

/// <summary>Browser tool aliases are an editing view only. Persisted objects use tools[].
/// Deliberately separate from the older editors so their provider-specific policies do not change.</summary>
public abstract class ProviderToolsChatConfig : ProviderChatConfig
{
    private readonly string[] toolTypes;
    protected ProviderToolsChatConfig(JsonObject config, string[] types) : base(Resolve(config, types)) => toolTypes = types;
    private static bool Matches(string? candidate, string type) => candidate == type || candidate is not null
        && candidate.StartsWith(type + "_", StringComparison.Ordinal) && candidate.Length == type.Length + 9
        && candidate[(type.Length + 1)..].All(char.IsAsciiDigit);
    public static JsonObject Resolve(JsonObject config, string[] types)
    {
        var next = (JsonObject)config.DeepClone();
        foreach (var type in types)
        {
            var tools = (config["tools"] as JsonArray)?.OfType<JsonObject>().ToArray() ?? [];
            var tool = tools.FirstOrDefault(t => Text(t["type"]) == type)
                ?? tools.FirstOrDefault(t => Matches(Text(t["type"]), type)) ?? config[type] as JsonObject;
            // An explicit null alias is needed to prevent an old tools[] entry being resurrected.
            next[type] = tool?.DeepClone();
            if (next[type] is JsonObject copy) copy["type"] ??= type;
        }
        return next;
    }
    public static JsonObject Canonical(JsonObject config, string[] types)
    {
        var next = Resolve(config, types);
        foreach (var type in types) if (config.ContainsKey(type)) next[type] = config[type]?.DeepClone();
        var tools = new JsonArray();
        if (config["tools"] is JsonArray existing)
            foreach (var tool in existing.OfType<JsonObject>())
                if (Text(tool["type"]) is { Length: > 0 } candidate && !types.Any(type => Matches(candidate, type))) tools.Add(tool.DeepClone());
        foreach (var type in types)
        {
            if (next[type] is JsonObject tool)
            {
                var copy = (JsonObject)tool.DeepClone(); copy["type"] ??= type; tools.Add(copy);
            }
            next.Remove(type);
        }
        next.Remove("tools");
        if (tools.Count > 0) next["tools"] = tools;
        return next;
    }
    public virtual JsonObject Commit() => Canonical(Root, toolTypes);
    public void ToggleTool(string type, bool enabled) => Set(type, enabled ? ToolDefault(type) : null);
    public virtual JsonObject ToolDefault(string type) => new() { ["type"] = type };
}
