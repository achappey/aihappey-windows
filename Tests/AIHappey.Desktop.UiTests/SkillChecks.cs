using System.Reflection;
using System.Text.Json;
using AIHappey.Desktop.Core;
using AIHappey.Vercel.Models;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace AIHappey.Desktop.UiTests;

public partial class App
{
    private async Task CheckChatSkillsSettingsAsync(ElementTheme theme)
    {
        var root = new Grid { RequestedTheme = theme }; window!.Content = root;
        window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1100, 900)); window.Activate(); await Task.Delay(100);
        var context = "Skills / " + theme; var original = new ChatPreferences(); var prefetches = 0;
        var items = new DesktopSkill[] { new("provider/sample", "Sample", "Remote description", "remote", "2"),
            new("mcp:server:sample", "MCP sample", "Connected description", "mcp", Server: "Fixture server"),
            new("provider/other", "Other", "Other description", "remote") };
        var dialog = new ChatSettingsDialog(original, "openai", _ => Task.FromResult(new DesktopSkillSelection(items,
            new HashSet<string> { "provider/sample" }, "api.example")), (_, _) => { prefetches++; throw new IOException("offline"); })
            { XamlRoot = root.XamlRoot, RequestedTheme = theme };
        ChatPreferences? saved = null; dialog.SaveAsync = next => { saved = next; return Task.CompletedTask; };
        SystemAppearance.PrepareDialog(dialog); var shown = dialog.ShowAsync(); await Task.Delay(120);
        Check(Descendants(dialog).OfType<TextBox>().Any(t => t.Name == "MaxOutputTokens")
            && !Descendants(dialog).OfType<TextBox>().Any(t => t.Name == "SystemInstructions"), context + ": General retains token limit but has no custom-instructions field");
        dialog.ShowSkillsTab(); await Task.Delay(120); dialog.UpdateLayout();
        Check(Descendants(dialog).OfType<ToggleButton>().Single(t => t.Name == "ChatSkillsTab").IsChecked == true,
            context + ": native Skills tab selected alongside General and provider");
        var toggles = Descendants(dialog).OfType<ToggleSwitch>().Where(t => t.Name == "ChatSkillToggle").ToArray();
        Check(toggles.Length == 3 && toggles.All(t => !string.IsNullOrWhiteSpace(AutomationProperties.GetName(t))), context + ": remote and MCP toggles have accessible labels");
        var rows = Descendants(dialog).OfType<SettingsExpander>().Where(e => e.Name == "ChatSkillCard").ToArray();
        Check(rows.Length == 3 && rows.All(e => !e.IsExpanded && e.Content is ToggleSwitch { Header: null, OnContent: "", OffContent: "" }),
            context + ": skills start as compact native expanders with header switches and no switch captions");
        var sample = rows.Single(e => e.Tag as string == "provider/sample");
        var title = (TextBlock)sample.Header; var sampleToggle = (ToggleSwitch)sample.Content;
        var titlePoint = title.TransformToVisual(sample).TransformPoint(new());
        var togglePoint = sampleToggle.TransformToVisual(sample).TransformPoint(new());
        Check(titlePoint.X + title.ActualWidth <= togglePoint.X
            && Math.Abs(titlePoint.Y + title.ActualHeight / 2 - togglePoint.Y - sampleToggle.ActualHeight / 2) < 2
            && Within(sampleToggle, dialog), context + ": skill title and right-side switch occupy one visible header row");
        var collapsedHeight = sample.ActualHeight;
        sample.IsExpanded = true; await Task.Delay(350); dialog.UpdateLayout();
        Check(sample.ActualHeight > collapsedHeight && Descendants(sample).OfType<TextBlock>().Any(t => t.Text == "Remote description")
            && Descendants(sample).OfType<TextBlock>().Any(t => t.Text == "2"), context + ": expanding reveals skill description and version");
        sample.IsExpanded = false; await Task.Delay(350); dialog.UpdateLayout();
        Check(!sample.IsExpanded && sample.ActualHeight <= collapsedHeight + 1,
            context + ": collapsing restores a compact skill row");
        Check(Descendants(dialog).OfType<TextBlock>().Any(t => t.Text == "Favorites (1)") && Descendants(dialog).OfType<TextBlock>().Any(t => t.Text == "api.example"),
            context + ": favorites and API source groups shown");
        toggles.Single(t => (string)t.Tag == "provider/sample").IsOn = true; await Task.Delay(60);
        Check(!sample.IsExpanded && sampleToggle.IsOn, context + ": enabling a skill leaves its details collapsed");
        Check(original.EnabledSkillIds.Count == 0 && prefetches == 1 && Descendants(dialog).OfType<TextBlock>().Single(t => t.Name == "ChatSkillFeedback").Text.Contains("first use"),
            context + ": toggle changes isolated draft, failed prefetch retains selection with feedback");
        toggles.Single(t => (string)t.Tag == "mcp:server:sample").IsOn = true;
        var search = Descendants(dialog).OfType<TextBox>().Single(t => t.Name == "ChatSkillSearch"); search.Text = "Connected"; await Task.Delay(80); dialog.UpdateLayout();
        Check(Descendants(dialog).OfType<ToggleSwitch>().Count(t => t.Name == "ChatSkillToggle") == 1, context + ": skill description search filters list without losing selections");
        var connected = Descendants(dialog).OfType<SettingsExpander>().Single(e => e.Name == "ChatSkillCard");
        connected.IsExpanded = true; await Task.Delay(80);
        window.AppWindow.Resize(new Windows.Graphics.SizeInt32(500, 900)); await Task.Delay(350); dialog.UpdateLayout();
        var contentScrollers = Descendants(dialog).OfType<ScrollViewer>().Where(s => s.HorizontalScrollMode == ScrollMode.Disabled).ToArray();
        Check(contentScrollers.All(s => s.ScrollableWidth < 1), context + ": Skills content fits narrow native dialog without horizontal overflow ("
            + string.Join(", ", contentScrollers.Select(s => $"{s.Name}: viewport={s.ViewportWidth}, overflow={s.ScrollableWidth}")) + ")");
        Check(Within((ToggleSwitch)connected.Content, dialog)
            && Descendants(connected).OfType<TextBlock>().Any(t => t.Text == "Connected description" && t.ActualWidth > 0),
            context + ": narrow expanded skill retains a visible header switch and description");
        InvokeButton(Descendants(dialog).OfType<Button>().Single(b => b.Name == "CloseButton")); await shown;
        Check(saved is not null && saved.EnabledSkillIds.SequenceEqual(["provider/sample", "mcp:server:sample"]) && dialog.Result is not null,
            context + ": close commits all skill selections through existing save boundary");
        var reset = new ChatSettingsDialog(saved!, null, _ => Task.FromResult(new DesktopSkillSelection(items, new HashSet<string>(), "api.example")))
            { XamlRoot = root.XamlRoot, RequestedTheme = theme };
        shown = reset.ShowAsync(); await Task.Delay(100); InvokeButton(Descendants(reset).OfType<Button>().Single(b => b.Name == "PrimaryButton")); await Task.Delay(80);
        InvokeButton(Descendants(reset).OfType<Button>().Single(b => b.Name == "CloseButton")); await shown;
        Check(reset.Result?.EnabledSkillIds.Count == 0, context + ": restore defaults clears enabled skills");
        var failedSave = new ChatSettingsDialog(saved!, null) { XamlRoot = root.XamlRoot, RequestedTheme = theme, SaveAsync = _ => throw new IOException("disk") };
        shown = failedSave.ShowAsync(); await Task.Delay(80); InvokeButton(Descendants(failedSave).OfType<Button>().Single(b => b.Name == "CloseButton")); await Task.Delay(80);
        Check(shown.Status == Windows.Foundation.AsyncStatus.Started && failedSave.Result is null, context + ": disk failure keeps dialog and previous state intact");
        failedSave.DiscardOnShutdown = true; failedSave.Hide(); await shown;
    }

    private async Task CheckSkillsAsync(ElementTheme theme)
    {
        await CheckSkillEditorAsync(theme);
        await CheckChatSkillsSettingsAsync(theme);
        var context = "Skills / " + theme;
        var session = new DesktopSession(new UiHost(true), new UiRuntime(), new());
        session.Settings.Chat.EnabledSkillIds = ["provider/sample", "mcp:server:missing"];
        var shell = new ChatShell(session) { RequestedTheme = theme }; window.Content = shell; await Task.Delay(200); await HistoryIdleAsync(shell);
        try
        {
            typeof(ChatShell).GetField("runtimeSkillCatalog", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(shell,
                new CatalogItem[] { new(CatalogKind.Skill, "provider/sample", "Sample", "Remote description") });
            typeof(ChatShell).GetField("runtimeSkillPartition", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(shell, session.HistoryPartition);
            Call(shell, "RenderContextTags"); shell.UpdateLayout();
            Check(Descendants(shell).OfType<Border>().Count(b => b.Name == "EnabledSkillBadge") == 2, context + ": composer shows enabled and disconnected selections with removable badges");
            var runtime = (McpTurnSnapshot)Call(shell, "CaptureSkillRuntime", session.Settings.Chat)!;
            Check(runtime.Tools.Count == 2 && runtime.Context.Single().GetRawText().Contains("provider/sample") && !runtime.Context.Single().GetRawText().Contains("mcp:server:missing"),
                context + ": request context/tools include resolved enabled skills but not disconnected ones");
            Call(shell, "ResetContext");
            Check(Descendants(shell).OfType<Border>().Count(b => b.Name == "EnabledSkillBadge") == 2, context + ": attachment reset preserves chat skill preferences");
            InvokeButton(Descendants(shell).OfType<Button>().Single(b => b.Name == "DisableSkill" && (string)b.Tag == "provider/sample")); await HistoryIdleAsync(shell);
            var persisted = await SettingsStore.LoadAsync(session.DataDirectory, new());
            Check(!session.Settings.Chat.EnabledSkillIds.Contains("provider/sample") && !persisted.Chat.EnabledSkillIds.Contains("provider/sample")
                && Descendants(shell).OfType<Border>().Count(b => b.Name == "EnabledSkillBadge") == 1, context + ": badge removal persists disablement and rerenders composer");
            runtime = (McpTurnSnapshot)Call(shell, "CaptureSkillRuntime", session.Settings.Chat)!;
            Check(runtime.Tools.Count == 0 && runtime.Context.Count == 0, context + ": removing last available skill removes request tools and system catalog");
            Call(shell, "UpdateMode", ServiceKind.Agents);
            Check(!Descendants(shell).OfType<Border>().Any(b => b.Name == "EnabledSkillBadge"), context + ": model-only badges are hidden for Agents");
            Call(shell, "UpdateMode", ServiceKind.Ai); shell.RequestedTheme = theme == ElementTheme.Light ? ElementTheme.Dark : ElementTheme.Light; await Task.Delay(50);
            Check(Descendants(shell).OfType<Border>().Single(b => b.Name == "EnabledSkillBadge").Tag is "mcp:server:missing", context + ": unavailable badge survives native theme switch");
        }
        finally { await shell.ShutdownAsync(); window.Content = null; if (Directory.Exists(session.DataDirectory)) Directory.Delete(session.DataDirectory, true); }
    }
}
