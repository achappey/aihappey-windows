using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI.ViewManagement;

namespace AIHappey.Desktop.Core;

/// <summary>Toolkit welcome placeholder with a static reduced-motion/high-contrast fallback.</summary>
internal sealed class WelcomeShimmer : Grid
{
    private readonly Shimmer shimmer = new() { IsActive = false, IsHitTestVisible = false };
    private readonly Border placeholder = new() { CornerRadius = new CornerRadius(6), IsHitTestVisible = false };
    private readonly UISettings settings = new();
    private readonly AccessibilitySettings accessibility = new();
    private bool colorsSubscribed, animationsSubscribed, animationUnavailable;
    public bool MotionAllowed { get; set; } = true;
    public bool IsAnimating => shimmer.IsActive;

    public WelcomeShimmer()
    {
        Name = "WelcomeShimmer"; Height = 34; MaxWidth = 420;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        Children.Add(placeholder); Children.Add(shimmer);
        AutomationProperties.SetName(this, DesktopResources.Get("Loading"));
        ControlAppearance.Apply(this, ControlAppearance.NativeResources, palette =>
        {
            var color = palette.Text; color.A = 36;
            placeholder.Background = new SolidColorBrush(color);
            RefreshAnimation();
        });
        ActualThemeChanged += (_, _) => RefreshAnimation();
        RegisterPropertyChangedCallback(VisibilityProperty, (_, _) => RefreshAnimation());
        Loaded += (_, _) =>
        {
            // Follow the existing native shimmer policy; unpackaged hosts may reject notifications.
            try { if (!colorsSubscribed) { settings.ColorValuesChanged += ColorsChanged; colorsSubscribed = true; } }
            catch (System.Runtime.InteropServices.COMException) { animationUnavailable = true; }
            try { if (!animationsSubscribed) { settings.AnimationsEnabledChanged += AnimationsChanged; animationsSubscribed = true; } }
            catch (System.Runtime.InteropServices.COMException) { animationUnavailable = true; }
            RefreshAnimation();
        };
        Unloaded += (_, _) =>
        {
            shimmer.IsActive = false;
            try { if (colorsSubscribed) settings.ColorValuesChanged -= ColorsChanged; }
            catch (System.Runtime.InteropServices.COMException) { animationUnavailable = true; }
            try { if (animationsSubscribed) settings.AnimationsEnabledChanged -= AnimationsChanged; }
            catch (System.Runtime.InteropServices.COMException) { animationUnavailable = true; }
            colorsSubscribed = animationsSubscribed = false;
        };
    }

    private void AnimationsChanged(UISettings sender, object args) => DispatcherQueue.TryEnqueue(RefreshAnimation);
    private void ColorsChanged(UISettings sender, object args) => DispatcherQueue.TryEnqueue(RefreshAnimation);

    public void RefreshAnimation()
    {
        var visible = IsLoaded && MotionAllowed && !animationUnavailable;
        for (DependencyObject? owner = this; visible && owner is not null; owner = VisualTreeHelper.GetParent(owner))
            if (owner is UIElement element && element.Visibility != Visibility.Visible) visible = false;
        try { shimmer.IsActive = visible && settings.AnimationsEnabled && !accessibility.HighContrast; }
        catch (System.Runtime.InteropServices.COMException) { animationUnavailable = true; shimmer.IsActive = false; }
        // Do not expose an animated brush in high contrast, or an empty slot when animation is disabled.
        shimmer.Visibility = shimmer.IsActive ? Visibility.Visible : Visibility.Collapsed;
    }
}
