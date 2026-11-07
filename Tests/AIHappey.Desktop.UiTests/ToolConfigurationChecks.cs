using AIHappey.Desktop.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace AIHappey.Desktop.UiTests;

public partial class App
{
    private async Task CheckToolConfigurationAsync(ElementTheme theme)
    {
        var context = "Tool configuration / " + theme;
        var root = new Grid { RequestedTheme = theme }; window!.Content = root; window.Activate(); await Task.Delay(100);
        var scale = root.XamlRoot.RasterizationScale;
        window.AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(1200 * scale), (int)(1000 * scale))); await Task.Delay(80);
        var count = 24;
        IReadOnlyList<ChatToolOption> Tools(ChatPreferences _) => Enumerable.Range(1, count).Select(i => new ChatToolOption("fixture_" + i,
            i == 24 ? "fixture_with_a_very_long_tool_name_that_must_wrap_instead_of_requiring_horizontal_scrolling" : "fixture_" + i,
            new("mcp", "fixture", "Fixture service", RequestOptions: new() { AllowedCallers = i == 1 ? ["programmatic"] : null }))).ToArray();
        var original = new ChatPreferences(); ChatPreferences? saved = null; var failSave = false;
        ChatSettingsDialog Create(ChatPreferences preferences) => new(preferences, "openai", availableTools: Tools,
            providers: ["openai", "anthropic", "google", "mistral", "deepseek"])
            { XamlRoot = root.XamlRoot, RequestedTheme = theme, SaveAsync = next => { if (failSave) throw new IOException("fixture"); saved = next; return Task.CompletedTask; } };
        var dialog = Create(original); SystemAppearance.PrepareDialog(dialog); var shown = dialog.ShowAsync(); await Task.Delay(160);
        var tabs = Field<NavigationView>(dialog, "tabs");
        Check(tabs.MenuItems.OfType<NavigationViewItem>().Take(4).Select(t => t.Name).SequenceEqual(["ChatGeneralTab", "ChatToolsTab", "ChatToolConfigurationTab", "ChatSkillsTab"]), context + ": recognizable tab order");
        dialog.ShowToolConfigurationTab(); await Task.Delay(100); dialog.UpdateLayout();
        var draft = Field<ChatPreferences>(dialog, "draft");
        DropDownButton Caller(string id) => Descendants(dialog).OfType<DropDownButton>().Single(b => b.Name == "ChatToolCallers" && b.Tag as string == id);
        ToggleSwitch Search(string id) => Descendants(dialog).OfType<ToggleSwitch>().Single(b => b.Name == "ChatToolSearch" && b.Tag as string == id);
        async Task Select(string id, string value)
        {
            var button = Caller(id); button.StartBringIntoView(); await Task.Delay(100);
            button.Flyout.ShowAt(button); await Task.Delay(100);
            Invoke(((MenuFlyout)button.Flyout).Items.OfType<ToggleMenuFlyoutItem>().Single(i => i.Tag as string == value));
            button.Flyout.Hide(); await Task.Delay(60);
        }
        var flyout = (MenuFlyout)Caller("fixture_1").Flyout;
        Check(flyout.Items.OfType<ToggleMenuFlyoutItem>().Single(i => i.Tag as string == "programmatic").IsChecked, context + ": inherited source caller selection shown");
        await Select("fixture_1", "direct");
        Check(draft.ToolRequestConfig["fixture_1"].AllowedCallers!.SequenceEqual(["programmatic", "direct"]), context + ": native flyout supports both calling methods");
        await Select("fixture_1", "programmatic"); await Select("fixture_1", "direct");
        Check(!draft.ToolRequestConfig.ContainsKey("fixture_1"), context + ": clearing callers removes the explicit override and restores source defaults");
        await Select("all", "direct");
        Check(Tools(draft).All(t => draft.ToolRequestConfig[t.Name].AllowedCallers!.SequenceEqual(["direct"])), context + ": All tools updates the visible catalog");
        ToggleSettingsSwitch(Search("all"));
        Check(Tools(draft).All(t => draft.ToolRequestConfig[t.Name].DeferLoading == true) && original.ToolRequestConfig.Count == 0, context + ": bulk search edits only the isolated draft");
        var before = Caller("fixture_1"); before.Focus(FocusState.Keyboard); dialog.RefreshAvailableTools(); await Task.Delay(50);
        Check(ReferenceEquals(before, Caller("fixture_1")) && ReferenceEquals(FocusManager.GetFocusedElement(root.XamlRoot), before), context + ": equivalent catalog refresh preserves controls and keyboard focus");
        var namespaces = Descendants(dialog).OfType<ToggleSwitch>().Single(t => t.Name == "ChatUseToolNamespaces");
        ToggleSettingsSwitch(namespaces); await Task.Delay(80);
        Check(draft.UseToolNamespaces && Descendants(dialog).OfType<TextBlock>().Any(t => t.Name == "ChatToolNamespaceHeading" && t.Text.Contains("fixture_service")), context + ": namespaces use recognizable source headings");
        count = 0; dialog.RefreshAvailableTools(); await Task.Delay(60);
        Check(!namespaces.IsEnabled && !Descendants(dialog).OfType<DropDownButton>().Any(b => b.Name == "ChatToolCallers") && draft.ToolRequestConfig.Count == 24,
            context + ": disconnected/empty catalog keeps hidden overrides without stale rows");
        count = 24; dialog.ShowToolsTab(); dialog.ShowToolConfigurationTab(); await Task.Delay(100);
        Check(Search("fixture_24").IsOn && draft.UseToolNamespaces, context + ": tab entry refresh restores returning tools and draft edits");
        foreach (var size in new[] { (Width: 500, Height: 650), (Width: 1100, Height: 550), (Width: 1200, Height: 1000) })
        {
            window.AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(size.Width * scale), (int)(size.Height * scale))); await Task.Delay(160);
            dialog.UpdateLayout(); var page = Field<ScrollViewer>(dialog, "page");
            await ScrollSettingsToBottomAsync(dialog, page);
            var last = Descendants(dialog).OfType<ToggleSwitch>().Last(t => t.Name == "ChatToolSearch");
            CheckSettingsBottomToggle(dialog, page, last, context + $" / {size.Width}x{size.Height}");
            Check(page.ScrollableWidth < 1 && tabs.ActualWidth <= Field<Grid>(dialog, "layout").ActualWidth + 1,
                context + ": narrow content and native navigation overflow stay inside dialog");
            Check(AutomationProperties.GetName(last).Length > 0 && AutomationProperties.GetAutomationId(last).Length > 0 && last.Focus(FocusState.Keyboard), context + ": final action is labeled and keyboard focusable");
        }
        failSave = true; InvokeButton(Descendants(dialog).OfType<Button>().Single(b => b.Name == "CloseButton")); await Task.Delay(100);
        Check(shown.Status == Windows.Foundation.AsyncStatus.Started && saved is null && draft.ToolRequestConfig.Count == 24,
            context + ": save failure keeps request configuration draft open");
        failSave = false; InvokeButton(Descendants(dialog).OfType<Button>().Single(b => b.Name == "CloseButton")); await shown;
        Check(saved is { UseToolNamespaces: true } && saved.ToolRequestConfig.Count == 24 && original.ToolRequestConfig.Count == 0, context + ": Close commits session settings without changing original preferences");
        var reopen = Create(saved!); SystemAppearance.PrepareDialog(reopen); shown = reopen.ShowAsync(); await Task.Delay(120);
        dialog = reopen; dialog.ShowToolConfigurationTab(); await Task.Delay(80);
        Check(Search("fixture_1").IsOn && ((MenuFlyout)Caller("fixture_1").Flyout).Items.OfType<ToggleMenuFlyoutItem>().Single(i => i.Tag as string == "direct").IsChecked,
            context + ": reopening during session restores tool configuration");
        InvokeButton(Descendants(dialog).OfType<Button>().Single(b => b.Name == "PrimaryButton")); await Task.Delay(80);
        dialog.ShowToolConfigurationTab(); await Task.Delay(80);
        Check(!Field<ChatPreferences>(dialog, "draft").UseToolNamespaces && Field<ChatPreferences>(dialog, "draft").ToolRequestConfig.Count == 0 && !Search("fixture_1").IsOn,
            context + ": Restore defaults rebuilds the tab and clears request-only settings");
        InvokeButton(Descendants(dialog).OfType<Button>().Single(b => b.Name == "CloseButton")); await shown;
        foreach (var service in new[] { ServiceKind.Ai, ServiceKind.Agents })
        {
            var isolated = new ChatSettingsDialog(original, "mistral", service: service, realtimePreferences: service == ServiceKind.Ai ? new() : null);
            Check(!Field<NavigationView>(isolated, "tabs").MenuItems.OfType<NavigationViewItem>().Any(t => t.Name == "ChatToolConfigurationTab"), context + ": tab excluded from agent/realtime configuration");
        }
    }
}
