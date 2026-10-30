using AIHappey.Desktop.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AIHappey.Desktop.UiTests;

public partial class App
{
    private async Task CheckProviderLogoLoadingAsync(Grid root, ElementTheme theme)
    {
        var embedded = ProviderCatalog.Get("echo").Icons.First(icon => icon.Src.StartsWith("data:image/png;base64,", StringComparison.Ordinal)).Src;
        // Both sources are valid PNGs. Base64 permits whitespace, giving distinct
        // deterministic sources to verify theme ordering without a remote dependency.
        var light = embedded; var dark = embedded + "\n";
        var provider = new CatalogProvider { Id = "logo-fixture", Name = "Logo fixture", Icons = [
            new("http://127.0.0.1/unsafe.png", "light"), new("data:image/png;base64,invalid", "light"), new(light, "light"),
            new("http://127.0.0.1/unsafe.png", "dark"), new("data:image/png;base64,invalid", "dark"), new(dark, "dark")] };
        var type = typeof(ChatShell).Assembly.GetType("AIHappey.Desktop.Core.ProviderLogo")!;
        var logo = (Grid)Activator.CreateInstance(type, provider, 64d, true)!;
        string? Source() => (string?)type.GetProperty("DisplayedSource", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(logo);
        async Task WaitForSourceAsync(string source)
        {
            for (var attempt = 0; attempt < 100 && Source() != source; attempt++) await Task.Delay(50);
            Check(Source() == source && logo.Children.OfType<Image>().Single().Visibility == Visibility.Visible
                && logo.Children.OfType<IconElement>().Single().Visibility == Visibility.Collapsed,
                $"Provider logos / {theme}: native PNG renders after unsafe and malformed candidates are skipped");
        }
        try
        {
            root.Children.Add(logo);
            await WaitForSourceAsync(theme == ElementTheme.Dark ? dark : light);
            logo.RequestedTheme = theme == ElementTheme.Dark ? ElementTheme.Light : ElementTheme.Dark;
            await WaitForSourceAsync(theme == ElementTheme.Dark ? light : dark);
            root.Children.Remove(logo); await Task.Delay(80);
            Check(Source() is null && logo.Children.OfType<Image>().Single().Source is null, $"Provider logos / {theme}: unload cancels image lifetime and clears the source");
            root.Children.Add(logo); await WaitForSourceAsync(theme == ElementTheme.Dark ? light : dark);
        }
        finally { root.Children.Remove(logo); }
    }
}
