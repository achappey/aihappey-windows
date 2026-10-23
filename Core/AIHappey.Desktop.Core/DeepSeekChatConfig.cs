using System.Text.Json.Nodes;

namespace AIHappey.Desktop.Core;

/// <summary>Matches the browser DeepSeek form, including its legacy reasoning migration.</summary>
public sealed class DeepSeekChatConfig : ProviderChatConfig
{
    public static readonly string[] Efforts = ["low", "high", "max"];
    public DeepSeekChatConfig(JsonObject config) : base(Normalize(config)) { }
    public static JsonObject Normalize(JsonObject config)
    {
        var next = (JsonObject)config.DeepClone();
        if (!next.ContainsKey("reasoning") && (next.ContainsKey("thinking") || next.ContainsKey("reasoning_effort")))
            next["reasoning"] = new JsonObject { ["effort"] = Text((next["thinking"] as JsonObject)?["type"]) == "disabled"
                ? JsonValue.Create("none") : next["reasoning_effort"]?.DeepClone() ?? JsonValue.Create("high") };
        next.Remove("thinking"); next.Remove("reasoning_effort"); return next;
    }
    public bool ReasoningOn => String("reasoning/effort") != "none";
    public string Effort => Efforts.Contains(String("reasoning/effort")) ? String("reasoning/effort") : "high";
    public void ToggleReasoning(bool enabled) => Set("reasoning/effort", JsonValue.Create(enabled ? "high" : "none"));
    public bool WebSearchOn => (Get("tools") as JsonArray)?.OfType<JsonObject>().Any(t => Text(t["type"]) == "web_search") == true;
    public void WebSearch(bool enabled)
    {
        var tools = Get("tools") as JsonArray ?? new();
        var existing = tools.OfType<JsonObject>().FirstOrDefault(t => Text(t["type"]) == "web_search");
        var next = new JsonArray(tools.Where(t => t is not JsonObject obj || Text(obj["type"]) != "web_search").Select(t => t?.DeepClone()).ToArray());
        if (enabled) next.Add(existing?.DeepClone() ?? new JsonObject { ["type"] = "web_search" });
        Set("tools", next); // The browser deliberately retains tools:[] when switched off.
    }
}
