using FluentIcons.Common;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AIHappey.Desktop.Core;

/// <summary>Centered, read-only catalog inspection. No relationship to the conversation's details pane.</summary>
internal sealed class CatalogDetailsDialog : ContentDialog, IResponsiveDialog
{
    private readonly CatalogItem item;
    private readonly Grid layout = new() { RowSpacing = 16 };
    private readonly NavigationView tabs = new() { Name = "CatalogDetailsTabs", PaneDisplayMode = NavigationViewPaneDisplayMode.Top,
        IsSettingsVisible = false, IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed,
        IsPaneToggleButtonVisible = false, AlwaysShowHeader = false, Height = 56 };
    private readonly StackPanel body = new() { Name = "CatalogDialogBody", Spacing = 12 };
    private readonly ScrollViewer viewer;
    private IReadOnlyList<CatalogVersion> versions = [];
    private bool loadingVersions;
    private bool actionsEnabled = true;
    private string? versionsError;
    private string selectedTab = "general";
    public Action<string?>? DownloadRequested { get; set; }
    public Action? StartChatRequested { get; set; }

    public CatalogDetailsDialog(CatalogItem item)
    {
        this.item = item;
        Name = "CatalogDetailsDialog";
        Title = item.Name;
        CloseButtonText = DesktopResources.Get("Close");
        DefaultButton = ContentDialogButton.Close;
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;
        // Concrete, app-owned size values; never resolve/cast optional native styles.
        Resources["ContentDialogMaxWidth"] = 760d;
        Resources["ContentDialogMinWidth"] = 0d;
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        layout.Children.Add(tabs);
        AddTab("general", DesktopResources.Get("General"));
        if (item.Kind == CatalogKind.Agent) { AddTab("instructions", DesktopResources.Get("Instructions")); AddTab("definition", DesktopResources.Get("Definition")); }
        else AddTab("versions", DesktopResources.Get("Versions"));
        viewer = new ScrollViewer { Content = body, HorizontalScrollMode = ScrollMode.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        Grid.SetRow(viewer, 1); layout.Children.Add(viewer); Content = layout;
        tabs.SelectedItem = tabs.MenuItems[0];
        tabs.SelectionChanged += (_, args) =>
        {
            if (args.SelectedItem is not NavigationViewItem { Tag: string key } || selectedTab == key) return;
            selectedTab = key; Render(); viewer.ChangeView(null, 0, null, true);
        };
        Opened += (_, _) => { SizeToRoot(); XamlRoot.Changed += RootChanged; };
        Closed += (_, _) => XamlRoot.Changed -= RootChanged;
        Render();
    }

    private void RootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => SizeToRoot();
    void IResponsiveDialog.SizeToRoot() => SizeToRoot();
    private void SizeToRoot()
    {
        layout.Width = Math.Max(0, Math.Min(680, XamlRoot.Size.Width - 96));
        layout.Height = Math.Max(0, Math.Min(500, XamlRoot.Size.Height - 240));
    }

    private void AddTab(string key, string label)
    {
        var tab = new NavigationViewItem { Name = "CatalogTab" + char.ToUpperInvariant(key[0]) + key[1..], Tag = key, Content = label };
        ToolbarControls.Label(tab, label); AutomationProperties.SetAutomationId(tab, tab.Name);
        tabs.MenuItems.Add(tab);
    }

    public void SetVersions(IReadOnlyList<CatalogVersion> value, bool loading, string? error = null)
    { versions = value; loadingVersions = loading; versionsError = error; if (selectedTab == "versions") Render(); }

    public void SetActionsEnabled(bool enabled)
    {
        actionsEnabled = enabled;
        foreach (var button in ControlAppearance.Descendants(body).OfType<Button>()) button.IsEnabled = enabled;
        // Close/Escape and tab browsing remain available during read-only HTTP operations.
    }

    private void Render()
    {
        body.Children.Clear();
        switch (selectedTab)
        {
            case "general": RenderGeneral(); break;
            case "instructions":
                var instructions = item.Definition is { } definition ? CatalogProjection.Text(definition, "instructions") : null;
                Card(DesktopResources.Get("InstructionsReadOnly")).Children.Add(Text(instructions ?? DesktopResources.Get("InstructionsUnavailable")));
                break;
            case "definition":
                Card(DesktopResources.Get("DefinitionReadOnly")).Children.Add(Text(item.Definition is { } value
                    ? JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true })
                    : DesktopResources.Get("DefinitionNotExposed")));
                break;
            case "versions": RenderVersions(); break;
        }
        SetActionsEnabled(actionsEnabled);
    }

    private void RenderGeneral()
    {
        var card = Card(item.Id);
        var badges = new MessageFooterPanel();
        if (item.Model is not null) badges.Children.Add(Badge(item.Model));
        if (item.Version is not null) badges.Children.Add(Badge(DesktopResources.Format("DefaultVersion", item.Version)));
        if (item.LatestVersion is not null) badges.Children.Add(Badge(DesktopResources.Format("LatestVersion", item.LatestVersion)));
        if (badges.Children.Count > 0) card.Children.Add(badges);
        card.Children.Add(Text(item.Description));
        if (item.Owner is not null) card.Children.Add(Text(DesktopResources.Format("Owner", item.Owner)));
        if (item.Created is { } created)
        { try { card.Children.Add(Text(DesktopResources.Format("Created", DateTimeOffset.FromUnixTimeSeconds(created).ToLocalTime()))); } catch (ArgumentOutOfRangeException) { } }
        var actions = new MessageFooterPanel();
        if (item.CanDownload)
        {
            var download = Button(item.Kind == CatalogKind.Agent ? DesktopResources.Get("DownloadDefinition") : DesktopResources.Get("DownloadDefault"), Icon.ArrowDownload);
            download.Click += (_, _) => DownloadRequested?.Invoke(null); actions.Children.Add(download);
        }
        if (item.Kind == CatalogKind.Agent)
        {
            var chat = Button(DesktopResources.Get("StartChat"), Icon.Chat); chat.Click += (_, _) => StartChatRequested?.Invoke(); actions.Children.Add(chat);
        }
        if (actions.Children.Count > 0) AddActions(card, actions);
        if (item.Kind == CatalogKind.Skill) Card(DesktopResources.Get("Downloads")).Children.Add(Text(DesktopResources.Get("SkillDownloadHint")));
        else if (!item.Definition.HasValue) Card(DesktopResources.Get("DefinitionUnavailable")).Children.Add(Text(DesktopResources.Get("DefinitionUnavailableHint")));
    }

    private void RenderVersions()
    {
        if (loadingVersions) Card(DesktopResources.Get("Versions")).Children.Add(Text(DesktopResources.Get("LoadingVersions")));
        else if (versionsError is not null) Card(DesktopResources.Get("VersionsUnavailable")).Children.Add(Text(versionsError));
        else if (versions.Count == 0) Card(DesktopResources.Get("Versions")).Children.Add(Text(DesktopResources.Get("NoVersions")));
        foreach (var version in versions)
        {
            var card = Card(version.Version);
            var badges = new MessageFooterPanel();
            if (item.Version == version.Version) badges.Children.Add(Badge(DesktopResources.Get("Default")));
            if (item.LatestVersion == version.Version) badges.Children.Add(Badge(DesktopResources.Get("Latest")));
            if (badges.Children.Count > 0) card.Children.Add(badges);
            if (version.Description is not null) card.Children.Add(Text(version.Description));
            if (item.CanDownload)
            {
                var actions = new MessageFooterPanel();
                var download = Button(DesktopResources.Format("DownloadVersion", version.Version), Icon.ArrowDownload);
                download.Click += (_, _) => DownloadRequested?.Invoke(version.Version); actions.Children.Add(download); AddActions(card, actions);
            }
        }
    }

    private StackPanel Card(string title)
    {
        NativeSettingsSurface.Card(body, "CatalogDialogCard", title, out var content);
        return content;
    }

    private static TextBlock Text(string text) => new() { Text = text, IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap, FontSize = 14 };
    private static Border Badge(string text)
    {
        var badge = new Border { Child = Text(text) };
        NativeCardSurface.Badge(badge); return badge;
    }
    private static Button Button(string label, Icon icon)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        content.Children.Add(DesktopIcons.Create(icon, 16)); content.Children.Add(Text(label));
        var button = new Button { Name = "CatalogDialogAction", Content = content, Padding = new Thickness(8), MinHeight = 36 };
        NativeCardSurface.Action(button); ToolbarControls.Label(button, label); return button;
    }
    private static void AddActions(StackPanel content, UIElement actions)
    {
        var footer = new Border { Child = actions, Padding = new Thickness(0, 8, 0, 0), BorderThickness = new Thickness(0, 1, 0, 0) };
        NativeCardSurface.Divider(footer); content.Children.Add(footer);
    }
}
