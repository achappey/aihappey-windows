using System.Reflection;
using System.Text.Json.Nodes;
using AIHappey.Desktop.Core;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.UI.ViewManagement;

namespace AIHappey.Desktop.UiTests;

public partial class App
{
    private async Task CheckVideosAsync(ElementTheme theme)
    {
        var context = "Videos / " + theme;
        var directory = Path.Combine(Path.GetTempPath(), "aihappey-video-ui-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        const string png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Wl6zGAAAAAASUVORK5CYII=";
        try
        {
            var page = new VideosPage { RequestedTheme = theme }; window!.Content = page;
            window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 900)); window.Activate(); await Task.Delay(150); page.UpdateLayout();
            var prompt = (TextBox)page.FindName("Prompt"); var grid = (GridView)page.FindName("LibraryGrid"); var send = (Button)page.FindName("Send");
            Check(prompt.AcceptsReturn && prompt.MinHeight == 96 && AutomationProperties.GetName(prompt).Length > 0 && !send.IsEnabled, context + ": native accessible multiline composer with disabled empty/no-model send");
            page.SetModelAvailable(true); prompt.Text = "A lighthouse"; await Task.Delay(30); Check(send.IsEnabled, context + ": prompt and selected video model enable send");
            Check(((MenuFlyout)((Button)page.FindName("AddAttachment")).Flyout).Items.OfType<MenuFlyoutItem>().Select(i => i.Text)
                .SequenceEqual([DesktopResources.Get("Attachments"), DesktopResources.Get("Link")]), context + ": native attachment flyout exposes files and media URLs");
            var image = ComposerAttachment.Local("reference.png", "image/png", Convert.FromBase64String(png));
            page.AdmitAttachment(image); page.AdmitAttachment(ComposerAttachment.Local("source.mp4", "video/mp4", new byte[] { 1, 2, 3 }));
            page.AdmitAttachment(ComposerAttachment.Link("https://example.invalid/ref.png", "image/png"));
            await Task.Delay(60); page.UpdateLayout();
            Check(page.Attachments.Count == 2 && page.Attachments.Count(f => !f.IsLink) == 1 && page.Attachments[0].Name == "source.mp4", context + ": local composer attachment replaces previous input while URL references remain");
            InvokeButton(Descendants(page).OfType<Button>().First(b => b.Name == "VideoRemoveAttachment")); Check(page.Attachments.Count == 1 && page.Attachments[0].IsLink, context + ": native remove tag updates only video draft");
            page.SetSubmitting(true); Check(!send.IsEnabled && prompt.IsReadOnly && !((Button)page.FindName("Settings")).IsEnabled, context + ": submission reserves composer without affecting library navigation"); page.SetSubmitting(false);
            var job = new VideoJob { Model = "p/video", RequestedVideos = 2, State = "pending" };
            page.SetItems([], [job]); await Task.Delay(100); page.UpdateLayout();
            Check(grid.Items.Count == 2 && grid.ItemsPanelRoot is ItemsWrapGrid && ((TextBlock)page.FindName("EmptyLibrary")).Visibility == Visibility.Collapsed, context + ": pending jobs render native virtualized gallery placeholders");
            var firstCard = grid.Items[0]; var shimmer = Descendants(page).OfType<VideoShimmer>().First();
            shimmer.MotionAllowed = false; shimmer.RefreshAnimation(); Check(!shimmer.IsAnimating && AutomationProperties.GetName(shimmer).Contains(DesktopResources.Get("VideoProcessing")), context + ": reduced-motion fallback keeps accessible queued status");
            shimmer.MotionAllowed = true; shimmer.RefreshAnimation();
            Check(shimmer.IsAnimating == (new UISettings().AnimationsEnabled && !new AccessibilitySettings().HighContrast), context + ": shimmer honors Windows animation/high-contrast policy");
            page.Visibility = Visibility.Collapsed; page.RefreshShimmers(); Check(!shimmer.IsAnimating, context + ": hidden page stops shimmer animation"); page.Visibility = Visibility.Visible; page.RefreshShimmers();
            Check(prompt.Focus(FocusState.Keyboard), context + ": composer supports keyboard focus");
            page.SetItems([], [job.Snapshot()]); Check(ReferenceEquals(firstCard, grid.Items[0]) && ReferenceEquals(Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(page.XamlRoot), prompt), context + ": polling refresh preserves card identity and keyboard focus");
            foreach (var width in new[] { 480, 720, 1280 })
            {
                window.AppWindow.Resize(new Windows.Graphics.SizeInt32(width, 900)); await Task.Delay(70); page.UpdateLayout();
                Check(((ItemsWrapGrid)grid.ItemsPanelRoot).MaximumRowsOrColumns is >= 1 and <= 3 && prompt.ActualWidth <= page.ActualWidth
                    && Descendants(grid).OfType<ScrollViewer>().First().ScrollableWidth < 1, context + $": native gallery fits {width}px viewport");
            }
            page.RequestedTheme = theme == ElementTheme.Light ? ElementTheme.Dark : ElementTheme.Light; await Task.Delay(50);
            Check(page.ActualTheme != theme && ReferenceEquals(firstCard, grid.Items[0]), context + ": runtime theme change retains gallery identity");
            var path = Path.Combine(directory, "video.mp4"); await File.WriteAllBytesAsync(path, new byte[] { 1, 2, 3 });
            var video = new LibraryVideo(new() { Id = Guid.NewGuid().ToString("N"), Prompt = "A lighthouse", Model = "p/video" }, new("video.mp4", "video/mp4", .25), path);
            page.SetItems([video], []); await Task.Delay(80); Check(grid.Items.Count == 1 && ((Border)grid.Items[0]).Tag is LibraryVideo && !Descendants(page).OfType<VideoShimmer>().Any(), context + ": completed output replaces placeholders without inline autoplay");
            page.Notice("Fixture warning", InfoBarSeverity.Warning); Check(Descendants(page).OfType<InfoBar>().Single().IsClosable, context + ": warnings use dismissible native InfoBar");
            window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 900)); await Task.Delay(50);
            var inputStore = new VideoInputStore(Path.Combine(directory, "inputs")); var input = await inputStore.AddAsync(image);
            var preferences = new VideoPreferences { StorageRoot = directory, InputReferences = [input], FirstFrame = input, LastFrame = input };
            preferences.ProviderOptions["future"] = new() { ["keep"] = true }; VideoPreferences? saved = null;
            var dialog = new VideoSettingsDialog(preferences, inputStore) { XamlRoot = page.XamlRoot, RequestedTheme = theme, SaveAsync = next => { saved = next; return Task.CompletedTask; } };
            SystemAppearance.PrepareDialog(dialog); var shown = dialog.ShowAsync(); await Task.Delay(120); dialog.UpdateLayout();
            var tabs = Descendants(dialog).OfType<NavigationView>().Single(t => t.Name == "VideoSettingsTabs"); CheckNativeTabs(tabs, context + " settings");
            var sections = Descendants(dialog).OfType<SettingsExpander>().ToArray();
            Check(sections.Select(s => s.Name).SequenceEqual(["VideoSizeCard", "VideoAspectCard", "VideoOutputCard", "VideoPropertiesCard", "VideoReferencesCard", "VideoFramesCard", "VideoOtherCard"])
                && sections.All(s => !s.IsExpanded && s.Content is null), context + ": seven native cards initially collapsed without header switches");
            Check(!VideoSettingFields(dialog).Any(e => e.Name is "VideoProviderOptions" or "VideoProviderOptionsCard")
                && !Descendants(dialog).OfType<FrameworkElement>().Any(e => e.Name is "VideoFirstFrameCard" or "VideoLastFrameCard"), context + ": raw provider editor removed and both frames share one card");
            foreach (var section in sections) section.IsExpanded = true;
            await Task.Delay(80); dialog.UpdateLayout();
            Check(!Descendants(dialog).OfType<FrameworkElement>().Any(e => e.Name == "VideoStorageCard")
                && Descendants(dialog).OfType<Button>().Any(b => b.Name == "VideoRemove_references")
                && Descendants(dialog).OfType<Button>().Any(b => b.Name == "VideoRemove_first_frame") && Descendants(dialog).OfType<Button>().Any(b => b.Name == "VideoRemove_last_frame"),
                context + ": native generation settings include references and both frames, not storage");
            var batch = Descendants(dialog).OfType<TextBox>().Single(t => t.Name == "VideoBatchLimit"); batch.Text = "0";
            InvokeButton(Descendants(dialog).OfType<Button>().Single(b => b.Name == "CloseButton")); await Task.Delay(70);
            Check(shown.Status == Windows.Foundation.AsyncStatus.Started && saved is null, context + ": invalid settings cannot close or mutate live preferences"); batch.Text = "2";
            Descendants(dialog).OfType<Slider>().Single(s => s.Name == "VideoCount").Value = 3;
            Check(Descendants(dialog).OfType<TextBlock>().Single(t => t.Name == "VideoCountLabel").Text == DesktopResources.Format("VideoCountValue", 3), context + ": output count label follows slider");
            InvokeButton(Descendants(dialog).OfType<Button>().Single(b => b.Name == "VideoRemove_first_frame"));
            var frames = sections.Single(s => s.Name == "VideoFramesCard");
            Check(Descendants(frames).OfType<Button>().Any(b => b.Name == "VideoRemove_last_frame")
                && Descendants(frames).OfType<Button>().Count(b => b.Name.StartsWith("VideoAdd_")) == 2, context + ": removing first frame preserves last frame and both add actions inside Frames");
            foreach (var width in new[] { 500, 1000 })
            {
                window.AppWindow.Resize(new Windows.Graphics.SizeInt32(width, 900)); await Task.Delay(70); dialog.UpdateLayout();
                Check(Descendants(dialog).OfType<ScrollViewer>().All(v => v.ScrollableWidth < 1) && tabs.PaneDisplayMode == NavigationViewPaneDisplayMode.Top, context + $": settings use native tab overflow at {width}px");
                var audioPanel = Descendants(dialog).OfType<StackPanel>().Single(p => p.Name == "VideoAudioFields");
                var audioLabel = Descendants(audioPanel).OfType<TextBlock>().Single(t => t.Name == "VideoGenerateAudioLabel");
                var audio = Descendants(audioPanel).OfType<ToggleSwitch>().Single();
                foreach (var enabled in new[] { false, true })
                {
                    audio.IsOn = enabled; dialog.UpdateLayout();
                    Check(audio.Header is null && AutomationProperties.GetName(audio) == DesktopResources.Get("VideoGenerateAudio")
                        && audio.TransformToVisual(audioPanel).TransformPoint(new()).Y >= audioLabel.ActualHeight + 7
                        && audio.ActualWidth <= audioPanel.ActualWidth, context + $": accessible audio label never overlaps {(enabled ? "On" : "Off")} switch at {width}px");
                }
            }
            InvokeButton(Descendants(dialog).OfType<Button>().Single(b => b.Name == "CloseButton")); await shown;
            Check(saved?.N == 3 && saved.GenerateAudio && saved.MaxVideosPerCall == 2 && saved.FirstFrame is null && saved.LastFrame is not null && saved.InputReferences.Count == 1
                && saved.ProviderOptions["future"]["keep"]!.GetValue<bool>() && saved.StorageRoot == directory && preferences.FirstFrame is not null && preferences.N == 1,
                context + ": saved video settings are cloned and preserve storage, reference inputs and unknown provider fields; saved=" + System.Text.Json.JsonSerializer.Serialize(saved)
                + "; original=" + System.Text.Json.JsonSerializer.Serialize(preferences));
            var failing = new VideoSettingsDialog(preferences, inputStore) { XamlRoot = page.XamlRoot, SaveAsync = _ => throw new IOException("blocked") };
            shown = failing.ShowAsync(); await Task.Delay(70); InvokeButton(Descendants(failing).OfType<Button>().Single(b => b.Name == "CloseButton")); await Task.Delay(60);
            Check(shown.Status == Windows.Foundation.AsyncStatus.Started, context + ": failed settings save keeps dialog open"); failing.DiscardOnShutdown = true; failing.Hide(); await shown;
            await CheckVideoDimensionSettingsAsync(page.XamlRoot, theme, directory, inputStore, input);
            var preview = new VideoPreviewDialog(video) { XamlRoot = page.XamlRoot }; var previewShown = preview.ShowAsync(); await Task.Delay(100);
            Check(Descendants(preview).OfType<MediaPlayerElement>().Single().AutoPlay == false && preview.PrimaryButtonText == DesktopResources.Get("Save") && preview.SecondaryButtonText == DesktopResources.Get("Delete"), context + ": native explicit playback preview exposes Save and Delete");
            preview.Hide(); await previewShown; Check(typeof(VideoPreviewDialog).GetField("player", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(preview) is null, context + ": preview close releases media player and source");
            await CheckVideoStorageAsync(page.XamlRoot, theme, directory, path);
            window.Content = null;
            await CheckVideoNavigationAsync(theme, directory);
        }
        finally { window!.Content = null; Directory.Delete(directory, true); }
    }
    // Collapsed native card bodies need not be attached to the visual tree.
    private static IEnumerable<FrameworkElement> VideoSettingFields(VideoSettingsDialog dialog)
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
    private async Task CheckVideoDimensionSettingsAsync(XamlRoot root, ElementTheme theme, string directory, VideoInputStore store, VideoInputFile input)
    {
        var context = "Video dimension settings / " + theme;
        VideoPreferences? saved = null;
        var original = new VideoPreferences { StorageRoot = directory, PollingIntervalSeconds = 17, Resolution = "1200x800", AspectRatio = "7:5", N = 4, MaxVideosPerCall = 2,
            Duration = 8, Fps = 24, Seed = -9, GenerateAudio = true, InputReferences = [input], FirstFrame = input, LastFrame = input };
        original.ProviderOptions["future"] = new() { ["nested"] = new JsonObject { ["keep"] = new JsonArray(true, "value", 42) } };
        VideoSettingsDialog Create(VideoPreferences preferences) => new(preferences, store)
        { XamlRoot = root, RequestedTheme = theme, SaveAsync = value => { saved = value; return Task.CompletedTask; } };
        TextBox Input(VideoSettingsDialog dialog, string name) => VideoSettingFields(dialog).OfType<TextBox>().Single(field => field.Name == name);
        ComboBox Preset(VideoSettingsDialog dialog, string name) => VideoSettingFields(dialog).OfType<ComboBox>().Single(field => field.Name == name + "Preset");
        void Close(VideoSettingsDialog dialog) => InvokeButton(Descendants(dialog).OfType<Button>().Single(b => b.Name == "CloseButton"));
        var dialog = Create(original); SystemAppearance.PrepareDialog(dialog);
        var shown = dialog.ShowAsync(); await Task.Delay(100);
        var sections = Descendants(dialog).OfType<SettingsExpander>().ToArray();
        foreach (var section in sections) section.IsExpanded = true;
        await Task.Delay(80); dialog.UpdateLayout();
        foreach (var (name, w, h, presets) in new[] { ("VideoResolution", "1200", "800", VideoPreferences.ResolutionPresets), ("VideoAspectRatio", "7", "5", VideoPreferences.AspectPresets) })
        {
            var width = Input(dialog, name + "Width"); var height = Input(dialog, name + "Height"); var select = Preset(dialog, name);
            Check(width.Text == w && height.Text == h && width.IsEnabled && height.IsEnabled
                && width.Header.ToString() == DesktopResources.Get("ImageWidth") && height.Header.ToString() == DesktopResources.Get("ImageHeight")
                && AutomationProperties.GetName(width).Length > 0 && AutomationProperties.GetName(height).Length > 0
                && (string)((ComboBoxItem)select.SelectedItem).Tag == "custom", context + ": accessible custom width and height for " + name);
            Check(select.Items.Cast<ComboBoxItem>().Skip(1).SkipLast(1).Select(i => (string)i.Tag).SequenceEqual(presets), context + ": existing video presets unchanged for " + name);
        }
        foreach (var windowWidth in new[] { 480, 720, 1280 })
        {
            window!.AppWindow.Resize(new Windows.Graphics.SizeInt32(windowWidth, 900)); await Task.Delay(100); dialog.UpdateLayout();
            foreach (var name in new[] { "VideoResolution", "VideoAspectRatio" })
            {
                var select = Preset(dialog, name); var width = Input(dialog, name + "Width"); var height = Input(dialog, name + "Height");
                var row = VideoSettingFields(dialog).OfType<Grid>().Single(g => g.Name == name + "Fields");
                Check(Grid.GetColumnSpan(select) == 2 && Grid.GetRow(width) == 1 && Grid.GetRow(height) == 1 && Grid.GetColumn(height) == 1
                    && Math.Abs(width.ActualWidth - height.ActualWidth) < 1 && width.ActualWidth > 40
                    && width.TransformToVisual(row).TransformPoint(new()).Y >= select.ActualHeight, context + $": equal-width inputs below preset for {name} at {windowWidth}px");
            }
            var output = sections.Single(s => s.Name == "VideoOutputCard"); var countPanel = VideoSettingFields(dialog).OfType<StackPanel>().Single(p => p.Name == "VideoCountPanel");
            var batch = Input(dialog, "VideoBatchLimit"); var body = (Panel)output.ItemsHeader;
            Check(batch.TransformToVisual(body).TransformPoint(new()).Y >= countPanel.ActualHeight && Math.Abs(batch.ActualWidth - countPanel.ActualWidth) < 1
                && Descendants(dialog).OfType<ScrollViewer>().All(s => s.ScrollableWidth < 1), context + $": output fields stack full-width without horizontal scrolling at {windowWidth}px");
        }
        foreach (var section in sections) section.IsExpanded = false;
        await Task.Delay(20);
        foreach (var section in sections) section.IsExpanded = true;
        await Task.Delay(60);
        Check(Input(dialog, "VideoResolutionWidth").Text == "1200" && Input(dialog, "VideoAspectRatioHeight").Text == "5" && Input(dialog, "VideoSeed").Text == "-9", context + ": collapse/reopen retains edits");
        foreach (var (name, first, second, known, sectionName) in new[] { ("VideoResolution", "640", "480", "1920x1080", "VideoSizeCard"), ("VideoAspectRatio", "16", "9", "4:3", "VideoAspectCard") })
        {
            var select = Preset(dialog, name); var width = Input(dialog, name + "Width"); var height = Input(dialog, name + "Height");
            select.SelectedIndex = 0;
            Check(!width.IsEnabled && !height.IsEnabled && width.Text == "" && height.Text == "", context + ": provider default clears and disables " + name);
            select.SelectedIndex = select.Items.Count - 1;
            Check(width.IsEnabled && height.IsEnabled && width.Text == first && height.Text == second, context + ": Custom starts with complete pair for " + name);
            select.SelectedItem = select.Items.Cast<ComboBoxItem>().Single(i => (string)i.Tag == known);
            var parts = known.Split(name == "VideoResolution" ? 'x' : ':');
            Check(width.Text == parts[0] && height.Text == parts[1], context + ": preset populates inputs for " + name);
            width.Text = first; height.Text = second;
            Check((string)((ComboBoxItem)select.SelectedItem).Tag == (name == "VideoResolution" ? "640x480" : "16:9"), context + ": edited pair selects matching preset for " + name);
            width.Text = name == "VideoResolution" ? "1200" : "7"; height.Text = name == "VideoResolution" ? "800" : "5";
            Check((string)((ComboBoxItem)select.SelectedItem).Tag == "custom", context + ": non-preset selects Custom for " + name);
            foreach (var bad in new[] { "", "0", "-1", "1.5", "1000000", "abc" })
            {
                width.Text = bad; var section = sections.Single(s => s.Name == sectionName); section.IsExpanded = false;
                Close(dialog); await Task.Delay(80);
                Check(shown.Status == Windows.Foundation.AsyncStatus.Started && saved is null && section.IsExpanded && width.FocusState != FocusState.Unfocused
                    && Descendants(dialog).OfType<TextBlock>().Single(t => t.Name == "VideoSettingsValidation").Visibility == Visibility.Visible, context + ": invalid width reveals/focuses " + name + ": " + bad);
            }
            width.Text = name == "VideoResolution" ? "1200" : "7"; height.Text = ""; Close(dialog); await Task.Delay(60);
            Check(shown.Status == Windows.Foundation.AsyncStatus.Started && saved is null && height.FocusState != FocusState.Unfocused, context + ": incomplete height cannot close " + name);
            height.Text = name == "VideoResolution" ? "800" : "5";
        }
        foreach (var (name, sectionName) in new[] { ("VideoBatchLimit", "VideoOutputCard"), ("VideoDuration", "VideoPropertiesCard"), ("VideoFps", "VideoPropertiesCard"), ("VideoSeed", "VideoOtherCard") })
        {
            var field = Input(dialog, name);
            foreach (var bad in name == "VideoSeed" ? new[] { "1.5", "abc", "2147483648" } : new[] { "0", "-1", "1.5", "abc", "2147483648" })
            {
                field.Text = bad; var section = sections.Single(s => s.Name == sectionName); section.IsExpanded = false;
                Close(dialog); await Task.Delay(70);
                Check(shown.Status == Windows.Foundation.AsyncStatus.Started && saved is null && section.IsExpanded && field.FocusState != FocusState.Unfocused, context + ": invalid numeric input revealed for " + name + ": " + bad);
            }
            field.Text = "";
        }
        Close(dialog); await shown;
        Check(saved?.Resolution == "1200x800" && saved.AspectRatio == "7:5" && saved.N == 4 && saved.GenerateAudio && saved.MaxVideosPerCall is null && saved.Duration is null && saved.Fps is null && saved.Seed is null
            && JsonNode.DeepEquals(saved.ProviderOptions["future"], original.ProviderOptions["future"]) && original.MaxVideosPerCall == 2 && original.Duration == 8, context + ": custom dimensions, optional numbers and unknown provider data round-trip without mutating original");
        var roundTrip = saved!; saved = null; dialog = Create(roundTrip); shown = dialog.ShowAsync(); await Task.Delay(80); Close(dialog); await shown;
        Check(saved?.Resolution == roundTrip.Resolution && saved.AspectRatio == roundTrip.AspectRatio && JsonNode.DeepEquals(saved.ProviderOptions["future"], original.ProviderOptions["future"]), context + ": closing untouched collapsed cards preserves values and nested provider data");
        saved = null; dialog = Create(original); shown = dialog.ShowAsync(); await Task.Delay(80);
        InvokeButton(Descendants(dialog).OfType<Button>().Single(b => b.Name == "PrimaryButton")); await Task.Delay(60);
        Check(Descendants(dialog).OfType<SettingsExpander>().All(s => !s.IsExpanded)
            && new[] { "VideoResolutionWidth", "VideoResolutionHeight", "VideoAspectRatioWidth", "VideoAspectRatioHeight" }.All(n => !Input(dialog, n).IsEnabled && Input(dialog, n).Text == ""), context + ": Restore defaults rebuilds collapsed cards with cleared dimensions");
        Close(dialog); await shown;
        Check(saved?.Resolution is null && saved?.AspectRatio is null && saved?.N == 1 && !saved.GenerateAudio && saved.MaxVideosPerCall is null && saved.Duration is null && saved.Fps is null && saved.Seed is null
            && saved.InputReferences.Count == 0 && saved.FirstFrame is null && saved.LastFrame is null && saved.ProviderOptions.Count == 0
            && saved.StorageRoot == directory && saved.PollingIntervalSeconds == 17 && original.FirstFrame is not null && File.Exists(store.PathFor(input)), context + ": Restore defaults retains storage/polling and existing input files, resets inference/provider defaults only on cloned draft");
    }
    private async Task CheckVideoStorageAsync(XamlRoot root, ElementTheme theme, string directory, string file)
    {
        var context = "Video storage / " + theme; var original = new DesktopSettings { Videos = new() { StorageRoot = directory, N = 7, Duration = 8, PollingIntervalSeconds = 17 } };
        var dialog = new SettingsDialog(original, true, "en", []) { XamlRoot = root }; SystemAppearance.PrepareDialog(dialog);
        var shown = dialog.ShowAsync(); await Task.Delay(80);
        ((NavigationView)dialog.FindName("Tabs")).SelectedItem = dialog.FindName("ArtificialIntelligenceTab");
        dialog.AiModelView.SelectedItem = dialog.AiModelView.MenuItems.OfType<NavigationViewItem>().Single(i => (string)i.Tag == "video"); dialog.UpdateLayout();
        var storage = Descendants(dialog).OfType<StackPanel>().Single(p => p.Name == "VideoStorageSettings");
        Check(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(storage) is StackPanel parent && parent.Children.Last() == storage
            && Descendants(storage).OfType<CommunityToolkit.WinUI.Controls.SettingsCard>().Select(c => c.Name).SequenceEqual(["VideoStorageCard", "VideoPollingCard"]), context + ": Video type has native storage and polling cards below model controls");
        InvokeButton(Descendants(storage).OfType<Button>().Single(b => b.Name == "VideoDefaultFolder"));
        Descendants(storage).OfType<Slider>().Single(s => s.Name == "VideoPollingInterval").Value = 23;
        InvokeButton(Descendants(dialog).OfType<Button>().Single(b => b.Name == "CloseButton")); await shown;
        Check(dialog.Result is null && original.Videos.StorageRoot == directory && original.Videos.PollingIntervalSeconds == 17 && File.Exists(file), context + ": Cancel leaves folder, polling and stored videos unchanged");
        dialog = new(original, true, "en", []) { XamlRoot = root }; shown = dialog.ShowAsync(); await Task.Delay(80);
        var draft = Field<VideoPreferences>(dialog, "videoPreferences"); draft.StorageRoot = file;
        InvokeButton(Descendants(dialog).OfType<Button>().Single(b => b.Name == "PrimaryButton")); await Task.Delay(70);
        Check(shown.Status == Windows.Foundation.AsyncStatus.Started && dialog.Result is null && (string)((NavigationViewItem)dialog.AiModelView.SelectedItem).Tag == "video", context + ": failed folder validation selects Video type and keeps dialog open");
        draft.StorageRoot = Path.Combine(directory, "new-videos"); draft.PollingIntervalSeconds = 23;
        InvokeButton(Descendants(dialog).OfType<Button>().Single(b => b.Name == "PrimaryButton")); await shown;
        Check(dialog.Result?.Videos.StorageRoot == draft.StorageRoot && dialog.Result.Videos.PollingIntervalSeconds == 23 && dialog.Result.Videos.N == 7
            && Directory.Exists(draft.StorageRoot) && File.Exists(file) && original.Videos.StorageRoot == directory, context + ": Save prepares only new folder and preserves inference settings/original files");
    }
    private async Task CheckVideoNavigationAsync(ElementTheme theme, string directory)
    {
        var context = "Video navigation / " + theme; var session = new DesktopSession(new UiHost(true), new UiRuntime(), new() { Videos = new() { StorageRoot = directory } });
        var shell = new ChatShell(session) { RequestedTheme = theme }; window!.Content = shell; window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1000, 900)); window.Activate(); await Task.Delay(150);
        try
        {
            await (Task)Call(shell, "StopVideoJobsAsync")!;
            var partition = ImageLibraryStore.Partition(session); var fake = new UiVideoClient(); using var anonymous = new HttpClient();
            var jobs = new VideoJobCoordinator(Path.Combine(directory, "jobs"), fake, anonymous, () => partition, () => 60);
            typeof(ChatShell).GetField("videoJobs", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(shell, jobs);
            typeof(ChatShell).GetField("aiModelTargets", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(shell, new ChatTarget[] { new("p/video", "Video") { ModelType = "video" }, new("p/language", "Language") { ModelType = "language" } });
            await (Task)Call(shell, "LoadVideosAsync", CancellationToken.None, true)!;
            var pageType = typeof(ChatShell).Assembly.GetType("AIHappey.Desktop.Core.DesktopPage")!;
            Call(shell, "ShowPage", Enum.Parse(pageType, "Videos"));
            var picker = Field<ChatTargetPicker>(shell, "videoTarget"); var page = Field<VideosPage>(shell, "videosPage");
            Check(picker.Text == "p/video" && picker.ItemsSource is IEnumerable<ChatTarget> models && models.All(m => m.ModelType == "video") && page.Visibility == Visibility.Visible, context + ": video-only picker and native navigation are wired");
            var request = new VideoPreferences().Requests(new("p/video", "Video") { ModelType = "video" }, "queued fixture", null, [], null, null);
            await jobs.SubmitAsync(request, directory); await jobs.PollOnceAsync();
            Call(shell, "ShowPage", Enum.Parse(pageType, "Chat")); var before = fake.Polls; await jobs.PollOnceAsync();
            Check(fake.Polls == before + 1 && !Field<bool>(shell, "busy") && page.Visibility == Visibility.Collapsed
                && Field<StackPanel>(shell, "pageNavigation").Children.OfType<ToggleButton>().All(b => b.IsEnabled), context + ": polling continues on Chat without blocking sidebar navigation");
            Call(shell, "ShowPage", Enum.Parse(pageType, "Videos")); await (Task)Call(shell, "ReloadVideoLibraryAsync", CancellationToken.None)!;
            Check(((GridView)page.FindName("LibraryGrid")).Items.Count == 1, context + ": returning to Videos restores saved queued placeholders");
            await CheckModelHeadersAsync(shell, session, theme);
        }
        finally { await shell.ShutdownAsync(); window.Content = null; }
    }
    private sealed class UiVideoClient : IDesktopVideoClient
    {
        public int Polls;
        public Task<JsonObject> StartAsync(JsonObject request, string partition, CancellationToken ct) => Task.FromResult(new JsonObject { ["operation"] = "p/ui-job" });
        public Task<JsonObject> StatusAsync(string operation, string partition, CancellationToken ct) { Polls++; return Task.FromResult(new JsonObject { ["status"] = "pending" }); }
    }
}
