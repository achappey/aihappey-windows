using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace AIHappey.Desktop.Core;

/// <summary>Centered, read-only catalog inspection. No relationship to the conversation's details pane.</summary>
internal sealed class CatalogDetailsDialog : ContentDialog
{
    private readonly CatalogItem item;
    private readonly Grid layout = new() { RowSpacing = 16 };
    private readonly StackPanel tabs = new() { Orientation = Orientation.Horizontal, Spacing = 4 };
    private readonly StackPanel body = new() { Name = "CatalogDialogBody", Spacing = 12 };
    private readonly ScrollViewer viewer;
    private readonly Dictionary<string, ToggleButton> tabButtons = [];
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
        CloseButtonText = "Close";
        DefaultButton = ContentDialogButton.Close;
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;
        // Concrete, app-owned size values; never resolve/cast optional native styles.
        Resources["ContentDialogMaxWidth"] = 760d;
        Resources["ContentDialogMinWidth"] = 0d;
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        var tabSurface = new Border { Child = tabs, CornerRadius = new CornerRadius(8), Padding = new Thickness(4), HorizontalAlignment = HorizontalAlignment.Left };
        ControlAppearance.Apply(tabSurface, (_, _) => { }, palette => tabSurface.Background = new SolidColorBrush(palette.Selected));
        layout.Children.Add(new ScrollViewer { Content = tabSurface, HorizontalScrollMode = ScrollMode.Enabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollMode = ScrollMode.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled });
        AddTab("general", "General");
        if (item.Kind == CatalogKind.Agent) { AddTab("instructions", "Instructions"); AddTab("definition", "Definition"); }
        else AddTab("versions", "Versions");
        viewer = new ScrollViewer { Content = body, HorizontalScrollMode = ScrollMode.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        Grid.SetRow(viewer, 1); layout.Children.Add(viewer); Content = layout;
        Opened += (_, _) => { SizeToRoot(); XamlRoot.Changed += RootChanged; };
        Closed += (_, _) => XamlRoot.Changed -= RootChanged;
        ActualThemeChanged += (_, _) => RefreshTabs();
        Render();
    }

    private void RootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => SizeToRoot();
    private void SizeToRoot()
    {
        layout.Width = Math.Max(0, Math.Min(680, XamlRoot.Size.Width - 96));
        layout.Height = Math.Max(0, Math.Min(500, XamlRoot.Size.Height - 240));
        MaxWidth = Math.Max(0, Math.Min(760, XamlRoot.Size.Width - 32));
    }

    private void AddTab(string key, string label)
    {
        var button = new ToggleButton { Name = "CatalogTab" + label, Content = label, Padding = new Thickness(12, 8, 12, 8), MinHeight = 36,
            CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(0) };
        ToolbarControls.Label(button, label);
        ControlAppearance.Apply(button, ControlAppearance.NativeResources, palette =>
        { button.Background = new SolidColorBrush(selectedTab == key ? palette.Surface : palette.Background); button.Foreground = new SolidColorBrush(palette.Text); });
        button.Click += (_, _) => { selectedTab = key; Render(); viewer.ChangeView(null, 0, null, true); };
        tabButtons.Add(key, button); tabs.Children.Add(button);
    }

