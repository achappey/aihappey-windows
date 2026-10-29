using FluentIcons.Common;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using System.Text.Json.Nodes;

namespace AIHappey.Desktop.Core;

public sealed partial class AgentEditDialog
{
    private StackPanel Details()
    {
        var panel = Panel();
        fields.Text(panel, "agent.argumentHint", () => DesktopAgent.Text(draft.Definition["argumentHint"]), v => draft.Definition["argumentHint"] = v).Name = "AgentArgumentHint";
        var owners = availableAgents.Select(agent => agent.OwnedBy?.Trim()).Append(DesktopAgent.Text(draft.Definition["owned_by"]).Trim())
            .Where(owner => !string.IsNullOrEmpty(owner)).Cast<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.CurrentCultureIgnoreCase).ToArray();
        var owner = new AutoSuggestBox { Name = "AgentOwnedBy", Header = DesktopResources.Get("AgentOwnedBy"), Text = DesktopAgent.Text(draft.Definition["owned_by"]),
            QueryIcon = DesktopIcons.Create(Icon.ChevronDown, 12), HorizontalAlignment = HorizontalAlignment.Stretch };
        ControlAppearance.Stock(owner); AutomationProperties.SetName(owner, DesktopResources.Get("AgentOwnedBy")); panel.Children.Add(owner);
        void OwnerSuggestions(string query = "") => owner.ItemsSource = owners.Where(value => value.Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToArray();
        owner.GotFocus += (_, _) => { OwnerSuggestions(); owner.IsSuggestionListOpen = true; };
        owner.TextChanged += (_, args) =>
        {
            if (owner.Text.Length == 0) draft.Definition.Remove("owned_by"); else draft.Definition["owned_by"] = owner.Text;
            if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput) { OwnerSuggestions(owner.Text); owner.IsSuggestionListOpen = true; }
        };
        owner.SuggestionChosen += (_, args) => { if (args.SelectedItem is string value) owner.Text = value; };
        owner.QuerySubmitted += (_, args) =>
        {
            if (args.ChosenSuggestion is string value) owner.Text = value;
            else { OwnerSuggestions(); owner.IsSuggestionListOpen = true; }
        };
        owner.LostFocus += (_, _) => { owner.IsSuggestionListOpen = false; owner.Text = owner.Text.Trim(); };
        NativeSettingsSurface.Expander(panel, "AgentTags", DesktopResources.Get("AgentTags"), null, out var tags);
        var input = new AutoSuggestBox { Name = "AgentTagInput", Header = DesktopResources.Get("AgentTags"),
            QueryIcon = DesktopIcons.Create(Icon.ChevronDown, 12), HorizontalAlignment = HorizontalAlignment.Stretch };
        ControlAppearance.Stock(input); AutomationProperties.SetName(input, DesktopResources.Get("AgentTags"));
        tags.Children.Add(input);
        var add = new Button { Name = "AgentTagAdd", Content = DesktopResources.Get("Add"), IsEnabled = false };
        tags.Children.Add(add);
        var selected = Panel(); tags.Children.Add(selected);
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
        Render(); panel.Children.Add(Icons()); return panel;
    }
}
