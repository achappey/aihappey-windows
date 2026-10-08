using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI.ViewManagement;

namespace AIHappey.Desktop.Core;

internal interface IResponsiveDialog { void SizeToRoot(); }

/// <summary>One Windows app-theme policy for both hosts. There is no saved/in-app theme preference.</summary>
public sealed class SystemAppearance : IDisposable
{
    private readonly FrameworkElement root;
    private readonly UISettings settings = new();
    private bool disposed;

    public static ApplicationTheme CurrentTheme => IsDark(new UISettings()) ? ApplicationTheme.Dark : ApplicationTheme.Light;

    public SystemAppearance(FrameworkElement root)
    {
        this.root = root;
        Apply();
        settings.ColorValuesChanged += ColorsChanged;
    }

    private static bool IsDark(UISettings settings)
    {
        // Windows' foreground is light when its app background is dark (also works for custom contrast themes).
        var color = settings.GetColorValue(UIColorType.Foreground);
        return 5 * color.G + 2 * color.R + color.B > 8 * 128;
    }

    private void Apply() { if (!disposed) root.RequestedTheme = IsDark(settings) ? ElementTheme.Dark : ElementTheme.Light; }
    private void ColorsChanged(UISettings sender, object args) => root.DispatcherQueue.TryEnqueue(Apply);
    public void Dispose() { disposed = true; settings.ColorValuesChanged -= ColorsChanged; }

    public static void PrepareDialog(ContentDialog dialog)
    {
        // ContentDialog's outer control is the full-window modal host. Constraining its
        // MaxWidth after Opened shrinks that host against the popup's left edge, not the
        // centered BackgroundElement inside it. Size content + ContentDialogMaxWidth instead.
        dialog.HorizontalAlignment = HorizontalAlignment.Stretch;
        dialog.VerticalAlignment = VerticalAlignment.Stretch;
        dialog.MaxWidth = dialog.MaxHeight = double.PositiveInfinity;
        if (dialog is IResponsiveDialog responsive) responsive.SizeToRoot();
        if (dialog.XamlRoot?.Content is FrameworkElement owner)
        {
            dialog.RequestedTheme = owner.ActualTheme;
            void ThemeChanged(FrameworkElement sender, object args) => dialog.RequestedTheme = owner.ActualTheme;
            owner.ActualThemeChanged += ThemeChanged;
            dialog.Closed += (_, _) => owner.ActualThemeChanged -= ThemeChanged;
        }
        // All dialogs use the same stock WinUI chrome and control templates. Do not walk
        // template parts after opening or copy application colors into native state setters.
        ControlAppearance.Stock(dialog);
    }
}
