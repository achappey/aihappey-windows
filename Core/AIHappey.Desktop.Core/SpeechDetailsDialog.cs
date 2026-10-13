using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;

namespace AIHappey.Desktop.Core;

public enum SpeechDetailAction { None, Download }
public sealed class SpeechDetailsDialog : ContentDialog, IResponsiveDialog
{
    private readonly StackPanel layout = new() { Spacing = 12 };
    private readonly MediaPlayerElement element = new() { Name = "SpeechPlayer", AreTransportControlsEnabled = true, AutoPlay = false, HorizontalAlignment = HorizontalAlignment.Stretch };
    private MediaPlayer? player; private bool closed;
    public SpeechDetailAction Action { get; private set; }
    public SpeechDetailsDialog(LibrarySpeech speech)
    {
        Name = "SpeechDetailsDialog"; Title = DesktopResources.Get("SpeechDetails"); CloseButtonText = DesktopResources.Get("Close"); PrimaryButtonText = DesktopResources.Get("Download");
        Resources["ContentDialogMaxWidth"] = 1000d; Resources["ContentDialogMinWidth"] = 0d; PrimaryButtonClick += (_, _) => Action = SpeechDetailAction.Download;
        layout.Children.Add(element); var tabs = new TabView(); tabs.TabItems.Add(Tab("SpeechInput", Text(speech.Item.Text)));
        tabs.TabItems.Add(Tab("SpeechProviderInput", Json(speech.Item.Input))); tabs.TabItems.Add(Tab("SpeechProviderResult", Json(speech.Item.Response))); layout.Children.Add(tabs); Content = layout;
        Opened += async (_, _) => { SizeToRoot(); XamlRoot.Changed += RootChanged; try { var file = await StorageFile.GetFileFromPathAsync(speech.Path); if (closed) return; player = new MediaPlayer(); player.Source = MediaSource.CreateFromStorageFile(file); element.SetMediaPlayer(player); } catch { element.Visibility = Visibility.Collapsed; } };
        Closed += (_, _) => { closed = true; XamlRoot.Changed -= RootChanged; ReleasePlayer(); };
    }
    private static TabViewItem Tab(string key, UIElement content) => new() { Header = DesktopResources.Get(key), Content = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };
    private static TextBlock Text(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, Margin = new Thickness(12) };
    private static TextBox Json(JsonNode value) => new() { Text = value.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }), IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"), Margin = new Thickness(12) };
    public void ReleasePlayer() { if (player is null) return; player.Pause(); player.Source = null; element.SetMediaPlayer(null); player.Dispose(); player = null; }
    private void RootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => SizeToRoot(); void IResponsiveDialog.SizeToRoot() => SizeToRoot();
    private void SizeToRoot() { layout.Width = Math.Max(0, Math.Min(880, XamlRoot.Size.Width - 96)); layout.MaxHeight = Math.Max(240, XamlRoot.Size.Height - 240); }
}
