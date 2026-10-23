using System.Globalization;
using System.Text.Json.Nodes;

namespace AIHappey.Desktop.Core;

public sealed partial class AnthropicChatConfig
{
    private static bool Finite(JsonNode? node) => node is JsonValue value && value.TryGetValue<double>(out var number) && double.IsFinite(number);
    private static bool Quantity(JsonNode? node, params string[] types) => node is JsonObject obj && types.Contains(Text(obj["type"]) ?? "") && Finite(obj["value"]);
    public static bool ContextValid(JsonObject entry)
    {
        bool Optional(string key, Func<JsonNode?, bool> valid) => !entry.ContainsKey(key) || valid(entry[key]);
        return Text(entry["type"]) switch
        {
            "clear_tool_uses_20250919" => Optional("trigger", v => Quantity(v, "input_tokens", "tool_uses"))
                && Optional("keep", v => Quantity(v, "tool_uses")) && Optional("clear_at_least", v => Quantity(v, "input_tokens"))
                && Optional("clear_tool_inputs", v => v is JsonValue b && b.TryGetValue<bool>(out _) || v is JsonArray a && a.All(n => Text(n) is not null)),
            "clear_thinking_20251015" => Optional("keep", v => Text(v) == "all" || v is JsonObject obj && Text(obj["type"]) == "all" || Quantity(v, "thinking_turns")),
            "compact_20260112" => Optional("trigger", v => Quantity(v, "input_tokens")),
            _ => false
        };
    }
    // Browser sanitization intentionally rebuilds this documented subtree on list edits.
    // Unrelated provider options and tools never pass through this sanitizer.
    public static JsonObject CleanContext(JsonObject entry)
    {
        var type = Text(entry["type"]); var result = new JsonObject { ["type"] = type };
        JsonObject? ReadQuantity(string key, params string[] types)
        {
            if (entry[key] is not JsonObject obj || !types.Contains(Text(obj["type"]) ?? "")) return null;
            var node = obj["value"]; double number;
            if (node is null) number = 0;
            else if (node is JsonValue b && b.TryGetValue<bool>(out var boolean)) number = boolean ? 1 : 0;
            else if (!double.TryParse(Text(node) ?? node.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number))
            {
                if (Text(node)?.Trim() == "") number = 0; else return null;
            }
            return double.IsFinite(number) ? new JsonObject { ["type"] = Text(obj["type"]), ["value"] = number } : null;
        }
        void CopyQuantity(string key, params string[] types) { if (ReadQuantity(key, types) is { } quantity) result[key] = quantity; }
        switch (type)
        {
            case "clear_tool_uses_20250919":
                CopyQuantity("trigger", "input_tokens", "tool_uses"); CopyQuantity("keep", "tool_uses"); CopyQuantity("clear_at_least", "input_tokens");
                if (entry["clear_tool_inputs"] is JsonValue boolean && boolean.TryGetValue<bool>(out _)) result["clear_tool_inputs"] = boolean.DeepClone();
                foreach (var key in new[] { "clear_tool_inputs", "exclude_tools" })
                    if (entry[key] is JsonArray values)
                    {
                        var strings = values.Select(Text).Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!.Trim()).ToArray();
                        if (strings.Length > 0) result[key] = Strings(strings);
                    }
                break;
            case "clear_thinking_20251015":
                if (Text(entry["keep"]) == "all") result["keep"] = "all";
                else if (entry["keep"] is JsonObject keep && Text(keep["type"]) == "all") result["keep"] = new JsonObject { ["type"] = "all" };
                else CopyQuantity("keep", "thinking_turns");
                break;
            case "compact_20260112":
                CopyQuantity("trigger", "input_tokens");
                if (Text(entry["instructions"])?.Trim() is { Length: > 0 } instructions) result["instructions"] = instructions;
                if (entry["pause_after_compaction"] is JsonValue pause && pause.TryGetValue<bool>(out _)) result["pause_after_compaction"] = pause.DeepClone();
                break;
            default: result["type"] = "clear_tool_uses_20250919"; break;
        }
        return result;
    }
    public void ContextEntries(JsonArray entries)
    {
        var next = new JsonArray(entries.OfType<JsonObject>().Select(e => (JsonNode?)CleanContext(e)).ToArray());
        if (next.Count == 0) Set("context_management", null, true);
        else Set("context_management/edits", next);
    }
}
