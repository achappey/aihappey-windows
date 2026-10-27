using System.Text.Json.Nodes;

namespace AIHappey.Desktop.Core;

/// <summary>Matches the browser Mistral conversation configuration, not Chat Completions SDK options.</summary>
public sealed class MistralChatConfig : ProviderToolsChatConfig
{
    public static readonly string[] ToolTypes = ["web_search", "web_search_premium", "image_generation", "code_interpreter", "document_library"];
    public static readonly string[] Efforts = ["high", "none"];
    public static readonly string[] CompletionKeys = ["reasoning_effort", "random_seed", "top_p", "presence_penalty", "frequency_penalty"];
    public MistralChatConfig(JsonObject config) : base(Normalize(config), ToolTypes) { }
    public static JsonObject Defaults() => JsonNode.Parse("""{"tools":[{"type":"web_search_premium"},{"type":"code_interpreter"},{"type":"image_generation"}]}""")!.AsObject();
    public static JsonObject Normalize(JsonObject config)
    {
        var next = (JsonObject)config.DeepClone();
        var args = next["completion_args"] as JsonObject ?? new();
        foreach (var key in CompletionKeys)
        {
            var value = args[key] ?? next[key];
            if (value is not null) args[key] = value.DeepClone(); else args.Remove(key);
            next.Remove(key);
        }
        next.Remove("completion_args");
        if (args.Count > 0) next["completion_args"] = args.DeepClone();
        return next;
    }
    public override JsonObject Commit() => Normalize(base.Commit());
    public override JsonObject ToolDefault(string type) => type == "document_library"
        ? new() { ["type"] = type, ["library_ids"] = new JsonArray() } : base.ToolDefault(type);
    public bool SearchOn => Enabled("web_search") || Enabled("web_search_premium");
    public void Search(bool enabled)
    {
        Set("web_search_premium", null); ToggleTool("web_search", enabled);
    }
    public void Premium(bool enabled)
    {
        ToggleTool("web_search", !enabled); ToggleTool("web_search_premium", enabled);
    }
    public bool ReasoningOn => Get("completion_args/reasoning_effort") is not null;
    public void Reasoning(bool enabled) => Completion("reasoning_effort", enabled ? JsonValue.Create("high") : null);
    public void Completion(string key, JsonNode? value) => Set("completion_args/" + key, value, value is null);
}
