using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace AIHappey.Desktop.Core;

/// <summary>Browser-style nested server sections, projected only from the captured system context.
/// No discovery, resource reads, or tool execution happens in this view.</summary>
internal sealed class McpSystemContextView : UserControl
{
    private readonly Grid layout = new() { ColumnSpacing = 16, RowSpacing = 12 };
    private readonly StackPanel navigation = new() { Name = "McpContextSections", Spacing = 4 };
    private readonly StackPanel content = new() { Name = "McpContextSectionContent", Spacing = 12 };
    private readonly List<(ToggleButton Button, Func<UIElement> Render)> sections = [];
    private readonly JsonElement block;
    private readonly JsonElement server;
    private int selected;

    public McpSystemContextView(JsonElement block)
    {
        this.block = block.Clone();
        server = this.block.GetProperty("modelContextProtocolServer");
        Name = "McpSystemContextView";
        layout.ColumnDefinitions.Add(new() { Width = new GridLength(160) });
        layout.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        navigation.HorizontalAlignment = HorizontalAlignment.Stretch;
        Grid.SetColumn(content, 1); layout.Children.Add(navigation); layout.Children.Add(content); Content = layout;

        AddSection("General", General);
        var instructions = PortableConversations.String(this.block, "instructions");
        if (!string.IsNullOrWhiteSpace(instructions))
            AddSection("Instructions", () => Card(DesktopResources.Get("Instructions"), new ChatMarkdown { Text = instructions }));
        // Match the browser's optional metadata section. Retain the original JSON value, not a flattened string.
        if (TryMetadata(out var metadata))
            AddSection("McpMetadata", () => Card(DesktopResources.Get("McpMetadata"), SystemContextDialog.JsonTree(metadata, null, 0)));
        AddCatalogSection("resources", "McpResources");
        AddCatalogSection("resourceTemplates", "McpResourceTemplates");

        SizeChanged += (_, args) => ArrangeSections(args.NewSize.Width);
        Select(0);
    }

    private void AddSection(string key, Func<UIElement> render, string? label = null)
    {
        label ??= DesktopResources.Get(key);
        var index = sections.Count;
        var button = new ToggleButton { Name = "McpContext" + key, Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap },
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(12, 10, 12, 10), MinHeight = 40, CornerRadius = new CornerRadius(6) };
        ControlAppearance.Stock(button); ToolbarControls.Label(button, label);
        button.Click += (_, _) => Select(index);
        sections.Add((button, render)); navigation.Children.Add(button);
    }

    private void Select(int index)
    {
        selected = index;
        for (var i = 0; i < sections.Count; i++) sections[i].Button.IsChecked = i == selected;
        content.Children.Clear(); content.Children.Add(sections[selected].Render());
    }

    private void ArrangeSections(double width)
    {
        // Keep vertical navigation at normal widths; stack above the detail card when space is limited.
        var narrow = width < 560;
        layout.ColumnDefinitions[0].Width = narrow ? new GridLength(1, GridUnitType.Star) : new GridLength(160);
        layout.ColumnDefinitions[1].Width = narrow ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        layout.ColumnSpacing = narrow ? 0 : 16;
        Grid.SetColumn(content, narrow ? 0 : 1); Grid.SetRow(content, narrow ? 1 : 0);
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
