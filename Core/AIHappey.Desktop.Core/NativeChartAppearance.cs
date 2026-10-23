using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls.Charts;
using Microsoft.UI.Xaml.Media;

namespace AIHappey.Desktop.Core;

/// <summary>The same theme-aware native series palette for dashboard and conversation charts.</summary>
internal static class NativeChartAppearance
{
    internal static void Apply(Chart chart)
    {
        chart.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        void Theme()
        {
            var colors = chart.ActualTheme == ElementTheme.Dark
                ? new[] { 0x80B4FFu, 0x50D8D0u, 0x9BD98Bu, 0xFFD479u, 0xEAA1DDu, 0xFF9F96u, 0xB7A5FFu, 0x96D8F5u }
                : new[] { 0x245EB5u, 0x087F8Cu, 0x327F35u, 0x966000u, 0x9D3688u, 0xBD382Du, 0x6941B5u, 0x006B99u };
            for (var i = 0; i < chart.Series.Count; i++)
            {
                var color = colors[i % colors.Length];
                var brush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, (byte)(color >> 16), (byte)(color >> 8), (byte)color));
                chart.Series[i].Stroke = brush;
                if (chart.Series[i] is BarSeries bars) bars.Fill = brush;
            }
        }
        chart.Loaded += (_, _) => Theme(); chart.ActualThemeChanged += (_, _) => Theme();
        Theme();
    }
}
