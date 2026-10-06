using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Markup;
using System.Runtime.CompilerServices;
using Windows.UI.ViewManagement;
using Windows.UI;

namespace AIHappey.Desktop.Core;

/// <summary>
/// App-owned, typed resource overrides. Never parse detached ThemeResource/StaticResource snippets
/// or index Application.Current.Resources for optional styling: those lookups can fail before load.
/// Native control templates still supply layout, keyboard input, focus visuals, and automation.
/// </summary>
internal static class ControlAppearance
{
    private static readonly ConditionalWeakTable<Control, object> nativeControls = new();
    public static ControlPalette Palette(FrameworkElement control) => new AccessibilitySettings().HighContrast
        ? ControlPalette.HighContrast(new UISettings()) : control.ActualTheme == ElementTheme.Dark ? ControlPalette.Dark : ControlPalette.Light;

    public static void Apply(FrameworkElement control, Action<ResourceDictionary, ControlPalette> populate, Action<ControlPalette> setBaseValues)
    {
        var dark = new ResourceDictionary();
        var light = new ResourceDictionary();
        var contrast = new ResourceDictionary();
        var settings = new UISettings();
        var accessibility = new AccessibilitySettings();
        populate(dark, ControlPalette.Dark);
        populate(light, ControlPalette.Light);
        populate(contrast, ControlPalette.HighContrast(settings));
        control.Resources.ThemeDictionaries["Default"] = dark;
        control.Resources.ThemeDictionaries["Light"] = light;
        control.Resources.ThemeDictionaries["HighContrast"] = contrast;

        // Style setter values may be resolved before a control joins the tree; set idle values
        // directly as well. Template state resources above still govern hover/pressed/checked.
        void UpdateBaseValues()
        {
            var palette = accessibility.HighContrast ? ControlPalette.HighContrast(settings)
                : control.ActualTheme == ElementTheme.Dark ? ControlPalette.Dark : ControlPalette.Light;
            setBaseValues(palette);
            if (control is Control native && native.IsLoaded) TemplateColors(native, palette);
        }
        void Refresh() { populate(contrast, ControlPalette.HighContrast(settings)); UpdateBaseValues(); }
        void ColorsChanged(UISettings sender, object args) => control.DispatcherQueue.TryEnqueue(Refresh);
        // AccessibilitySettings.HighContrastChanged registration fails with ERROR_NOT_FOUND
        // in this unpackaged host. UISettings color notifications plus ActualThemeChanged suffice.
        control.ActualThemeChanged += (_, _) => Refresh();
        control.Loaded += (_, _) => { Refresh(); settings.ColorValuesChanged += ColorsChanged; };
        control.Unloaded += (_, _) => settings.ColorValuesChanged -= ColorsChanged;
        UpdateBaseValues();
    }

    public static void Brush(ResourceDictionary resources, string key, Color color)
    {
        // Mutating existing brushes also updates an already-materialized native template.
        if (resources.TryGetValue(key, out var value) && value is SolidColorBrush brush) brush.Color = color;
        else resources[key] = new SolidColorBrush(color);
    }

    public static void NativeResources(ResourceDictionary resources, ControlPalette palette)
    {
        foreach (var prefix in new[] { "Button", "ToggleButton", "ComboBox", "TextControl", "MenuFlyoutItem", "ToolTip" })
            foreach (var state in new[] { "", "PointerOver", "Pressed", "Focused", "Disabled" })
            {
                Brush(resources, prefix + "Background" + state, state == "PointerOver" ? palette.Hover : state == "Pressed" ? palette.Pressed : prefix == "TextControl" ? palette.Surface : palette.Selected);
                Brush(resources, prefix + "Foreground" + state, state == "Disabled" ? palette.Disabled : palette.Text);
                Brush(resources, prefix + "BorderBrush" + state, palette.Stroke);
            }
        foreach (var state in new[] { "", "PointerOver", "Focused", "Disabled" })
            Brush(resources, "TextControlPlaceholderForeground" + state, palette.Disabled);
        Brush(resources, "MenuFlyoutPresenterBackground", palette.Panel);
        Brush(resources, "MenuFlyoutPresenterBorderBrush", palette.Stroke);
        Brush(resources, "ContentDialogBackground", palette.Surface);
        Brush(resources, "ContentDialogForeground", palette.Text);
        foreach (var state in new[] { "", "PointerOver", "Pressed", "Selected", "SelectedPointerOver", "SelectedPressed", "Disabled" })
        {
            Brush(resources, "ListViewItemBackground" + state, state.Contains("Selected") ? palette.Selected : state.Contains("PointerOver") ? palette.Hover : palette.Background);
            Brush(resources, "ListViewItemForeground" + state, state == "Disabled" ? palette.Disabled : palette.Text);
        }
        foreach (var state in new[] { "", "PointerOver", "Pressed", "Disabled" })
        {
            Brush(resources, "TextControlButtonForeground" + state, state == "Disabled" ? palette.Disabled : palette.Text);
            Brush(resources, "TextControlButtonBackground" + state, state == "PointerOver" ? palette.Hover : state == "Pressed" ? palette.Pressed : palette.Surface);
            Brush(resources, "TextControlButtonBorderBrush" + state, palette.Background);
        }
        Brush(resources, "AutoSuggestBoxSuggestionsListBackground", palette.Panel);
        Brush(resources, "AutoSuggestBoxSuggestionsListBorderBrush", palette.Stroke);
    }

