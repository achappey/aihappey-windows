using CommunityToolkit.WinUI.Controls;
using FluentIcons.Common;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AIHappey.Desktop.Core;

/// <summary>The same native, logo-led settings row in MCP management and discovery.</summary>
internal sealed class McpServerRow
{
    public SettingsExpander View { get; }
    public StackPanel Details { get; } = new() { Spacing = 12, Padding = new Thickness(16) };
    public StackPanel Actions { get; } = new() { Orientation = Orientation.Horizontal, Spacing = 8 };

    public McpServerRow(Panel parent, string name, string id, string title, IReadOnlyList<McpIcon> icons,
        UIElement? headerAction, HashSet<string> expanded, string? expansionKey = null)
    {
        View = NativeSettingsSurface.Expander(parent, name, title, headerAction, out var body);
        View.Tag = id; AutomationProperties.SetAutomationId(View, name + "_" + id);
        var heading = new Grid { ColumnSpacing = 12 };
        heading.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        heading.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        heading.Children.Add(new McpServerIcon(icons, 32));
        var label = new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(label, 1); heading.Children.Add(label); View.Header = heading;
        var key = expansionKey ?? id;
        View.IsExpanded = expanded.Contains(key);
        View.Expanded += (_, _) => expanded.Add(key);
        View.Collapsed += (_, _) => expanded.Remove(key);
        body.Padding = new Thickness(0); body.Spacing = 0;
        body.Children.Add(Details);
        var footer = new Border { Name = "McpServerRowFooter", Child = Actions,
            Padding = new Thickness(12, 8, 12, 8), BorderThickness = new Thickness(0, 1, 0, 0) };
        NativeCardSurface.Divider(footer); body.Children.Add(footer);
    }

    public void Text(string? value, bool small = false, bool selectable = false)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        var text = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = selectable };
        NativeCardSurface.Secondary(text, small); Details.Children.Add(text);
    }

    public Button Action(string name, string id, string label, Icon icon, Action action, bool enabled = true)
    {
        var button = new Button { Name = name, Tag = View.Tag, Content = DesktopIcons.Create(icon),
            Width = 32, Height = 32, Padding = new Thickness(6), IsEnabled = enabled };
        ControlAppearance.Stock(button); ToolbarControls.Label(button, label);
        AutomationProperties.SetAutomationId(button, id);
        button.Click += (_, _) => action(); Actions.Children.Add(button); return button;
    }

    public static DesktopMcpServer? Installed(IEnumerable<DesktopMcpServer> servers, McpCatalogItem item) =>
        servers.FirstOrDefault(server => server.Id.Equals(item.Id, StringComparison.OrdinalIgnoreCase)
            || server.Name.Equals(item.Name, StringComparison.OrdinalIgnoreCase)
            || server.Url.Equals(item.Url, StringComparison.OrdinalIgnoreCase));

    public static McpServerRow Catalog(Panel parent, string name, string prefix, McpCatalogItem item,
        DesktopMcpServer? existing, HashSet<string> expanded, Action<bool> change, Action configure)
    {
        var row = new McpServerRow(parent, name, item.Id, item.Name,
            existing is { Icons.Count: > 0 } ? existing.Icons : item.Icons, null, expanded, "catalog/" + item.Id);
        row.Text(item.Version, small: true); row.Text(item.Description); row.Text(item.Url, selectable: true);
        var install = existing is null;
        var key = install ? "McpInstall" : "McpRemove";
        row.Action(prefix + (install ? "Install" : "Remove"), prefix + "Action_" + item.Id,
            DesktopResources.Format("ActionForItem", DesktopResources.Get(key), item.Name),
            install ? Icon.ArrowDownload : Icon.Delete, () => change(install), existing?.PluginId is null);
        if (existing is not null && existing.PluginId is null)
            row.Action(prefix + "Configure", prefix + "Configure_" + item.Id,
                DesktopResources.Format("ActionForItem", DesktopResources.Get("Edit"), item.Name), Icon.Edit, configure);
        return row;
    }
}

/// <summary>Presentation state survives sequential native dialogs without retaining closed controls.</summary>
internal sealed class McpDialogState
{
    public HashSet<string> Expanded { get; } = [];
    public string Query { get; set; } = "";
    public int Visible { get; set; } = 20;
    public double Offset { get; set; }
    public string? FocusId { get; set; }
    public McpCatalogResult? Catalog { get; set; }
}
