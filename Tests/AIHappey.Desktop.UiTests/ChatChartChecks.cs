using System.Reflection;
using System.Text.Json.Nodes;
using AIHappey.Desktop.Core;
using AIHappey.Vercel.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Charts;
using Microsoft.UI.Xaml.Media;

namespace AIHappey.Desktop.UiTests;

public partial class App
{
    private const string NativeChartFixture = """
        {
          "Axes": [{ "Type": "CategoryAxis", "Label": "Month" }, { "Type": "LinearAxis", "Minimum": 0 }],
          "Series": [{ "Type": "BarSeries", "Title": "Sales", "XAxis": 0, "YAxis": 1,
            "XValues": { "ItemsSource": ["Jan", "Feb", "Mar"] }, "YValues": { "ItemsSource": [12, 24, 18] } }],
          "ShowLegend": true
        }
        """;

    private async Task CheckChatChartsAsync(ElementTheme theme)
    {
        var context = "Chat charts / " + theme;
        var create = typeof(ChatMarkdown).Assembly.GetType("AIHappey.Desktop.Core.ChatChartJson")!
            .GetMethod("TryCreate", BindingFlags.NonPublic | BindingFlags.Static)!;
        Chart? Parse(string json) => (Chart?)create.Invoke(null, [json]);
        var baselineRoot = new Grid { RequestedTheme = theme };
        var baseline = Parse(NativeChartFixture)!; baselineRoot.Children.Add(baseline);
        window!.Content = baselineRoot; window.Activate(); await Task.Delay(1200);
        await CheckChartPixelsAsync(baseline, context + ": standalone chart baseline");
        baselineRoot.Children.Clear();
        var baselineTabs = (UserControl)Activator.CreateInstance(typeof(ChatMarkdown).Assembly.GetType("AIHappey.Desktop.Core.ToolReviewTabs")!, ["ChartBaselineTabs"])!;
        baselineTabs.GetType().GetMethod("Add")!.Invoke(baselineTabs, ["Chart", baseline, "BaselineChartTab"]);
        baselineTabs.GetType().GetMethod("Add")!.Invoke(baselineTabs, ["Code", new TextBlock { Text = NativeChartFixture }, "BaselineCodeTab"]);
        baselineTabs.GetType().GetProperty("SelectedIndex")!.SetValue(baselineTabs, 0);
        baselineRoot.Children.Add(baselineTabs); await Task.Delay(1000);
        await CheckChartPixelsAsync(baseline, context + ": native tabs outside Markdown baseline");
        var preview = new ChatMarkdown { ChartsEnabled = true, Text = "```winui-chart\n" + NativeChartFixture + "\n```" };
        var previewRoot = new Grid { RequestedTheme = theme, Padding = new Thickness(24) };
        previewRoot.Children.Add(preview);
        window!.Content = previewRoot; window.Activate();
        await Task.Delay(180); previewRoot.UpdateLayout(); await Task.Delay(60);
        Check(Descendants(preview).OfType<Chart>().Any(c => c.Name == "MessageChart" && c.ActualHeight > 0),
            context + ": a finished chart fence produces a visible native chart in the actual Markdown control");
        var initialChart = Descendants(preview).OfType<Chart>().Single();
        await CheckChartPixelsAsync(initialChart, context + ": initial plot");
        await Task.Delay(1000);
        await CheckChartPixelsAsync(initialChart, context + ": settled plot");
        var lines = JsonNode.Parse(NativeChartFixture)!.AsObject();
        lines["Series"]![0]!["Type"] = "LineSeries";
        var secondLine = lines["Series"]![0]!.DeepClone(); secondLine["Title"] = "Second series";
        secondLine["YValues"]!["ItemsSource"] = new JsonArray(8, 11, 15); lines["Series"]!.AsArray().Add(secondLine);
        preview.Text = "```winui-chart\n" + lines.ToJsonString() + "\n```";
        await Task.Delay(1000);
        var lineChart = Descendants(preview).OfType<Chart>().Single();
        await CheckChartPixelsAsync(lineChart, context + ": two line series remain drawn after updating the same Markdown control");
        var lineTabs = Descendants(preview).OfType<NavigationView>().Single(n => n.Name == "MessageChartTabsNavigation");
        for (var repeat = 0; repeat < 2; repeat++)
        {
            lineTabs.SelectedItem = lineTabs.MenuItems[1]; await Task.Delay(100);
            lineTabs.SelectedItem = lineTabs.MenuItems[0]; await Task.Delay(700);
            await CheckChartPixelsAsync(lineChart, context + $": two line series remain drawn after tab roundtrip {repeat + 1}");
        }
        window.Content = null;
        string Change(Action<JsonObject> edit)
        { var node = JsonNode.Parse(NativeChartFixture)!.AsObject(); edit(node); return node.ToJsonString(); }
        JsonObject Series(JsonObject node) => node["Series"]![0]!.AsObject();
        Check(Parse(NativeChartFixture) is { Axes.Count: 2, Series.Count: 1, ShowLegend: true } native
            && native.Series[0] is BarSeries { Orientation: BarOrientation.Vertical }
            && ReferenceEquals(native.Series[0].XAxis, native.Axes[0])
            && ((IEnumerable<double>)native.Series[0].YValues.ItemsSource).SequenceEqual([12d, 24d, 18d]),
            context + ": native properties and indexed axes map to native controls and typed Samples");
        Check(Parse(Change(n => { n["Type"] = "Chart"; Series(n)["Type"] = "LineSeries"; }))?.Series[0] is LineSeries,
            context + ": line charts and optional native root discriminator are supported");
        Check(Parse(Change(n => Series(n)["Orientation"] = "Horizontal"))?.Series[0] is BarSeries { Orientation: BarOrientation.Horizontal },
            context + ": native horizontal bar orientation is supported");
        Check(Parse(Change(n => { n["Axes"]![0]!["Type"] = "LinearAxis"; Series(n)["Type"] = "LineSeries";
            Series(n)["XValues"]!["ItemsSource"] = new JsonArray(1, 2, 3); })) is null,
            context + ": numeric X axes incompatible with the current native series safely fall back");
        Check(Parse(Change(n => n["Series"]!.AsArray().Add(Series(n).DeepClone())))?.Series.Count == 2,
            context + ": multiple series share the same native axes");
        var invalid = new Dictionary<string, string>
        {
            ["malformed JSON"] = "{not JSON}", ["null root"] = "null", ["array root"] = "[]",
            ["missing axes/series"] = "{}", ["comments"] = "/*comment*/" + NativeChartFixture,
            ["duplicate properties"] = NativeChartFixture.Replace("\"ShowLegend\": true", "\"ShowLegend\": false, \"ShowLegend\": true"),
            ["oversized JSON"] = new string(' ', 128 * 1024) + NativeChartFixture,
            ["unknown root property"] = Change(n => n["Source"] = "https://untrusted.invalid/"),
            ["arbitrary type activation"] = Change(n => Series(n)["Type"] = "System.IO.FileInfo"),
            ["unsupported chart"] = Change(n => Series(n)["Type"] = "PieSeries"),
            ["unknown axis"] = Change(n => n["Axes"]![0]!["Type"] = "LogarithmicAxis"),
            ["negative axis reference"] = Change(n => Series(n)["XAxis"] = -1),
            ["out-of-range axis reference"] = Change(n => Series(n)["YAxis"] = 5),
            ["missing axis reference"] = Change(n => Series(n).Remove("XAxis")),
            ["same X and Y axis"] = Change(n => Series(n)["YAxis"] = 0),
            ["category Y axis"] = Change(n => n["Axes"]![1]!["Type"] = "CategoryAxis"),
            ["mismatched samples"] = Change(n => Series(n)["YValues"]!["ItemsSource"] = new JsonArray(1)),
            ["null samples"] = Change(n => Series(n)["YValues"] = null),
            ["empty samples"] = Change(n => Series(n)["YValues"]!["ItemsSource"] = new JsonArray()),
            ["string numeric sample"] = Change(n => Series(n)["YValues"]!["ItemsSource"]![0] = "12"),
            ["nonfinite numeric sample"] = NativeChartFixture.Replace("[12, 24, 18]", "[1e999, 24, 18]"),
            ["extreme numeric sample"] = Change(n => Series(n)["YValues"]!["ItemsSource"]![0] = 1e100),
            ["unknown enum"] = Change(n => Series(n)["Orientation"] = "Diagonal"),
            ["numeric enum"] = Change(n => Series(n)["Orientation"] = 1),
            ["line orientation"] = Change(n => { Series(n)["Type"] = "LineSeries"; Series(n)["Orientation"] = "Horizontal"; }),
            ["inverted axis bounds"] = Change(n => { n["Axes"]![1]!["Minimum"] = 5; n["Axes"]![1]!["Maximum"] = 2; }),
            ["unknown sample property"] = Change(n => Series(n)["YValues"]!["Binding"] = "unsafe"),
            ["null axis"] = Change(n => n["Axes"]![0] = null),
            ["null series"] = Change(n => n["Series"]![0] = null),
            ["reordered shared categories"] = Change(n => { var other = Series(n).DeepClone();
                other["XValues"]!["ItemsSource"] = new JsonArray("Mar", "Jan", "Feb"); n["Series"]!.AsArray().Add(other); }),
            ["too many axes"] = Change(n => { for (var i = 0; i < 3; i++) n["Axes"]!.AsArray().Add(new JsonObject { ["Type"] = "LinearAxis" }); }),
            ["too many series"] = Change(n => { for (var i = 0; i < 8; i++) n["Series"]!.AsArray().Add(Series(n).DeepClone()); }),
            ["too many samples"] = Change(n => { Series(n)["XValues"]!["ItemsSource"] = new JsonArray(Enumerable.Range(0, 1025).Select(i => (JsonNode?)JsonValue.Create(i.ToString())).ToArray());
                Series(n)["YValues"]!["ItemsSource"] = new JsonArray(Enumerable.Range(0, 1025).Select(i => (JsonNode?)JsonValue.Create(i)).ToArray()); }),
            ["excessive depth"] = new string('[', 20) + "0" + new string(']', 20)
        };
        foreach (var (label, json) in invalid) Check(Parse(json) is null, context + ": safely rejects " + label);

        var session = new DesktopSession(new UiHost(true), new UiRuntime(), new());
        var shell = new ChatShell(session) { RequestedTheme = theme };
        var fence = "```winui-chart\n" + NativeChartFixture + "\n```";
        var source = "**Before chart**\n\n[Reference link][target]\n\n" + fence
            + "\n\n| Name | Value |\n| --- | --- |\n| Example | 42 |\n\n**After chart**\n\n[target]: https://example.com/\n\n![Blocked](https://untrusted.invalid/image.png)";
        var message = new ConversationMessage { Status = "streaming", Message = new UIMessage { Id = "chart-assistant", Role = Role.assistant,
            Metadata = [], Parts = [new ReasoningUIPart { Text = fence }, new TextUIPart { Text = source }] } };
        var conversation = new Conversation { Target = "test-agent", Service = ServiceKind.Agents, Messages =
            [new() { Status = "complete", Message = new UIMessage { Id = "chart-user", Role = Role.user, Metadata = [], Parts = [new TextUIPart { Text = fence }] } }, message] };
        try
        {
            window!.Content = shell; window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 900)); window.Activate();
            for (var attempt = 0; attempt < 100 && !Field<bool>(shell, "initialized"); attempt++) await Task.Delay(20);
            await HistoryIdleAsync(shell);
            await (Task)Call(shell, "OpenConversationAsync", conversation)!;
            await Task.Delay(120); shell.UpdateLayout();
            var transcript = Field<StackPanel>(shell, "transcript");
            Chart[] Charts() => Descendants(transcript).OfType<Chart>().Where(c => c.Name == "MessageChart").ToArray();
            ChatMarkdown Answer() => Descendants(transcript).OfType<ChatMarkdown>().Last(m => m.Name == "MessageMarkdown");
            Check(Charts().Length == 0 && !Answer().ChartsEnabled, context + ": no previews while streaming, including syntactically complete fences");
            var streamingMarkdown = Answer();
            message.Status = "complete"; Call(shell, "RenderTranscript"); await Task.Delay(180); shell.UpdateLayout();
            Check(ReferenceEquals(streamingMarkdown, Answer()) && Charts().Length == 1 && Answer().ChartsEnabled,
                context + ": unchanged-text completion enables one assistant chart without replacing the Markdown control");
            Check(Descendants(transcript).OfType<ChatMarkdown>().Where(m => m.Name == "ReasoningMarkdown").All(m => !m.ChartsEnabled)
                && !Descendants(transcript).OfType<ChatMarkdown>().First(m => m.Name == "MessageMarkdown").ChartsEnabled,
                context + ": user text and assistant reasoning never opt into chart rendering");
            Check(Answer().Text == source && RenderedRuns(transcript).Any(r => r.Text == "Before chart") && RenderedRuns(transcript).Any(r => r.Text == "After chart")
                && RenderedRuns(transcript).Any(r => r.Text == "Example"), context + ": original text and surrounding headings/table survive chart enhancement");
            Check(Descendants(transcript).OfType<Image>().All(image => image.Source is null), context + ": enhanced Markdown still blocks remote images");
            var tabs = Descendants(transcript).OfType<NavigationView>().Single(n => n.Name == "MessageChartTabsNavigation");
            Check(tabs.MenuItems.Count == 2 && ((NavigationViewItem)tabs.SelectedItem).Name == "MessageChartPreviewTab"
                && AutomationProperties.GetName((NavigationViewItem)tabs.MenuItems[0]) == DesktopResources.Get("Chart"),
                context + ": accessible localized native Chart/Code tabs default to Chart");
            tabs.SelectedItem = tabs.MenuItems[1]; await Task.Delay(80); shell.UpdateLayout();
            Check(RenderedRuns(tabs).Any(r => r.Text.Contains("\"Axes\""))
                && Descendants(tabs).OfType<RichTextBlock>().All(t => t.IsTextSelectionEnabled)
                && Descendants(tabs).OfType<Button>().Any(b => AutomationProperties.GetName(b) == DesktopResources.Get("CopyCode")),
                context + ": Code retains selectable original JSON and a native copy action");
            tabs.SelectedItem = tabs.MenuItems[0]; await Task.Delay(60);
            await Task.Delay(1000);
            await CheckChartPixelsAsync(Charts().Single(), context + ": plot remains drawn after switching back from Code");
            foreach (var width in new[] { 520, 1280 })
            {
                window.AppWindow.Resize(new Windows.Graphics.SizeInt32(width, 900)); await Task.Delay(100); shell.UpdateLayout();
                Check(Field<ScrollViewer>(shell, "scroll").ScrollableWidth < 1 && Charts()[0].ActualHeight == 320,
                    context + $": chart has bounded native height and no horizontal overflow at {width}px");
            }
            var oldColor = ((SolidColorBrush)Charts()[0].Series[0].Stroke).Color;
            shell.RequestedTheme = shell.ActualTheme == ElementTheme.Light ? ElementTheme.Dark : ElementTheme.Light;
            await Task.Delay(180); shell.UpdateLayout();
            Check(Charts().Length == 1 && ((SolidColorBrush)Charts()[0].Series[0].Stroke).Color != oldColor,
                context + ": chart survives runtime theme changes with the dashboard palette");
            foreach (var status in new[] { "streaming", "interrupted", "stopped", "failed", "approval required" })
            {
                message.Status = status; Call(shell, "RenderTranscript"); await Task.Delay(60);
                Check(Charts().Length == 0 && !Answer().ChartsEnabled, context + ": ordinary code for status " + status);
            }
            message.Status = "complete";
            message.Message.Parts[1] = new TextUIPart { Text = fence + "\n\n```winui-chart\n{broken}\n```\n\n```json\n" + NativeChartFixture + "\n```" };
            Call(shell, "RenderTranscript"); await Task.Delay(120);
            Check(Charts().Length == 1 && RenderedRuns(transcript).Any(r => r.Text == "{broken}"), context + ": invalid and unrelated code blocks remain code beside a valid chart");
            message.Message.Parts[1] = new TextUIPart { Text = "```winui-chart\n" + NativeChartFixture };
            Call(shell, "RenderTranscript"); await Task.Delay(100);
            Check(Charts().Length == 0, context + ": unterminated chart fence stays ordinary code even after completion");
            message.Message.Parts[1] = new TextUIPart { Text = string.Join("\n\n", Enumerable.Repeat(fence, 6)) };
            Call(shell, "RenderTranscript");
            for (var attempt = 0; attempt < 40 && Charts().Length != 4; attempt++) { await Task.Delay(40); shell.UpdateLayout(); }
            Check(Charts().Length == 4, context + $": at most four native chart previews per Markdown part (actual {Charts().Length})");
            message.Message.Parts[1] = new TextUIPart { Text = "> " + fence.Replace("\n", "\n> ") };
            Call(shell, "RenderTranscript"); await Task.Delay(100);
            Check(Charts().Length == 1, context + ": parser-verified chart inside a blockquote preserves nesting");
            var imported = PortableConversations.Read(PortableConversations.Write(conversation));
            await (Task)Call(shell, "OpenConversationAsync", imported)!; await Task.Delay(140);
            Check(Charts().Length == 1, context + ": completed charts render when portable conversation history is reopened");
            await Task.Delay(1000);
            await CheckChartPixelsAsync(Charts().Single(), context + ": nested chart stays drawn after history is reopened");
        }
        finally { await shell.ShutdownAsync(); window!.Content = null; }
    }

    private async Task CheckChartPixelsAsync(Chart chart, string context)
    {
        await Task.Delay(80);
        var (width, height, bytes) = ChartScreenCapture.Capture(window!, chart);
        var plotted = 0;
        for (var i = 0; i + 3 < bytes.Length; i += 4)
            if (Math.Max(bytes[i], Math.Max(bytes[i + 1], bytes[i + 2])) - Math.Min(bytes[i], Math.Min(bytes[i + 1], bytes[i + 2])) > 40) plotted++;
        results.Add(context + $": size={width}x{height}, plottedPixels={plotted}, tree="
            + string.Join("; ", Descendants(chart).OfType<FrameworkElement>().Select(e => $"{e.GetType().Name}/{e.Name}:{e.ActualWidth:F0}x{e.ActualHeight:F0}")));
        Check(plotted > 100, context + ": chart pixels contain actual plotted content rather than an empty control");
    }
}