    private void RefreshTabs()
    {
        foreach (var (key, button) in tabButtons)
        {
            button.IsChecked = key == selectedTab;
            var palette = ControlAppearance.Palette(button);
            button.Background = new SolidColorBrush(key == selectedTab ? palette.Surface : palette.Background);
            ControlAppearance.Refresh(button);
        }
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
        RefreshTabs(); body.Children.Clear();
        switch (selectedTab)
        {
            case "general": RenderGeneral(); break;
            case "instructions":
                var instructions = item.Definition is { } definition ? CatalogProjection.Text(definition, "instructions") : null;
                Card("Instructions (read-only)").Children.Add(Text(instructions ?? "The backend does not expose instructions for this agent."));
                break;
            case "definition":
                Card("Definition (read-only)").Children.Add(Text(item.Definition is { } value
                    ? JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true })
                    : "The backend does not expose this agent's definition. Definition download is unavailable."));
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
        if (item.Version is not null) badges.Children.Add(Badge("Default " + item.Version));
        if (item.LatestVersion is not null) badges.Children.Add(Badge("Latest " + item.LatestVersion));
        if (badges.Children.Count > 0) card.Children.Add(badges);
        card.Children.Add(Text(item.Description));
        if (item.Owner is not null) card.Children.Add(Text("Owner: " + item.Owner));
        if (item.Created is { } created)
        { try { card.Children.Add(Text("Created: " + DateTimeOffset.FromUnixTimeSeconds(created).ToLocalTime().ToString("g"))); } catch (ArgumentOutOfRangeException) { } }
        var actions = new MessageFooterPanel();
        if (item.CanDownload)
        {
            var download = Button(item.Kind == CatalogKind.Agent ? "Download definition" : "Download default version", "\uE896");
            download.Click += (_, _) => DownloadRequested?.Invoke(null); actions.Children.Add(download);
        }
        if (item.Kind == CatalogKind.Agent)
        {
            var chat = Button("Start chat", "\uE8F2"); chat.Click += (_, _) => StartChatRequested?.Invoke(); actions.Children.Add(chat);
        }
        if (actions.Children.Count > 0) AddActions(card, actions);
        if (item.Kind == CatalogKind.Skill) Card("Downloads").Children.Add(Text("Downloading saves a ZIP file only. It does not install or enable this skill."));
        else if (!item.Definition.HasValue) Card("Definition unavailable").Children.Add(Text("You can chat with this agent, but its backend does not expose a downloadable definition."));
    }

    private void RenderVersions()
    {
        if (loadingVersions) Card("Versions").Children.Add(Text("Loading versions…"));
        else if (versionsError is not null) Card("Versions unavailable").Children.Add(Text(versionsError));
        else if (versions.Count == 0) Card("Versions").Children.Add(Text("No versions are available."));
        foreach (var version in versions)
        {
            var card = Card(version.Version);
            var badges = new MessageFooterPanel();
            if (item.Version == version.Version) badges.Children.Add(Badge("Default"));
            if (item.LatestVersion == version.Version) badges.Children.Add(Badge("Latest"));
            if (badges.Children.Count > 0) card.Children.Add(badges);
            if (version.Description is not null) card.Children.Add(Text(version.Description));
            if (item.CanDownload)
            {
                var actions = new MessageFooterPanel();
                var download = Button("Download version " + version.Version, "\uE896");
                download.Click += (_, _) => DownloadRequested?.Invoke(version.Version); actions.Children.Add(download); AddActions(card, actions);
            }
        }
    }

    private StackPanel Card(string title)
    {
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(new TextBlock { Text = title, FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        var border = new Border { Name = "CatalogDialogCard", Child = content, CornerRadius = new CornerRadius(8), Padding = new Thickness(16), BorderThickness = new Thickness(1) };
        ControlAppearance.Apply(border, (_, _) => { }, palette => { border.Background = new SolidColorBrush(palette.Panel); border.BorderBrush = new SolidColorBrush(palette.Stroke); });
        body.Children.Add(border); return content;
    }

    private static TextBlock Text(string text) => new() { Text = text, IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap, FontSize = 14 };
    private static Border Badge(string text)
    {
        var badge = new Border { Child = Text(text), CornerRadius = new CornerRadius(16), Padding = new Thickness(10, 4, 10, 4) };
        ControlAppearance.TokenBadge(badge); return badge;
    }
    private static Button Button(string label, string glyph)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        content.Children.Add(new FontIcon { Glyph = glyph, FontSize = 16 }); content.Children.Add(Text(label));
        var button = new Button { Name = "CatalogDialogAction", Content = content, Padding = new Thickness(8), MinHeight = 36 };
        ToolbarControls.Subtle(button); ToolbarControls.Label(button, label); return button;
    }
    private static void AddActions(StackPanel content, UIElement actions)
    {
        var footer = new Border { Child = actions, Padding = new Thickness(0, 8, 0, 0), BorderThickness = new Thickness(0, 1, 0, 0) };
        ControlAppearance.Separator(footer); content.Children.Add(footer);
    }
}
