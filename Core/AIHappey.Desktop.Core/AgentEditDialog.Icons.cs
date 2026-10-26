using FluentIcons.Common;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using System.Text.Json.Nodes;

namespace AIHappey.Desktop.Core;

public sealed partial class AgentEditDialog
{
    private StackPanel Icons()
    {
        var panel = Panel();
        NativeSettingsSurface.Card(panel, "AgentIcons", DesktopResources.Get("AgentIcons"), out var body);
        var rows = Panel(); body.Children.Add(rows);
        var source = IconUrl("AgentIconNewUrl", ""); body.Children.Add(source);
        var theme = IconTheme("", _ => { }); theme.Name = "AgentIconNewTheme"; body.Children.Add(theme);
        var add = new Button { Name = "AgentIconAdd", Content = DesktopResources.Get("AgentIconAdd"), IsEnabled = false };
        ControlAppearance.Stock(add); body.Children.Add(add);

        void Render()
        {
            rows.Children.Clear();
            if (draft.Definition["icons"] is not JsonArray icons) return;
            var index = 0;
            foreach (var icon in icons.OfType<JsonObject>())
            {
                var row = Panel();
                var url = IconUrl("AgentIconUrl", DesktopAgent.Text(icon["src"]));
                AutomationProperties.SetAutomationId(url, "AgentIconUrl_" + index++);
                url.TextChanged += (_, _) => icon["src"] = url.Text;
                row.Children.Add(url);
                row.Children.Add(IconTheme(DesktopAgent.Text(icon["theme"]), value => DesktopAgentIcons.SetTheme(icon, value)));
                var remove = new Button { Name = "AgentIconRemove", Content = DesktopIcons.Create(Icon.Delete, 16) };
                ControlAppearance.Stock(remove); ToolbarControls.Label(remove, DesktopResources.Get("AgentIconRemove"));
                remove.Click += (_, _) =>
                {
                    icons.Remove(icon); if (icons.Count == 0) draft.Definition.Remove("icons"); Render();
                };
                row.Children.Add(remove); rows.Children.Add(row);
            }
        }
        void Add()
        {
            var value = (theme.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
            if (!DesktopAgentIcons.Add(draft, source.Text, value)) return;
            source.Text = ""; theme.SelectedIndex = 0; Render(); source.Focus(FocusState.Programmatic);
        }
        source.TextChanged += (_, _) => add.IsEnabled = !string.IsNullOrWhiteSpace(source.Text);
        source.KeyDown += (_, args) => { if (args.Key == Windows.System.VirtualKey.Enter) { args.Handled = true; Add(); } };
        add.Click += (_, _) => Add(); Render(); return panel;
    }

    private static TextBox IconUrl(string name, string value)
    {
        var input = new TextBox { Name = name, Header = DesktopResources.Get("AgentIconUrl"), Text = value,
            HorizontalAlignment = HorizontalAlignment.Stretch };
        ControlAppearance.Stock(input); AutomationProperties.SetName(input, DesktopResources.Get("AgentIconUrl")); return input;
    }

    private static ComboBox IconTheme(string value, Action<string> change)
    {
        var select = new ComboBox { Name = "AgentIconTheme", Header = DesktopResources.Get("AgentIconTheme"),
            HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var (key, label) in new[] { ("", "AgentIconThemeAny"), ("light", "AgentIconThemeLight"), ("dark", "AgentIconThemeDark") })
            select.Items.Add(new ComboBoxItem { Tag = key, Content = DesktopResources.Get(label) });
        // Retain unknown themes on imported documents until explicitly changed.
        if (value.Length > 0 && value is not "light" and not "dark") select.Items.Add(new ComboBoxItem { Tag = value, Content = value });
        select.SelectedItem = select.Items.OfType<ComboBoxItem>().First(item => item.Tag as string == value);
        select.SelectionChanged += (_, _) => { if (select.SelectedItem is ComboBoxItem { Tag: string theme }) change(theme); };
        ControlAppearance.Stock(select); AutomationProperties.SetName(select, DesktopResources.Get("AgentIconTheme")); return select;
    }
}
