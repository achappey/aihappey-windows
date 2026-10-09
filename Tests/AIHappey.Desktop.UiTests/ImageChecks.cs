using System.Reflection;
using System.Text.Json.Nodes;
using AIHappey.Desktop.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media.Imaging;

namespace AIHappey.Desktop.UiTests;

public partial class App
{
    private async Task CheckImagesAsync(ElementTheme theme)
    {
        var context = "Images / " + theme;
        var directory = Path.Combine(Path.GetTempPath(), "aihappey-image-ui-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        const string png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Wl6zGAAAAAASUVORK5CYII=";
        try
        {
            var page = new ImagesPage { RequestedTheme = theme };
            window!.Content = page; window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 900)); window.Activate(); await Task.Delay(180); page.UpdateLayout();
            var prompt = (TextBox)page.FindName("Prompt"); var grid = (GridView)page.FindName("LibraryGrid");
            Check(prompt.AcceptsReturn && prompt.MinHeight == 96 && AutomationProperties.GetName(prompt).Length > 0, context + ": native accessible multiline composer");
            Check(((Button)page.FindName("Send")).IsEnabled == false, context + ": empty/no-model prompt cannot generate");
            page.SetModelAvailable(true); prompt.Text = "A lighthouse"; await Task.Delay(60);
            Check(((Button)page.FindName("Send")).IsEnabled, context + ": selected model and prompt enable send");
            var plus = (MenuFlyout)((Button)page.FindName("AddAttachment")).Flyout;
            var menu = (MenuFlyout)prompt.ContextFlyout;
            Check(plus.Items.OfType<MenuFlyoutItem>().Select(i => i.Text).SequenceEqual(["Attachments", "Link"])
                && menu.Items.OfType<MenuFlyoutItem>().Any(i => i.Text == "Attachments") && menu.Items.OfType<MenuFlyoutItem>().Any(i => i.Text == "Link"), context + ": plus and context menus support files and links");
            var file = ComposerAttachment.Local("source.png", "image/png", Convert.FromBase64String(png));
            page.AdmitAttachment(file); page.AdmitAttachment(ComposerAttachment.Link("https://example.invalid/image.png", "image/png"));
            await Task.Delay(80); page.UpdateLayout();
            Check(page.Attachments.Count == 2 && Descendants(page).OfType<Border>().Count(b => b.Name == "ImageAttachmentTag") == 2, context + ": local/link attachment tags");
            InvokeButton(Descendants(page).OfType<Button>().First(b => b.Name == "ImageRemoveAttachment"));
            await Task.Delay(60);
            Check(page.Attachments.Count == 1 && page.Attachments[0].IsLink, context + ": removing an attachment updates image-only draft");
            page.SetBusy(true, true);
            Check(prompt.IsReadOnly && !((Button)page.FindName("AddAttachment")).IsEnabled && ((Button)page.FindName("Stop")).Visibility == Visibility.Visible, context + ": generation busy state reserves input and exposes Stop");
            page.SetBusy(false, false);
            var path = Path.Combine(directory, "image.png"); await File.WriteAllBytesAsync(path, Convert.FromBase64String(png));
            var output = new StoredImageOutput { File = "image.png", MediaType = "image/png", Cost = 0.04 };
            var image = new LibraryImage(new() { Id = Guid.NewGuid().ToString("N"), Model = "openai/image", Prompt = "A lighthouse" }, output, path);
            page.SetItems([image], 2); await Task.Delay(120); page.UpdateLayout();
            Check(grid.Items.Count == 3 && grid.ItemsPanelRoot is ItemsWrapGrid, context + ": virtualized native gallery with pending placeholders");
            Check(((ImageTile)grid.Items[2]).Thumbnail is BitmapImage bitmap && bitmap.DecodePixelWidth == 320 && ((ImageTile)grid.Items[0]).Pending, context + ": bounded thumbnails and pending tiles");
            Check(((HyperlinkButton)page.FindName("OpenFolder")).Content.ToString() == "Open folder", context + ": subtle Explorer link above gallery");
            foreach (var width in new[] { 480, 720, 1280 })
            {
                window.AppWindow.Resize(new Windows.Graphics.SizeInt32(width, 900)); await Task.Delay(90); page.UpdateLayout();
                var panel = (ItemsWrapGrid)grid.ItemsPanelRoot;
                Check(panel.MaximumRowsOrColumns >= 1 && panel.MaximumRowsOrColumns <= 5 && prompt.ActualWidth <= page.ActualWidth
                    && Descendants(grid).OfType<ScrollViewer>().First().ScrollableWidth < 1, context + $": native responsive gallery at {width}px");
            }
            page.RequestedTheme = theme == ElementTheme.Dark ? ElementTheme.Light : ElementTheme.Dark; await Task.Delay(80);
            Check(page.ActualTheme != theme, context + ": runtime native theme switch");
            page.Notice("Test warning", InfoBarSeverity.Warning); Check(Descendants(page).OfType<InfoBar>().Single().IsClosable, context + ": dismissible warning");
            window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 900)); await Task.Delay(80);

            ImagePreferences? saved = null;
            var preferences = new ImagePreferences { StorageRoot = directory }; preferences.ProviderOptions["openai"]["future"] = new JsonObject { ["keep"] = true };
            var dialog = new ImageSettingsDialog(preferences, "openai", directory) { XamlRoot = page.XamlRoot, RequestedTheme = theme, SaveAsync = p => { saved = p; return Task.CompletedTask; } };
            SystemAppearance.PrepareDialog(dialog); var shown = dialog.ShowAsync(); await Task.Delay(140); dialog.UpdateLayout();
            Check(Descendants(dialog).OfType<ToggleButton>().Any(t => t.Name == "ImageProviderTab" && t.Content.ToString() == "OpenAI"), context + ": OpenAI provider tab for OpenAI selection");
            var count = Descendants(dialog).OfType<Slider>().Single(s => s.Name == "ImageCount"); count.Value = 3;
            var batch = Descendants(dialog).OfType<TextBox>().Single(t => t.Name == "ImageBatchLimit"); batch.Text = "0";
            InvokeButton(Descendants(dialog).OfType<Button>().Single(b => b.Name == "CloseButton")); await Task.Delay(100);
            Check(shown.Status == Windows.Foundation.AsyncStatus.Started && saved is null, context + ": invalid batch size blocks save/close"); batch.Text = "2";
            var providerTab = Descendants(dialog).OfType<ToggleButton>().Single(t => t.Name == "ImageProviderTab");
            ((IToggleProvider)new ToggleButtonAutomationPeer(providerTab).GetPattern(PatternInterface.Toggle)).Toggle(); await Task.Delay(80);
            Check(Descendants(dialog).OfType<ComboBox>().Count(c => c.Name.StartsWith("ImageOpenAI")) == 3, context + ": all three OpenAI image settings rendered");
            Descendants(dialog).OfType<ComboBox>().Single(c => c.Name == "ImageOpenAIquality").SelectedIndex = 3;
            InvokeButton(Descendants(dialog).OfType<Button>().Single(b => b.Name == "CloseButton")); await shown;
            Check(saved?.N == 3 && saved.MaxImagesPerCall == 2 && saved.ProviderOptions["openai"]["quality"]!.GetValue<string>() == "high"
                && saved.ProviderOptions["openai"]["future"]!["keep"]!.GetValue<bool>() && preferences.N == 1, context + ": saved image settings are independent and preserve unknown provider fields");
            foreach (var provider in new string?[] { "other", null })
            {
                dialog = new ImageSettingsDialog(preferences, provider, directory) { XamlRoot = page.XamlRoot, RequestedTheme = theme };
                shown = dialog.ShowAsync(); await Task.Delay(90);
                Check(!Descendants(dialog).OfType<ToggleButton>().Any(t => t.Name == "ImageProviderTab"), context + ": no OpenAI form for " + (provider ?? "no model")); dialog.DiscardOnShutdown = true; dialog.Hide(); await shown;
            }
            var failure = new ImageSettingsDialog(preferences, "openai", directory) { XamlRoot = page.XamlRoot, SaveAsync = _ => throw new IOException("blocked") };
            shown = failure.ShowAsync(); await Task.Delay(90); InvokeButton(Descendants(failure).OfType<Button>().Single(b => b.Name == "CloseButton")); await Task.Delay(90);
            Check(shown.Status == Windows.Foundation.AsyncStatus.Started, context + ": storage failure leaves settings dialog open"); failure.DiscardOnShutdown = true; failure.Hide(); await shown;
            var preview = new ImagePreviewDialog(image) { XamlRoot = page.XamlRoot }; var previewShown = preview.ShowAsync(); await Task.Delay(100);
            Check(Descendants(preview).OfType<Image>().Any(i => i.Name == "FullGeneratedImage")
                && Descendants(preview).OfType<Button>().Count(b => b.Name.StartsWith("ImagePreview")) == 3, context + ": full-resolution native preview and Save/Delete/Add to prompt"); preview.Hide(); await previewShown;
            page.ClearDraft(); Check(page.Attachments.Count == 0 && page.PromptText == "", context + ": partition changes can clear image draft without chat mutation");
            File.WriteAllLines(report, results);
        }
        finally { window!.Content = null; Directory.Delete(directory, true); }
    }
}
