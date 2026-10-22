using FluentIcons.Common;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace AIHappey.Desktop.Core;

public sealed partial class ChatShell
{
    private sealed record NavigationLink(string Key, ButtonBase Button, Icon Icon, Func<Task> Activate,
        Grid Row, Button Options, MenuFlyout Menu);

    private sealed class NavigationGroup(string name)
    {
        public string Name { get; } = name;
        public StackPanel Panel { get; } = new() { Name = "NavigationGroup" + name, Spacing = 2 };
        public StackPanel Hidden { get; } = new() { Name = "HiddenNavigation" + name, Spacing = 2 };
        public Button More { get; } = new() { Name = "NavigationMore" + name, HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(6) };
        public MenuFlyout CompactMenu { get; } = new();
        public List<NavigationLink> Links { get; } = [];
        public bool Expanded { get; set; }
    }

    private readonly List<NavigationGroup> navigationGroups = [];

    private NavigationGroup CreateNavigationGroup(string name, StackPanel parent, string? heading = null)
    {
        if (name != "Primary") parent.Children.Add(SidebarSeparator(name + "Separator"));
        if (heading is not null) parent.Children.Add(SidebarHeading(heading));
        var group = new NavigationGroup(name);
        navigationGroups.Add(group); parent.Children.Add(group.Panel);
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        content.Children.Add(DesktopIcons.Create(Icon.MoreHorizontal));
        content.Children.Add(new TextBlock { Text = DesktopResources.Get("NavigationMore"), VerticalAlignment = VerticalAlignment.Center });
        group.More.Content = content;
        ToolbarControls.Subtle(group.More);
        ToolbarControls.Label(group.More, DesktopResources.Get("NavigationMore"));
        group.More.Click += (_, _) =>
        {
            if (!split.IsPaneOpen) { BuildCompactNavigationMenu(group); group.CompactMenu.ShowAt(group.More); }
            else { group.Expanded = !group.Expanded; UpdateNavigationLayout(); }
        };
        PrepareNavigationMenu(group.CompactMenu, group.More);
        return group;
    }

    private void AddNavigationLink(NavigationGroup group, string key, ButtonBase button, Icon icon, Func<Task> activate)
    {
        var row = new Grid { Name = "NavigationRow" + key, ColumnSpacing = 2 };
        row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        row.Children.Add(button);
        var options = new Button { Name = "NavigationOptions" + key, Content = DesktopIcons.Create(Icon.MoreHorizontal, 16),
            Width = 28, Height = 32, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center };
        ToolbarControls.Subtle(options);
        ToolbarControls.Label(options, string.Format(DesktopResources.Get("NavigationOptions"), AutomationProperties.GetName(button)));
        Grid.SetColumn(options, 1); row.Children.Add(options);
        var menu = new MenuFlyout();
        var link = new NavigationLink(key, button, icon, activate, row, options, menu);
        group.Links.Add(link);
        menu.Opening += (_, _) =>
        {
            menu.Items.Clear();
            var hidden = session.Settings.Navigation.IsHidden(key);
            var action = new MenuFlyoutItem { Name = "NavigationVisibility" + key,
                Text = DesktopResources.Get(hidden ? "NavigationShow" : "NavigationHide"),
                Icon = DesktopIcons.Create(hidden ? Icon.Eye : Icon.EyeOff), IsEnabled = button.IsEnabled && !busy };
            ControlAppearance.Native(action);
            action.Click += async (_, _) => await SetNavigationHiddenAsync(key, !hidden);
            menu.Items.Add(action);
        };
        PrepareNavigationMenu(menu, button);
        options.Flyout = menu; button.ContextFlyout = menu;
    }

