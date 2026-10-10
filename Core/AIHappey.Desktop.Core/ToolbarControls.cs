using FluentIcons.Common;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace AIHappey.Desktop.Core;

/// <summary>Small visual helpers; all interaction, focus, and accessibility use native control templates.</summary>
internal static class ToolbarControls
{
    public static void Label(DependencyObject control, string label)
    {
        AutomationProperties.SetName(control, label);
        var tooltip = new ToolTip { Content = label };
        ControlAppearance.Native(tooltip);
        tooltip.Opened += (_, _) => { if (control is FrameworkElement owner) tooltip.RequestedTheme = owner.ActualTheme; };
        ToolTipService.SetToolTip(control, tooltip);
    }

    public static ToggleButton CreateModeButton(IconElement icon, string label)
    {
        var button = new ToggleButton
        {
            Content = icon, Width = 40, Height = 40, Padding = new Thickness(0),
            CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1)
        };
        ControlAppearance.Stock(button);
        Label(button, label);
        return button;
    }

    public static void Outline(AutoSuggestBox control)
    {
        ControlAppearance.Stock(control);
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Button, object> selectorButtons = new();
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ListView, object> selectorLists = new();

    private static void SelectorButton(Button button)
    {
        if (selectorButtons.TryGetValue(button, out _)) return;
        selectorButtons.Add(button, new object());
        button.BorderThickness = new Thickness(0);
        ControlAppearance.Apply(button, (resources, palette) => ButtonStates(resources, "Button", palette), palette =>
        {
            button.Background = new SolidColorBrush(palette.Surface);
            button.Foreground = new SolidColorBrush(palette.Text);
            button.BorderBrush = new SolidColorBrush(palette.Background);
        });
    }

    private static void SelectorItems(ListView list)
    {
        if (selectorLists.TryGetValue(list, out _)) return;
        selectorLists.Add(list, new object());
        ControlAppearance.BorderlessItems(list);
        list.Loaded += (_, _) => ControlAppearance.RefreshItems(list);
    }

    public static Button CopyButton()
    {
        var button = new Button
        {
            Content = DesktopIcons.Create(Icon.Copy), Width = 32, Height = 32,
            Padding = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Left
        };
        ControlAppearance.Apply(button, (resources, palette) => ButtonStates(resources, "Button", palette), palette => BaseValues(button, palette));
        Label(button, DesktopResources.Get("CopyMessage"));
        return button;
    }

    public static void Subtle(Button button)
    {
        button.BorderThickness = new Thickness(0);
        ControlAppearance.Apply(button, (resources, palette) => ButtonStates(resources, "Button", palette), palette => BaseValues(button, palette));
    }

    private static void BaseValues(Control control, ControlPalette palette)
    {
        control.Background = new SolidColorBrush(palette.Background);
        control.BorderBrush = new SolidColorBrush(palette.Background);
        control.Foreground = new SolidColorBrush(palette.Text);
    }

    private static void ButtonStates(ResourceDictionary resources, string prefix, ControlPalette palette)
    {
        State(resources, prefix, "", palette.Background, palette.Background, palette.Text);
        State(resources, prefix, "PointerOver", palette.Hover, palette.Background, palette.Text);
        State(resources, prefix, "Pressed", palette.Pressed, palette.Background, palette.Text);
        State(resources, prefix, "Disabled", palette.Background, palette.Background, palette.Disabled);
    }

    private static void State(ResourceDictionary resources, string prefix, string state, Color background, Color border, Color foreground)
    {
        ControlAppearance.Brush(resources, prefix + "Background" + state, background);
        ControlAppearance.Brush(resources, prefix + "BorderBrush" + state, border);
        ControlAppearance.Brush(resources, prefix + "Foreground" + state, foreground);
    }

}
