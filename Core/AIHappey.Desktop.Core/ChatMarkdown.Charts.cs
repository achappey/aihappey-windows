using Markdig.Syntax;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Windows.ApplicationModel.DataTransfer;
using MarkdownCodeBlock = Markdig.Syntax.CodeBlock;

namespace AIHappey.Desktop.Core;

public sealed partial class ChatMarkdown
{
    private bool chartsEnabled, chartsQueued;
    private MarkdownDocument? chartDocument;
    public bool ChartsEnabled
    {
        get => chartsEnabled;
        set { if (chartsEnabled == value) return; chartsEnabled = value; QueueRefresh(); }
    }

    private void QueueCharts()
    {
        if (!ChartsEnabled || chartsQueued || ReferenceEquals(chartDocument, Markdown.MarkdownDocument)) return;
        chartsQueued = DispatcherQueue.TryEnqueue(() => { chartsQueued = false; if (IsLoaded) EnhanceCharts(); });
    }

    private void EnhanceCharts()
    {
        if (!ChartsEnabled || Markdown.MarkdownDocument is not { } document || ReferenceEquals(chartDocument, document)) return;
        var blocks = document.Descendants<MarkdownCodeBlock>().ToArray();
        if (!blocks.OfType<FencedCodeBlock>().Any(IsChart)) { chartDocument = document; return; }

        // Labs has no public code-renderer registration hook. Its pinned version renders every code block
        // as Border -> RichTextBlock. Verify BOTH its shape and its exact parsed lines before enhancing
        // anything. No private reflection, text matching across arbitrary controls, or Markdown splitting.
        var borders = ControlAppearance.Descendants(Markdown).OfType<Border>().Where(border =>
            border.Child is RichTextBlock text && text.FontFamily.Source == Markdown.Config.Themes.CodeBlockFontFamily.Source).ToArray();
        if (borders.Length != blocks.Length) return;
        var root = ControlAppearance.Descendants(Markdown).OfType<Grid>().FirstOrDefault(grid => grid.Name == "MarkdownContainer");
        if (root?.Children.OfType<RichTextBlock>().SingleOrDefault() is not { } flow) return;
        for (var i = 0; i < blocks.Length; i++)
        {
            var rendered = ((RichTextBlock)borders[i].Child).Blocks.OfType<Paragraph>().ToArray();
            var lines = blocks[i].Lines.Lines;
            if (lines is null || rendered.Length != lines.Length) return;
            for (var line = 0; line < lines.Length; line++)
            {
                var expected = lines[line].ToString();
                if (string.IsNullOrWhiteSpace(expected)) expected = "";
                if (rendered[line].Inlines.Any(inline => inline is not Run)
                    || string.Concat(rendered[line].Inlines.Cast<Run>().Select(run => run.Text)) != expected) return;
            }
        }
        chartDocument = document;
        var count = 0;
        for (var i = 0; i < blocks.Length && count < 4; i++)
        {
            if (blocks[i] is not FencedCodeBlock fence || !IsChart(fence)) continue;
            if (!CanLift(flow, borders[i])) continue;
            var source = fence.Lines.ToString();
            if (ChatChartJson.TryCreate(source) is not { } chart) continue;
            var border = borders[i];
            var original = (RichTextBlock)border.Child;
            try
            {
                var code = new StackPanel { Spacing = 8 };
                var copy = ToolbarControls.CopyButton(); copy.HorizontalAlignment = HorizontalAlignment.Right;
                ToolbarControls.Label(copy, DesktopResources.Get("CopyCode"));
                copy.Click += (_, _) =>
                {
                    try { var package = new DataPackage(); package.SetText(source); Clipboard.SetContent(package); }
                    catch (Exception error) { System.Diagnostics.Trace.WriteLine($"Chart source copy failed: {error.GetType().Name}"); }
                };
                original.IsTextSelectionEnabled = true;
                var tabs = new ToolReviewTabs("MessageChartTabs");
                code.Children.Add(copy);
                tabs.Add(DesktopResources.Get("Chart"), chart, "MessageChartPreviewTab");
                tabs.Add(DesktopResources.Get("Code"), code, "MessageChartCodeTab");
                tabs.SelectedIndex = 0;
                border.Child = null;
                code.Children.Add(original);
                border.Name = "MessageChartBlock";
                border.Child = tabs;
                count++;
            }
            catch (Exception error)
            {
                if (original.Parent is Panel parent) parent.Children.Remove(original);
                border.Child = original; border.Name = ""; border.ClearValue(WidthProperty);
                System.Diagnostics.Trace.WriteLine($"Chart tabs unavailable: {error.GetType().Name}");
            }
        }
        if (count > 0) LiftCharts(root, flow);
    }

    private static bool IsChart(FencedCodeBlock block) => block.Info == "winui-chart"
        && string.IsNullOrWhiteSpace(block.Arguments) && block.ClosingFencedCharCount >= block.OpeningFencedCharCount;

}
