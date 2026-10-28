using System.Globalization;
using AIHappey.Desktop.Core;
using FluentIcons.Common;
using FluentIcons.WinUI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AIHappey.Desktop.UiTests;

public partial class App
{
    private void CheckMediaCost(DependencyObject parent, string name, double? expected, string context)
    {
        var rows = Descendants(parent).OfType<StackPanel>().Where(p => p.Name == name).ToArray();
        if (expected is not { } cost)
        {
            Check(rows.All(row => row.Visibility == Visibility.Collapsed), context + ": absent cost hides both icon and amount"); return;
        }
        var row = rows.Single(); var amount = cost.ToString("F4", CultureInfo.CurrentCulture);
        Check(row.Visibility == Visibility.Visible && row.Children.OfType<FluentIcon>().Single().Icon == Icon.Money
            && row.Children.OfType<TextBlock>().Single().Text == amount
            && AutomationProperties.GetName(row) == DesktopResources.Format("GenerationCost", amount), context + ": existing chat money icon and accessible four-decimal amount, without currency assumptions");
    }
}
