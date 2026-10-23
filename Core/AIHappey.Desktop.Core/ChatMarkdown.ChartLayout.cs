using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Documents;

namespace AIHappey.Desktop.Core;

public sealed partial class ChatMarkdown
{
    private Grid? chartLayoutParent;
    private RichTextBlock? chartLayoutFlow;
    private StackPanel? chartLayout;
    private int chartLayoutIndex;

    private static bool Contains(DependencyObject parent, DependencyObject target) =>
        ControlAppearance.Descendants(parent).Any(child => ReferenceEquals(child, target));

    // Native Chart composition surfaces disappear inside RichTextBlock's InlineUIContainer after
    // text layout. Only lift whole block containers, never split a paragraph containing inline text.
    private static bool CanLift(RichTextBlock flow, Border target)
    {
        foreach (var paragraph in flow.Blocks.OfType<Paragraph>())
            foreach (var inline in paragraph.Inlines.OfType<InlineUIContainer>())
                if (inline.Child is { } child && Contains(child, target))
                {
                    if (paragraph.Inlines.Count != 1) return false;
                    if (ReferenceEquals(child, target)) return true;
                    var nested = ControlAppearance.Descendants(child).OfType<RichTextBlock>().FirstOrDefault(text => Contains(text, target));
                    return nested is not null && (nested.Parent is Panel or Border) && CanLift(nested, target);
                }
        return false;
    }

    private void LiftCharts(Grid parent, RichTextBlock flow)
    {
        chartLayoutParent = parent; chartLayoutFlow = flow; chartLayoutIndex = parent.Children.IndexOf(flow);
        try
        {
            chartLayout = LiftFlow(flow);
            parent.Children.Remove(flow);
            parent.Children.Insert(chartLayoutIndex, chartLayout);
        }
        catch (Exception error)
        {
            // Rebuild ordinary toolkit Markdown on any unexpected pinned-toolkit structure change.
            RestoreChartLayout();
            var text = Markdown.Text; Markdown.Text = ""; Markdown.Text = text;
            chartDocument = Markdown.MarkdownDocument;
            System.Diagnostics.Trace.WriteLine($"Chart block layout unavailable: {error.GetType().Name}");
        }
    }

    private StackPanel LiftFlow(RichTextBlock flow)
    {
        var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = flow.Margin, MinWidth = flow.MinWidth, MaxWidth = flow.MaxWidth };
        Grid.SetColumn(panel, Grid.GetColumn(flow)); Grid.SetRow(panel, Grid.GetRow(flow));
        Grid.SetColumnSpan(panel, Grid.GetColumnSpan(flow)); Grid.SetRowSpan(panel, Grid.GetRowSpan(flow));
        var blocks = flow.Blocks.ToArray(); flow.Blocks.Clear();
        RichTextBlock? text = null;
        foreach (var block in blocks)
        {
            if (block is Paragraph paragraph && paragraph.Inlines.Count == 1
                && paragraph.Inlines[0] is InlineUIContainer { Child: { } child } inline
                && ControlAppearance.Descendants(child).OfType<Border>().Any(border => border.Name == "MessageChartBlock"))
            {
                inline.Child = null;
                // Quotes and other block containers may have their own nested text document. Lift
                // bottom-up so no chart remains under an inline text container at any ancestor level.
                foreach (var nested in ControlAppearance.Descendants(child).OfType<RichTextBlock>().Reverse().ToArray())
                {
                    if (!ControlAppearance.Descendants(nested).OfType<Border>().Any(border => border.Name == "MessageChartBlock")) continue;
                    if (nested.Parent is Panel owner)
                    {
                        var index = owner.Children.IndexOf(nested); var lifted = LiftFlow(nested);
                        owner.Children.RemoveAt(index); owner.Children.Insert(index, lifted);
                    }
                    else if (nested.Parent is Border ownerBorder) ownerBorder.Child = LiftFlow(nested);
                }
                panel.Children.Add(child); text = null;
            }
            else
            {
                if (text is null)
                {
                    text = new RichTextBlock { FontFamily = flow.FontFamily, FontSize = flow.FontSize,
                        FontWeight = flow.FontWeight, FontStyle = flow.FontStyle, FontStretch = flow.FontStretch,
                        CharacterSpacing = flow.CharacterSpacing, TextAlignment = flow.TextAlignment,
                        TextWrapping = flow.TextWrapping, LineHeight = flow.LineHeight, LineStackingStrategy = flow.LineStackingStrategy,
                        IsTextSelectionEnabled = flow.IsTextSelectionEnabled };
                    text.SetBinding(RichTextBlock.ForegroundProperty, new Binding { Source = Primary, Path = new PropertyPath("Foreground") });
                    panel.Children.Add(text);
                }
                text.Blocks.Add(block);
            }
        }
        return panel;
    }

    private void RestoreChartLayout()
    {
        if (chartLayoutParent is { } parent && chartLayoutFlow is { } flow)
        {
            if (chartLayout is { } layout) parent.Children.Remove(layout);
            if (!parent.Children.Contains(flow)) parent.Children.Insert(Math.Min(chartLayoutIndex, parent.Children.Count), flow);
        }
        chartLayoutParent = null; chartLayoutFlow = null; chartLayout = null; chartDocument = null;
    }
}
