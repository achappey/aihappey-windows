using System.Reflection;
using System.Security.Cryptography;
using AIHappey.Desktop.Core;
using FluentIcons.Common;
using FluentIcons.WinUI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace AIHappey.Desktop.UiTests;

public partial class App
{
    private static FluentIcon CreateTestIcon(Icon icon, double size = 20, IconVariant variant = IconVariant.Regular)
        => (FluentIcon)typeof(ChatShell).Assembly.GetType("AIHappey.Desktop.Core.DesktopIcons")!
            .GetMethod("Create")!.Invoke(null, [icon, size, variant])!;

    private async Task CheckIconsAsync(ElementTheme theme)
    {
        var context = $"Fluent icons / {theme}";
        var root = new StackPanel { RequestedTheme = theme, Spacing = 8 };
        window!.Content = root; window.Activate();
        Check(File.Exists(Path.Combine(AppContext.BaseDirectory, "FluentIcons.WinUI", "Assets", "FluentSystemIcons-Size20.otf")),
            context + ": package font is present in the published native host");
        var first = CreateTestIcon(Icon.Brain);
        var second = CreateTestIcon(Icon.Brain);
        Check(!ReferenceEquals(first, second) && first.IconSize == IconSize.Resizable && first.IconVariant == IconVariant.Regular
            && first.FontSize == 20 && first.ReadLocalValue(IconElement.ForegroundProperty) == DependencyProperty.UnsetValue,
            context + ": factory creates fresh regular resizable icons without pinning foreground");
        var samples = new[] { Icon.Brain, Icon.Bot, Icon.Cloud, Icon.Connector, Icon.Copy, Icon.Send, Icon.Image, Icon.Video, Icon.Mic, Icon.Money, Icon.DataUsage };
        var icons = new List<FluentIcon>();
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        root.Children.Add(row);
        foreach (var icon in samples)
        {
            var control = CreateTestIcon(icon, 32);
            icons.Add(control);
            row.Children.Add(new Button { Content = control, Width = 48, Height = 48, Padding = new Thickness(0) });
        }
        var regular = CreateTestIcon(Icon.Star, 32);
        var filled = CreateTestIcon(Icon.Star, 32, IconVariant.Filled);
        row.Children.Add(new Button { Content = regular, Width = 48, Height = 48, Padding = new Thickness(0) });
        row.Children.Add(new Button { Content = filled, Width = 48, Height = 48, Padding = new Thickness(0) });
        var stateIcon = CreateTestIcon(Icon.Copy);
        var stateButton = new Button { Content = stateIcon, Width = 40, Height = 40, Padding = new Thickness(0) };
        var toolbar = typeof(ChatShell).Assembly.GetType("AIHappey.Desktop.Core.ToolbarControls")!;
        toolbar.GetMethod("Subtle")!.Invoke(null, [stateButton]);
        root.Children.Add(stateButton);
        await Task.Delay(100); root.UpdateLayout();
        var hashes = new HashSet<string>();
        foreach (var icon in icons)
        {
            Check(icon.IsLoaded && icon.ActualWidth > 0 && icon.ActualWidth <= 40 && icon.ActualHeight > 0
                && icon.FontFamily.Source.Contains("FluentIcons.WinUI/Assets/FluentSystemIcons-Size20.otf"),
                context + ": named package artwork has bounded layout: " + icon.Icon);
            var pixels = await IconPixelsAsync(icon);
            Check(Enumerable.Range(0, pixels.Length / 4).Any(i => pixels[i * 4 + 3] != 0),
                context + ": glyph paints pixels: " + icon.Icon);
            Check(hashes.Add(Convert.ToHexString(SHA256.HashData(pixels))),
                context + ": distinct artwork, not a shared missing-font glyph: " + icon.Icon);
        }
        var regularPixels = await IconPixelsAsync(regular);
        var filledPixels = await IconPixelsAsync(filled);
        Check(!regularPixels.SequenceEqual(filledPixels) && regular.Glyph != filled.Glyph,
            context + ": regular and filled Star variants render different artwork");
        var presenter = Descendants(stateButton).OfType<ContentPresenter>().Single(p => p.Name == "ContentPresenter");
        foreach (var state in new[] { "Normal", "PointerOver", "Pressed", "Disabled" })
        {
            if (state == "Disabled") stateButton.IsEnabled = false;
            Check(VisualStateManager.GoToState(stateButton, state, false), context + ": native button state exists: " + state);
            await Task.Delay(25);
            Check(stateIcon.ReadLocalValue(IconElement.ForegroundProperty) == DependencyProperty.UnsetValue
                && stateIcon.Foreground is SolidColorBrush iconBrush && presenter.Foreground is SolidColorBrush presenterBrush
                && iconBrush.Color == presenterBrush.Color, context + ": icon inherits rendered state foreground: " + state);
        }
        stateButton.IsEnabled = true;
        root.RequestedTheme = theme == ElementTheme.Light ? ElementTheme.Dark : ElementTheme.Light;
        await Task.Delay(60);
        Check(stateIcon.Foreground is SolidColorBrush switchedIcon && presenter.Foreground is SolidColorBrush switchedPresenter
            && switchedIcon.Color == switchedPresenter.Color, context + ": inherited foreground survives runtime theme change");
        window.Content = null;

        var shell = new ChatShell(new DesktopSession(new UiHost(true), new UiRuntime())) { RequestedTheme = theme };
        try
        {
            window.Content = shell; await Task.Delay(100); await HistoryIdleAsync(shell);
            var category = Field<Button>(shell, "aiCategory");
            var header = (Grid)category.Content;
            var chevron = header.Children.OfType<FluentIcon>().Single(i => i.Name == "ArtificialIntelligenceChevron");
            var brain = header.Children.OfType<FluentIcon>().Single(i => i.Icon == Icon.Brain);
            InvokeButton(category);
            Check(chevron.Icon == Icon.ChevronDown && brain.Icon == Icon.Brain, context + ": collapse changes only the named chevron");
            var pageType = typeof(ChatShell).Assembly.GetType("AIHappey.Desktop.Core.DesktopPage")!;
            typeof(ChatShell).GetMethod("ShowPage", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(shell, [Enum.Parse(pageType, "Models")]);
            Check(chevron.Icon == Icon.ChevronUp && brain.Icon == Icon.Brain, context + ": navigation re-expands chevron without replacing brain");
            Check(!Descendants(shell).OfType<IconElement>().Any(i => i is Microsoft.UI.Xaml.Controls.SymbolIcon or PathIcon),
                context + ": app navigation and toolbars no longer use legacy symbol or copied path artwork");
        }
        finally { await shell.ShutdownAsync(); window.Content = null; }
        File.WriteAllLines(report, results);
    }

    private static async Task<byte[]> IconPixelsAsync(FrameworkElement element)
    {
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(element, 48, 48);
        using var reader = DataReader.FromBuffer(await bitmap.GetPixelsAsync());
        var pixels = new byte[reader.UnconsumedBufferLength];
        reader.ReadBytes(pixels);
        return pixels;
    }
}
