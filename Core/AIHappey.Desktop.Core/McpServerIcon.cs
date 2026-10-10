using FluentIcons.WinUI;
using FluentIcons.Common;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace AIHappey.Desktop.Core;

/// <summary>A small native image slot. Generic artwork remains until the selected image successfully opens.</summary>
internal sealed class McpServerIcon : UserControl
{
    private readonly IReadOnlyList<McpIcon> icons;
    private readonly Grid slot = new();
    private readonly FluentIcon fallback;
    private int version;

    public McpServerIcon(IReadOnlyList<McpIcon> icons, double size)
    {
        this.icons = icons;
        Name = "McpServerIcon"; Width = Height = size; IsTabStop = false;
        VerticalAlignment = VerticalAlignment.Center;
        fallback = new FluentIcon { Name = "McpGenericIcon", Icon = Icon.Globe, FontSize = size <= 20 ? 14 : 24 };
        slot.Children.Add(fallback); Content = slot;
        AutomationProperties.SetAccessibilityView(this, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        Loaded += (_, _) => Refresh();
        ActualThemeChanged += (_, _) => { if (IsLoaded) Refresh(); };
        Unloaded += (_, _) => version++;
    }

    private async void Refresh()
    {
        var current = ++version;
        slot.Children.Clear(); slot.Children.Add(fallback); fallback.Visibility = Visibility.Visible;
        var icon = McpIcons.Select(icons, ActualTheme == ElementTheme.Dark ? "dark" : "light");
        if (icon is null || AppContext.TryGetSwitch("AIHappey.Desktop.DisableRemoteImages", out var disabled) && disabled) return;
        var image = new Image { Name = "McpServerImage", Width = Width, Height = Height, Stretch = Stretch.Uniform, Visibility = Visibility.Collapsed };
        image.ImageOpened += (_, _) =>
        {
            if (current != version) return;
            fallback.Visibility = Visibility.Collapsed; image.Visibility = Visibility.Visible;
        };
        image.ImageFailed += (_, _) =>
        {
            if (current != version) return;
            fallback.Visibility = Visibility.Visible; image.Visibility = Visibility.Collapsed;
        };
        slot.Children.Add(image);
        try
        {
            var svg = McpIcons.IsSvg(icon);
            if (icon.Source.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                using var stream = new MemoryStream(McpIcons.EmbeddedBytes(icon));
                using var random = stream.AsRandomAccessStream();
                if (svg)
                {
                    var source = new SvgImageSource(); image.Source = source;
                    await source.SetSourceAsync(random);
                }
                else
                {
                    var source = new BitmapImage { DecodePixelWidth = (int)Math.Ceiling(Width * (XamlRoot?.RasterizationScale ?? 1)) };
                    image.Source = source; await source.SetSourceAsync(random);
                }
            }
            else if (AttachmentDownloads.RemoteUri(icon.Source) is { } uri)
                image.Source = svg ? new SvgImageSource(uri) : new BitmapImage(uri)
                { DecodePixelWidth = (int)Math.Ceiling(Width * (XamlRoot?.RasterizationScale ?? 1)) };
        }
        catch (Exception)
        {
            // An invalid or unsupported image must not interrupt chat or expose server errors.
            if (current == version) { fallback.Visibility = Visibility.Visible; image.Visibility = Visibility.Collapsed; }
        }
    }
}
