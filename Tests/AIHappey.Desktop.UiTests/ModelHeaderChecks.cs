using System.Reflection;
using AIHappey.Desktop.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AIHappey.Desktop.UiTests;

public partial class App
{
    private async Task CheckModelHeadersAsync(ChatShell shell, DesktopSession session, ElementTheme theme)
    {
        var context = "Model headers / " + theme;
        var pageType = typeof(ChatShell).Assembly.GetType("AIHappey.Desktop.Core.DesktopPage")!;
        ChatTarget[] models = [new("p/language", "Chat") { ModelType = "language" },
            new("p/image", "Image") { ModelType = "image" }, new("p/video", "Video") { ModelType = "video" },
            new("p/speech", "Speech") { ModelType = "speech" }, new("p/transcription", "Transcription") { ModelType = "transcription" },
            new("google/gemini-3.8-live", "A very long realtime model name for clipping checks") { ModelType = "audio" }];
        typeof(ChatShell).GetField("targets", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(shell, models);
        typeof(ChatShell).GetField("aiModelTargets", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(shell, models);
        Field<ChatTargetPicker>(shell, "target").Text = "p/language";
        foreach (var (page, toolbar, pickerName, load) in new[] {
            ("Chat", "chatModelToolbar", "target", ""), ("Images", "imageModelToolbar", "imageTarget", "LoadImagesAsync"),
            ("Videos", "videoModelToolbar", "videoTarget", "LoadVideosAsync"), ("Speech", "speechModelToolbar", "speechTarget", "LoadSpeechAsync"),
            ("Transcriptions", "transcriptionModelToolbar", "transcriptionTarget", "LoadTranscriptionsAsync"),
            ("Realtime", "realtimeHeader", "realtimeTarget", "LoadRealtimeAsync") })
        {
            Call(shell, "ShowPage", Enum.Parse(pageType, page));
            if (load.Length > 0) await (Task)Call(shell, load, CancellationToken.None, true)!;
            var header = Field<Grid>(shell, toolbar);
            var favorite = header.Children.OfType<Button>().Last();
            Check(header.ColumnDefinitions.Count == 2 && header.ColumnDefinitions[1].Width.IsAuto
                && favorite.Padding.Left == 0 && AutomationProperties.GetName(favorite).Length > 0,
                context + ": " + page + " reserves accessible favorite-button space");
            foreach (var width in new[] { 480, 720, 1280 })
            {
                window!.AppWindow.Resize(new Windows.Graphics.SizeInt32(width, 900));
                await Task.Delay(70); shell.UpdateLayout();
                var star = favorite.TransformToVisual(shell).TransformBounds(new Windows.Foundation.Rect(0, 0, favorite.ActualWidth, favorite.ActualHeight));
                var bounds = header.TransformToVisual(shell).TransformBounds(new Windows.Foundation.Rect(0, 0, header.ActualWidth, header.ActualHeight));
                Check(favorite.Visibility == Visibility.Visible && favorite.ActualWidth >= 40 && star.Right <= bounds.Right + 1
                    && star.Right <= shell.ActualWidth + 1 && star.Left >= bounds.Left - 1,
                    context + $": {page} favorite fully fits {width}px viewport");
            }
            if (page == "Chat") continue;
            // Exercise the common persistence path, then check it reaches every header immediately.
            var selected = models.First(m => m.ModelType == (page == "Realtime" ? "audio" : page == "Transcriptions" ? "transcription" : page == "Images" ? "image" : page == "Videos" ? "video" : "speech"));
            await (Task)Call(shell, "SaveModelFavoriteAsync", selected, CancellationToken.None)!;
            var key = ModelOverviewCatalog.FavoriteKey(selected);
            Check(Field<HashSet<string>>(shell, "modelFavorites").Contains(key), context + ": " + page + " adds its exact typed favorite");
            await (Task)Call(shell, "SaveModelFavoriteAsync", selected, CancellationToken.None)!;
            Check(!Field<HashSet<string>>(shell, "modelFavorites").Contains(key), context + ": " + page + " removes its typed favorite");
        }
    }
}
