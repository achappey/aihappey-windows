using FluentIcons.Common;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;

namespace AIHappey.Desktop.Core;

/// <summary>Adds row-local actions without replacing the native list's selection, focus, or automation.</summary>
internal sealed class ConversationRow
{
    private readonly ListViewItem container;
    private readonly Button more;
    private readonly MenuFlyout menu = new() { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight };
    private readonly MenuFlyoutItem rename = new() { Text = DesktopResources.Get("Rename"), Icon = DesktopIcons.Create(Icon.Edit) };
    private readonly MenuFlyoutItem export = new() { Text = DesktopResources.Get("Export"), Icon = DesktopIcons.Create(Icon.ArrowExport) };
    private readonly MenuFlyoutItem pin = new() { Text = DesktopResources.Get("PinChat"), Icon = DesktopIcons.Create(Icon.Pin) };
    private readonly MenuFlyoutItem delete = new() { Text = DesktopResources.Get("Delete"), Icon = DesktopIcons.Create(Icon.Delete) };
    private readonly Func<Conversation, bool> isPinned;
    private Conversation? conversation;
    private bool hovered;

    public Grid Root { get; }

    public static DataTemplate Template() => (DataTemplate)XamlReader.Load("""
        <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                      xmlns:ic="using:FluentIcons.WinUI">
            <Grid ColumnSpacing="8" Background="Transparent">
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="*" />
                    <ColumnDefinition Width="Auto" />
                </Grid.ColumnDefinitions>
                <TextBlock Text="{Binding Title}" TextTrimming="CharacterEllipsis"
                           VerticalAlignment="Center" />
                <Button x:Name="ConversationActions" Grid.Column="1" Width="32" Height="32"
                        Padding="0" CornerRadius="6" Opacity="0" IsHitTestVisible="False">
                    <ic:FluentIcon Icon="MoreHorizontal" />
                </Button>
            </Grid>
        </DataTemplate>
        """);

    public ConversationRow(ListViewItem container, Grid root, Func<Conversation, Task> renameChat, Func<Conversation, Task> exportChat,
        Func<Conversation, Task> togglePin, Func<Conversation, Task> deleteChat, Func<Conversation, bool> isPinned)
    {
        this.container = container;
        this.isPinned = isPinned;
        Root = root;
        more = (Button)root.FindName("ConversationActions");
        ToolbarControls.Subtle(more);
        ToolbarControls.Label(more, DesktopResources.Get("ChatActions"));
        foreach (var item in new[] { rename, export, pin, delete }) ControlAppearance.Native(item);
        menu.Items.Add(rename);
        menu.Items.Add(export);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(pin);
        menu.Items.Add(delete);
        more.Flyout = menu;
        rename.Click += async (_, _) => { if (conversation is { } chat) await renameChat(chat); };
        export.Click += async (_, _) => { if (conversation is { } chat) await exportChat(chat); };
        pin.Click += async (_, _) => { if (conversation is { } chat) await togglePin(chat); };
        delete.Click += async (_, _) => { if (conversation is { } chat) await deleteChat(chat); };
        container.PointerEntered += (_, _) => SetHovered(true);
        container.PointerExited += (_, _) => SetHovered(false);
        container.PointerCanceled += (_, _) => SetHovered(false);
        container.GotFocus += (_, _) => UpdateVisibility();
        container.LostFocus += (_, _) => container.DispatcherQueue.TryEnqueue(UpdateVisibility);
        container.RegisterPropertyChangedCallback(Control.FocusStateProperty, (_, _) => UpdateVisibility());
        more.RegisterPropertyChangedCallback(Control.FocusStateProperty, (_, _) => UpdateVisibility());
        container.Unloaded += (_, _) => { SetHovered(false); menu.Hide(); };
        container.ActualThemeChanged += (_, _) => menu.Hide();
        container.RegisterPropertyChangedCallback(Control.IsEnabledProperty, (_, _) =>
        {
            if (!container.IsEnabled) menu.Hide();
            UpdateVisibility();
        });
        menu.Opening += (_, _) =>
        {
            foreach (var item in menu.Items.OfType<MenuFlyoutItem>()) item.IsEnabled = conversation is not null && container.IsEnabled;
            var pinned = conversation is { } chat && isPinned(chat);
            pin.Text = DesktopResources.Get(pinned ? "UnpinChat" : "PinChat");
            pin.Icon = DesktopIcons.Create(pinned ? Icon.PinOff : Icon.Pin);
            var palette = ControlAppearance.Palette(container);
            var style = new Style(typeof(MenuFlyoutPresenter));
            style.Setters.Add(new Setter(FrameworkElement.RequestedThemeProperty, container.ActualTheme));
            style.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(palette.Panel)));
            style.Setters.Add(new Setter(Control.ForegroundProperty, new SolidColorBrush(palette.Text)));
            style.Setters.Add(new Setter(Control.BorderBrushProperty, new SolidColorBrush(palette.Stroke)));
            style.Setters.Add(new Setter(Control.CornerRadiusProperty, new CornerRadius(8)));
            menu.MenuFlyoutPresenterStyle = style;
            foreach (var item in menu.Items.OfType<MenuFlyoutItem>()) item.RequestedTheme = container.ActualTheme;
            UpdateVisibility();
        };
        menu.Opened += (_, _) =>
        {
            foreach (var item in menu.Items.OfType<MenuFlyoutItem>()) ControlAppearance.Refresh(item);
            UpdateVisibility();
        };
        menu.Closed += (_, _) => UpdateVisibility();
    }

    public void Bind(Conversation? chat)
    {
        if (conversation?.Id != chat?.Id)
        {
            menu.Hide();
            hovered = false;
        }
        conversation = chat;
        AutomationProperties.SetName(container, chat?.Title ?? "");
        AutomationProperties.SetItemStatus(container, chat is not null && isPinned(chat) ? DesktopResources.Get("ChatPinned") : "");
        AutomationProperties.SetName(more, chat is null ? DesktopResources.Get("ChatActions") : DesktopResources.Format("ChatActionsFor", chat.Title));
        UpdateVisibility();
    }

    private void SetHovered(bool value)
    {
        hovered = value;
        UpdateVisibility();
    }

    private void UpdateVisibility()
    {
        // Opacity, rather than collapsing the button, prevents title reflow and retains tab access.
        var interacting = hovered || container.FocusState == FocusState.Keyboard || more.FocusState == FocusState.Keyboard || menu.IsOpen;
        var pinned = conversation is { } chat && isPinned(chat);
        var visible = conversation is not null && (pinned || interacting);
        more.Content = DesktopIcons.Create(pinned && !interacting ? Icon.Pin : Icon.MoreHorizontal);
        more.Opacity = visible ? 1 : 0;
        more.IsHitTestVisible = visible && container.IsEnabled;
    }
}
