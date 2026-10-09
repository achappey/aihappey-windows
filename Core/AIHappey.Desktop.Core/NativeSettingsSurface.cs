using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AIHappey.Desktop.Core;

/// <summary>Stock toolkit surfaces; the toolkit owns theme brushes, borders and focus visuals.</summary>
internal static class NativeSettingsSurface
{
    public static SettingsCard Card(Panel parent, string name, string title, out StackPanel body)
    {
        body = new StackPanel { Spacing = 12 };
        var card = new SettingsCard
        {
            Name = name, Header = Title(title), Content = body,
            ContentAlignment = ContentAlignment.Vertical,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        ControlAppearance.Stock(card); AutomationProperties.SetName(card, title);
        parent.Children.Add(card); return card;
    }

    public static SettingsCard ToggleCard(Panel parent, string name, string title, UIElement toggle)
    {
        var card = new SettingsCard { Name = name, Header = Title(title), Content = toggle };
        ControlAppearance.Stock(card); AutomationProperties.SetName(card, title);
        parent.Children.Add(card); return card;
    }

    public static SettingsExpander Expander(Panel parent, string name, string title, UIElement? headerContent, out StackPanel body)
    {
        body = new StackPanel { Spacing = 12, Padding = new Thickness(16) };
        var expander = new SettingsExpander
        {
            Name = name, Header = Title(title), Content = headerContent, ItemsHeader = body,
            IsExpanded = false, HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        ControlAppearance.Stock(expander); AutomationProperties.SetName(expander, title);
        parent.Children.Add(expander); return expander;
    }

    private static TextBlock Title(string title) => new()
    {
        Text = title, TextWrapping = TextWrapping.Wrap,
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
    };

    // Collapsed expander content may not have a visual parent yet. Inspect our field panels,
    // not the toolkit template, to find the section owning a validation error.
    public static bool Contains(Panel panel, Control control) => panel.Children.Any(child =>
        ReferenceEquals(child, control) || child is Panel nested && Contains(nested, control));
}
