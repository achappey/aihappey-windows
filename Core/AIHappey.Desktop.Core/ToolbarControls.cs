using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Markup;
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
        ControlAppearance.Apply(button, (resources, palette) =>
        {
            ButtonStates(resources, "ToggleButton", palette);
            State(resources, "ToggleButton", "Checked", palette.Selected, palette.Stroke, palette.Text);
            State(resources, "ToggleButton", "CheckedPointerOver", palette.Hover, palette.Stroke, palette.Text);
            State(resources, "ToggleButton", "CheckedPressed", palette.Pressed, palette.Stroke, palette.Text);
            State(resources, "ToggleButton", "CheckedDisabled", palette.Background, palette.Disabled, palette.Disabled);
        }, palette => BaseValues(button, palette));
        Label(button, label);
        return button;
    }

    public static void Outline(AutoSuggestBox control)
    {
        ControlAppearance.Apply(control, (resources, palette) =>
        {
            ControlAppearance.NativeResources(resources, palette);
            ControlAppearance.Brush(resources, "TextControlBorderBrush", palette.Stroke);
            ControlAppearance.Brush(resources, "TextControlBorderBrushPointerOver", palette.Disabled);
            ControlAppearance.Brush(resources, "TextControlBackground", palette.Background);
            ControlAppearance.Brush(resources, "TextControlBackgroundPointerOver", palette.Background);
        }, palette =>
        {
            control.Background = new SolidColorBrush(palette.Surface);
            control.BorderBrush = new SolidColorBrush(palette.Stroke);
            control.Foreground = new SolidColorBrush(palette.Text);
        });
        void RefreshSelector()
        {
            if (!control.IsLoaded) return;
            var palette = ControlAppearance.Palette(control);
            var text = ControlAppearance.Descendants(control).OfType<TextBox>().FirstOrDefault();
            if (text is not null)
            {
                text.RequestedTheme = control.ActualTheme;
                ControlAppearance.Native(text); ControlAppearance.Refresh(text);
            }
            // These controls load inside nested templates, after the outer selector's Loaded.
            // Give them their own resource/state overrides, not only outer foreground values.
            foreach (var button in ControlAppearance.Descendants(control).OfType<Button>().Where(button => button.Name is "QueryButton" or "DeleteButton"))
            {
                button.RequestedTheme = control.ActualTheme;
                SelectorButton(button);
                ControlAppearance.Refresh(button);
            }
            if (!control.IsSuggestionListOpen || control.XamlRoot is null) return;
            foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(control.XamlRoot))
            {
                var list = ControlAppearance.Descendants(popup.Child).OfType<ListView>().FirstOrDefault(part => part.Name == "SuggestionsList");
                if (list is null) continue; // Never recolor unrelated dialogs or menus.
                if (popup.Child is FrameworkElement root) root.RequestedTheme = control.ActualTheme;
                list.RequestedTheme = control.ActualTheme;
                ControlAppearance.Native(list);
                SelectorItems(list);
                ControlAppearance.Refresh(list); ControlAppearance.RefreshItems(list);
                // Popup surfaces do not inherit the selector's resource scope consistently.
                foreach (var border in ControlAppearance.Descendants(popup.Child).OfType<Border>().Where(border => !ControlAppearance.Descendants(list).Contains(border)))
                {
                    border.Background = new SolidColorBrush(palette.Panel);
                    border.BorderBrush = new SolidColorBrush(palette.Stroke);
                }
            }
        }
        void QueueRefresh()
        {
            RefreshSelector();
            control.DispatcherQueue.TryEnqueue(() =>
            {
                if (control.IsLoaded) RefreshSelector();
            });
        }
        control.Loaded += (_, _) => QueueRefresh();
        control.ActualThemeChanged += (_, _) => QueueRefresh();
        control.TextChanged += (_, _) => QueueRefresh();
        control.RegisterPropertyChangedCallback(AutoSuggestBox.IsSuggestionListOpenProperty, (_, _) => QueueRefresh());
        control.RegisterPropertyChangedCallback(Control.IsEnabledProperty, (_, _) => QueueRefresh());
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
            Content = new SymbolIcon(Symbol.Copy), Width = 32, Height = 32,
            Padding = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Left
        };
        ControlAppearance.Apply(button, (resources, palette) => ButtonStates(resources, "Button", palette), palette => BaseValues(button, palette));
        Label(button, "Copy message");
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
        if (control is ContentControl { Content: IconElement icon }) icon.Foreground = control.Foreground;
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

    // Microsoft Fluent UI System Icons (MIT); see THIRD-PARTY-NOTICES.txt.
    public static PathIcon BrainIcon() => Icon("M5.70049 3.94804C5.92703 2.81534 6.92158 2 8.07672 2C8.86031 2 9.55704 2.37193 10 2.94888C10.443 2.37193 11.1397 2 11.9233 2C13.0784 2 14.073 2.81534 14.2995 3.94804L14.4249 4.57508L14.8311 4.65632C16.0922 4.90854 17 6.01586 17 7.30196V7.5C17 8.22648 16.6901 8.88058 16.1954 9.33735C17.2649 9.86867 18 10.9723 18 12.2475C18 13.7835 16.9244 15.1075 15.425 15.4247L15.3879 15.6102C15.1099 16.9998 13.8899 18 12.4728 18C11.4417 18 10.5332 17.4751 10 16.6779C9.46679 17.4751 8.5583 18 7.52721 18C6.11013 18 4.89005 16.9998 4.61214 15.6102L4.57504 15.4247C3.07561 15.1075 2 13.7835 2 12.2475C2 10.9723 2.73508 9.86867 3.80465 9.33735C3.30987 8.88058 3 8.22648 3 7.5V7.30196C3 6.01586 3.90778 4.90854 5.16891 4.65632L5.57508 4.57508L5.70049 3.94804ZM14.5 10C14.2239 10 14 9.77614 14 9.5C14 9.22386 14.2239 9 14.5 9C15.3284 9 16 8.32843 16 7.5V7.30196C16 6.49254 15.4287 5.79564 14.635 5.6369L13.9019 5.49029C13.704 5.4507 13.5493 5.29599 13.5097 5.09806L13.3189 4.14416C13.1859 3.47888 12.6017 3 11.9233 3C11.1372 3 10.5 3.63723 10.5 4.42328V15.0272C10.5 16.1167 11.3833 17 12.4728 17C13.4132 17 14.2229 16.3362 14.4073 15.4141L14.5097 14.9019C14.5493 14.704 14.704 14.5493 14.9019 14.5097L15.1932 14.4515C16.2438 14.2413 17 13.3189 17 12.2475C17 11.0063 15.9937 10 14.7525 10H14.5ZM9.5 13.9993L9.5 4.42328C9.5 3.63723 8.86277 3 8.07672 3C7.39826 3 6.81413 3.47888 6.68107 4.14416L6.49029 5.09806C6.4507 5.29599 6.29599 5.4507 6.09806 5.49029L5.36503 5.6369C4.57132 5.79564 4 6.49254 4 7.30196V7.5C4 8.32843 4.67157 9 5.5 9C5.77614 9 6 9.22386 6 9.5C6 9.77614 5.77614 10 5.5 10H5.24755C4.00626 10 3 11.0063 3 12.2475C3 13.3189 3.75621 14.2413 4.80677 14.4515L5.09806 14.5097C5.29599 14.5493 5.4507 14.704 5.49029 14.9019L5.59272 15.4141C5.77715 16.3362 6.58681 17 7.52721 17C8.61675 17 9.5 16.1167 9.5 15.0272L9.5 13.9993V13.9993Z");

    public static PathIcon BotIcon() => Icon("M12 5.5C11.4477 5.5 11 5.94772 11 6.5C11 7.05228 11.4477 7.5 12 7.5C12.5523 7.5 13 7.05228 13 6.5C13 5.94772 12.5523 5.5 12 5.5ZM7 6.5C7 5.94772 7.44772 5.5 8 5.5C8.55228 5.5 9 5.94772 9 6.5C9 7.05228 8.55228 7.5 8 7.5C7.44772 7.5 7 7.05228 7 6.5ZM10.5 2.5C10.5 2.22386 10.2761 2 10 2C9.72386 2 9.5 2.22386 9.5 2.5V3H6.5C5.67157 3 5 3.67157 5 4.5V8.5C5 9.32843 5.67157 10 6.5 10H13.5C14.3284 10 15 9.32843 15 8.5V4.5C15 3.67157 14.3284 3 13.5 3H10.5V2.5ZM6.5 4H13.5C13.7761 4 14 4.22386 14 4.5V8.5C14 8.77614 13.7761 9 13.5 9H6.5C6.22386 9 6 8.77614 6 8.5V4.5C6 4.22386 6.22386 4 6.5 4ZM10.25 17.9984C12.8656 17.9649 14.4449 17.4031 15.3718 16.5574C16.247 15.7588 16.4607 14.7813 16.4947 14.0019H16.5V13.3124C16.5 12.3131 15.69 11.5031 14.6907 11.5031H11.5V11.5H8.5V11.5031H5.3093C4.31005 11.5031 3.5 12.3131 3.5 13.3124V14.0019H3.50533C3.53931 14.7813 3.75297 15.7588 4.6282 16.5574C5.55506 17.4031 7.13442 17.9649 9.75 17.9984V18H10.25V17.9984ZM5.3093 12.5031H14.6907C15.1377 12.5031 15.5 12.8654 15.5 13.3124V13.75C15.5 14.4396 15.3688 15.2064 14.6978 15.8187C14.0103 16.446 12.6605 17 10 17C7.33946 17 5.98969 16.446 5.30224 15.8187C4.63123 15.2064 4.5 14.4396 4.5 13.75V13.3124C4.5 12.8654 4.86233 12.5031 5.3093 12.5031Z");

    private static PathIcon Icon(string data) => (PathIcon)XamlReader.Load($"""
        <PathIcon xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" Width="20" Height="20" Data="{data}" />
        """);
}
