using System.Globalization;
using FluentIcons.Common;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AIHappey.Desktop.Core;

/// <summary>Show a reported generation cost without assuming a currency that metadata does not specify.</summary>
internal static class MediaCostPresentation
{
    public static string Format(double? cost) => cost?.ToString("F4", CultureInfo.CurrentCulture) ?? "";
    public static string Label(double? cost) => cost.HasValue ? DesktopResources.Format("GenerationCost", Format(cost)) : "";

    public static StackPanel Create(double cost, string name)
    {
        var row = new StackPanel { Name = name, Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(DesktopIcons.Create(Icon.Money, 14));
        row.Children.Add(new TextBlock { Text = Format(cost), FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        ToolbarControls.Label(row, Label(cost)); ToolTipService.SetToolTip(row, Label(cost));
        return row;
    }
}
