using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls.Charts;

namespace AIHappey.Desktop.Core;

/// <summary>A deliberately small data-only projection of native chart properties, not a general XAML/object loader.</summary>
internal static class ChatChartJson
{
    private const int MaxJsonLength = 128 * 1024, MaxSamples = 1024, MaxTotalSamples = 4096;
    private static readonly JsonSerializerOptions Options = new()
    {
        MaxDepth = 16, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter<GridLines>(allowIntegerValues: false),
            new JsonStringEnumConverter<BarOrientation>(allowIntegerValues: false) }
    };

    // These records mirror the supported native properties. Type is only a concrete-type discriminator;
    // references are axis indexes because native object references cannot be represented directly in JSON.
    private sealed record ChartData(string? Type, AxisData[]? Axes, SeriesData[]? Series,
        bool ShowLegend = false, string? LegendTitle = null);
    private sealed record AxisData(string? Type, string? Label = null, bool ShowTickLabels = true,
        GridLines GridLines = GridLines.None, double? Minimum = null, double? Maximum = null);
    private sealed record SeriesData(string? Type, int? XAxis, int? YAxis, SamplesData? XValues, SamplesData? YValues,
        string? Title = null, BarOrientation? Orientation = null);
    private sealed record SamplesData(JsonElement[]? ItemsSource);

    internal static Chart? TryCreate(string json)
    {
        if (json.Length > MaxJsonLength) return null;
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            if (!UniqueProperties(document.RootElement)) return null;
            var data = document.RootElement.Deserialize<ChartData>(Options);
            if (data is null || !Valid(data)) return null;

            var chart = new Chart { Name = "MessageChart", Height = 320, MinWidth = 0,
                HorizontalAlignment = HorizontalAlignment.Stretch, ShowLegend = data.ShowLegend,
                LegendTitle = data.LegendTitle ?? "" };
            AutomationProperties.SetName(chart, data.LegendTitle ?? DesktopResources.Get("Chart"));
            foreach (var axis in data.Axes!)
            {
                CartesianAxis native = axis.Type == "CategoryAxis" ? new CategoryAxis()
                    : new LinearAxis { Minimum = axis.Minimum, Maximum = axis.Maximum };
                native.Label = axis.Label ?? ""; native.ShowTickLabels = axis.ShowTickLabels; native.GridLines = axis.GridLines;
                chart.Axes.Add(native);
            }
            foreach (var series in data.Series!)
            {
                CartesianSeries native = series.Type == "LineSeries" ? new LineSeries()
                    : new BarSeries { Orientation = series.Orientation ?? BarOrientation.Vertical };
                native.Title = series.Title ?? "";
                native.XAxis = (CartesianAxis)chart.Axes[series.XAxis!.Value];
                native.YAxis = (CartesianAxis)chart.Axes[series.YAxis!.Value];
                native.XValues = new Samples { ItemsSource = series.XValues!.ItemsSource!.Select(v => v.GetString()!).ToList() };
                native.YValues = new Samples { ItemsSource = series.YValues!.ItemsSource!.Select(v => v.GetDouble()).ToList() };
                chart.Series.Add(native);
            }
            NativeChartAppearance.Apply(chart);
            return chart;
        }
        catch (Exception error)
        {
            // AI data is optional presentation. Never replace the original code on a recoverable failure.
            System.Diagnostics.Trace.WriteLine($"Chart preview unavailable: {error.GetType().Name}");
            return null;
        }
    }

    private static bool Valid(ChartData data)
    {
        if (data.Type is not (null or "Chart") || !Text(data.LegendTitle)
            || data.Axes is not { Length: >= 2 and <= 4 } axes || data.Series is not { Length: >= 1 and <= 8 } series) return false;
        foreach (var axis in axes)
            if (axis is null || axis.Type is not ("CategoryAxis" or "LinearAxis") || !Text(axis.Label)
                || !Enum.IsDefined(axis.GridLines) || !Number(axis.Minimum) || !Number(axis.Maximum)
                || axis.Minimum is { } min && axis.Maximum is { } max && min >= max
                || axis.Type == "CategoryAxis" && (axis.Minimum.HasValue || axis.Maximum.HasValue)) return false;
        var total = 0;
        var categories = new Dictionary<int, string[]>();
        foreach (var item in series)
        {
            if (item is null || item.Type is not ("LineSeries" or "BarSeries") || !Text(item.Title)
                || item.XAxis is not { } x || item.YAxis is not { } y || x < 0 || x >= axes.Length || y < 0 || y >= axes.Length || x == y
                || axes[y].Type != "LinearAxis" || axes[x].Type != "CategoryAxis"
                || item.Orientation is { } orientation && (item.Type != "BarSeries" || !Enum.IsDefined(orientation))
                || item.XValues?.ItemsSource is not { Length: >= 1 and <= MaxSamples } xs
                || item.YValues?.ItemsSource is not { } ys || xs.Length != ys.Length
                || (total += ys.Length) > MaxTotalSamples || ys.Any(v => !Number(v))) return false;
            if (xs.Any(v => v.ValueKind != JsonValueKind.String || !Text(v.GetString()))) return false;
            var labels = xs.Select(v => v.GetString()!).ToArray();
            // Shared category axes must not silently realign differently ordered datasets.
            if (labels.Distinct(StringComparer.Ordinal).Count() != labels.Length
                || categories.TryGetValue(x, out var existing) && !existing.SequenceEqual(labels)) return false;
            categories[x] = labels;
        }
        return true;
    }

    private static bool Text(string? value) => value is null || value.Length <= 256;
    private static bool Number(double? value) => value is null || double.IsFinite(value.Value) && Math.Abs(value.Value) <= 1e12;
    private static bool Number(JsonElement value) => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && Number(number);

    private static bool UniqueProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
                if (!names.Add(property.Name) || !UniqueProperties(property.Value)) return false;
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) if (!UniqueProperties(item)) return false;
        return true;
    }
}
