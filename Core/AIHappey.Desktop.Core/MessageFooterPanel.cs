using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace AIHappey.Desktop.Core;

/// <summary>Small native Panel layout: footer actions retain natural widths and wrap at narrow viewports.</summary>
internal sealed class MessageFooterPanel : Panel
{
    public bool AlignRight { get; init; }
    protected override Size MeasureOverride(Size availableSize)
    {
        double x = 0, y = 0, lineHeight = 0, width = 0;
        foreach (var child in Children)
        {
            child.Measure(new Size(availableSize.Width, double.PositiveInfinity));
            var size = child.DesiredSize;
            if (x > 0 && x + size.Width > availableSize.Width) { width = Math.Max(width, x - 6); x = 0; y += lineHeight + 6; lineHeight = 0; }
            x += size.Width + 6; lineHeight = Math.Max(lineHeight, size.Height);
        }
        return new Size(Math.Max(width, Math.Max(0, x - 6)), y + lineHeight);
    }
    protected override Size ArrangeOverride(Size finalSize)
    {
        double y = 0;
        var start = 0;
        while (start < Children.Count)
        {
            double lineWidth = 0, lineHeight = 0;
            var end = start;
            // Know the tallest item before arranging this row. A counter or badge is shorter
            // than the 32px action buttons, and must share their vertical center, not their top.
            while (end < Children.Count)
            {
                var size = Children[end].DesiredSize;
                if (end > start && lineWidth + size.Width > finalSize.Width) break;
                lineWidth += size.Width + 6;
                lineHeight = Math.Max(lineHeight, size.Height);
                end++;
            }
            double x = AlignRight ? Math.Max(0, finalSize.Width - Math.Max(0, lineWidth - 6)) : 0;
            for (var index = start; index < end; index++)
            {
                var child = Children[index];
                var size = child.DesiredSize;
                child.Arrange(new Rect(x, y + (lineHeight - size.Height) / 2, Math.Min(size.Width, finalSize.Width), size.Height));
                x += size.Width + 6;
            }
            y += lineHeight + 6;
            start = end;
        }
        return finalSize;
    }
}
