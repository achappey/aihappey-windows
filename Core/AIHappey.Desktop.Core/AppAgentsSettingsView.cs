using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AIHappey.Desktop.Core;

/// <summary>All five browser assignments are editable; only welcome/naming are executed.</summary>
public sealed class AppAgentsSettingsView : StackPanel
{
    private readonly AppAgentPreferences draft;
    private readonly Dictionary<AppAgentRole, ComboBox> pickers = [];
    private readonly Dictionary<AppAgentRole, TextBlock> unavailable = [];
    private bool hydrating;
    public AppAgentsSettingsView(AppAgentPreferences draft, IReadOnlyList<DesktopAgent>? agents = null)
    {
        this.draft = draft;
        Name = "AppAgentsSettingsView"; Spacing = 16;
        foreach (var role in Enum.GetValues<AppAgentRole>())
        {
            var label = DesktopResources.Get("AppAgent" + role);
            NativeSettingsSurface.Card(this, "AppAgentCard_" + role, label, out var body);
            var hint = DesktopResources.Get("AppAgent" + role + "Hint");
            body.Children.Add(new TextBlock { Text = hint, TextWrapping = TextWrapping.Wrap });
            var picker = new ComboBox { Name = "AppAgent_" + role, HorizontalAlignment = HorizontalAlignment.Stretch,
                DisplayMemberPath = nameof(AgentChoice.Label) };
            ControlAppearance.Stock(picker); AutomationProperties.SetName(picker, label); AutomationProperties.SetHelpText(picker, hint);
            pickers[role] = picker; body.Children.Add(picker);
            var warning = new TextBlock { Text = DesktopResources.Get("AppAgentUnavailable"), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
            unavailable[role] = warning; body.Children.Add(warning);
            if (role is not AppAgentRole.WelcomeMessage and not AppAgentRole.ConversationName)
                body.Children.Add(new TextBlock { Text = DesktopResources.Get("AppAgentNotActive"), TextWrapping = TextWrapping.Wrap });
            picker.SelectionChanged += (_, _) =>
            {
                if (hydrating || picker.SelectedItem is not AgentChoice choice) return;
                draft.Set(role, choice.Name);
                warning.Visibility = choice.Missing ? Visibility.Visible : Visibility.Collapsed;
            };
        }
        SetAgents(agents ?? []);
    }
    public void SetAgents(IReadOnlyList<DesktopAgent> agents)
    {
        var names = agents.Select(a => a.Name).Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase).ToArray();
        hydrating = true;
        try
        {
            foreach (var (role, picker) in pickers)
            {
                var selected = draft.Get(role);
                var choices = new List<AgentChoice> { new("", DesktopResources.Get("AppAgentNone")) };
                if (selected.Length > 0 && !names.Contains(selected, StringComparer.Ordinal)) choices.Add(new(selected, selected, true));
                choices.AddRange(names.Select(n => new AgentChoice(n, n)));
                picker.ItemsSource = choices; picker.SelectedItem = choices.Single(c => c.Name == selected);
                unavailable[role].Visibility = ((AgentChoice)picker.SelectedItem).Missing ? Visibility.Visible : Visibility.Collapsed;
            }
        }
        finally { hydrating = false; }
    }
    public sealed record AgentChoice(string Name, string Label, bool Missing = false);
}
