using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AIHappey.Desktop.Core;

/// <summary>Safe, read-only presentation shared by approvals and conversation activity.</summary>
internal static class ToolOutputView
{
    public static UIElement Create(JsonElement part, string tabsName = "ToolOutputTabs")
    {
        if (!DesktopToolApprovals.HasOutput(part))
            return Text(PortableConversations.String(part, "errorText") ?? DesktopResources.Get("NoToolOutputResult"));
        var output = part.GetProperty("output");
        if (output.ValueKind != JsonValueKind.Object) return Value(output);
        var sections = new ToolReviewTabs(tabsName);
        if (output.TryGetProperty("structuredContent", out var structured) && structured.ValueKind != JsonValueKind.Null)
            sections.Add(DesktopResources.Get("StructuredContent"), Value(structured), "ToolStructuredOutput");
        if (output.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            var i = 0;
            foreach (var block in content.EnumerateArray())
            {
                var kind = PortableConversations.String(block, "type") ?? "unknown";
                var view = kind == "text" && PortableConversations.String(block, "text") is { } text
                    ? (UIElement)new ChatMarkdown { Text = text } : Value(block);
                var label = DesktopResources.Get(kind switch { "text" => "ToolContentText", "image" => "ToolContentImage",
                    "audio" => "ToolContentAudio", "resource" => "ToolContentResource", "resource_link" => "ToolContentLink", _ => "ToolContentOther" });
                sections.Add($"{label} {++i}", view, "ToolOutputBlock");
            }
        }
        if (sections.Count == 0) return Value(output);
        sections.Add(DesktopResources.Get("RawOutput"), Value(output), "ToolRawOutput");
        sections.SelectedIndex = 0;
        return sections;
    }

    public static TextBlock Text(string value) => new() { Text = value, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
    public static UIElement Value(JsonElement value) => Text(value.ValueKind == JsonValueKind.String
        ? value.GetString() ?? "" : JsonSerializer.Serialize(value, PortableConversations.Json));
}

public sealed class ToolOutputDialog : ContentDialog, IResponsiveDialog
{
    private readonly Grid layout = new();

    public ToolOutputDialog(string toolName, JsonElement part)
    {
        Name = "ToolOutputDialog";
        Title = DesktopResources.Format("ToolOutputTitle", toolName);
        CloseButtonText = DesktopResources.Get("Close");
        DefaultButton = ContentDialogButton.Close;
        Resources["ContentDialogMaxWidth"] = 840d;
        Resources["ContentDialogMinWidth"] = 0d;
        layout.Children.Add(ToolOutputView.Create(part));
        Content = layout;
        Opened += (_, _) => { SizeToRoot(); XamlRoot.Changed += RootChanged; };
        Closed += (_, _) => XamlRoot.Changed -= RootChanged;
    }

    private void RootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => SizeToRoot();
    void IResponsiveDialog.SizeToRoot() => SizeToRoot();
    private void SizeToRoot()
    {
        if (XamlRoot is null) return;
        layout.Width = Math.Max(0, Math.Min(760, XamlRoot.Size.Width - 96));
        layout.Height = Math.Max(80, Math.Min(520, XamlRoot.Size.Height - 240));
    }
}
