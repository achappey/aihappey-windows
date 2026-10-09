using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;

namespace AIHappey.Desktop.Core;

public enum VideoPreviewAction { None, Save, Delete }

public sealed class VideoPreviewDialog : ContentDialog, IResponsiveDialog
{
    private readonly StackPanel layout = new() { Spacing = 12 };
    private readonly MediaPlayerElement element = new() { Name = "VideoPlayer", AreTransportControlsEnabled = true, AutoPlay = false, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock error = new() { Text = DesktopResources.Get("VideoPlaybackFailed"), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    private MediaPlayer? player;
    private bool closed;
    public VideoPreviewAction Action { get; private set; }
    public VideoPreviewDialog(LibraryVideo video)
    {
        Name = "VideoPreviewDialog"; Title = DesktopResources.Get("Videos"); CloseButtonText = DesktopResources.Get("Close");
        PrimaryButtonText = DesktopResources.Get("Save"); SecondaryButtonText = DesktopResources.Get("Delete");
        Resources["ContentDialogMaxWidth"] = 1000d; Resources["ContentDialogMinWidth"] = 0d;
        layout.Children.Add(element); layout.Children.Add(error); layout.Children.Add(new TextBlock { Text = video.Model + "\n" + video.Generation.Prompt, TextWrapping = TextWrapping.Wrap, MaxLines = 4, IsTextSelectionEnabled = true }); Content = layout;
        PrimaryButtonClick += (_, _) => Action = VideoPreviewAction.Save; SecondaryButtonClick += (_, _) => Action = VideoPreviewAction.Delete;
        Opened += async (_, _) =>
        {
            SizeToRoot(); XamlRoot.Changed += RootChanged;
            try
            {
                VideoDisk.CheckPath(video.Path); var file = await StorageFile.GetFileFromPathAsync(video.Path); if (closed) return;
                player = new MediaPlayer { AutoPlay = false }; player.MediaFailed += Failed;
                player.Source = MediaSource.CreateFromStorageFile(file); element.SetMediaPlayer(player);
            }
            catch (Exception) { error.Visibility = Visibility.Visible; }
        };
        Closed += (_, _) => { closed = true; XamlRoot.Changed -= RootChanged; ReleasePlayer(); };
    }
    private void Failed(MediaPlayer sender, MediaPlayerFailedEventArgs args) => DispatcherQueue.TryEnqueue(() => { if (!closed) error.Visibility = Visibility.Visible; });
    public void ReleasePlayer()
    { if (player is null) return; player.MediaFailed -= Failed; player.Pause(); player.Source = null; element.SetMediaPlayer(null); player.Dispose(); player = null; }
    private void RootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => SizeToRoot();
    void IResponsiveDialog.SizeToRoot() => SizeToRoot();
    private void SizeToRoot() { layout.Width = Math.Max(0, Math.Min(880, XamlRoot.Size.Width - 96)); element.Height = Math.Max(80, Math.Min(layout.Width * 9 / 16, XamlRoot.Size.Height - 380)); }
}
