using FluentIcons.Common;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using System.Text.Json.Nodes;

namespace AIHappey.Desktop.Core;

public sealed partial class AgentEditDialog
{
    private StackPanel Tags()
    {
        var panel = Panel();
        var input = new AutoSuggestBox { Name = "AgentTagInput", Header = DesktopResources.Get("AgentTags"),
            QueryIcon = DesktopIcons.Create(Icon.ChevronDown, 12), HorizontalAlignment = HorizontalAlignment.Stretch };
        ControlAppearance.Stock(input); AutomationProperties.SetName(input, DesktopResources.Get("AgentTags"));
        panel.Children.Add(input);
        var add = new Button { Name = "AgentTagAdd", Content = DesktopResources.Get("Add"), IsEnabled = false };
        panel.Children.Add(add);
        var selected = Panel(); panel.Children.Add(selected);
        var options = DesktopAgentMetadata.NormalizeTags(availableAgents.SelectMany(agent => agent.Tags).Concat(Current()))
            .Order(StringComparer.CurrentCultureIgnoreCase).ToArray();

        IReadOnlyList<string> Current() => DesktopAgentMetadata.NormalizeTags((draft.Definition["tags"] as JsonArray)?.Select(DesktopAgent.Text) ?? []);
        void Write(IEnumerable<string> values) => draft.Definition["tags"] = new JsonArray(values.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
        void Suggestions(string query = "") => input.ItemsSource = options.Where(tag => !Current().Contains(tag)
            && tag.Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToArray();
        void Render()
        {
            selected.Children.Clear();
            foreach (var tag in Current())
            {
                var row = new Grid { ColumnSpacing = 8 };
                row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
                row.Children.Add(new TextBlock { Text = tag, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
                var remove = new Button { Name = "AgentTagRemove", Tag = tag, Content = DesktopIcons.Create(Icon.Dismiss, 16) };
                AutomationProperties.SetName(remove, DesktopResources.Get("Remove") + " " + tag);
                remove.Click += (_, _) => { Write(Current().Where(value => value != tag)); Render(); Suggestions(input.Text); };
                Grid.SetColumn(remove, 1); row.Children.Add(remove); selected.Children.Add(row);
            }
        }
        void Add(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            Write(DesktopAgentMetadata.NormalizeTags(Current().Append(value))); input.Text = ""; input.IsSuggestionListOpen = false; Render();
        }
        input.GotFocus += (_, _) => { Suggestions(); input.IsSuggestionListOpen = true; };
        input.TextChanged += (_, args) =>
        {
            add.IsEnabled = !string.IsNullOrWhiteSpace(input.Text);
            if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput) { Suggestions(input.Text); input.IsSuggestionListOpen = true; }
        };
        input.SuggestionChosen += (_, args) => { if (args.SelectedItem is string value) Add(value); };
        input.QuerySubmitted += (_, args) => Add(args.ChosenSuggestion as string ?? args.QueryText);
        input.LostFocus += (_, _) => input.IsSuggestionListOpen = false;
        add.Click += (_, _) => Add(input.Text);
        Render(); return panel;
    }
}