    public static void Native(Control control)
    {
        // Item containers and popup/template parts can be recycled or loaded repeatedly.
        if (nativeControls.TryGetValue(control, out _)) return;
        nativeControls.Add(control, new object());
        Apply(control, NativeResources, palette =>
        {
            control.Background = new SolidColorBrush(control is TextBox or PasswordBox ? palette.Surface : control is ListView ? palette.Panel : control is MenuFlyoutItem or ListViewItem ? palette.Background : palette.Selected);
            control.Foreground = new SolidColorBrush(palette.Text);
            control.BorderBrush = new SolidColorBrush(control is MenuFlyoutItem or ListView or ListViewItem ? palette.Background : palette.Stroke);
            if (control is MenuFlyoutItem or ListView or ListViewItem) control.BorderThickness = new Thickness(0);
        });
    }

    public static IEnumerable<DependencyObject> Descendants(DependencyObject element)
    {
        yield return element;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(element, index))) yield return child;
    }

    // Some stock templates resolve visual-state brushes outside the control's local resource
    // scope. Give their actual rendered state animations concrete colors too, not just the DPs.
    private static void TemplateColors(Control control, ControlPalette palette)
    {
        foreach (var element in TemplateElements(control))
        {
            if (element is IconElement icon) icon.Foreground = new SolidColorBrush(palette.Text);
            if (element is TextBlock label) label.Foreground = new SolidColorBrush(palette.Text);
            foreach (var group in VisualStateManager.GetVisualStateGroups(element))
                foreach (var state in group.States)
                {
                    var fill = control is TextBox or PasswordBox or AutoSuggestBox ? palette.Surface
                        : state.Name.Contains("Pressed") ? palette.Pressed
                        : state.Name.Contains("PointerOver") ? palette.Hover
                        : state.Name.Contains("Checked") || state.Name.Contains("Selected") ? palette.Selected
                        : control.Background is SolidColorBrush background ? background.Color : palette.Background;
                    Color? StateColor(string property) => property.Contains("Foreground") ? state.Name.Contains("Disabled") ? palette.Disabled : palette.Text
                        : property.Contains("BorderBrush") ? control is MenuFlyoutItem or ListView or ListViewItem ? palette.Background : palette.Stroke
                        : property.Contains("Background") ? fill : null;
                    // Modern SDK templates use setters, older ones use storyboards; cover both.
                    foreach (var setter in state.Setters.OfType<Setter>())
                    {
                        var property = setter.Target?.Path?.Path ?? setter.Property?.ToString() ?? "";
                        if (StateColor(property) is { } color) setter.Value = new SolidColorBrush(color);
                    }
                    if (state.Storyboard is null) continue;
                    foreach (var animation in state.Storyboard.Children.OfType<ObjectAnimationUsingKeyFrames>())
                    {
                        var property = Storyboard.GetTargetProperty(animation);
                        Color? color = StateColor(property);
                        if (color is not { } value) continue;
                        foreach (var frame in animation.KeyFrames.OfType<DiscreteObjectKeyFrame>()) frame.Value = new SolidColorBrush(value);
                    }
                }
            if (element is ContentPresenter presenter && presenter.Name == "ContentPresenter")
            {
                presenter.Foreground = control.Foreground;
                presenter.Background = control.Background;
                presenter.BorderBrush = control.BorderBrush;
                presenter.BorderThickness = control.BorderThickness;
            }
            if (element is Border border && border.Name == "BorderElement" && control is TextBox or PasswordBox or AutoSuggestBox)
            {
                border.Background = new SolidColorBrush(palette.Surface);
                border.BorderBrush = new SolidColorBrush(palette.Stroke);
            }
            if (element is Control part && (part.Name == "QueryButton" || part.Name == "DeleteButton"))
            {
                part.Background = new SolidColorBrush(palette.Surface);
                part.Foreground = new SolidColorBrush(palette.Text);
                part.BorderThickness = new Thickness(0);
                if (part.IsLoaded) TemplateColors(part, palette);
            }
            if (element is ListViewItemPresenter item)
            {
                item.Background = new SolidColorBrush(palette.Background);
                item.BorderThickness = new Thickness(0);
                item.BorderBrush = new SolidColorBrush(palette.Background);
                item.PointerOverBackground = item.SelectedPointerOverBackground = new SolidColorBrush(palette.Hover);
                item.SelectedBackground = new SolidColorBrush(palette.Selected);
                item.PressedBackground = item.SelectedPressedBackground = new SolidColorBrush(palette.Pressed);
                item.PointerOverForeground = item.SelectedForeground = new SolidColorBrush(palette.Text);
            }
        }
    }

    private static IEnumerable<FrameworkElement> TemplateElements(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is FrameworkElement element) yield return element;
            // WinUI exposes no public TemplatedParent: stop at nested native controls instead.
            if (child is not Control)
                foreach (var nestedElement in TemplateElements(child)) yield return nestedElement;
        }
    }

    public static void Refresh(Control control) => TemplateColors(control, Palette(control));

    public static void BorderlessItems(ListView list)
    {
        // Keep the native presenter/selection engine, but do not inherit the default container's
        // border visual states. TemplateBinding only: no optional SDK resource keys are resolved.
        var style = new Style(typeof(ListViewItem));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(12, 10, 12, 10)));
        style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        style.Setters.Add(new Setter(Control.UseSystemFocusVisualsProperty, true));
        style.Setters.Add(new Setter(Control.TemplateProperty, (ControlTemplate)XamlReader.Load("""
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="ListViewItem">
                <ListViewItemPresenter x:Name="Root"
                    Content="{TemplateBinding Content}" ContentTemplate="{TemplateBinding ContentTemplate}"
                    Background="{TemplateBinding Background}" Foreground="{TemplateBinding Foreground}"
                    HorizontalContentAlignment="{TemplateBinding HorizontalContentAlignment}"
                    VerticalContentAlignment="{TemplateBinding VerticalContentAlignment}"
                    ContentMargin="{TemplateBinding Padding}" CornerRadius="6" BorderThickness="0"
                    SelectionCheckMarkVisualEnabled="False" />
            </ControlTemplate>
            """)));
        list.ItemContainerStyle = style;
        list.ContainerContentChanging += (_, args) =>
        {
            if (args.InRecycleQueue) return;
            var container = args.ItemContainer;
            Native(container);
            container.DispatcherQueue.TryEnqueue(() => { if (container.IsLoaded) Refresh(container); });
        };
        list.RegisterPropertyChangedCallback(Control.IsEnabledProperty, (_, _) => list.DispatcherQueue.TryEnqueue(() => RefreshItems(list)));
    }

    public static void RefreshItems(ListView list)
    {
        for (var index = 0; index < list.Items.Count; index++)
        {
            if (list.ContainerFromIndex(index) is not ListViewItem item) continue;
            Native(item);
            item.BorderThickness = new Thickness(0);
            Refresh(item);
        }
    }

    public static void Separator(Border section)
    {
        Apply(section, (_, _) => { }, palette => section.BorderBrush = new SolidColorBrush(palette.Stroke));
    }

    public static void TokenBadge(Border badge)
    {
        Apply(badge, (_, _) => { }, palette => badge.Background = new SolidColorBrush(palette.Selected));
    }

    public static void MessageCard(Border border, bool user)
    {
        Apply(border, (_, _) => { }, palette =>
        {
            border.Background = new SolidColorBrush(user ? palette.Selected : palette.Surface);
            border.BorderBrush = new SolidColorBrush(palette.Stroke);
        });
    }
}

