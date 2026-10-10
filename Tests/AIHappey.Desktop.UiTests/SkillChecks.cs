using System.Reflection;
using System.Text.Json;
using AIHappey.Desktop.Core;
using AIHappey.Vercel.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace AIHappey.Desktop.UiTests;

public partial class App
{
    private async Task CheckSkillsAsync(ElementTheme theme)
    {
        await CheckSkillEditorAsync(theme);
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
        SystemAppearance.PrepareDialog(dialog); var shown = dialog.ShowAsync(); await Task.Delay(120); dialog.ShowSkillsTab(); await Task.Delay(120); dialog.UpdateLayout();
        Check(Descendants(dialog).OfType<ToggleButton>().Single(t => t.Name == "ChatSkillsTab").IsChecked == true,
            context + ": native Skills tab selected alongside General and provider");
        var toggles = Descendants(dialog).OfType<ToggleSwitch>().Where(t => t.Name == "ChatSkillToggle").ToArray();
        Check(toggles.Length == 3 && toggles.All(t => !string.IsNullOrWhiteSpace(AutomationProperties.GetName(t))), context + ": remote and MCP toggles have accessible labels");
        Check(Descendants(dialog).OfType<TextBlock>().Any(t => t.Text == "Favorites (1)") && Descendants(dialog).OfType<TextBlock>().Any(t => t.Text == "api.example"),
            context + ": favorites and API source groups shown");
        toggles.Single(t => (string)t.Tag == "provider/sample").IsOn = true; await Task.Delay(60);
        Check(original.EnabledSkillIds.Count == 0 && prefetches == 1 && Descendants(dialog).OfType<TextBlock>().Single(t => t.Name == "ChatSkillFeedback").Text.Contains("first use"),
            context + ": toggle changes isolated draft, failed prefetch retains selection with feedback");
        toggles.Single(t => (string)t.Tag == "mcp:server:sample").IsOn = true;
        var search = Descendants(dialog).OfType<TextBox>().Single(t => t.Name == "ChatSkillSearch"); search.Text = "Connected"; await Task.Delay(80); dialog.UpdateLayout();
        Check(Descendants(dialog).OfType<ToggleSwitch>().Count(t => t.Name == "ChatSkillToggle") == 1, context + ": skill description search filters list without losing selections");
        window.AppWindow.Resize(new Windows.Graphics.SizeInt32(500, 900)); await Task.Delay(100); dialog.UpdateLayout();
        Check(Descendants(dialog).OfType<ScrollViewer>().All(s => s.ScrollableWidth < 1), context + ": Skills tab fits narrow native dialog without horizontal overflow");
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
