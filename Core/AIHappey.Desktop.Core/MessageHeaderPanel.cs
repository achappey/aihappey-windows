using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace AIHappey.Desktop.Core;

/// <summary>One compact row: only the model shrinks, leaving the disclosure and time together.</summary>
internal sealed class MessageHeaderPanel : Panel
{
    public UIElement? Model { get; set; }
    private const double Spacing = 6;

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = Math.Max(0, Children.Count - 1) * Spacing, height = 0;
        foreach (var child in Children.Where(child => child != Model))
        {
            child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            width += child.DesiredSize.Width;
            height = Math.Max(height, child.DesiredSize.Height);
        }
        if (Model is not null)
        {
            Model.Measure(new Size(Math.Max(0, availableSize.Width - width), double.PositiveInfinity));
            width += Model.DesiredSize.Width;
            height = Math.Max(height, Model.DesiredSize.Height);
        }
        return new Size(Math.Min(width, availableSize.Width), height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var fixedWidth = Children.Where(child => child != Model).Sum(child => child.DesiredSize.Width)
            + Math.Max(0, Children.Count - 1) * Spacing;
        double x = 0;
        foreach (var child in Children)
        {
            var size = child.DesiredSize;
            var width = child == Model ? Math.Min(size.Width, Math.Max(0, finalSize.Width - fixedWidth)) : size.Width;
            child.Arrange(new Rect(x, Math.Max(0, (finalSize.Height - size.Height) / 2), width, size.Height));
            x += width + Spacing;
        }
        return finalSize;
    }
}
