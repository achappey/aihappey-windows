using AIHappey.Desktop.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace AIHappey.Desktop.UiTests;

public partial class App
{
    private async Task CheckRealtimeAsync(ElementTheme theme)
    {
        var context = "Realtime / " + theme;
        var session = new DesktopSession(new UiHost(true), new UiRuntime(), new DesktopSettings());
        var shell = new ChatShell(session) { RequestedTheme = theme };
        try
        {
            window!.Content = shell; window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 900)); window.Activate();
            await Task.Delay(200); await HistoryIdleAsync(shell);
            var primary = Field<StackPanel>(shell, "pageNavigation").Children.OfType<StackPanel>().Single(p => p.Name == "NavigationGroupPrimary");
            var keys = primary.Children.OfType<Grid>().Select(p => p.Name).ToArray();
            Check(keys.Take(3).SequenceEqual(["NavigationRownew", "NavigationRowrealtime", "NavigationRowsearch-conversations"]), context + ": primary sidebar places Realtime immediately after New chat");
            typeof(ChatShell).GetField("aiModelTargets", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(shell, new ChatTarget[]
            {
                new("openai/gpt-realtime-2", "OpenAI realtime") { ModelType = "audio" },
                new("google/gemini-live", "Google Live") { ModelType = "audio" },
                new("other/realtime", "Unsupported provider") { ModelType = "audio" },
                new("openai/language", "Language model") { ModelType = "language" }
            });
            await (Task)Call(shell, "LoadRealtimeAsync", CancellationToken.None, true)!;
            var pages = typeof(ChatShell).Assembly.GetType("AIHappey.Desktop.Core.DesktopPage")!;
            Call(shell, "ShowPage", Enum.Parse(pages, "Realtime")); shell.UpdateLayout();
            var picker = Field<AutoSuggestBox>(shell, "realtimeTarget");
            Check(picker.ItemsSource is IEnumerable<ChatTarget> models && models.Select(m => m.Id).ToHashSet().SetEquals(["openai/gpt-realtime-2", "google/gemini-live"]), context + ": picker exposes only supported provider audio models");
            Check(Field<StackPanel>(shell, "composer").Visibility == Visibility.Visible && Field<StackPanel>(shell, "realtimeHeader").Visibility == Visibility.Visible
                && Field<StackPanel>(shell, "chatModes").Visibility == Visibility.Collapsed, context + ": native shared composer with realtime model header");
            Check(!Field<ToggleButton>(shell, "realtimeMute").IsEnabled && !Field<Button>(shell, "realtimeStop").IsEnabled
                && Field<Image>(shell, "realtimePreview").Visibility == Visibility.Collapsed, context + ": opening the page never starts a microphone, provider session, or visual sharing");
            picker.Text = "google/gemini-live"; Call(shell, "UpdateRealtimeControls");
            Check(Field<ToggleButton>(shell, "realtimeCamera").Visibility == Visibility.Visible && Field<ToggleButton>(shell, "realtimeScreen").Visibility == Visibility.Visible, context + ": Google exposes camera and screen controls");
            picker.Text = "openai/gpt-realtime-2"; Call(shell, "UpdateRealtimeControls");
            Check(Field<ToggleButton>(shell, "realtimeCamera").Visibility == Visibility.Collapsed, context + ": OpenAI audio session does not expose unsupported video controls");
            var format = typeof(ChatShell).GetMethod("RealtimeErrorMessage", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
            var unsafeText = (string)format.Invoke(null, [new Exception("wss://provider/?access_token=secret")])!;
            var safeText = (string)format.Invoke(null, [new RealtimeFailureException("google-close-1008", "Provider closed with code 1008.")])!;
            Check(!unsafeText.Contains("secret") && safeText.Contains("google-close-1008"), context + ": diagnostic reasons preserve safe codes without leaking raw exception URLs");
            var split = Field<SplitView>(shell, "split"); split.IsPaneOpen = false; await Task.Delay(100); shell.UpdateLayout();
            var realtimeButton = primary.Children.OfType<Grid>().SelectMany(row => row.Children.OfType<ToggleButton>()).Single(b => b.Name == "NavigateRealtime");
            Check(realtimeButton.ActualWidth == 32 && realtimeButton.ActualHeight == 32, context + ": realtime navigation supports the compact native rail");
            Call(shell, "ShowPage", Enum.Parse(pages, "Chat"));
            Check(Field<StackPanel>(shell, "realtimeControls").Visibility == Visibility.Collapsed && Field<StackPanel>(shell, "realtimeHeader").Visibility == Visibility.Collapsed, context + ": leaving realtime restores regular chat controls");
        }
        finally { await shell.ShutdownAsync(); window!.Content = null; }
    }
}
