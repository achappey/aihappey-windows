using System.Text.Json.Nodes;

namespace AIHappey.Desktop.Core;

public sealed partial class GoogleChatConfig
{
    public static readonly string[] SourceTypes = ["repository", "gcs", "inline"];
    public static readonly string[] NetworkModes = ["default", "disabled", "allowlist"];
    public static JsonObject SourceDefault() => new() { ["type"] = "repository", ["source"] = "", ["target"] = "/workspace" };
    public static JsonObject AllowlistDefault() => new() { ["domain"] = "*" };
    public bool EnvironmentOn => Get("environment") is { } node && node.ToJsonString() != "false";
    public string EnvironmentId => String("environment") is { } id && id != "remote" ? id : "";
    public string NetworkMode => String("environment/network") == "disabled" ? "disabled"
        : Get("environment/network") is JsonObject ? "allowlist" : "default";
    public static JsonNode? BuildEnvironment(bool enabled, string id, JsonArray sources, string networkMode, JsonArray rules, JsonObject? existing = null)
    {
        if (!enabled) return null;
        if (!string.IsNullOrWhiteSpace(id)) return JsonValue.Create(id.Trim());
        var cleanSources = new JsonArray();
        foreach (var source in sources.OfType<JsonObject>())
        {
            var type = Text(source["type"]) ?? "repository";
            if (!SourceTypes.Contains(type)) type = "repository";
            var target = Text(source["target"])?.Trim() ?? "";
            var value = Text(source[type == "inline" ? "content" : "source"]) ?? "";
            if (type != "inline") value = value.Trim();
            if (target.Length == 0 || target == "/" || value.Length == 0) continue;
            var next = (JsonObject)source.DeepClone();
            next["type"] = type; next["target"] = target;
            next.Remove(type == "inline" ? "source" : "content"); next[type == "inline" ? "content" : "source"] = value;
            cleanSources.Add(next);
        }
        var cleanRules = new JsonArray();
        foreach (var rule in rules.OfType<JsonObject>())
        {
            var domain = Text(rule["domain"])?.Trim() ?? "";
            if (domain.Length == 0) continue;
            var next = (JsonObject)rule.DeepClone(); next["domain"] = domain; next.Remove("transform");
            var transform = new JsonObject();
            if (rule["transform"] is JsonObject entries)
                foreach (var pair in entries)
                {
                    var key = pair.Key.Trim(); var value = (Text(pair.Value) ?? pair.Value?.ToJsonString() ?? "").Trim();
                    if (key.Length > 0 && value.Length > 0) transform[key] = value;
                }
            if (transform.Count > 0) next["transform"] = transform;
            cleanRules.Add(next);
        }
        var environment = existing is null ? new JsonObject() : (JsonObject)existing.DeepClone();
        var existingNetwork = environment["network"] as JsonObject;
        environment["type"] = "remote"; environment.Remove("sources"); environment.Remove("network");
        if (cleanSources.Count > 0) environment["sources"] = cleanSources;
        if (networkMode == "disabled") environment["network"] = "disabled";
        else if (networkMode == "allowlist" && cleanRules.Count > 0)
        {
            var network = existingNetwork is null ? new JsonObject() : (JsonObject)existingNetwork.DeepClone();
            network["allowlist"] = cleanRules; environment["network"] = network;
        }
        return environment.Count == 1 ? JsonValue.Create("remote") : environment;
    }
}
