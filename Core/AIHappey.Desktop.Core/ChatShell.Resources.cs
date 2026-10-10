using FluentIcons.WinUI;
using FluentIcons.Common;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AIHappey.Desktop.Core;

public sealed partial class ChatShell
{
    private readonly MenuFlyoutItem selectResources = new() { Name = "SelectMcpResources", Text = DesktopResources.Get("McpResources"),
        Icon = DesktopIcons.Create(Icon.Folder), IsEnabled = false };
    private readonly List<McpSelectedResource> selectedResources = [];
    private McpResourcesDialog? resourcesDialog;

    private void UpdateResourceMenu()
    {
        selectResources.Visibility = Service == ServiceKind.Ai ? Visibility.Visible : Visibility.Collapsed;
        selectResources.IsEnabled = Service == ServiceKind.Ai && initialized && !busy && !closing && Mcp.Capture().Resources.Count > 0;
    }

    private async Task SelectResourcesAsync()
    {
        if (!CanAddContext || Service != ServiceKind.Ai || resourcesDialog is not null) return;
        var entries = Mcp.Capture().Resources;
        if (entries.Count == 0) return;
        var version = contextVersion; var partition = session.HistoryPartition;
        var dialog = new McpResourcesDialog(entries) { XamlRoot = XamlRoot };
        SystemAppearance.PrepareDialog(dialog); resourcesDialog = dialog; historyDialogOpen = true;
        try
        {
            await dialog.ShowAsync();
            if (!IsCurrentContext(version, partition) || dialog.Selection is not { } selected) return;
            // Browser selection uses the resolved URI as its draft identity; preserve that replacement behavior.
            selectedResources.RemoveAll(r => r.Uri == selected.Uri);
            selectedResources.Add(selected); RenderContextTags();
        }
        finally
        {
            resourcesDialog = null; historyDialogOpen = false;
            if (!closing) input.Focus(FocusState.Programmatic);
        }
    }

    private void RenderResourceTags()
    {
        foreach (var resource in selectedResources)
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            content.Children.Add(new FluentIcon { Icon = Icon.Folder, FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
            content.Children.Add(new TextBlock { Text = resource.Name, MaxWidth = 260, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
            var remove = new Button { Name = "RemoveMcpResource", Content = DesktopIcons.Create(Icon.Dismiss, 10),
                Width = 24, Height = 24, Padding = new Thickness(0), IsEnabled = !busy };
            ToolbarControls.Subtle(remove); ToolbarControls.Label(remove, DesktopResources.Format("RemoveContext", resource.Name));
            remove.Click += (_, _) =>
            {
                if (busy || closing) return;
                selectedResources.Remove(resource); RenderContextTags(); input.Focus(FocusState.Programmatic);
            };
            content.Children.Add(remove);
            var tag = new Border { Name = "McpResourceTag", Child = content, Padding = new Thickness(10, 2, 4, 2), CornerRadius = new CornerRadius(16) };
            ControlAppearance.TokenBadge(tag); ToolbarControls.Label(tag, resource.Name + "\n" + resource.Uri); contextTags.Children.Add(tag);
        }
    }
}
