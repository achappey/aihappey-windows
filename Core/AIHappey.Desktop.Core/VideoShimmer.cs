using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI.ViewManagement;

namespace AIHappey.Desktop.Core;

/// <summary>Animation is scoped to loaded, visible pending tiles, with an accessible static fallback.</summary>
public sealed class VideoShimmer : Grid
{
    private readonly Storyboard animation = new();
    private readonly UISettings settings = new();
    private readonly AccessibilitySettings accessibility = new();
    private readonly Border sheen = new() { Opacity = .25, IsHitTestVisible = false, CornerRadius = new CornerRadius(8) };
    private bool colorsSubscribed, animationsSubscribed, animationUnavailable;
    public bool MotionAllowed { get; set; } = true;
    public bool IsAnimating { get; private set; }
    public VideoShimmer(string label)
    {
        Name = "VideoShimmer";
        Children.Add(sheen);
        var text = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(20) }; Children.Add(text);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(this, label);
        ControlAppearance.Apply(this, ControlAppearance.NativeResources, palette =>
        {
            var color = palette.Text; color.A = 40;
            sheen.Background = new LinearGradientBrush { StartPoint = new Windows.Foundation.Point(0, 0), EndPoint = new Windows.Foundation.Point(1, 1),
                GradientStops = { new GradientStop { Color = Windows.UI.Color.FromArgb(0, color.R, color.G, color.B), Offset = 0 },
                    new GradientStop { Color = color, Offset = .5 }, new GradientStop { Color = Windows.UI.Color.FromArgb(0, color.R, color.G, color.B), Offset = 1 } } };
        });
        var pulse = new DoubleAnimation { From = .15, To = 1, Duration = new Duration(TimeSpan.FromSeconds(1.3)), AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
        Storyboard.SetTarget(pulse, sheen); Storyboard.SetTargetProperty(pulse, "Opacity"); animation.Children.Add(pulse);
        ActualThemeChanged += (_, _) => RefreshAnimation();
        Loaded += (_, _) =>
        {
            // HighContrastChanged registration returns ERROR_NOT_FOUND in unpackaged hosts.
            // Follow ControlAppearance: color notifications plus ActualThemeChanged cover it.
            try { if (!colorsSubscribed) { settings.ColorValuesChanged += ColorsChanged; colorsSubscribed = true; } }
            catch (System.Runtime.InteropServices.COMException error) { DisableAnimation(error); }
            try { if (!animationsSubscribed) { settings.AnimationsEnabledChanged += AnimationsChanged; animationsSubscribed = true; } }
            catch (System.Runtime.InteropServices.COMException error) { DisableAnimation(error); }
            RefreshAnimation();
        };
        Unloaded += (_, _) =>
        {
            Stop();
            try { if (colorsSubscribed) settings.ColorValuesChanged -= ColorsChanged; }
            catch (System.Runtime.InteropServices.COMException error) { DisableAnimation(error); }
            try { if (animationsSubscribed) settings.AnimationsEnabledChanged -= AnimationsChanged; }
            catch (System.Runtime.InteropServices.COMException error) { DisableAnimation(error); }
            colorsSubscribed = animationsSubscribed = false;
        };
    }
    private void AnimationsChanged(UISettings sender, object args) => DispatcherQueue.TryEnqueue(RefreshAnimation);
    private void ColorsChanged(UISettings sender, object args) => DispatcherQueue.TryEnqueue(RefreshAnimation);
    public void RefreshAnimation()
    {
        var visible = IsLoaded && MotionAllowed;
        for (DependencyObject? owner = this; visible && owner is not null; owner = VisualTreeHelper.GetParent(owner))
            if (owner is UIElement element && element.Visibility != Visibility.Visible) visible = false;
        try
        {
            if (visible && !animationUnavailable && settings.AnimationsEnabled && !accessibility.HighContrast)
            { if (!IsAnimating) { animation.Begin(); IsAnimating = true; } }
            else Stop();
        }
        catch (System.Runtime.InteropServices.COMException error) { DisableAnimation(error); Stop(); }
    }
    private void DisableAnimation(Exception error)
    { animationUnavailable = true; System.Diagnostics.Debug.WriteLine("Video shimmer unavailable; retaining static pending card: " + error); }
    public void Stop()
    {
        if (IsAnimating)
            try { animation.Stop(); } catch (System.Runtime.InteropServices.COMException error) { DisableAnimation(error); }
        IsAnimating = false; sheen.Opacity = .25;
    }
}
