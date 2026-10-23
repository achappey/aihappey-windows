using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using static AIHappey.Desktop.Core.ChatSettingsFields;

namespace AIHappey.Desktop.Core;

internal sealed partial class AnthropicChatSettingsForm
{
    private void ContextManagement()
    {
        var body = Section("context_management", A("contextManagement.title"));
        var entries = new StackPanel { Spacing = 12 }; body.Children.Add(entries);
        int? editing = null; ProviderChatConfig? draft = null;
        JsonArray Values() => (JsonArray?)model.Get("context_management/edits")?.DeepClone() ?? new();
        void Cancel() { editing = null; draft = null; Render(); }
        void Render()
        {
            entries.Children.Clear(); childFields.Remove("context_management");
            var values = Values();
            if (values.Count == 0) entries.Children.Add(new TextBlock { Text = L(A("contextManagement.empty")), TextWrapping = TextWrapping.Wrap });
            for (var i = 0; i < values.Count; i++)
            {
                var index = i; var row = new StackPanel { Spacing = 8 }; entries.Children.Add(row);
                var entry = values[index] as JsonObject ?? new();
                var type = ProviderChatConfig.Text(entry["type"]) ?? AnthropicChatConfig.ContextTypes[0];
                row.Children.Add(new TextBlock { Text = L(A("contextManagement.types." + type)), TextWrapping = TextWrapping.Wrap, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
                var summary = new ProviderChatConfig(entry);
                var details = new[] { summary.NumberText("trigger/value"), summary.NumberText("keep/value"), summary.String("instructions") }.Where(v => v.Length > 0);
                row.Children.Add(new TextBlock { Text = string.Join(" · ", details), TextWrapping = TextWrapping.Wrap });
                var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 }; row.Children.Add(actions);
                void Move(int destination) => fields.Changed(() =>
                {
                    var next = Values(); var item = next[index]?.DeepClone(); next.RemoveAt(index); next.Insert(destination, item); model.ContextEntries(next);
                    if (editing == index) editing = destination; else if (editing == destination) editing = index; Render();
                });
                Button(actions, "up", () => Move(index - 1)).IsEnabled = index > 0;
                Button(actions, "down", () => Move(index + 1)).IsEnabled = index < values.Count - 1;
                Button(actions, "edit", () => { editing = index; draft = new(entry); Render(); });
                Button(actions, "delete", () => fields.Changed(() =>
                {
                    var next = Values(); next.RemoveAt(index); model.ContextEntries(next);
                    if (editing == index) { editing = null; draft = null; } else if (editing > index) editing--; Render();
                }));
                if (editing != index || draft is null) continue;
                var editor = Column(row); var local = new ChatSettingsFields(); childFields["context_management"] = local;
                local.Select(editor, A("contextManagement.type"), () => draft.String("type"), AnthropicChatConfig.ContextTypes,
                    value => { draft = new(AnthropicChatConfig.ContextDefault(value)); Render(); }, label: value => L(A("contextManagement.types." + value)));
                ContextEditor(editor, local, draft);
                var error = new TextBlock { Text = L(A("contextManagement.validation.invalid")), TextWrapping = TextWrapping.Wrap }; editor.Children.Add(error);
                var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 }; editor.Children.Add(buttons);
                Button(buttons, "cancel", Cancel);
                var save = Button(buttons, "save", () => fields.Changed(() =>
                {
                    if (!local.IsValid || !AnthropicChatConfig.ContextValid(draft.Root)) { (local.FirstInvalid)?.Focus(FocusState.Programmatic); return; }
                    var next = Values(); next[index] = draft.Root.DeepClone(); model.ContextEntries(next); Cancel();
                }));
                local.Watch(() =>
                {
                    save.IsEnabled = local.IsValid && AnthropicChatConfig.ContextValid(draft.Root);
                    error.Visibility = save.IsEnabled ? Visibility.Collapsed : Visibility.Visible;
                });
                local.Refresh();
            }
        }
        Button(body, A("contextManagement.add"), () => fields.Changed(() =>
        {
            var next = Values(); var entry = AnthropicChatConfig.ContextDefault(AnthropicChatConfig.ContextTypes[0]); next.Add(entry);
            model.ContextEntries(next); editing = next.Count - 1; draft = new(entry); Render();
        }));
        Render();
    }
    private static void ContextEditor(Panel editor, ChatSettingsFields local, ProviderChatConfig draft)
    {
        string K(string key) => A("contextManagement." + key);
        void Quantity(string key, string[] types)
        {
            var (left, right) = Pair(editor);
            local.Select(left, K(key == "clear_at_least" ? "clearAtLeast" : key), () => draft.String(key + "/type", "none"), types.Prepend("none"), value =>
            {
                if (value == "none") draft.Set(key, null, true);
                else if (draft.String(key + "/type") != value) draft.Set(key, new JsonObject { ["type"] = value });
            }, label: value => L(K("modes." + value)));
            bool Active() => types.Contains(draft.String(key + "/type"));
            local.Visible(right, Active);
            local.Number(right, K(key == "clear_at_least" ? "clearAtLeastValue" : key + "Value"), () => draft.NumberText(key + "/value"),
                value => draft.Set(key + "/value", value is null ? null : JsonValue.Create(value.Value), value is null), Active, optional: false);
        }
        switch (draft.String("type"))
        {
            case "clear_tool_uses_20250919":
                Quantity("trigger", ["input_tokens", "tool_uses"]); Quantity("keep", ["tool_uses"]); Quantity("clear_at_least", ["input_tokens"]);
                local.Select(editor, K("clearToolInputs"), () => draft.Get("clear_tool_inputs") is JsonArray ? "list"
                    : draft.Get("clear_tool_inputs") is JsonValue b && b.TryGetValue<bool>(out _) ? "boolean" : "none", ["none", "boolean", "list"], value =>
                    draft.Set("clear_tool_inputs", value == "boolean" ? JsonValue.Create(true) : value == "list" ? new JsonArray() : null, value == "none"),
                    label: value => L(K("modes." + value)));
                bool BooleanOn() => draft.Get("clear_tool_inputs") is JsonValue b && b.TryGetValue<bool>(out _);
                var boolean = Column(editor); local.Visible(boolean, BooleanOn);
                local.Select(boolean, K("clearToolInputsValue"), () => draft.Boolean("clear_tool_inputs") ? "true" : "false", ["true", "false"],
                    value => draft.Set("clear_tool_inputs", JsonValue.Create(value == "true")), BooleanOn, label: value => L(K("booleanValues." + value)));
                var list = Column(editor); local.Visible(list, () => draft.Get("clear_tool_inputs") is JsonArray);
                local.Text(list, K("clearToolInputsList"), () => draft.Joined("clear_tool_inputs"), value => draft.Set("clear_tool_inputs", ProviderChatConfig.Strings(ProviderChatConfig.Split(value))), () => draft.Get("clear_tool_inputs") is JsonArray);
                local.Text(editor, K("excludeTools"), () => draft.Joined("exclude_tools"), value => draft.StringList("exclude_tools", value));
                break;
            case "clear_thinking_20251015":
                local.Select(editor, K("keep"), () => ProviderChatConfig.Text(draft.Get("keep")) == "all" ? "all_literal"
                    : draft.String("keep/type") == "all" ? "all_object" : draft.String("keep/type", "none"), ["none", "thinking_turns", "all_object", "all_literal"], value =>
                    {
                        if (value == "none") draft.Set("keep", null, true);
                        else if (value == "all_literal") draft.Set("keep", JsonValue.Create("all"));
                        else if (value == "all_object") draft.Set("keep", new JsonObject { ["type"] = "all" });
                        else if (draft.String("keep/type") != "thinking_turns") draft.Set("keep", new JsonObject { ["type"] = "thinking_turns" });
                    }, label: value => L(K("modes." + value)));
                var turns = Column(editor); local.Visible(turns, () => draft.String("keep/type") == "thinking_turns");
                local.Number(turns, K("keepValue"), () => draft.NumberText("keep/value"), value => draft.Set("keep/value", value is null ? null : JsonValue.Create(value.Value), value is null),
                    () => draft.String("keep/type") == "thinking_turns", optional: false);
                break;
            case "compact_20260112":
                Quantity("trigger", ["input_tokens"]);
                local.Select(editor, K("pauseAfterCompaction"), () => draft.Get("pause_after_compaction") is JsonValue p && p.TryGetValue<bool>(out var pause) ? pause ? "true" : "false" : "none",
                    ["none", "true", "false"], value => draft.Set("pause_after_compaction", JsonValue.Create(value == "true"), value == "none"),
                    label: value => L(K("booleanModes." + value)));
                local.Text(editor, K("instructions"), () => draft.String("instructions"), value => draft.Set("instructions", JsonValue.Create(value)), multiline: true);
                break;
        }
    }
}
