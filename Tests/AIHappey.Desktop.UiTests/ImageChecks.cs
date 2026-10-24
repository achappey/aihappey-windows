using System.Reflection;
using System.Text.Json.Nodes;
using AIHappey.Desktop.Core;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
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
            Check(!Descendants(dialog).OfType<FrameworkElement>().Any(e => e.Name is "ImageStorageCard" or "ImageStoragePath" or "ImageChooseFolder" or "ImageDefaultFolder"),
                context + ": generation settings no longer contain storage controls");
            var outputSection = Descendants(dialog).OfType<SettingsExpander>().Single(section => section.Name == "ImageOutputCard");
            outputSection.IsExpanded = true; await Task.Delay(80); dialog.UpdateLayout();
            var count = Descendants(dialog).OfType<Slider>().Single(s => s.Name == "ImageCount"); count.Value = 3;
            var batch = Descendants(dialog).OfType<TextBox>().Single(t => t.Name == "ImageBatchLimit"); batch.Text = "0";
            InvokeButton(Descendants(dialog).OfType<Button>().Single(b => b.Name == "CloseButton")); await Task.Delay(100);
            Check(shown.Status == Windows.Foundation.AsyncStatus.Started && saved is null, context + ": invalid batch size blocks save/close"); batch.Text = "2";
            var providerTab = Descendants(dialog).OfType<ToggleButton>().Single(t => t.Name == "ImageProviderTab");
            ((IToggleProvider)new ToggleButtonAutomationPeer(providerTab).GetPattern(PatternInterface.Toggle)).Toggle(); await Task.Delay(80);
            Check(Descendants(dialog).OfType<ComboBox>().Count(c => c.Name.StartsWith("ImageOpenAI")) == 3, context + ": all three OpenAI image settings rendered");
            Descendants(dialog).OfType<ComboBox>().Single(c => c.Name == "ImageOpenAIquality").SelectedIndex = 3;
            outputSection.IsExpanded = false; batch.Text = "0";
            InvokeButton(Descendants(dialog).OfType<Button>().Single(b => b.Name == "CloseButton")); await Task.Delay(100);
            Check(shown.Status == Windows.Foundation.AsyncStatus.Started && saved is null && outputSection.IsExpanded && providerTab.IsChecked == false
                && batch.FocusState != FocusState.Unfocused && count.Value == 3,
                context + ": invalid hidden output switches back to General, expands and focuses its field without losing tab edits");
            batch.Text = "2";
            InvokeButton(Descendants(dialog).OfType<Button>().Single(b => b.Name == "CloseButton")); await shown;
            Check(saved?.N == 3 && saved.MaxImagesPerCall == 2 && saved.ProviderOptions["openai"]["quality"]!.GetValue<string>() == "high"
                && saved.ProviderOptions["openai"]["future"]!["keep"]!.GetValue<bool>() && saved.StorageRoot == directory && preferences.N == 1, context + ": saved image settings are independent and preserve storage and unknown provider fields");
            await CheckImageDimensionSettingsAsync(page.XamlRoot, theme, directory);
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
            await CheckImageStorageAsync(page.XamlRoot, theme, directory, path);
            File.WriteAllLines(report, results);
        }
        finally { window!.Content = null; Directory.Delete(directory, true); }
    }

    private async Task CheckImageDimensionSettingsAsync(XamlRoot root, ElementTheme theme, string directory)
    {
        var context = "Image dimension settings / " + theme;
        ImagePreferences? saved = null;
        var original = new ImagePreferences { StorageRoot = directory, Size = "1200x800", AspectRatio = "7:5", N = 4, MaxImagesPerCall = 2, Seed = -9 };
        ImageSettingsDialog Create(ImagePreferences preferences) => new(preferences, null, directory)
        { XamlRoot = root, RequestedTheme = theme, SaveAsync = value => { saved = value; return Task.CompletedTask; } };
        TextBox Input(ImageSettingsDialog dialog, string name) => ImageSettingFields(dialog).OfType<TextBox>().Single(field => field.Name == name);
        ComboBox Preset(ImageSettingsDialog dialog, string name) => ImageSettingFields(dialog).OfType<ComboBox>().Single(field => field.Name == name + "Preset");
        void Close(ImageSettingsDialog dialog) => InvokeButton(Descendants(dialog).OfType<Button>().Single(button => button.Name == "CloseButton"));
        var dialog = Create(original); SystemAppearance.PrepareDialog(dialog);
        var shown = dialog.ShowAsync(); await Task.Delay(120); dialog.UpdateLayout();
        var sections = Descendants(dialog).OfType<SettingsExpander>().ToArray();
        Check(sections.Select(section => section.Name).SequenceEqual(new[] { "ImageSizeCard", "ImageAspectRatioCard", "ImageOutputCard", "ImageOtherCard" })
            && sections.All(section => !section.IsExpanded && section.Content is null)
            && !ImageSettingFields(dialog).OfType<ToggleSwitch>().Any(), context + ": four native expandable sections initially collapsed, without header switches");
        foreach (var section in sections) section.IsExpanded = true;
        await Task.Delay(100); dialog.UpdateLayout();
        foreach (var (name, w, h) in new[] { ("ImageSize", "1200", "800"), ("ImageAspectRatio", "7", "5") })
        {
            var width = Input(dialog, name + "Width"); var height = Input(dialog, name + "Height");
            Check(width.Text == w && height.Text == h && width.IsEnabled && height.IsEnabled
                && width.Header.ToString() == "Width" && height.Header.ToString() == "Height"
                && AutomationProperties.GetName(width).Length > 0 && AutomationProperties.GetName(height).Length > 0
                && (string)((ComboBoxItem)Preset(dialog, name).SelectedItem).Tag == "custom", context + ": separate accessible custom inputs round-trip " + name);
        }
        Check(Input(dialog, "ImageBatchLimit").Header.ToString() == "Maximum images per call" && Input(dialog, "ImageSeed").Header.ToString() == "Seed",
            context + ": optional suffixes removed from labels");
        var manager = new Microsoft.Windows.ApplicationModel.Resources.ResourceManager(); var resourceContext = manager.CreateResourceContext();
        resourceContext.QualifierValues["Language"] = "nl";
        foreach (var (key, expected) in new[] { ("ImageWidth", "Breedte"), ("ImageHeight", "Hoogte"), ("ImageBatchLimit", "Maximum afbeeldingen per aanvraag"), ("ImageSeed", "Seed") })
            Check(manager.MainResourceMap.GetValue("Resources/" + key, resourceContext).ValueAsString == expected, context + ": Dutch label " + key);

        foreach (var windowWidth in new[] { 480, 720, 1280 })
        {
            window!.AppWindow.Resize(new Windows.Graphics.SizeInt32(windowWidth, 900)); await Task.Delay(100); dialog.UpdateLayout();
            var output = Descendants(dialog).OfType<StackPanel>().Single(panel => panel.Name == "ImageOutputFields");
            var countPanel = (StackPanel)output.Children[0]; var batch = Input(dialog, "ImageBatchLimit");
            Check(output.Orientation == Orientation.Vertical && output.Children[1] == batch
                && Math.Abs(countPanel.ActualWidth - batch.ActualWidth) < 1
                && batch.TransformToVisual(output).TransformPoint(new()).Y >= countPanel.ActualHeight,
                context + $": full-width output controls on separate rows at {windowWidth}px");
            foreach (var name in new[] { "ImageSize", "ImageAspectRatio" })
            {
                var select = Preset(dialog, name); var width = Input(dialog, name + "Width"); var height = Input(dialog, name + "Height");
                var row = Descendants(dialog).OfType<Grid>().Single(grid => grid.Name == name + "Fields");
                Check(Grid.GetRow(select) == 0 && Grid.GetColumnSpan(select) == 2 && Grid.GetRow(width) == 1 && Grid.GetRow(height) == 1
                    && Grid.GetColumn(width) == 0 && Grid.GetColumn(height) == 1 && Math.Abs(width.ActualWidth - height.ActualWidth) < 1
                    && width.TransformToVisual(row).TransformPoint(new()).Y >= select.ActualHeight,
                    context + $": equal-width dimension inputs below dropdown for {name} at {windowWidth}px");
            }
            Check(Descendants(dialog).OfType<ScrollViewer>().All(scroll => scroll.ScrollableWidth < 1)
                && new[] { "ImageSizeWidth", "ImageSizeHeight", "ImageAspectRatioWidth", "ImageAspectRatioHeight" }.All(name => Input(dialog, name).ActualWidth > 40),
                context + $": dimension inputs remain usable without horizontal scrolling at {windowWidth}px");
        }
        foreach (var section in sections) section.IsExpanded = false;
        await Task.Delay(40);
        foreach (var section in sections) section.IsExpanded = true;
        await Task.Delay(80);
        Check(Input(dialog, "ImageSizeWidth").Text == "1200" && Input(dialog, "ImageAspectRatioHeight").Text == "5" && Input(dialog, "ImageSeed").Text == "-9",
            context + ": collapse and expand preserve dimension and numeric edits");
        foreach (var name in new[] { "ImageSize", "ImageAspectRatio" })
        {
            var select = Preset(dialog, name); var width = Input(dialog, name + "Width"); var height = Input(dialog, name + "Height");
            select.SelectedIndex = 0; await Task.Delay(30);
            Check(width.Visibility == Visibility.Visible && height.Visibility == Visibility.Visible && !width.IsEnabled && !height.IsEnabled
                && width.Text == "" && height.Text == "", context + ": provider default clears and disables visible inputs for " + name);
            select.SelectedIndex = select.Items.Count - 1; await Task.Delay(30);
            Check(width.IsEnabled && height.IsEnabled && width.Text == (name == "ImageSize" ? "256" : "1")
                && height.Text == width.Text, context + ": Custom starts with an editable complete pair for " + name);
            select.SelectedIndex = 2; await Task.Delay(30);
            Check(width.Text == (name == "ImageSize" ? "512" : "4") && height.Text == (name == "ImageSize" ? "512" : "3"), context + ": preset populates both inputs for " + name);
            width.Text = name == "ImageSize" ? "1024" : "16"; height.Text = name == "ImageSize" ? "1536" : "9"; await Task.Delay(30);
            Check((string)((ComboBoxItem)select.SelectedItem).Tag == (name == "ImageSize" ? "1024x1536" : "16:9"), context + ": editing inputs synchronizes preset selection for " + name);
            width.Text = name == "ImageSize" ? "1200" : "7"; height.Text = name == "ImageSize" ? "800" : "5"; await Task.Delay(30);
            Check((string)((ComboBoxItem)select.SelectedItem).Tag == "custom", context + ": editing to a non-preset selects Custom for " + name);
            foreach (var invalid in new[] { "", "0", "-1", "1.5", "1000000", "abc" })
            {
                var section = sections.Single(section => section.Name == name + "Card");
                width.Text = invalid; section.IsExpanded = false; await Task.Delay(20); Close(dialog); await Task.Delay(80);
                Check(shown.Status == Windows.Foundation.AsyncStatus.Started && saved is null
                    && section.IsExpanded && width.FocusState != FocusState.Unfocused
                    && Descendants(dialog).OfType<TextBlock>().Single(text => text.Name == "ImageSettingsValidation").Visibility == Visibility.Visible,
                    context + ": invalid/incomplete width blocks close for " + name + ": " + invalid);
            }
            width.Text = name == "ImageSize" ? "1200" : "7"; height.Text = ""; await Task.Delay(20); Close(dialog); await Task.Delay(40);
            Check(shown.Status == Windows.Foundation.AsyncStatus.Started && saved is null, context + ": incomplete height blocks close for " + name);
            height.Text = name == "ImageSize" ? "800" : "5";
        }
        Input(dialog, "ImageBatchLimit").Text = ""; Input(dialog, "ImageSeed").Text = ""; await Task.Delay(30); Close(dialog); await shown;
        Check(saved?.Size == "1200x800" && saved.AspectRatio == "7:5" && saved.MaxImagesPerCall is null && saved.Seed is null && saved.N == 4
            && original.MaxImagesPerCall == 2 && original.Seed == -9, context + ": custom dimensions serialize unchanged and optional numbers remain optional");
        var roundTrip = saved!; saved = null; dialog = Create(roundTrip); shown = dialog.ShowAsync(); await Task.Delay(90); Close(dialog); await shown;
        Check(saved?.Size == roundTrip.Size && saved.AspectRatio == roundTrip.AspectRatio, context + ": reopening and closing preserves custom dimensions");
        saved = null; dialog = Create(original); shown = dialog.ShowAsync(); await Task.Delay(90);
        InvokeButton(Descendants(dialog).OfType<Button>().Single(button => button.Name == "PrimaryButton")); await Task.Delay(60);
        Check(Descendants(dialog).OfType<SettingsExpander>().All(section => !section.IsExpanded)
            && new[] { "ImageSizeWidth", "ImageSizeHeight", "ImageAspectRatioWidth", "ImageAspectRatioHeight" }.All(name => !Input(dialog, name).IsEnabled && Input(dialog, name).Text == ""),
            context + ": Restore defaults rebuilds cleared, disabled dimension inputs");
        Close(dialog); await shown;
        Check(saved?.Size is null && saved?.AspectRatio is null && saved?.N == 1 && saved.MaxImagesPerCall is null && saved.Seed is null && saved.StorageRoot == directory,
            context + ": Restore defaults saves provider defaults and preserves storage");
    }

    // Collapsed SettingsExpander bodies may be detached from the visual tree.
    private static IEnumerable<FrameworkElement> ImageSettingFields(ImageSettingsDialog dialog)
    {
        static IEnumerable<FrameworkElement> Fields(Panel panel)
        {
            foreach (var child in panel.Children.OfType<FrameworkElement>())
            {
                yield return child;
                if (child is Panel nested) foreach (var field in Fields(nested)) yield return field;
            }
        }
        return Descendants(dialog).OfType<SettingsExpander>().SelectMany(section => Fields((Panel)section.ItemsHeader));
    }

    private async Task CheckImageStorageAsync(XamlRoot root, ElementTheme theme, string directory, string originalImage)
    {
        var context = "Image storage settings / " + theme;
        var original = new DesktopSettings { Images = new() { StorageRoot = directory, N = 8, Seed = -9, Size = "1024x1536", MaskPath = originalImage } };
        original.Images.ProviderOptions["future"] = new() { ["keep"] = true };
        original.Transcriptions.ProviderOptions["future"] = new() { ["keep"] = true };
        SettingsDialog Create() => new(original, true, "en", []) { XamlRoot = root, RequestedTheme = theme };
        void SelectImage(SettingsDialog dialog)
        {
            ((NavigationView)dialog.FindName("Tabs")).SelectedItem = dialog.FindName("ArtificialIntelligenceTab");
            dialog.AiModelView.SelectedItem = dialog.AiModelView.MenuItems.OfType<NavigationViewItem>().Single(item => (string)item.Tag == "image");
            dialog.UpdateLayout();
        }
        Button Action(SettingsDialog dialog, string name) => Descendants(dialog).OfType<Button>().Single(button => button.Name == name);
        var canceled = Create(); SystemAppearance.PrepareDialog(canceled);
        var shown = canceled.ShowAsync(); await Task.Delay(100); SelectImage(canceled);
        var storage = Descendants(canceled).OfType<StackPanel>().Single(panel => panel.Name == "ImageStorageSettings");
        Check(VisualTreeHelper.GetParent(storage) is StackPanel imagePage && imagePage.Children.Last() == storage
            && imagePage.Children.OfType<ToggleSwitch>().Single().Name == "AiChatWith_image"
            && Descendants(storage).OfType<CommunityToolkit.WinUI.Controls.SettingsCard>().Single().Name == "ImageStorageCard",
            context + ": complete native Storage card follows existing Image model settings");
        Check(Descendants(storage).OfType<TextBlock>().Single(text => text.Name == "ImageStoragePath").Text == directory
            && new[] { "ImageChooseFolder", "ImageDefaultFolder" }.All(name => !string.IsNullOrEmpty(AutomationProperties.GetName(Action(canceled, name)))),
            context + ": custom path and accessible native folder actions");
        InvokeButton(Action(canceled, "ImageDefaultFolder")); canceled.UpdateLayout();
        Check(DesktopBranding.AppName == "UI image app" && Descendants(storage).OfType<TextBlock>().Single(text => text.Name == "ImageStoragePath").Text
            == Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "UI image app", "Images"))
            && original.Images.StorageRoot == directory, context + ": default-folder action uses injected app branding and edits only draft");
        InvokeButton(Action(canceled, "CloseButton")); await shown;
        Check(canceled.Result is null && original.Images.StorageRoot == directory && File.Exists(originalImage), context + ": Cancel leaves storage and files untouched");

        var unchanged = Create(); shown = unchanged.ShowAsync(); await Task.Delay(100);
        InvokeButton(Action(unchanged, "PrimaryButton")); await shown;
        Check(unchanged.Result?.Images.StorageRoot == directory && unchanged.Result.Images.N == 8 && unchanged.Result.Images.Seed == -9
            && unchanged.Result.Images.MaskPath == originalImage && unchanged.Result.Images.ProviderOptions["future"]["keep"]!.GetValue<bool>()
            && unchanged.Result.Transcriptions.ProviderOptions["future"]["keep"]!.GetValue<bool>(),
            context + ": saving unrelated user settings preserves custom storage, inference, masks, provider fields, and transcriptions");
        unchanged.Result!.Images.ProviderOptions["future"]["keep"] = false;
        Check(original.Images.ProviderOptions["future"]["keep"]!.GetValue<bool>(), context + ": saved image provider options remain deep-cloned");

        var edited = Create(); shown = edited.ShowAsync(); await Task.Delay(100);
        var draft = Field<ImagePreferences>(edited, "imagePreferences");
        // Exercise the save boundary with a real disk error, without automating the OS-owned folder picker.
        draft.StorageRoot = originalImage;
        InvokeButton(Action(edited, "PrimaryButton")); await Task.Delay(80);
        Check(shown.Status == Windows.Foundation.AsyncStatus.Started && edited.Result is null
            && (string)((NavigationViewItem)edited.AiModelView.SelectedItem).Tag == "image"
            && Descendants(edited).OfType<TextBlock>().Single(text => text.Name == "ImageStorageValidation").Visibility == Visibility.Visible,
            context + ": invalid disk destination keeps dialog open and exposes error on Image tab");
        var nextRoot = Path.Combine(directory, "new-library"); draft.StorageRoot = nextRoot;
        InvokeButton(Action(edited, "PrimaryButton")); await shown;
        Check(edited.Result?.Images.StorageRoot == nextRoot && Directory.Exists(nextRoot) && File.Exists(originalImage) && original.Images.StorageRoot == directory,
            context + ": Save prepares new storage without moving files or mutating original settings");
    }
}
