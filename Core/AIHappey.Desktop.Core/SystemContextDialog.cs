using System.Text.Json;
using AIHappey.Vercel.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace AIHappey.Desktop.Core;

/// <summary>Read-only inspection of the same composed parts used for inference. No remote reads.</summary>
internal sealed class SystemContextDialog : ContentDialog, IResponsiveDialog
{
    private readonly Grid layout = new() { RowSpacing = 16 };
    private readonly StackPanel tabs = new() { Orientation = Orientation.Horizontal, Spacing = 4 };
    private readonly StackPanel body = new() { Name = "SystemContextBody", Spacing = 12 };
    private readonly ScrollViewer viewer;
    private readonly List<ToggleButton> buttons = [];
    private readonly List<(string Label, string Text, JsonElement? Json, bool Markdown)> parts = [];
    private readonly List<JsonElement> tools = [];
    private readonly HashSet<int> mcpJsonParts = [];
    private readonly Dictionary<int, McpSystemContextView> mcpViews = [];
    private readonly bool agentMode;

    public SystemContextDialog(UIMessage message, string appName, bool agentMode)
    {
        this.agentMode = agentMode;
        Name = "SystemContextDialog"; Title = DesktopResources.Get("Context");
        CloseButtonText = DesktopResources.Get("Close"); DefaultButton = ContentDialogButton.Close;
        Resources["ContentDialogMaxWidth"] = 1000d; Resources["ContentDialogMinWidth"] = 0d;
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        layout.Children.Add(new ScrollViewer { Content = tabs, HorizontalScrollMode = ScrollMode.Enabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollMode = ScrollMode.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled });
        viewer = new ScrollViewer { Content = body, HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalContentAlignment = HorizontalAlignment.Stretch };
        Grid.SetRow(viewer, 1); layout.Children.Add(viewer); Content = layout;
        // Read from this exact composed context, not a second live discovery snapshot.
        // Tool names/schemas therefore match the request, including collision aliases.
        foreach (var part in message.Parts.Where(part => part.Type == "text"))
        {
            try
            {
                using var document = JsonDocument.Parse(PortableConversations.Text(part));
                var value = document.RootElement;
                if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("modelContextProtocolServer", out _)
                    && value.TryGetProperty("tools", out var serverTools) && serverTools.ValueKind == JsonValueKind.Array)
                    tools.AddRange(serverTools.EnumerateArray().Where(tool => tool.ValueKind == JsonValueKind.Object).Select(tool => tool.Clone()));
            }
            catch (JsonException) { /* Other system parts may contain plain text. */ }
        }
        if (tools.Count > 0)
        {
            var label = DesktopResources.Format("McpToolsCount", tools.Count);
            parts.Add((label, "", null, false)); AddTab(label, 0);
        }
        foreach (var part in message.Parts)
        {
            var text = part.Type == "text" ? PortableConversations.Text(part) : PortableConversations.Element(part).GetRawText();
            JsonElement? json = null; var label = DesktopResources.Get("Instructions"); var markdown = false;
            try
            {
                using var document = JsonDocument.Parse(text); var value = document.RootElement;
                json = value.Clone(); label = DesktopResources.Get("ContextPart") + " " + (parts.Count + 1);
                if (value.ValueKind == JsonValueKind.Object)
                {
                    if (value.TryGetProperty("chatBotInstructions", out var instruction) && instruction.ValueKind == JsonValueKind.String)
                    { label = appName; text = instruction.GetString() ?? ""; json = null; markdown = true; }
                    else if (value.TryGetProperty("systemInformation", out var system)) { label = DesktopResources.Get("SystemContext"); json = system.Clone(); }
                    else if (value.TryGetProperty("availableSkills", out _)) label = DesktopResources.Get("Skills");
                    else if (value.TryGetProperty("modelContextProtocolServer", out var server))
                    {
                        var title = PortableConversations.String(server, "title");
                        label = string.IsNullOrWhiteSpace(title) ? PortableConversations.String(server, "name") ?? "MCP" : title;
                        mcpJsonParts.Add(parts.Count);
                    }
                    else if (value.TryGetProperty("preferredLanguage", out _) || value.TryGetProperty("username", out _) || value.TryGetProperty("id", out _))
                        label = PortableConversations.String(value, "name") ?? DesktopResources.Get("User");
                }
            }
            catch (JsonException) { /* Plain-text user instructions stay plain text. */ }
            parts.Add((label, text, json, markdown));
            var index = parts.Count - 1;
            AddTab(label, index);
        }
        Opened += (_, _) => { SizeToRoot(); XamlRoot.Changed += RootChanged; };
        Closed += (_, _) => XamlRoot.Changed -= RootChanged;
        if (parts.Count > 0) Render(0);
    }

    private void AddTab(string label, int index)
    {
        var button = new ToggleButton { Name = index == 0 && tools.Count > 0 ? "SystemContextToolsTab" : "SystemContextTab" + index,
            Content = label, Padding = new Thickness(12, 8, 12, 8), MinHeight = 36 };
        ControlAppearance.Stock(button); ToolbarControls.Label(button, label);
        button.Click += (_, _) => Render(index); buttons.Add(button); tabs.Children.Add(button);
    }

    private void Render(int index)
    {
        for (var i = 0; i < buttons.Count; i++) buttons[i].IsChecked = i == index;
        body.Children.Clear(); var part = parts[index];
        if (agentMode) body.Children.Add(Text(DesktopResources.Get("AgentContextOmitted")));
        if (index == 0 && tools.Count > 0)
        {
            foreach (var tool in tools) body.Children.Add(ToolCard(tool));
            viewer.ChangeView(null, 0, null, true); return;
        }
        if (mcpJsonParts.Contains(index) && part.Json is { } serverContext)
        {
            if (!mcpViews.TryGetValue(index, out var serverView))
            { serverView = new McpSystemContextView(serverContext); mcpViews.Add(index, serverView); }
            body.Children.Add(serverView); viewer.ChangeView(null, 0, null, true); return;
        }
        var card = new StackPanel { Spacing = 16 };
        card.Children.Add(new TextBlock { Text = part.Label, TextWrapping = TextWrapping.Wrap,
            FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        if (part.Json is { } json) card.Children.Add(JsonTree(json, null, 0));
        else if (part.Markdown) card.Children.Add(new ChatMarkdown { Text = part.Text });
        else card.Children.Add(Text(part.Text));
        var border = new Border { Child = card, Padding = new Thickness(20), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8) };
        ControlAppearance.TokenBadge(border); body.Children.Add(border);
        viewer.ChangeView(null, 0, null, true);
    }

    private static Border ToolCard(JsonElement tool)
    {
        var content = new StackPanel { Spacing = 12 };
        var title = PortableConversations.String(tool, "title");
        var name = PortableConversations.String(tool, "name") ?? "MCP";
        content.Children.Add(new TextBlock { Text = string.IsNullOrWhiteSpace(title) ? name : title,
            FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        if (!string.IsNullOrWhiteSpace(title) && title != name) content.Children.Add(Text(name, true));
        if (PortableConversations.String(tool, "description") is { Length: > 0 } description) content.Children.Add(Text(description));
        if (tool.TryGetProperty("annotations", out var annotations) && annotations.ValueKind == JsonValueKind.Object)
        {
            var badges = new MessageFooterPanel();
            foreach (var (flag, key) in new[] { ("readOnlyHint", "McpReadOnly"), ("idempotentHint", "McpIdempotent"),
                ("destructiveHint", "McpDestructive"), ("openWorldHint", "McpOpenWorld") })
            {
                if (!annotations.TryGetProperty(flag, out var enabled) || enabled.ValueKind != JsonValueKind.True) continue;
                var badge = new Border { Child = new TextBlock { Text = DesktopResources.Get(key), FontSize = 12 },
                    Padding = new Thickness(10, 4, 10, 4), CornerRadius = new CornerRadius(16) };
                ControlAppearance.TokenBadge(badge); badges.Children.Add(badge);
            }
            if (badges.Children.Count > 0) content.Children.Add(badges);
        }
        foreach (var (field, key) in new[] { ("inputSchema", "Input"), ("outputSchema", "Output") })
        {
            if (!tool.TryGetProperty(field, out var schema) || schema.ValueKind != JsonValueKind.Object) continue;
            var expander = new Expander { Header = DesktopResources.Get(key),
                Content = JsonTree(schema, null, 0),
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            ControlAppearance.Native(expander); content.Children.Add(expander);
        }
        var card = new Border { Name = "SystemContextToolCard", Child = content, Padding = new Thickness(20),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8) };
        ControlAppearance.TokenBadge(card); return card;
    }

    internal static UIElement JsonTree(JsonElement value, string? name, int depth)
    {
        if (value.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array) || depth >= 8)
            return Text((name is null ? "" : name + ": ") + value.GetRawText(), true);
        var children = new StackPanel { Spacing = 8, Margin = new Thickness(12, 4, 0, 4) };
        if (value.ValueKind == JsonValueKind.Object)
            foreach (var property in value.EnumerateObject()) children.Children.Add(JsonTree(property.Value, property.Name, depth + 1));
        else
        {
            var index = 0;
            foreach (var element in value.EnumerateArray()) children.Children.Add(JsonTree(element, "[" + index++ + "]", depth + 1));
        }
        var expander = new Expander { Header = (name is null ? "" : name + ": ") + (value.ValueKind == JsonValueKind.Object ? "{Object}" : "[Array]"),
            Content = children, IsExpanded = depth < 2, HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch };
        ControlAppearance.Native(expander); return expander;
    }

    private static TextBlock Text(string text, bool code = false) => new() { Text = text, TextWrapping = TextWrapping.Wrap,
        IsTextSelectionEnabled = true, FontFamily = new FontFamily(code ? "Cascadia Mono, Consolas" : "Segoe UI") };
    private void RootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => SizeToRoot();
    void IResponsiveDialog.SizeToRoot() => SizeToRoot();
    private void SizeToRoot()
    {
        layout.Width = Math.Max(0, Math.Min(900, XamlRoot.Size.Width - 96));
        layout.Height = Math.Max(0, Math.Min(680, XamlRoot.Size.Height - 240));
    }
}
