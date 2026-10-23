using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Charts;
using Microsoft.UI.Xaml.Media;

namespace AIHappey.Desktop.Core;

internal sealed record UsageChartSeries(string Label, IReadOnlyList<double> Values);

/// <summary>Small app-owned presentation helpers, not a generic chart specification/JSON renderer.</summary>
internal static class UsageDashboardVisuals
{
    internal static string Text(string key) => DesktopResources.Get("Usage" + key);

    internal static TextBlock Label(string text, double size = 14) => new()
    { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };

    internal static Border Card(string title, UIElement content, string name = "UsageCard")
    {
        var heading = Label(title, 16); heading.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level2);
        var body = new StackPanel { Spacing = 12 }; body.Children.Add(heading); body.Children.Add(content);
        var card = new Border { Name = name, Child = body, Padding = new Thickness(16), VerticalAlignment = VerticalAlignment.Top };
        NativeCardSurface.Card(card); return card;
    }

    internal static Border Kpi(string key, string value)
    {
        var text = Label(value, 28); text.Name = "UsageKpiValue"; text.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        return Card(Text(key), text, "UsageKpi" + key);
    }

    internal static OverviewCardsPanel Grid(params UIElement[] elements)
    { var grid = new OverviewCardsPanel(); foreach (var element in elements) grid.Children.Add(element); return grid; }

    internal static Border ChartCard(string title, IReadOnlyList<string> labels, IReadOnlyList<UsageChartSeries> datasets,
        bool line = false, bool horizontal = false, bool positiveOnly = false)
    {
        if (labels.Count == 0 || !datasets.Any(s => s.Values.Any(v => double.IsFinite(v) && (!positiveOnly || v > 0))))
            return Card(title, Label(Text("NoData")), "UsageChartCard");
        if (datasets.Any(s => s.Values.Count != labels.Count)) throw new ArgumentException("Chart categories and values must align.");
        var chart = new Chart { Name = "UsageChart", Height = 320, MinWidth = 0, ShowLegend = datasets.Count > 1,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), FontSize = 12, HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(chart, title);
        // Categorical days intentionally preserve the web's sparse recorded-day buckets (no fabricated zero days).
        var categories = new CategoryAxis { ShowTickLabels = !line || labels.Count <= 16 };
        var numbers = new LinearAxis { Minimum = 0, GridLines = GridLines.Major };
        chart.Axes.Add(categories); chart.Axes.Add(numbers);
        var shortLabels = labels.Select((s, i) => s.Length > 28 ? s[..25] + "… " + (i + 1) : s).ToList();
        foreach (var data in datasets)
        {
            CartesianSeries series = line ? new LineSeries() : new BarSeries { Orientation = horizontal ? BarOrientation.Horizontal : BarOrientation.Vertical };
            series.Title = data.Label; series.XAxis = categories; series.YAxis = numbers;
            series.XValues = new Samples { ItemsSource = shortLabels };
            series.YValues = new Samples { ItemsSource = data.Values.ToList() };
            chart.Series.Add(series);
        }
        NativeChartAppearance.Apply(chart);
        var body = new StackPanel { Spacing = 12 }; body.Children.Add(chart);
        // Native charts currently have no hover tooltips. Always provide exact, selectable, paginated values.
        var details = new Expander { Name = "UsageChartValues", Header = Text("ChartValues"), HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch };
        ControlAppearance.Native(details);
        details.Expanding += (_, _) =>
        {
            details.Content ??= TableBody([Text("Category"), .. datasets.Select(s => s.Label)],
                labels.Select((label, i) => new[] { label }.Concat(datasets.Select(s => UsageFormatting.Metric(s.Values[i]))).ToArray()).ToArray());
        };
        body.Children.Add(details); return Card(title, body, "UsageChartCard");
    }

    internal static Border Table(string title, string[] headers, IReadOnlyList<string[]> rows) => Card(title, TableBody(headers, rows), "UsageTable");

    private static UIElement TableBody(string[] headers, IReadOnlyList<string[]> rows)
    {
        if (rows.Count == 0) return Label(Text("NoData"));
        const int pageSize = 15; int page = 0;
        var body = new StackPanel { Name = "UsageTableBody", Spacing = 8 };
        var content = new StackPanel { Spacing = 0 };
        var previous = new Button { Name = "UsagePrevious", Content = Text("Previous") };
        var next = new Button { Name = "UsageNext", Content = Text("Next") };
        var position = Label(""); AutomationProperties.SetLiveSetting(position, AutomationLiveSetting.Polite);
        var pager = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, HorizontalAlignment = HorizontalAlignment.Right };
        ControlAppearance.Native(previous); ControlAppearance.Native(next);
        pager.Children.Add(previous); pager.Children.Add(position); pager.Children.Add(next);
        UIElement Row(string[] values, bool header)
        {
            var row = new Microsoft.UI.Xaml.Controls.Grid { ColumnSpacing = 8, Padding = new Thickness(0, 8, 0, 8), Name = header ? "UsageTableHeader" : "UsageTableRow" };
            for (var c = 0; c < headers.Length; c++)
            {
                row.ColumnDefinitions.Add(new() { Width = new GridLength(c == 0 ? 2 : 1, GridUnitType.Star) });
                var cell = Label(c < values.Length ? values[c] : "—", 13);
                if (header) cell.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
                else AutomationProperties.SetName(cell, headers[c] + ": " + cell.Text);
                Microsoft.UI.Xaml.Controls.Grid.SetColumn(cell, c); row.Children.Add(cell);
            }
            return row;
        }
        void Render()
        {
            content.Children.Clear(); content.Children.Add(Row(headers, true));
            foreach (var row in rows.Skip(page * pageSize).Take(pageSize)) content.Children.Add(Row(row, false));
            var pages = Math.Max(1, (rows.Count + pageSize - 1) / pageSize);
            position.Text = $"{page + 1} / {pages}";
            previous.IsEnabled = page > 0; next.IsEnabled = page + 1 < pages;
            pager.Visibility = pages > 1 ? Visibility.Visible : Visibility.Collapsed;
        }
        previous.Click += (_, _) => { if (page > 0) { page--; Render(); } };
        next.Click += (_, _) => { if ((page + 1) * pageSize < rows.Count) { page++; Render(); } };
        body.Children.Add(content); body.Children.Add(pager); Render(); return body;
    }
}
