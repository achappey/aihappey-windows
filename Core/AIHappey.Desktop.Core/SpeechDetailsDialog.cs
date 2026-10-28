using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;

namespace AIHappey.Desktop.Core;

public enum SpeechDetailAction { None, Download }
public sealed class SpeechDetailsDialog : ContentDialog, IResponsiveDialog
{
    private readonly Grid layout = new() { RowSpacing = 12 };
    private readonly MediaDetailsView details = new("SpeechDetails");
    private readonly MediaPlayerElement element = new() { Name = "SpeechPlayer", AreTransportControlsEnabled = true, AutoPlay = false, HorizontalAlignment = HorizontalAlignment.Stretch, Height = 96 };
    private MediaPlayer? player; private bool closed;
    public SpeechDetailAction Action { get; private set; }
    public SpeechDetailsDialog(LibrarySpeech speech)
    {
        Name = "SpeechDetailsDialog"; Title = DesktopResources.Get("SpeechDetails"); CloseButtonText = DesktopResources.Get("Close"); PrimaryButtonText = DesktopResources.Get("Download");
        Resources["ContentDialogMaxWidth"] = 1000d; Resources["ContentDialogMinWidth"] = 0d; PrimaryButtonClick += (_, _) => Action = SpeechDetailAction.Download;
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto }); layout.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        layout.Children.Add(element); Grid.SetRow(details, 1); layout.Children.Add(details);
        details.AddTab("SpeechInput", MediaDetailsView.Text(speech.Item.Text));
        details.AddTab("SpeechProviderInput", MediaDetailsView.Json(MediaDetailsView.ProviderInput(speech.Item.Response, speech.Item.Input), "SpeechProviderInput"));
        details.AddTab("SpeechProviderResult", MediaDetailsView.Json(MediaDetailsView.ProviderResult(speech.Item.Response), "SpeechProviderResult")); Content = layout;
        Opened += async (_, _) => { SizeToRoot(); XamlRoot.Changed += RootChanged; try { var file = await StorageFile.GetFileFromPathAsync(speech.Path); if (closed) return; player = new MediaPlayer(); player.Source = MediaSource.CreateFromStorageFile(file); element.SetMediaPlayer(player); } catch { element.Visibility = Visibility.Collapsed; } };
        Closed += (_, _) => { closed = true; XamlRoot.Changed -= RootChanged; ReleasePlayer(); };
    }
    public void ReleasePlayer() { if (player is null) return; player.Pause(); player.Source = null; element.SetMediaPlayer(null); player.Dispose(); player = null; }
    private void RootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => SizeToRoot(); void IResponsiveDialog.SizeToRoot() => SizeToRoot();
    private void SizeToRoot()
    {
        layout.Width = Math.Max(0, Math.Min(880, XamlRoot.Size.Width - 96));
        // Work in logical pixels: a small window at 150% scaling still needs a provider-data viewport.
        layout.Height = Math.Max(0, Math.Min(720, XamlRoot.Size.Height - 180));
        element.Height = Math.Clamp(layout.Height / 5, 48, 96);
    }
}
