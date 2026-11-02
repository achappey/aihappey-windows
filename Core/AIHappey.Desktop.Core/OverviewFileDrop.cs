using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.ApplicationModel.DataTransfer;

namespace AIHappey.Desktop.Core;

/// <summary>One drop owner per overview, including handled child events and empty page space.</summary>
internal static class OverviewFileDrop
{
    public static Grid Wrap(UIElement content, Func<bool> enabled, Func<DataPackageView, Task> import, string caption)
    {
        var root = new Grid { AllowDrop = true, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        var outline = new Rectangle { Name = "OverviewDropOutline", IsHitTestVisible = false, Visibility = Visibility.Collapsed,
            Margin = new Thickness(8), StrokeThickness = 2, StrokeDashArray = new DoubleCollection { 2, 3 }, RadiusX = 8, RadiusY = 8 };
        root.Children.Add(content); root.Children.Add(outline);
        ControlAppearance.Apply(outline, (_, _) => { }, palette => outline.Stroke = new SolidColorBrush(palette.Text));
        var dropping = false;
        bool Accepts(DragEventArgs args) => !dropping && enabled() && args.DataView.Contains(StandardDataFormats.StorageItems)
            && args.AllowedOperations.HasFlag(DataPackageOperation.Copy);
        void Over(object sender, DragEventArgs args)
        {
            args.Handled = true; var accepts = Accepts(args);
            args.AcceptedOperation = accepts ? DataPackageOperation.Copy : DataPackageOperation.None;
            args.DragUIOverride.Caption = accepts ? caption : ""; args.DragUIOverride.IsCaptionVisible = accepts;
            outline.Visibility = accepts ? Visibility.Visible : Visibility.Collapsed;
        }
        async void Drop(object sender, DragEventArgs args)
        {
            args.Handled = true; outline.Visibility = Visibility.Collapsed;
            if (!Accepts(args)) { args.AcceptedOperation = DataPackageOperation.None; return; }
            args.AcceptedOperation = DataPackageOperation.Copy;
            dropping = true; var deferral = args.GetDeferral();
            try { await import(args.DataView); }
            finally { dropping = false; deferral.Complete(); }
        }
        root.AddHandler(UIElement.DragEnterEvent, new DragEventHandler(Over), true);
        root.AddHandler(UIElement.DragOverEvent, new DragEventHandler(Over), true);
        root.AddHandler(UIElement.DropEvent, new DragEventHandler(Drop), true);
        root.AddHandler(UIElement.DragLeaveEvent, new DragEventHandler((_, args) =>
        {
            var point = args.GetPosition(root);
            if (point.X <= 0 || point.Y <= 0 || point.X >= root.ActualWidth || point.Y >= root.ActualHeight) outline.Visibility = Visibility.Collapsed;
        }), true);
        root.Unloaded += (_, _) => outline.Visibility = Visibility.Collapsed;
        return root;
    }
}
