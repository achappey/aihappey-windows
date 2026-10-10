using System.Reflection;
using System.Text.Json;
using AIHappey.Desktop.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace AIHappey.Desktop.UiTests;

public partial class App
{
    private async Task CheckLocalToolsAsync(ElementTheme theme)
    {
        var context = "Local tools / " + theme;
        var root = new Grid { RequestedTheme = theme }; window!.Content = root;
        window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1100, 900)); window.Activate(); await Task.Delay(100);
        var original = new ChatPreferences(); ChatPreferences? saved = null;
        var dialog = new ChatSettingsDialog(original, "openai") { XamlRoot = root.XamlRoot, RequestedTheme = theme,
            SaveAsync = next => { saved = next; return Task.CompletedTask; } };
        SystemAppearance.PrepareDialog(dialog); var shown = dialog.ShowAsync(); await Task.Delay(120); dialog.ShowToolsTab(); await Task.Delay(100); dialog.UpdateLayout();
        var tabs = Descendants(dialog).OfType<ToggleButton>().Where(t => t.Name.StartsWith("Chat") && t.Name.EndsWith("Tab")).ToArray();
        Check(tabs.Single(t => t.Name == "ChatToolsTab").IsChecked == true && tabs.Count(t => t.IsChecked == true) == 1,
            context + ": Tools tab navigation selects exactly one native tab");
        var toggles = Descendants(dialog).OfType<ToggleSwitch>().Where(t => t.Name == "ChatPluginToggle").ToArray();
        Check(toggles.Length == 3 && toggles.All(t => !t.IsOn), context + ": all three plugins initially off");
        Check(toggles.Select(AutomationProperties.GetName).SequenceEqual(["Chat history", "Skill discovery", "Artificial Intelligence"])
            && toggles.All(t => AutomationProperties.GetAutomationId(t) == "ChatPlugin_" + t.Tag), context + ": browser labels and stable accessible identities");
        foreach (var toggle in toggles) toggle.IsOn = true;
        Check(original.ActivePlugins.Count == 0, context + ": toggles edit isolated draft");
        dialog.ShowSkillsTab(); await Task.Delay(60); dialog.ShowToolsTab(); await Task.Delay(60);
        Check(Descendants(dialog).OfType<ToggleSwitch>().Where(t => t.Name == "ChatPluginToggle").All(t => t.IsOn), context + ": navigation retains draft selections");
        window.AppWindow.Resize(new Windows.Graphics.SizeInt32(500, 900)); await Task.Delay(120); dialog.UpdateLayout();
        Check(Descendants(dialog).OfType<ScrollViewer>().Where(s => s.HorizontalScrollMode == ScrollMode.Disabled).All(s => s.ScrollableWidth < 1),
            context + ": Tools cards fit narrow native viewport");
        InvokeButton(Descendants(dialog).OfType<Button>().Single(b => b.Name == "CloseButton")); await shown;
        Check(saved?.ActivePlugins.Count == 3 && dialog.Result?.ActivePlugins.Count == 3, context + ": close saves all plugin selections");
        var reopen = new ChatSettingsDialog(saved!, null) { XamlRoot = root.XamlRoot, RequestedTheme = theme };
        shown = reopen.ShowAsync(); await Task.Delay(100); reopen.ShowToolsTab(); await Task.Delay(100);
        Check(Descendants(reopen).OfType<ToggleSwitch>().Where(t => t.Name == "ChatPluginToggle").All(t => t.IsOn), context + ": reopen restores enabled toggles");
        InvokeButton(Descendants(reopen).OfType<Button>().Single(b => b.Name == "PrimaryButton")); await Task.Delay(80); reopen.ShowToolsTab(); await Task.Delay(80);
        Check(Descendants(reopen).OfType<ToggleSwitch>().Where(t => t.Name == "ChatPluginToggle").All(t => !t.IsOn), context + ": restore defaults clears plugins and rebuilds tools page");
        InvokeButton(Descendants(reopen).OfType<Button>().Single(b => b.Name == "CloseButton")); await shown;

        var session = new DesktopSession(new UiHost(true), new UiRuntime(), new());
        var shell = new ChatShell(session) { RequestedTheme = theme }; window.Content = shell; await Task.Delay(200); await HistoryIdleAsync(shell);
        try
        {
            var runtime = (McpTurnSnapshot)Call(shell, "CaptureSkillRuntime", new ChatPreferences())!;
            Check(runtime.Tools.Count == 0, context + ": default Windows runtime has no plugin tools");
            var preferences = new ChatPreferences { ActivePlugins = DesktopLocalTools.Plugins.Select(p => p.Id).ToList() };
            runtime = (McpTurnSnapshot)Call(shell, "CaptureSkillRuntime", preferences)!;
            Check(runtime.Tools.Count == 13 && runtime.Tools.Select(t => t.GetProperty("name").GetString()).Distinct().Count() == 13,
                context + ": native shell captures all enabled plugin routes exactly once");
            Check(runtime.ConversationTools is not null, context + ": history runtime belongs to captured turn, not a context-preview side effect");
            var conversation = new Conversation { Id = "deleted-active" };
            typeof(ChatShell).GetField("current", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(shell, conversation);
            typeof(ChatShell).GetField("activeConversationTools", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(shell, runtime.ConversationTools);
            await (Task)Call(shell, "SaveTurnHistoryAsync", session.HistoryPartition, conversation, CancellationToken.None)!;
            await runtime.CallAsync("local_conversations_delete_conversation", JsonSerializer.SerializeToElement(new { conversationId = conversation.Id }), "delete", "en", default);
            await (Task)Call(shell, "SaveTurnHistoryAsync", session.HistoryPartition, conversation, CancellationToken.None)!;
            var history = (HistoryStore)typeof(ChatShell).GetField("history", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(shell)!;
            Check(await history.GetAsync(session.HistoryPartition, conversation.Id) is null
                && ReferenceEquals(typeof(ChatShell).GetField("current", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(shell), conversation),
                context + ": active deletion survives real shell save boundary without swapping running conversation");
            conversation.Messages.Add(new() { Message = new AIHappey.Vercel.Models.UIMessage { Id = "failed", Role = AIHappey.Vercel.Models.Role.assistant },
                Status = "failed", ErrorMessage = "Exact service error fixture" });
            Call(shell, "RenderTranscript"); await Task.Delay(80); shell.UpdateLayout();
            Call(shell, "Show", "Exact service error fixture", InfoBarSeverity.Error); await Task.Delay(80); shell.UpdateLayout();
            var notification = Descendants(shell).OfType<InfoBar>().Single(b => b.Name == "ChatNotice");
            Check(!Descendants(shell).OfType<InfoBar>().Any(b => b.Name == "MessageError")
                && !Descendants(shell).OfType<TextBlock>().Any(t => t.Text == DesktopResources.Get("Working")),
                context + ": empty failed assistant produces neither duplicate error card nor Busy");
            Check(notification.IsOpen && notification.Severity == InfoBarSeverity.Error && notification.Visibility == Visibility.Visible
                && notification.Content is TextBlock { Text: "Exact service error fixture", IsTextSelectionEnabled: true },
                context + ": exact selectable error shown only in top native error notification");
            var errorBackgrounds = Descendants(notification).OfType<Border>().Select(b => b.Background)
                .OfType<Microsoft.UI.Xaml.Media.SolidColorBrush>().Select(b => b.Color).ToArray();
            Check(errorBackgrounds.Any(c => c.R > c.G && c.R > c.B), context + ": native top error background is red in " + theme);
            conversation.Messages[0].Message.Parts.Add(new AIHappey.Vercel.Models.TextUIPart { Text = "Partial answer fixture" });
            Call(shell, "RenderTranscript"); await Task.Delay(60);
            Check(Descendants(shell).OfType<ChatMarkdown>().Any(m => m.Text == "Partial answer fixture")
                && Descendants(shell).OfType<InfoBar>().Count(b => b.IsOpen) == 1, context + ": partial text remains visible without duplicating top error");
            notification.IsOpen = false; await Task.Delay(50);
            Check(notification.Visibility == Visibility.Collapsed, context + ": dismiss closes the top notification");
        }
        finally { await shell.ShutdownAsync(); window.Content = null; if (Directory.Exists(session.DataDirectory)) Directory.Delete(session.DataDirectory, true); }
    }
}
