using System.Reflection;
using System.Text.Json.Nodes;
using AIHappey.Desktop.Core;
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
            Check(!Descendants(dialog).OfType<FrameworkElement>().Any(e => e.Name == "VideoStorageCard")
                && Descendants(dialog).OfType<Button>().Any(b => b.Name == "VideoRemove_references")
                && Descendants(dialog).OfType<Button>().Any(b => b.Name == "VideoRemove_first_frame") && Descendants(dialog).OfType<Button>().Any(b => b.Name == "VideoRemove_last_frame"),
                context + ": native generation settings include references and both frames, not storage");
            var batch = Descendants(dialog).OfType<TextBox>().Single(t => t.Name == "VideoBatchLimit"); batch.Text = "0";
            InvokeButton(Descendants(dialog).OfType<Button>().Single(b => b.Name == "CloseButton")); await Task.Delay(70);
            Check(shown.Status == Windows.Foundation.AsyncStatus.Started && saved is null, context + ": invalid settings cannot close or mutate live preferences"); batch.Text = "2";
            Descendants(dialog).OfType<Slider>().Single(s => s.Name == "VideoCount").Value = 3;
            InvokeButton(Descendants(dialog).OfType<Button>().Single(b => b.Name == "VideoRemove_first_frame"));
            foreach (var width in new[] { 500, 1000 })
            {
                window.AppWindow.Resize(new Windows.Graphics.SizeInt32(width, 900)); await Task.Delay(70); dialog.UpdateLayout();
                Check(Descendants(dialog).OfType<ScrollViewer>().All(v => v.ScrollableWidth < 1) && tabs.PaneDisplayMode == NavigationViewPaneDisplayMode.Top, context + $": settings use native tab overflow at {width}px");
            }
            InvokeButton(Descendants(dialog).OfType<Button>().Single(b => b.Name == "CloseButton")); await shown;
            Check(saved?.N == 3 && saved.MaxVideosPerCall == 2 && saved.FirstFrame is null && saved.LastFrame is not null && saved.InputReferences.Count == 1
                && saved.ProviderOptions["future"]["keep"]!.GetValue<bool>() && saved.StorageRoot == directory && preferences.FirstFrame is not null && preferences.N == 1,
                context + ": saved video settings are cloned and preserve storage, reference inputs and unknown provider fields; saved=" + System.Text.Json.JsonSerializer.Serialize(saved)
                + "; original=" + System.Text.Json.JsonSerializer.Serialize(preferences));
            var failing = new VideoSettingsDialog(preferences, inputStore) { XamlRoot = page.XamlRoot, SaveAsync = _ => throw new IOException("blocked") };
            shown = failing.ShowAsync(); await Task.Delay(70); InvokeButton(Descendants(failing).OfType<Button>().Single(b => b.Name == "CloseButton")); await Task.Delay(60);
            Check(shown.Status == Windows.Foundation.AsyncStatus.Started, context + ": failed settings save keeps dialog open"); failing.DiscardOnShutdown = true; failing.Hide(); await shown;
            var preview = new VideoPreviewDialog(video) { XamlRoot = page.XamlRoot }; var previewShown = preview.ShowAsync(); await Task.Delay(100);
            Check(Descendants(preview).OfType<MediaPlayerElement>().Single().AutoPlay == false && preview.PrimaryButtonText == DesktopResources.Get("Save") && preview.SecondaryButtonText == DesktopResources.Get("Delete"), context + ": native explicit playback preview exposes Save and Delete");
            preview.Hide(); await previewShown; Check(typeof(VideoPreviewDialog).GetField("player", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(preview) is null, context + ": preview close releases media player and source");
            await CheckVideoStorageAsync(page.XamlRoot, theme, directory, path);
            window.Content = null;
            await CheckVideoNavigationAsync(theme, directory);
        }
        finally { window!.Content = null; Directory.Delete(directory, true); }
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
            var picker = Field<AutoSuggestBox>(shell, "videoTarget"); var page = Field<VideosPage>(shell, "videosPage");
            Check(picker.Text == "p/video" && picker.ItemsSource is IEnumerable<ChatTarget> models && models.All(m => m.ModelType == "video") && page.Visibility == Visibility.Visible, context + ": video-only picker and native navigation are wired");
            var request = new VideoPreferences().Requests(new("p/video", "Video") { ModelType = "video" }, "queued fixture", null, [], null, null);
            await jobs.SubmitAsync(request, directory); await jobs.PollOnceAsync();
            Call(shell, "ShowPage", Enum.Parse(pageType, "Chat")); var before = fake.Polls; await jobs.PollOnceAsync();
            Check(fake.Polls == before + 1 && !Field<bool>(shell, "busy") && page.Visibility == Visibility.Collapsed
                && Field<StackPanel>(shell, "pageNavigation").Children.OfType<ToggleButton>().All(b => b.IsEnabled), context + ": polling continues on Chat without blocking sidebar navigation");
            Call(shell, "ShowPage", Enum.Parse(pageType, "Videos")); await (Task)Call(shell, "ReloadVideoLibraryAsync", CancellationToken.None)!;
            Check(((GridView)page.FindName("LibraryGrid")).Items.Count == 1, context + ": returning to Videos restores saved queued placeholders");
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
