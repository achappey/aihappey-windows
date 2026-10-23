using System.Globalization;
using System.Text.Json.Nodes;

namespace AIHappey.Desktop.Core;

/// <summary>A JSON editing draft, not a provider SDK or request translation layer.</summary>
public class ProviderChatConfig
{
    public JsonObject Root { get; protected set; }
    public ProviderChatConfig(JsonObject config) => Root = (JsonObject)config.DeepClone();
    protected virtual void Change(Action change) => change();
    public JsonNode? Get(string path)
    {
        JsonNode? node = Root;
        foreach (var key in path.Split('/')) node = node is JsonObject obj ? obj[key] : null;
        return node;
    }
    public static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    public string String(string path, string fallback = "") => Text(Get(path)) ?? fallback;
    public bool Boolean(string path, bool fallback = false) => Get(path) is JsonValue v && v.TryGetValue<bool>(out var result) ? result : fallback;
    public bool Enabled(string path) => Get(path) is JsonObject;
    public string NumberText(string path) => Get(path)?.ToJsonString() ?? "";
    public string Joined(string path) => Get(path) is JsonArray a ? string.Join(", ", a.Select(Text).Where(v => v is not null)) : "";
    public static JsonArray Strings(IEnumerable<string> values) => new(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
    // Anthropic's list inputs split on commas, retaining order and repeated nonempty entries.
    public static string[] Split(string value) => value.Split(',').Select(v => v.Trim()).Where(v => v.Length > 0).ToArray();
    public void Set(string path, JsonNode? value, bool omit = false) => Change(() => SetRaw(path, value, omit));
    protected void SetRaw(string path, JsonNode? value, bool omit = false)
    {
        var keys = path.Split('/'); var obj = Root;
        foreach (var key in keys[..^1])
        {
            if (obj[key] is not JsonObject child)
            {
                if (omit) return; // Removing an absent optional field must not create empty parents.
                child = new(); obj[key] = child;
            }
            obj = child;
        }
        if (omit) obj.Remove(keys[^1]); else obj[keys[^1]] = value?.DeepClone();
    }
    public void Optional(string path, string value) => Set(path, JsonValue.Create(value.Trim()), string.IsNullOrWhiteSpace(value));
    public void StringList(string path, string value)
    {
        var list = Split(value); Set(path, Strings(list), list.Length == 0);
    }
    public void ToggleArray(string path, string value, bool enabled)
    {
        var values = Get(path) is JsonArray a ? a.Select(Text).Where(v => v is not null).Cast<string>().ToList() : [];
        if (enabled) { if (!values.Contains(value)) values.Add(value); } else values.RemoveAll(v => v == value);
        Set(path, Strings(values), values.Count == 0);
    }
    public static bool TryNumber(string value, out double? number)
    {
        number = null;
        if (string.IsNullOrWhiteSpace(value)) return true;
        if (!double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) || !double.IsFinite(parsed)) return false;
        number = parsed; return true;
    }
}
