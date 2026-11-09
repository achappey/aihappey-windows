using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AIHappey.Desktop.Core;

/// <summary>Browser-style nested server sections, projected only from the captured system context.
/// No discovery, resource reads, or tool execution happens in this view.</summary>
internal sealed class McpSystemContextView : UserControl
{
    private readonly Grid layout = new() { RowSpacing = 12 };
    private readonly NavigationView navigation = new() { Name = "McpContextSections", PaneDisplayMode = NavigationViewPaneDisplayMode.Top,
        IsSettingsVisible = false, IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed,
        IsPaneToggleButtonVisible = false, AlwaysShowHeader = false, Height = 56 };
    private readonly StackPanel content = new() { Name = "McpContextSectionContent", Spacing = 12 };
    private readonly List<Func<UIElement>> sections = [];
    private readonly JsonElement block;
    private readonly JsonElement server;

    public McpSystemContextView(JsonElement block)
    {
        this.block = block.Clone();
        server = this.block.GetProperty("modelContextProtocolServer");
        Name = "McpSystemContextView";
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        Grid.SetRow(content, 1); layout.Children.Add(navigation); layout.Children.Add(content); Content = layout;

        AddSection("General", General);
        var instructions = PortableConversations.String(this.block, "instructions");
        if (!string.IsNullOrWhiteSpace(instructions))
            AddSection("Instructions", () => Card(DesktopResources.Get("Instructions"), new ChatMarkdown { Text = instructions }));
        // Match the browser's optional metadata section. Retain the original JSON value, not a flattened string.
        if (TryMetadata(out var metadata))
            AddSection("McpMetadata", () => Card(DesktopResources.Get("McpMetadata"), SystemContextDialog.JsonTree(metadata, null, 0)));
        AddCatalogSection("resources", "McpResources");
        AddCatalogSection("resourceTemplates", "McpResourceTemplates");

        navigation.SelectedItem = navigation.MenuItems[0];
        navigation.SelectionChanged += (_, args) =>
        {
            if (args.SelectedItem is NavigationViewItem { Tag: int index }) Select(index);
        };
        Select(0);
    }

    private void AddSection(string key, Func<UIElement> render, string? label = null)
    {
        label ??= DesktopResources.Get(key);
        var index = sections.Count;
        var tab = new NavigationViewItem { Name = "McpContext" + key, Content = label, Tag = index };
        ToolbarControls.Label(tab, label); AutomationProperties.SetAutomationId(tab, tab.Name);
        sections.Add(render); navigation.MenuItems.Add(tab);
    }

    private void Select(int index)
    {
        content.Children.Clear(); content.Children.Add(sections[index]());
    }

    private UIElement General()
    {
        var body = new StackPanel { Spacing = 12 };
        var name = PortableConversations.String(server, "name") ?? "MCP";
        var title = PortableConversations.String(server, "title");
        if (!string.IsNullOrWhiteSpace(title) && title != name) AddField(body, "Name", name);
        if (PortableConversations.String(server, "version") is { Length: > 0 } version)
            AddField(body, "McpVersion", version);
        AddLink(body, "McpServerUrl", PortableConversations.String(server, "mcpServerUrl"));
        AddLink(body, "McpWebsite", PortableConversations.String(server, "websiteUrl"));
        if (server.TryGetProperty("repository", out var repository) && repository.ValueKind == JsonValueKind.Object)
        {
            var url = PortableConversations.String(repository, "url");
            var folder = PortableConversations.String(repository, "subfolder");
            AddLink(body, "McpRepository", url is null ? null : url.TrimEnd('/') + (string.IsNullOrWhiteSpace(folder) ? "" : "/" + folder.TrimStart('/')));
        }

        // All server identity properties remain inspectable in the same native JSON tree as System/User.
        // This is the server object only: tools belong exclusively to the aggregate Tools tab.
        var properties = new Expander { Header = DesktopResources.Get("McpServerProperties"),
            Content = SystemContextDialog.JsonTree(server, null, 0), HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch };
        ControlAppearance.Native(properties); body.Children.Add(properties);
        return Card(string.IsNullOrWhiteSpace(title) ? name : title, body);
    }

    private bool TryMetadata(out JsonElement metadata)
    {
        foreach (var key in new[] { "meta", "metadata" })
        {
            if (server.TryGetProperty(key, out metadata) && metadata.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)) return true;
            if (block.TryGetProperty(key, out metadata) && metadata.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)) return true;
        }
        metadata = default; return false;
    }

    private void AddCatalogSection(string property, string key)
    {
        // Render the captured assistant-visible catalogs, never the raw user picker catalog.
        // Do not show empty placeholder tabs or fetch/read a resource merely to inspect context.
        if (!block.TryGetProperty(property, out var items) || items.ValueKind != JsonValueKind.Array || items.GetArrayLength() == 0) return;
        AddSection(key, () =>
        {
            var list = new StackPanel { Spacing = 12 };
            foreach (var item in items.EnumerateArray())
            {
                var title = PortableConversations.String(item, "title") ?? PortableConversations.String(item, "name") ?? DesktopResources.Get(key);
                list.Children.Add(Card(title, SystemContextDialog.JsonTree(item, null, 0)));
            }
            return list;
        }, DesktopResources.Get(key) + " (" + items.GetArrayLength() + ")");
    }

    private static void AddField(StackPanel parent, string key, string value)
    {
        var field = new StackPanel { Spacing = 4 };
        var label = new TextBlock { Text = DesktopResources.Get(key) };
        NativeCardSurface.Secondary(label, true); field.Children.Add(label);
        field.Children.Add(new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
        parent.Children.Add(field);
    }

    private static void AddLink(StackPanel parent, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        // Navigate only by explicit user action, and never accept executable/custom URI schemes.
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || !string.IsNullOrEmpty(uri.UserInfo)
            || !(uri.Scheme == "https" || uri.Scheme == "http" && uri.IsLoopback))
        { AddField(parent, key, value); return; }
        var field = new StackPanel { Spacing = 4 };
        var label = new TextBlock { Text = DesktopResources.Get(key) };
        NativeCardSurface.Secondary(label, true); field.Children.Add(label);
        var link = new HyperlinkButton { NavigateUri = uri, Content = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap },
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(0) };
        ControlAppearance.Native(link); ToolbarControls.Label(link, DesktopResources.Get(key) + ": " + value);
        field.Children.Add(link); parent.Children.Add(field);
    }

    private static Border Card(string title, UIElement child)
    {
        var body = new StackPanel { Spacing = 16 };
        var heading = new TextBlock { Text = title, FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetHeadingLevel(heading, Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level3);
        body.Children.Add(heading); body.Children.Add(child);
        var card = new Border { Name = "McpContextSectionCard", Child = body };
        NativeCardSurface.Card(card, true); return card;
    }
}
