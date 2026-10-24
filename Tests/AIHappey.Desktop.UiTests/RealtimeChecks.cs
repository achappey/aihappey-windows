using AIHappey.Desktop.Core;
using CommunityToolkit.WinUI.Controls;
using FluentIcons.Common;
using FluentIcons.WinUI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using System.Reflection;
using System.Text.Json.Nodes;

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
            foreach (var provider in new[] { "openai", "google", "other" }) ProviderPreferences.SetEnabled(session.Settings, "audio", provider, true);
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
            Check(Field<StackPanel>(shell, "composer").Visibility == Visibility.Visible && Field<Grid>(shell, "realtimeHeader").Visibility == Visibility.Visible
                && Field<StackPanel>(shell, "chatModes").Visibility == Visibility.Collapsed, context + ": native shared composer with realtime model header");
            Check(!Field<ToggleButton>(shell, "realtimeMute").IsEnabled && !Field<Button>(shell, "realtimeStop").IsEnabled
                && Field<Image>(shell, "realtimePreview").Visibility == Visibility.Collapsed, context + ": opening the page never starts a microphone, provider session, or visual sharing");
            picker.Text = "google/gemini-live"; Call(shell, "UpdateRealtimeControls");
            Check(Field<ToggleButton>(shell, "realtimeCamera").Visibility == Visibility.Visible && Field<ToggleButton>(shell, "realtimeScreen").Visibility == Visibility.Visible, context + ": Google exposes camera and screen controls");
            var send = Field<Button>(shell, "send");
            var mute = Field<ToggleButton>(shell, "realtimeMute");
            var realtimeStop = Field<Button>(shell, "realtimeStop");
            var controls = Field<StackPanel>(shell, "realtimeControls");
            var composer = Field<StackPanel>(shell, "composer");
            Check(((FluentIcon)send.Content).Icon == Icon.Live && realtimeStop.Visibility == Visibility.Collapsed
                && !Field<ToggleButton>(shell, "realtimeCamera").IsEnabled, context + ": idle uses Live and disabled media controls without Stop");
            Check(!composer.Children.Contains(controls) && controls.Children.Count == 4 && controls.Children.OfType<Control>().All(c => c is ButtonBase b && b.Content is FluentIcon)
                && !Descendants(composer).OfType<FrameworkElement>().Any(e => e.Name is "RealtimeSettings" or "RealtimeStatus"), context + ": icon-only real-time actions replace the standalone settings/status row");
            await CheckRealtimeSettingsEntryAsync(shell, "Google", false, context);
            var stateType = typeof(ChatShell).GetNestedType("RealtimeState", BindingFlags.NonPublic)!;
            var state = Activator.CreateInstance(stateType, Field<Conversation>(shell, "current"), session.HistoryPartition, picker.Text, McpTurnSnapshot.Empty, new RealtimePreferences())!;
            var connection = new RealtimeUiConnection();
            stateType.GetProperty("Connection")!.SetValue(state, connection);
            typeof(ChatShell).GetField("realtime", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(shell, state);
            Call(shell, "UpdateRealtimeControls");
            Check(realtimeStop.Visibility == Visibility.Visible && realtimeStop.IsEnabled && !mute.IsEnabled && !send.IsEnabled, context + ": connection startup offers session Stop, not a second operation stop");
            stateType.GetProperty("Connected")!.SetValue(state, true); Call(shell, "UpdateRealtimeControls");
            Check(((FluentIcon)send.Content).Icon == Icon.Send && mute.IsEnabled && !picker.IsEnabled, context + ": connected restores Send and enables microphone controls");
            Toggle(mute);
            Check(connection.Muted && ((FluentIcon)mute.Content).Icon == Icon.MicOff && AutomationProperties.GetName(mute) == "Unmute microphone", context + ": microphone toggles the session and exposes its changed action");
            await CheckRealtimeSettingsEntryAsync(shell, "Google", true, context);
            var scale = shell.XamlRoot.RasterizationScale;
            foreach (var width in new[] { 1280, 640, 500 })
            {
                window.AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(width * scale), (int)(900 * scale))); await Task.Delay(100); shell.UpdateLayout();
                var actions = Descendants(composer).OfType<Grid>().Single(g => g.Name == "ComposerActions");
                var buttons = controls.Children.OfType<Control>().Where(c => c.Visibility == Visibility.Visible).Append(send).ToArray();
                var sendBounds = send.TransformToVisual(composer).TransformBounds(new(0, 0, send.ActualWidth, send.ActualHeight));
                Check(buttons.All(b => Within(b, actions) && b.Padding == new Thickness(0) && b.ActualWidth >= 40)
                    && buttons.All(b => Math.Abs(b.TransformToVisual(composer).TransformPoint(new(0, 0)).Y - sendBounds.Y) < 1), context + $": every session action fits the same row at {width}px");
                foreach (var button in buttons.OfType<ButtonBase>())
                {
                    var icon = (FluentIcon)button.Content;
                    Check(icon.ActualWidth > 0 && Within(icon, button), context + $": {button.Name} artwork is fully visible at {width}px");
                }
            }
            stateType.GetProperty("Connected")!.SetValue(state, false); stateType.GetProperty("Reconnecting")!.SetValue(state, true); Call(shell, "UpdateRealtimeControls");
            Check(!mute.IsEnabled && realtimeStop.IsEnabled && !send.IsEnabled && AutomationProperties.GetItemStatus(mute).Contains("Reconnecting"), context + ": reconnecting retains Stop and reports accessible status");
            await (Task)Call(shell, "StopRealtimeAsync")!;
            Check(connection.Disposed && realtimeStop.Visibility == Visibility.Collapsed && mute.IsChecked != true && ((FluentIcon)send.Content).Icon == Icon.Live, context + ": stopping disposes session and restores idle actions");
            picker.Text = "openai/gpt-realtime-2"; Call(shell, "UpdateRealtimeControls");
            Check(Field<ToggleButton>(shell, "realtimeCamera").Visibility == Visibility.Collapsed, context + ": OpenAI audio session does not expose unsupported video controls");
            await CheckRealtimeSettingsEntryAsync(shell, "OpenAI", false, context);
            var format = typeof(ChatShell).GetMethod("RealtimeErrorMessage", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
            var unsafeText = (string)format.Invoke(null, [new Exception("wss://provider/?access_token=secret")])!;
            var safeText = (string)format.Invoke(null, [new RealtimeFailureException("google-close-1008", "Provider closed with code 1008.")])!;
            Check(!unsafeText.Contains("secret") && safeText.Contains("google-close-1008"), context + ": diagnostic reasons preserve safe codes without leaking raw exception URLs");
            var split = Field<SplitView>(shell, "split"); split.IsPaneOpen = false; await Task.Delay(100); shell.UpdateLayout();
            var realtimeButton = primary.Children.OfType<Grid>().SelectMany(row => row.Children.OfType<ToggleButton>()).Single(b => b.Name == "NavigateRealtime");
            Check(realtimeButton.ActualWidth == 32 && realtimeButton.ActualHeight == 32, context + ": realtime navigation supports the compact native rail");
            Check(Descendants(realtimeButton).OfType<FluentIcon>().Any(i => i.Icon == Icon.Live), context + ": real-time navigation uses browser Live artwork");
            Call(shell, "ShowPage", Enum.Parse(pages, "Chat"));
            Check(controls.Visibility == Visibility.Collapsed && Field<Grid>(shell, "realtimeHeader").Visibility == Visibility.Collapsed
                && ((FluentIcon)send.Content).Icon == Icon.Send && Field<Button>(shell, "dictate").Visibility == Visibility.Visible, context + ": leaving realtime restores regular chat controls and Send artwork");
            window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 900)); await Task.Delay(80);
            foreach (var provider in new[] { "openai", "google" }) await CheckRealtimeProviderCardsAsync(shell, provider, theme);
        }
        finally { await shell.ShutdownAsync(); window!.Content = null; }
    }

    private async Task CheckRealtimeSettingsEntryAsync(ChatShell shell, string provider, bool locked, string context)
    {
        var shown = (Task)Call(shell, "EditChatSettingsAsync")!; await Task.Delay(100);
        var dialog = Field<ChatSettingsDialog>(shell, "chatSettingsDialog");
        Toggle(await RealtimeProviderTabAsync(dialog)); await Task.Delay(100);
        var form = (FrameworkElement)Field<object>(dialog, "realtimeForm");
        var audio = Descendants(form).OfType<SettingsExpander>().Single(s => s.Name == "RealtimeAudioSection");
        audio.IsExpanded = true; await Task.Delay(70);
        Check((string)Descendants(dialog).OfType<ToggleButton>().Single(t => t.Name == "ChatProviderTab").Content == provider
            && Descendants(form).OfType<ComboBox>().Single(c => c.Name == "RealtimeVoice").IsEnabled != locked,
            context + $": shared Chat Settings resolves {provider} from the real-time model and provider lock={locked}");
        InvokeButton(Descendants(dialog).OfType<Button>().Single(b => b.Name == "CloseButton")); await shown;
    }

    private async Task CheckRealtimeProviderCardsAsync(ChatShell shell, string provider, ElementTheme theme)
    {
        var context = $"Realtime provider cards / {provider} / {theme}";
        var original = new RealtimePreferences { CameraFrameRate = .3, JpegQuality = .7 };
        original.ProviderOptions["openai"] = JsonNode.Parse("""{"session":{"future":{"keep":true},"audio":{"output":{"voice":"marin"}}}}""");
        original.ProviderOptions["google"] = JsonNode.Parse("""{"liveConnectConstraints":{"config":{"future":{"keep":true}}},"expireTime":"15m"}""");
        var originalJson = original.ProviderOptions.ToJsonString();
        RealtimePreferences? saved = null; ChatPreferences? savedChat = null; var failSave = true;
        var dialog = new ChatSettingsDialog(new(), provider, realtimePreferences: original) { XamlRoot = shell.XamlRoot, RequestedTheme = theme,
            SaveWithRealtimeAsync = (chat, realtime) => { if (failSave) throw new IOException("fixture"); savedChat = chat; saved = realtime; return Task.CompletedTask; } };
        SystemAppearance.PrepareDialog(dialog); var shown = dialog.ShowAsync(); await Task.Delay(100);
        Toggle(await RealtimeProviderTabAsync(dialog)); await Task.Delay(100);
        var form = (FrameworkElement)Field<object>(dialog, "realtimeForm");
        var sections = Descendants(form).OfType<SettingsExpander>().ToArray();
        var expected = provider == "openai" ? new[] { "Audio", "TurnDetection", "Responses" } : new[] { "Audio", "TurnDetection", "Tools", "Session", "Video" };
        Check(sections.Select(s => s.Name).SequenceEqual(expected.Select(s => "Realtime" + s + "Section")) && sections.All(s => !s.IsExpanded), context + ": logical native card groups default collapsed");
        Check(!Descendants(form).OfType<Button>().Any(b => b.Name == "RealtimeReset")
            && !((StackPanel)form).Children.OfType<TextBlock>().Any(), context + ": no introductory description or duplicate provider-reset button");
        foreach (var section in sections) section.IsExpanded = true;
        await Task.Delay(100); dialog.UpdateLayout();
        var voice = Descendants(form).OfType<ComboBox>().Single(c => c.Name == "RealtimeVoice"); voice.SelectedItem = provider == "openai" ? "cedar" : "Puck";
        var transcription = Descendants(form).OfType<ToggleSwitch>().Single(t => t.Name == "RealtimeInputTranscription");
        transcription.IsOn = !transcription.IsOn;
        var scale = shell.XamlRoot.RasterizationScale;
        foreach (var width in new[] { 1280, 500 })
        {
            window!.AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(width * scale), (int)(900 * scale))); await Task.Delay(100); dialog.UpdateLayout();
            foreach (var toggle in Descendants(form).OfType<ToggleSwitch>())
            {
                var row = (Grid)toggle.Parent; var label = row.Children.OfType<TextBlock>().Single();
                var labelBounds = label.TransformToVisual(row).TransformBounds(new(0, 0, label.ActualWidth, label.ActualHeight));
                var toggleBounds = toggle.TransformToVisual(row).TransformBounds(new(0, 0, toggle.ActualWidth, toggle.ActualHeight));
                Check(toggle.Header is null && Equals(toggle.OnContent, "") && Equals(toggle.OffContent, "") && label.TextWrapping == TextWrapping.Wrap
                    && labelBounds.Right + 10 <= toggleBounds.Left && Within(toggle, row) && row.ActualWidth <= form.ActualWidth + 1,
                    context + $": {toggle.Name} has wrapping label and nonoverlapping right-aligned switch at {width}px");
            }
            var page = Field<ScrollViewer>(dialog, "page");
            Check(page.ScrollableWidth < 1, context + $": expanded cards do not scroll horizontally at {width}px");
        }
        if (provider == "google")
        {
            var expiry = Descendants(form).OfType<TextBox>().Single(t => t.Name == "RealtimeExpiry"); expiry.Text = "invalid";
            var session = sections.Single(s => s.Name == "RealtimeSessionSection"); session.IsExpanded = false;
            Toggle(Descendants(dialog).OfType<ToggleButton>().Single(t => t.Name == "ChatGeneralTab"));
            InvokeButton(Descendants(dialog).OfType<Button>().Single(b => b.Name == "CloseButton")); await Task.Delay(120);
            Check(shown.Status == Windows.Foundation.AsyncStatus.Started && session.IsExpanded && expiry.FocusState != FocusState.Unfocused, context + ": invalid hidden expiry expands its section and receives focus");
            expiry.Text = "20m";
        }
        InvokeButton(Descendants(dialog).OfType<Button>().Single(b => b.Name == "CloseButton")); await Task.Delay(100);
        Check(shown.Status == Windows.Foundation.AsyncStatus.Started && saved is null && original.ProviderOptions.ToJsonString() == originalJson, context + ": save failure retains the dialog draft without mutating live settings");
        failSave = false; InvokeButton(Descendants(dialog).OfType<Button>().Single(b => b.Name == "CloseButton")); await shown;
        Check(saved is not null && savedChat is not null && saved.ProviderOptions[provider]!.ToJsonString().Contains("keep")
            && saved.ProviderOptions[provider]!.ToJsonString().Contains(provider == "openai" ? "cedar" : "Puck")
            && original.ProviderOptions.ToJsonString() == originalJson, context + ": close commits edited real-time and chat drafts together while preserving unknown options and original settings");
        window!.AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 900)); await Task.Delay(80);
        var resetDialog = new ChatSettingsDialog(new(), provider, realtimePreferences: saved) { XamlRoot = shell.XamlRoot, RequestedTheme = theme };
        shown = resetDialog.ShowAsync(); Toggle(await RealtimeProviderTabAsync(resetDialog)); await Task.Delay(80);
        InvokeButton(Descendants(resetDialog).OfType<Button>().Single(b => b.Name == "PrimaryButton")); await Task.Delay(60);
        Toggle(await RealtimeProviderTabAsync(resetDialog)); await Task.Delay(80);
        Check(Descendants(resetDialog).OfType<SettingsExpander>().Count() == expected.Length
            && Descendants(resetDialog).OfType<SettingsExpander>().All(s => !s.IsExpanded), context + ": footer Restore defaults rebuilds sections collapsed");
        InvokeButton(Descendants(resetDialog).OfType<Button>().Single(b => b.Name == "CloseButton")); await shown;
        var restored = resetDialog.RealtimeResult!;
        Check(!restored.ProviderOptions.ToJsonString().Contains("keep") && restored.CameraFrameRate == 1 && restored.JpegQuality == .8 && restored.Model == saved!.Model
            && RealtimeConfiguration.Session("openai/gpt-realtime-2", restored, "", [], "auto")["audio"]!["output"]!["voice"]!.ToString() == "marin"
            && !RealtimeConfiguration.Session("google/gemini-live", restored, "", [], "auto").ToJsonString().Contains("Puck"), context + ": single footer reset restores both providers and media defaults without changing the selected model");
    }

    private static async Task<ToggleButton> RealtimeProviderTabAsync(ChatSettingsDialog dialog)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            if (Descendants(dialog).OfType<ToggleButton>().FirstOrDefault(t => t.Name == "ChatProviderTab") is { } tab) return tab;
            await Task.Delay(50);
        }
        throw new InvalidOperationException($"Provider tab not rendered: provider={Field<string>(dialog, "provider")}; loaded={dialog.IsLoaded}; tabs={string.Join(",", Field<StackPanel>(dialog, "tabs").Children.OfType<ToggleButton>().Select(t => t.Name))}");
    }
}

internal sealed class RealtimeUiConnection : RealtimeSession
{
    public bool Muted { get; private set; }
    public bool Disposed { get; private set; }
    public override Task StartAsync(CancellationToken ct) => throw new InvalidOperationException("Native UI fixtures never open devices or contact providers.");
    public override Task SendAsync(JsonObject message, CancellationToken ct) => Task.CompletedTask;
    public override Task SetMutedAsync(bool muted) { Muted = muted; return Task.CompletedTask; }
    public override ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
}