    private void PrepareNavigationMenu(MenuFlyout menu, FrameworkElement owner)
    {
        menu.Opening += (_, _) =>
        {
            var palette = ControlAppearance.Palette(owner);
            var style = new Style(typeof(MenuFlyoutPresenter));
            style.Setters.Add(new Setter(FrameworkElement.RequestedThemeProperty, ActualTheme));
            style.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(palette.Panel)));
            style.Setters.Add(new Setter(Control.ForegroundProperty, new SolidColorBrush(palette.Text)));
            menu.MenuFlyoutPresenterStyle = style;
        };
        ActualThemeChanged += (_, _) => menu.Hide();
    }

    private void BuildCompactNavigationMenu(NavigationGroup group)
    {
        group.CompactMenu.Items.Clear();
        foreach (var link in group.Links.Where(link => session.Settings.Navigation.IsHidden(link.Key)))
        {
            var entry = new MenuFlyoutItem { Name = "NavigationOpen" + link.Key,
                Text = AutomationProperties.GetName(link.Button), Icon = DesktopIcons.Create(link.Icon), IsEnabled = link.Button.IsEnabled };
            entry.Click += async (_, _) => { group.CompactMenu.Hide(); await link.Activate(); };
            ControlAppearance.Native(entry); entry.RequestedTheme = ActualTheme;
            group.CompactMenu.Items.Add(entry);
        }
    }

    private void UpdateNavigationLayout()
    {
        var open = split.IsPaneOpen;
        foreach (var group in navigationGroups)
        {
            group.CompactMenu.Hide();
            group.Panel.Children.Clear(); group.Hidden.Children.Clear();
            group.Panel.Spacing = open ? 2 : 8;
            group.Hidden.Margin = new Thickness(16, 0, 0, 0);
            foreach (var link in group.Links)
            {
                link.Menu.Hide();
                var hidden = session.Settings.Navigation.IsHidden(link.Key);
                link.Options.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
                SizeNavigationButton(link.Button, open);
                if (hidden) group.Hidden.Children.Add(link.Row);
                else group.Panel.Children.Add(link.Row);
            }
            var hasHidden = group.Hidden.Children.Count > 0;
            if (!hasHidden) group.Expanded = false;
            SizeNavigationButton(group.More, open);
            // More is only rendered when this group has hidden links, including an entirely hidden group.
            if (hasHidden) group.Panel.Children.Add(group.More);
            group.Hidden.Visibility = open && group.Expanded ? Visibility.Visible : Visibility.Collapsed;
            group.Panel.Children.Add(group.Hidden);
            AutomationProperties.SetHelpText(group.More, DesktopResources.Get(open && group.Expanded ? "NavigationCollapse" : "NavigationExpand"));
        }
    }

    private static void SizeNavigationButton(ButtonBase button, bool open)
    {
        button.Width = open ? double.NaN : 32;
        button.Height = open ? double.NaN : 32;
        button.MinHeight = open ? 36 : 32;
        button.Padding = open ? new Thickness(12, 4, 12, 4) : new Thickness(0);
        button.HorizontalContentAlignment = open ? HorizontalAlignment.Left : HorizontalAlignment.Center;
        if (button.Content is not StackPanel content) return;
        content.Spacing = open ? 12 : 0;
        foreach (var label in content.Children.OfType<TextBlock>()) label.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task SetNavigationHiddenAsync(string key, bool hidden)
    {
        if (busy || closing || downloading || !initialized || historyDialogOpen || catalogDialog is not null || !NavigationPreferences.IsCustomizable(key)) return;
        if (session.Settings.Navigation.IsHidden(key) == hidden) return;
        var group = navigationGroups.Single(group => group.Links.Any(link => link.Key == key));
        var link = group.Links.Single(link => link.Key == key);
        var saved = false;
        await RunAsync(async _ =>
        {
            var next = session.Settings.Clone();
            next.Navigation.SetHidden(key, hidden);
            // The old live state and layout survive write failures. RunAsync serializes against other settings edits.
            await SettingsStore.SaveAsync(session.DataDirectory, next);
            session.Settings = next; saved = true;
            UpdateNavigationLayout();
        });
        if (closing) return;
        if (saved && hidden) group.More.Focus(FocusState.Programmatic);
        else link.Button.Focus(FocusState.Programmatic);
    }

    private void SetNavigationBusy(bool value)
    {
        foreach (var group in navigationGroups)
        {
            group.More.IsEnabled = !value;
            if (value) group.CompactMenu.Hide();
            foreach (var link in group.Links)
            {
                link.Options.IsEnabled = !value;
                if (value) link.Menu.Hide();
            }
        }
    }
}
