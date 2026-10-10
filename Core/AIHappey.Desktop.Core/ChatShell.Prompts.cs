using FluentIcons.Common;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AIHappey.Desktop.Core;

public sealed partial class ChatShell
{
    private readonly MenuFlyoutItem selectPrompts = new() { Name = "SelectMcpPrompts", Text = DesktopResources.Get("McpPrompts"),
        Icon = DesktopIcons.Create(Icon.TextBulletList), IsEnabled = false };
    private McpPromptsDialog? promptsDialog;

    private void UpdatePromptMenu()
    {
        selectPrompts.Visibility = Service == ServiceKind.Ai ? Visibility.Visible : Visibility.Collapsed;
        selectPrompts.IsEnabled = Service == ServiceKind.Ai && initialized && !busy && !closing && Mcp.HasPromptCapability;
    }

    private async Task SelectPromptsAsync()
    {
        if (!CanAddContext || Service != ServiceKind.Ai || promptsDialog is not null || !Mcp.HasPromptCapability) return;
        if (!targets.Any(t => t.Id == target.Text.Trim()))
        { Show(DesktopResources.Get("SelectTarget"), InfoBarSeverity.Warning); return; }
        var version = contextVersion; var partition = session.HistoryPartition;
        var dialog = new McpPromptsDialog(Mcp.CapturePrompts()) { XamlRoot = XamlRoot };
        SystemAppearance.PrepareDialog(dialog); promptsDialog = dialog; historyDialogOpen = true;
        try { await dialog.ShowAsync(); }
        finally { promptsDialog = null; historyDialogOpen = false; }
        if (IsCurrentContext(version, partition) && Service == ServiceKind.Ai && dialog.Selection is { } selected)
            await SendAsync(selected);
        if (!closing) input.Focus(FocusState.Programmatic);
    }
}
