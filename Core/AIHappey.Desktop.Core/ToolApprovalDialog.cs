using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AIHappey.Desktop.Core;

/// <summary>Read-only tool review. No output URL, HTML, widget or resource is executed or fetched.</summary>
public sealed class ToolApprovalDialog : ContentDialog, IResponsiveDialog
{
    private readonly StackPanel layout = new() { Spacing = 12 };
    private readonly ToolReviewTabs tabs = new("ToolApprovalTabs");
    private readonly TextBox reason = new() { Name = "ToolDenyReason", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
        MaxLength = 4000, MinHeight = 96, MaxHeight = 180, Visibility = Visibility.Collapsed };
    private readonly SplitButton automatic = new() { Name = "AutomaticToolApproval", Content = DesktopResources.Get("Automatic") };
    private readonly PendingToolApproval pending;
    private bool denying;
    public ToolApprovalDecision? Decision { get; private set; }

    public ToolApprovalDialog(PendingToolApproval pending)
    {
        this.pending = pending;
        Name = "ToolApprovalDialog"; Title = DesktopResources.Get("ToolApproval");
        PrimaryButtonText = DesktopResources.Get("Allow"); SecondaryButtonText = DesktopResources.Get("Deny");
        CloseButtonText = DesktopResources.Get("Stop"); DefaultButton = ContentDialogButton.None;
        Resources["ContentDialogMaxWidth"] = 840d; Resources["ContentDialogMinWidth"] = 0d;
        Content = layout;
        layout.Children.Add(new TextBlock { Text = pending.Title, TextWrapping = TextWrapping.Wrap, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        if (pending.Title != pending.ToolName) layout.Children.Add(Text(pending.ToolName));
        tabs.Add(DesktopResources.Get("Input"), pending.Part.TryGetProperty("input", out var value)
            ? ValueView(value) : Text(DesktopResources.Get("NoToolInput")), "ToolApprovalInput");
        tabs.Add(DesktopResources.Get("Output"), OutputView(pending.Part), "ToolApprovalOutput");
        tabs.SelectedIndex = 1;
        layout.Children.Add(tabs);
        reason.Header = DesktopResources.Get("DenyReason"); layout.Children.Add(reason);
        var menu = new MenuFlyout();
        var tool = new MenuFlyoutItem { Name = "AutoApproveThisTool", Text = DesktopResources.Format("AutoApproveTool", pending.ToolName), IsEnabled = pending.ToolName.Length > 0 };
        var all = new MenuFlyoutItem { Name = "AutoApproveAllTools", Text = DesktopResources.Get("AutoApproveAll"), Icon = new SymbolIcon(Symbol.Important) };
        tool.Click += (_, _) => CompleteAutomatic(ToolApprovalMode.ThisTool);
        all.Click += (_, _) => CompleteAutomatic(ToolApprovalMode.AllTools);
        menu.Items.Add(tool); menu.Items.Add(all); automatic.Flyout = menu;
        automatic.Click += (_, _) => CompleteAutomatic(ToolApprovalMode.ThisTool);
        automatic.IsEnabled = pending.ToolName.Length > 0;
        ToolbarControls.Label(automatic, DesktopResources.Get("AutomaticApprovalHint"));
        layout.Children.Add(automatic);
        foreach (var control in new Control[] { tabs, reason, automatic, tool, all }) ControlAppearance.Native(control);
        PrimaryButtonClick += (_, _) => Decision = denying ? new(false, reason.Text.Trim()) : new(true);
        SecondaryButtonClick += (_, args) =>
        {
            args.Cancel = true; denying = !denying;
            tabs.Visibility = automatic.Visibility = denying ? Visibility.Collapsed : Visibility.Visible;
            reason.Visibility = denying ? Visibility.Visible : Visibility.Collapsed;
            PrimaryButtonText = DesktopResources.Get(denying ? "Deny" : "Allow");
            SecondaryButtonText = DesktopResources.Get(denying ? "Cancel" : "Deny");
            Title = DesktopResources.Get(denying ? "ToolDeny" : "ToolApproval");
            if (denying) reason.Focus(FocusState.Programmatic);
        };
        Opened += (_, _) => { SizeToRoot(); XamlRoot.Changed += RootChanged; };
        Closed += (_, _) => XamlRoot.Changed -= RootChanged;
    }

    private void CompleteAutomatic(ToolApprovalMode mode)
    {
        if (denying || mode == ToolApprovalMode.ThisTool && pending.ToolName.Length == 0) return;
        Decision = new(true, mode == ToolApprovalMode.AllTools ? "BRRR" : pending.ToolName, mode); Hide();
    }

    private static UIElement OutputView(JsonElement part)
    {
        if (!DesktopToolApprovals.HasOutput(part))
            return Text(PortableConversations.String(part, "errorText") ?? DesktopResources.Get("NoToolOutput"));
        var output = part.GetProperty("output");
        if (output.ValueKind != JsonValueKind.Object) return ValueView(output);
        var sections = new ToolReviewTabs("ToolApprovalOutputTabs");
        if (output.TryGetProperty("structuredContent", out var structured) && structured.ValueKind != JsonValueKind.Null)
            sections.Add(DesktopResources.Get("StructuredContent"), ValueView(structured), "ToolStructuredOutput");
        if (output.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            var i = 0;
            foreach (var block in content.EnumerateArray())
            {
                var kind = PortableConversations.String(block, "type") ?? "unknown";
                var view = kind == "text" && PortableConversations.String(block, "text") is { } text
                    ? (UIElement)new ChatMarkdown { Text = text } : ValueView(block);
                var label = DesktopResources.Get(kind switch { "text" => "ToolContentText", "image" => "ToolContentImage",
                    "audio" => "ToolContentAudio", "resource" => "ToolContentResource", "resource_link" => "ToolContentLink", _ => "ToolContentOther" });
                sections.Add($"{label} {++i}", view, "ToolOutputBlock");
            }
        }
        if (sections.Count == 0) return ValueView(output);
        sections.Add(DesktopResources.Get("RawOutput"), ValueView(output), "ToolRawOutput");
        sections.SelectedIndex = 0;
        return sections;
    }

    private static TextBlock Text(string value) => new() { Text = value, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
    private static UIElement ValueView(JsonElement value) => Text(value.ValueKind == JsonValueKind.String
        ? value.GetString() ?? "" : JsonSerializer.Serialize(value, PortableConversations.Json));
    private void RootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => SizeToRoot();
    void IResponsiveDialog.SizeToRoot() => SizeToRoot();
    private void SizeToRoot()
    {
        if (XamlRoot is null) return;
        layout.Width = Math.Max(0, Math.Min(760, XamlRoot.Size.Width - 96));
        tabs.Height = Math.Max(80, Math.Min(420, XamlRoot.Size.Height - 330));
    }
}
