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
    private async Task CheckChatToolsSettingsAsync(ElementTheme theme)
    {
        var context = "Local tools / " + theme;
        var root = new Grid { RequestedTheme = theme }; window!.Content = root;
        window.Activate(); await Task.Delay(100);
        var scale = root.XamlRoot.RasterizationScale;
        window.AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(1200 * scale), (int)(900 * scale))); await Task.Delay(100);
        var original = new ChatPreferences { ApiDebugEnabled = true, ToolOutputContentSettings = new() { Text = false, Images = true, Audio = true, Video = true, Documents = true } }; ChatPreferences? saved = null;
        var dialog = new ChatSettingsDialog(original, "openai") { XamlRoot = root.XamlRoot, RequestedTheme = theme,
            SaveAsync = next => { saved = next; return Task.CompletedTask; } };
        SystemAppearance.PrepareDialog(dialog); var shown = dialog.ShowAsync(); await Task.Delay(120); dialog.ShowToolsTab(); await Task.Delay(100); dialog.UpdateLayout();
        var tabs = Descendants(dialog).OfType<ToggleButton>().Where(t => t.Name.StartsWith("Chat") && t.Name.EndsWith("Tab")).ToArray();
        Check(tabs.Single(t => t.Name == "ChatToolsTab").IsChecked == true && tabs.Count(t => t.IsChecked == true) == 1,
            context + ": Tools tab navigation selects exactly one native tab");
        Check(!Descendants(dialog).OfType<FrameworkElement>().Any(e => e.Name is "SendToolOutputImages" or "ToolOutputImagesCard"), context + ": obsolete image-output card is removed from inference tools");
        var toggles = Descendants(dialog).OfType<ToggleSwitch>().Where(t => t.Name == "ChatPluginToggle").ToArray();
        var pluginCount = DesktopLocalTools.Plugins.Count;
        Check(toggles.Length == pluginCount && pluginCount == 10 && toggles.All(t => !t.IsOn), context + ": all ten plugins initially off");
        Check(toggles.Select(AutomationProperties.GetName).SequenceEqual(DesktopLocalTools.Plugins.Select(p => DesktopResources.Get(p.ResourceKey)))
            && toggles.All(t => AutomationProperties.GetAutomationId(t) == "ChatPlugin_" + t.Tag), context + ": browser labels and stable accessible identities");
        var pluginGrid = Descendants(dialog).OfType<Grid>().Single(g => g.Name == "ChatPluginGrid");
        await WaitForSkillUiAsync(() => pluginGrid.ColumnDefinitions.Count == 2);
        var pluginCards = pluginGrid.Children.Cast<FrameworkElement>().ToArray();
        Check(pluginGrid.ColumnDefinitions.Count == 2 && pluginGrid.RowDefinitions.Count == (pluginCount + 1) / 2
            && pluginCards.Select(Grid.GetColumn).SequenceEqual(Enumerable.Range(0, pluginCount).Select(i => i % 2))
            && pluginCards.Select(Grid.GetRow).SequenceEqual(Enumerable.Range(0, pluginCount).Select(i => i / 2)), context + ": wide Tools tab uses two columns in plugin order");
        var first = (FrameworkElement)pluginGrid.Children[0]; var second = (FrameworkElement)pluginGrid.Children[1];
        var firstPoint = first.TransformToVisual(dialog).TransformPoint(new()); var secondPoint = second.TransformToVisual(dialog).TransformPoint(new());
        Check(Math.Abs(firstPoint.Y - secondPoint.Y) < 1 && secondPoint.X > firstPoint.X
            && toggles.All(t => Within(t, dialog)), context + ": wide plugin cards share a row with visible switches");
        foreach (var toggle in toggles) toggle.IsOn = true;
        Check(original.ActivePlugins.Count == 0, context + ": toggles edit isolated draft");
        dialog.ShowSkillsTab(); await Task.Delay(60); dialog.ShowToolsTab(); await Task.Delay(60);
        Check(Descendants(dialog).OfType<ToggleSwitch>().Where(t => t.Name == "ChatPluginToggle").All(t => t.IsOn), context + ": navigation retains draft selections");
        window.AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(500 * scale), (int)(900 * scale))); await Task.Delay(120); dialog.UpdateLayout();
        await WaitForSkillUiAsync(() => pluginGrid.ColumnDefinitions.Count == 1);
        Check(pluginGrid.ColumnDefinitions.Count == 1 && pluginGrid.RowDefinitions.Count == pluginCount
            && pluginCards.All(c => Grid.GetColumn(c) == 0)
            && pluginCards.Select(Grid.GetRow).SequenceEqual(Enumerable.Range(0, pluginCount)) && toggles.All(t => Within(t, dialog)),
            context + ": narrow Tools tab switches to one column without clipping switches");
        Check(Descendants(dialog).OfType<ScrollViewer>().Where(s => s.HorizontalScrollMode == ScrollMode.Disabled).All(s => s.ScrollableWidth < 1),
            context + ": Tools cards fit narrow native viewport");
        window.AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(1200 * scale), (int)(900 * scale))); await Task.Delay(120); dialog.UpdateLayout();
        await WaitForSkillUiAsync(() => pluginGrid.ColumnDefinitions.Count == 2);
        Check(pluginGrid.ColumnDefinitions.Count == 2 && toggles.All(t => t.IsOn), context + ": widening restores two columns without rebuilding or resetting switches");
        InvokeButton(Descendants(dialog).OfType<Button>().Single(b => b.Name == "CloseButton")); await shown;
        Check(saved?.ActivePlugins.Count == pluginCount && dialog.Result?.ActivePlugins.Count == pluginCount, context + ": close saves all plugin selections");
        var reopen = new ChatSettingsDialog(saved!, null) { XamlRoot = root.XamlRoot, RequestedTheme = theme };
        shown = reopen.ShowAsync(); await Task.Delay(100); reopen.ShowToolsTab(); await Task.Delay(100);
        Check(Descendants(reopen).OfType<ToggleSwitch>().Where(t => t.Name == "ChatPluginToggle").All(t => t.IsOn), context + ": reopen restores enabled toggles");
        InvokeButton(Descendants(reopen).OfType<Button>().Single(b => b.Name == "PrimaryButton")); await Task.Delay(80); reopen.ShowToolsTab(); await Task.Delay(80);
        Check(Descendants(reopen).OfType<ToggleSwitch>().Where(t => t.Name == "ChatPluginToggle").All(t => !t.IsOn), context + ": restore defaults clears plugins and rebuilds tools page");
        InvokeButton(Descendants(reopen).OfType<Button>().Single(b => b.Name == "CloseButton")); await shown;
        Check(reopen.Result is { ApiDebugEnabled: true } reset && !reset.ToolOutputContentSettings.Text && reset.ToolOutputContentSettings.Images
            && reset.ToolOutputContentSettings.Audio && reset.ToolOutputContentSettings.Video && reset.ToolOutputContentSettings.Documents,
            context + ": inference reset preserves user-settings debug and all five tool-output choices");
    }

    private async Task CheckLocalToolsAsync(ElementTheme theme)
    {
        await CheckChatToolsSettingsAsync(theme);
        var context = "Local tools / " + theme;
        var session = new DesktopSession(new UiHost(true), new UiRuntime(), new());
        session.Settings.Images.StorageRoot = Path.Combine(session.DataDirectory, "authoring-test-images");
        var shell = new ChatShell(session) { RequestedTheme = theme }; window.Content = shell; await Task.Delay(200); await HistoryIdleAsync(shell);
        try
        {
            var runtime = (McpTurnSnapshot)Call(shell, "CaptureSkillRuntime", new ChatPreferences())!;
            Check(runtime.Tools.Count == 0, context + ": default Windows runtime has no plugin tools");
            var preferences = new ChatPreferences { ActivePlugins = DesktopLocalTools.Plugins.Select(p => p.Id).ToList() };
            runtime = (McpTurnSnapshot)Call(shell, "CaptureSkillRuntime", preferences)!;
            var toolCount = DesktopLocalTools.Plugins.Sum(p => p.Tools.Count);
            Check(runtime.Tools.Count == toolCount && runtime.Tools.Select(t => t.GetProperty("name").GetString()).Distinct().Count() == toolCount,
                context + ": native shell captures all enabled plugin routes exactly once");
            foreach (var plugin in DesktopLocalTools.Plugins.Where(p => p.Id is DesktopLocalTools.WindowsSearch or DesktopLocalTools.Files
                or DesktopLocalTools.Images or DesktopLocalTools.AgentsEditor or DesktopLocalTools.AgentsRuntime or DesktopLocalTools.SkillEditor))
            {
                var independent = (McpTurnSnapshot)Call(shell, "CaptureSkillRuntime", new ChatPreferences { ActivePlugins = [plugin.Id] })!;
                Check(independent.Tools.Count == plugin.Tools.Count && independent.Tools.Select(t => t.GetProperty("name").GetString()).SequenceEqual(plugin.Tools.Select(t => t.GetProperty("name").GetString())),
                    context + ": independently enabled " + plugin.Id + " registers only its own tool without executing native work");
            }
            var suffix = Guid.NewGuid().ToString("N");
            var skillCreated = await runtime.CallAsync("local_skill_editor_create", JsonSerializer.SerializeToElement(new { name = "ui-" + suffix, description = "UI tool fixture" }), "skill-create", "en", default);
            Check(!skillCreated.GetProperty("isError").GetBoolean()
                && ((IReadOnlyList<DesktopSkill>)typeof(ChatShell).GetField("localSkills", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(shell)!).Any(s => s.Name == "ui-" + suffix),
                context + ": skill editor tool updates real native catalog state");
            var agentCreated = await runtime.CallAsync("local_agents_create", JsonSerializer.SerializeToElement(new { agentName = "UI " + suffix, agentDescription = "UI tool fixture", agentInstructions = "Test", modelId = "p/model", modelTemperature = 0 }), "agent-create", "en", default);
            Check(!agentCreated.GetProperty("isError").GetBoolean()
                && ((IReadOnlyList<DesktopAgent>)typeof(ChatShell).GetField("localAgents", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(shell)!).Any(a => a.Name == "UI " + suffix),
                context + ": agent editor tool updates real native catalog state");
            var maskCreated = await runtime.CallAsync("local_mask_create", JsonSerializer.SerializeToElement(new { name = "ui-mask.png", width = 2, height = 2, boxes = Array.Empty<object>() }), "mask-create", "en", default);
            var imageResult = await runtime.CallAsync("local_images_read", JsonSerializer.SerializeToElement(new { image = maskCreated.GetProperty("structuredContent").GetProperty("image") }), "image-read", "en", default);
            Check(!imageResult.GetProperty("isError").GetBoolean(), context + ": image tools share mask Files references in real shell");
            var outputDialog = new ToolOutputDialog("local_images_read", JsonSerializer.SerializeToElement(new { output = imageResult })) { XamlRoot = shell.XamlRoot };
            var imageShown = outputDialog.ShowAsync(); await Task.Delay(150);
            var outputTabs = Descendants(outputDialog).OfType<NavigationView>().Single(t => t.Name == "ToolOutputTabsNavigation");
            outputTabs.SelectedItem = outputTabs.MenuItems[2]; await Task.Delay(180);
            Check(Descendants(outputDialog).OfType<Image>().Any(i => i.Name == "ToolOutputImage" && i.Source is not null), context + ": tool-output PNG renders natively without exposing raw base64 as its preview");
            outputDialog.Hide(); await imageShown;
            if (Environment.GetCommandLineArgs().Contains("--authoring-tools-only")) return;
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
