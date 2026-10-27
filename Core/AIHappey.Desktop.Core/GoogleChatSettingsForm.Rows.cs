using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using static AIHappey.Desktop.Core.ChatSettingsFields;

namespace AIHappey.Desktop.Core;

internal sealed partial class GoogleChatSettingsForm
{
    // Each render owns its field registry. Removing a row removes its validation too,
    // and does not accumulate refresh handlers for controls no longer in the form.
    private Action Rows(string name, Panel parent, Func<JsonArray> read, Action<JsonArray> write, Func<bool> enabled,
        Action<Panel, ChatSettingsFields, ProviderChatConfig, Action, Action> editor,
        Func<JsonObject>? create = null, string addLabel = "add", int min = 0, int max = int.MaxValue)
    {
        var host = Column(parent);
        void Render()
        {
            host.Children.Clear();
            var local = new ChatSettingsFields(); childFields[name] = local;
            var values = read();
            for (var i = 0; i < values.Count; i++)
            {
                var index = i;
                if (values[i] is not JsonObject obj) continue;
                var draft = new ProviderChatConfig(obj);
                var row = Column(host);
                row.Name = "Google_" + name + "_Row_" + index;
                void Save() { values[index] = draft.Root.DeepClone(); write(values); fields.Refresh(); }
                editor(row, local, draft, Save, Render);
                var remove = Button(row, "delete", () => fields.Changed(() =>
                {
                    values.RemoveAt(index); write(values); Render();
                }));
                remove.Name = "Google_" + name + "_Remove_" + index;
                local.Watch(() => remove.IsEnabled = enabled() && values.Count > min);
            }
            if (create is not null)
            {
                var add = Button(host, addLabel, () => fields.Changed(() => { values.Add(create()); write(values); Render(); }));
                add.Name = "Google_" + name + "_Add";
                local.Watch(() => add.IsEnabled = enabled() && values.Count < max);
            }
            local.Refresh();
        }
        Render(); return Render;
    }
    private static void RowText(Panel parent, ChatSettingsFields fields, ProviderChatConfig draft, string label,
        string path, Action save, Func<bool> enabled, bool multiline = false)
        => fields.Text(parent, label, () => draft.String(path), value => { draft.Optional(path, value); save(); }, enabled, multiline);
    private static void RowNumber(Panel parent, ChatSettingsFields fields, ProviderChatConfig draft, string label,
        string path, Action save, Func<bool> enabled)
        => fields.Number(parent, label, () => draft.NumberText(path), value =>
        { draft.Set(path, value is null ? null : JsonValue.Create(value.Value), value is null); save(); }, enabled, whole: true);
    private static void RowSelect(Panel parent, ChatSettingsFields fields, ProviderChatConfig draft, string label,
        string path, string[] choices, Action save, Func<bool> enabled, bool inherit = false, Func<string, string>? translate = null)
        => fields.Select(parent, label, () => draft.String(path), choices, value =>
        { draft.Set(path, JsonValue.Create(value), inherit && value.Length == 0); save(); }, enabled, inherit, translate);
    private void StringListEditor(Panel parent, string name, string label, string path, Func<bool> enabled)
    {
        var local = new ChatSettingsFields(); childFields[name] = local;
        var host = Column(parent);
        var draft = new TextBox { Name = "Google_" + name + "_Draft", Header = L(label), TextWrapping = TextWrapping.Wrap };
        ControlAppearance.Stock(draft); AutomationProperties.SetName(draft, L(label)); host.Children.Add(draft);
        var entries = Column(host);
        string[] Values() => (model.Get(path) as JsonArray)?.Select(ProviderChatConfig.Text).OfType<string>().ToArray() ?? [];
        void Update(IEnumerable<string> values)
        {
            var list = values.ToArray(); model.Generation(path["generation_config/".Length..], ProviderChatConfig.Strings(list), list.Length == 0);
        }
        Button add = null!;
        add = Button(host, "add", () => Add()); add.Name = "Google_" + name + "_Add";
        void Add()
        {
            if (!add.IsEnabled) return;
            fields.Changed(() => { Update(Values().Append(draft.Text.Trim())); draft.Text = ""; Render(); });
        }
        void Refresh()
        {
            draft.IsEnabled = enabled(); add.IsEnabled = enabled() && draft.Text.Trim().Length > 0 && !Values().Contains(draft.Text.Trim());
        }
        string rendered = "";
        void Render()
        {
            entries.Children.Clear();
            var values = Values();
            rendered = ProviderChatConfig.Strings(values).ToJsonString();
            for (var i = 0; i < values.Length; i++)
            {
                var index = i; var row = Column(entries);
                row.Children.Add(new TextBlock { Text = values[i], TextWrapping = TextWrapping.Wrap });
                var remove = Button(row, "delete", () => fields.Changed(() => { Update(Values().Where((_, n) => n != index)); Render(); }));
                remove.Name = "Google_" + name + "_Remove_" + index; remove.IsEnabled = enabled();
            }
            Refresh();
        }
        local.Watch(() =>
        {
            if (rendered != ProviderChatConfig.Strings(Values()).ToJsonString()) Render();
            Refresh(); foreach (var button in entries.Children.OfType<Panel>().SelectMany(p => p.Children.OfType<Button>())) button.IsEnabled = enabled();
        });
        draft.TextChanged += (_, _) => Refresh();
        draft.KeyDown += (_, args) => { if (args.Key == Windows.System.VirtualKey.Enter) { args.Handled = true; Add(); } };
        Render();
    }
}
