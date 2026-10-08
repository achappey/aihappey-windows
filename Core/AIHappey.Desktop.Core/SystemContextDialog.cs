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
                    else if (value.TryGetProperty("modelContextProtocolServer", out var server)) label = PortableConversations.String(server, "title") ?? PortableConversations.String(server, "name") ?? "MCP";
                    else if (value.TryGetProperty("preferredLanguage", out _) || value.TryGetProperty("username", out _) || value.TryGetProperty("id", out _))
                        label = PortableConversations.String(value, "name") ?? DesktopResources.Get("User");
                }
            }
            catch (JsonException) { /* Plain-text user instructions stay plain text. */ }
            parts.Add((label, text, json, markdown));
            var index = parts.Count - 1;
            var button = new ToggleButton { Name = "SystemContextTab" + index, Content = label, Padding = new Thickness(12, 8, 12, 8), MinHeight = 36 };
            ControlAppearance.Stock(button); ToolbarControls.Label(button, label);
            button.Click += (_, _) => Render(index); buttons.Add(button); tabs.Children.Add(button);
        }
        Opened += (_, _) => { SizeToRoot(); XamlRoot.Changed += RootChanged; };
        Closed += (_, _) => XamlRoot.Changed -= RootChanged;
        if (parts.Count > 0) Render(0);
    }

    private void Render(int index)
    {
        for (var i = 0; i < buttons.Count; i++) buttons[i].IsChecked = i == index;
        body.Children.Clear(); var part = parts[index];
        if (agentMode) body.Children.Add(Text(DesktopResources.Get("AgentContextOmitted")));
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

    private static UIElement JsonTree(JsonElement value, string? name, int depth)
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