internal readonly record struct ControlPalette(Color Background, Color Selected, Color Hover, Color Pressed, Color Stroke, Color Text, Color Disabled, bool Contrast = false)
{
    private static Color Rgb(byte red, byte green, byte blue) => Color.FromArgb(255, red, green, blue);
    private static Color Transparent => Color.FromArgb(0, 0, 0, 0);
    public static ControlPalette Dark => new(Transparent, Rgb(38, 38, 38), Rgb(48, 48, 48), Rgb(32, 32, 32), Rgb(70, 70, 70), Rgb(243, 243, 243), Rgb(153, 153, 153));
    public static ControlPalette Light => new(Transparent, Rgb(235, 235, 235), Rgb(225, 225, 225), Rgb(245, 245, 245), Rgb(190, 190, 190), Rgb(26, 26, 26), Rgb(110, 110, 110));
    public Color Surface => Contrast ? Background : Text.R > 128 ? Rgb(10, 10, 10) : Rgb(255, 255, 255);
    public Color Panel => Contrast ? Background : Text.R > 128 ? Rgb(18, 18, 18) : Rgb(247, 247, 247);

    public static ControlPalette HighContrast(UISettings settings)
    {
        var background = settings.UIElementColor(UIElementType.ButtonFace);
        var foreground = settings.UIElementColor(UIElementType.ButtonText);
        // Foreground-backed outlines distinguish checked states without assuming an accent palette.
        return new(background, background, background, background, foreground, foreground, settings.UIElementColor(UIElementType.GrayText), true);
    }
}
