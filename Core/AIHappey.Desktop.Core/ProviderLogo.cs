using FluentIcons.Common;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace AIHappey.Desktop.Core;

/// <summary>Native, theme-aware logos. A failed explicit icon tries other icons, then Google's favicon.</summary>
internal sealed class ProviderLogo : Grid
{
    private readonly CatalogProvider provider;
    private readonly Image image = new() { Stretch = Stretch.Uniform, Visibility = Visibility.Collapsed };
    private readonly IconElement placeholder = DesktopIcons.Create(Icon.Brain);
    private CancellationTokenSource? lifetime;
    internal string? DisplayedSource { get; private set; }
    public ProviderLogo(CatalogProvider provider, double size = 48)
    {
        this.provider = provider; Width = Height = size; Name = "ProviderLogo";
        Children.Add(placeholder); Children.Add(image);
        ToolbarControls.Label(this, DesktopResources.Format("ProviderLogoLabel", provider.Name));
        Loaded += (_, _) => Reload(); ActualThemeChanged += (_, _) => { if (IsLoaded) Reload(); };
        Unloaded += (_, _) => { lifetime?.Cancel(); image.Source = null; };
    }
    private async void Reload()
    {
        lifetime?.Cancel(); lifetime?.Dispose(); lifetime = new(); var ct = lifetime.Token;
        image.Visibility = Visibility.Collapsed; placeholder.Visibility = Visibility.Visible; DisplayedSource = null;
        foreach (var source in ProviderCatalog.IconSources(provider, ActualTheme == ElementTheme.Dark))
        {
            if (ct.IsCancellationRequested) return;
            var embedded = source.StartsWith("data:image/png;base64,", StringComparison.OrdinalIgnoreCase);
            if (!embedded && (ModelProviders.SafeWebUri(source) is null
                || AppContext.TryGetSwitch("AIHappey.Desktop.DisableRemoteImages", out var disabled) && disabled)) continue;
            var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            RoutedEventHandler opened = (_, _) => ready.TrySetResult(true);
            ExceptionRoutedEventHandler failed = (_, _) => ready.TrySetResult(false);
            image.ImageOpened += opened; image.ImageFailed += failed;
            SvgImageSource? svg = null;
            try
            {
                image.Source = null;
                if (embedded)
                {
                    // Only bounded raster data from our bundled catalog, never HTML or script-bearing data URIs.
                    if (source.Length > 2_800_000) continue;
                    var bytes = Convert.FromBase64String(source[22..]);
                    if (bytes.Length > 2_000_000 || bytes.Length < 8 || !bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) continue;
                    using var stream = new InMemoryRandomAccessStream();
                    using (var writer = new DataWriter(stream.GetOutputStreamAt(0))) { writer.WriteBytes(bytes); await writer.StoreAsync().AsTask(ct); }
                    stream.Seek(0);
                    var bitmap = new BitmapImage { DecodePixelWidth = (int)(Width * 2) };
                    image.Source = bitmap; await bitmap.SetSourceAsync(stream).AsTask(ct); ready.TrySetResult(true);
                }
                else if (new Uri(source).AbsolutePath.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
                {
                    svg = new SvgImageSource();
                    svg.Opened += (_, _) => ready.TrySetResult(true); svg.OpenFailed += (_, _) => ready.TrySetResult(false);
                    image.Source = svg; svg.UriSource = new Uri(source);
                }
                else image.Source = new BitmapImage(new Uri(source)) { DecodePixelWidth = (int)(Width * 2) };
                if (await ready.Task.WaitAsync(TimeSpan.FromSeconds(12), ct) && !ct.IsCancellationRequested)
                { DisplayedSource = source; image.Visibility = Visibility.Visible; placeholder.Visibility = Visibility.Collapsed; return; }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception) { /* Network, decode or timeout failure: try the next bounded candidate. */ }
            finally { image.ImageOpened -= opened; image.ImageFailed -= failed; }
        }
        if (!ct.IsCancellationRequested) image.Source = null;
    }
}
