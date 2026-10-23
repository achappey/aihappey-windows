using AIHappey.Desktop.Core;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace AIHappey.Desktop.UiTests;

public partial class App
{
    private async Task CheckChatSettingsScrollingAsync(ElementTheme theme)
    {
        var root = new Grid { RequestedTheme = theme }; window!.Content = root;
        window.Activate(); await Task.Delay(100);
        var scale = root.XamlRoot.RasterizationScale;
        var original = new ChatPreferences();
        ChatPreferences? saved = null;
        var dialog = new ChatSettingsDialog(original, "openai", availableTools: _ => Enumerable.Range(1, 30)
            .Select(i => new ChatToolOption("fixture_" + i, "Fixture tool " + i)).ToArray())
            { XamlRoot = root.XamlRoot, RequestedTheme = theme, SaveAsync = next => { saved = next; return Task.CompletedTask; } };
        window.AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(1200 * scale), (int)(900 * scale))); await Task.Delay(100);
        SystemAppearance.PrepareDialog(dialog);
        var shown = dialog.ShowAsync(); await Task.Delay(160);
        var page = Field<ScrollViewer>(dialog, "page");
        string? lastPlugin = null; bool expectedPlugin = false, expectedPolicy = false;
        foreach (var size in new[] { (Width: 1200, Height: 1100), (Width: 1200, Height: 900), (Width: 500, Height: 650), (Width: 1100, Height: 550), (Width: 1200, Height: 1100) })
        {
            window.AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(size.Width * scale), (int)(size.Height * scale)));
            await Task.Delay(140); dialog.UpdateLayout();
            var context = $"Chat settings scroll / {theme} / {size.Width}x{size.Height}";
            dialog.ShowToolsTab(); await Task.Delay(100); dialog.UpdateLayout();
            var toggles = Descendants(dialog).OfType<ToggleSwitch>().Where(t => t.Name == "ChatPluginToggle").ToArray();
            var last = toggles.Last(); lastPlugin = (string)last.Tag;
            await ScrollSettingsToBottomAsync(dialog, page);
            RecordSettingsScrollGeometry(dialog, page, last, context + " / Tools");
            CheckSettingsBottomToggle(dialog, page, last, context + ": final plugin toggle");
            expectedPlugin = !last.IsOn; ToggleSettingsSwitch(last);
            Check(last.IsOn == expectedPlugin && original.ActivePlugins.Count == 0, context + ": final plugin can be toggled without changing original settings");

            Field<Action>(dialog, "selectGeneralTab")();
            await Task.Delay(80);
            foreach (var expander in Descendants(dialog).OfType<SettingsExpander>().Where(e => e.Name is "GeneralToolsCard" or "GeneralMcpPolicyCard"))
                expander.IsExpanded = true;
            await Task.Delay(160); dialog.UpdateLayout();
            var policy = Descendants(dialog).OfType<ToggleSwitch>().Single(t => t.Name == "ChatPolicyIdempotent");
            await ScrollSettingsToBottomAsync(dialog, page);
            RecordSettingsScrollGeometry(dialog, page, policy, context + " / General");
            CheckSettingsBottomToggle(dialog, page, policy, context + ": final policy toggle with both sections expanded");
            expectedPolicy = !policy.IsOn; ToggleSettingsSwitch(policy);
            Check(policy.IsOn == expectedPolicy, context + ": final policy switch can be toggled");
        }
        InvokeButton(Descendants(dialog).OfType<Button>().Single(b => b.Name == "CloseButton")); await shown;
        Check(saved is not null && saved.PluginEnabled(lastPlugin!) == expectedPlugin && saved.ToolAnnotations.IdempotentHint == expectedPolicy,
            "Chat settings scroll / " + theme + ": bottom plugin and policy changes persist on close");
    }

    private static async Task ScrollSettingsToBottomAsync(ChatSettingsDialog dialog, ScrollViewer page)
    {
        // Wait for expander animations and reflow before asking for the final scroll extent.
        for (var i = 0; i < 4; i++)
        {
            dialog.UpdateLayout(); page.ChangeView(null, page.ScrollableHeight, null, true); await Task.Delay(80);
        }
    }

    private void CheckSettingsBottomToggle(ChatSettingsDialog dialog, ScrollViewer page, ToggleSwitch toggle, string context)
    {
        var point = toggle.TransformToVisual(dialog).TransformPoint(new());
        var viewport = page.TransformToVisual(dialog).TransformPoint(new());
        var close = Descendants(dialog).OfType<Button>().Single(b => b.Name == "CloseButton");
        var footer = close.TransformToVisual(dialog).TransformPoint(new());
        Check(page.ScrollableHeight > 0 && Math.Abs(page.VerticalOffset - page.ScrollableHeight) <= 1,
            context + " reaches maximum scroll offset");
        Check(point.Y >= viewport.Y - 1 && point.Y + toggle.ActualHeight <= Math.Min(viewport.Y + page.ActualHeight, footer.Y) + 1,
            context + " is fully within the visible body above the footer");
        for (DependencyObject? current = VisualTreeHelper.GetParent(page); current is not null && current != dialog; current = VisualTreeHelper.GetParent(current))
            if (current is FrameworkElement ancestor)
            {
                var origin = ancestor.TransformToVisual(dialog).TransformPoint(new());
                Check(point.Y >= origin.Y - 1 && point.Y + toggle.ActualHeight <= origin.Y + ancestor.ActualHeight + 1,
                    context + " fits its " + ancestor.GetType().Name + "/" + ancestor.Name + " ancestor");
            }
        var center = toggle.TransformToVisual(dialog).TransformPoint(new(toggle.ActualWidth / 2, toggle.ActualHeight / 2));
        Check(VisualTreeHelper.FindElementsInHostCoordinates(center, dialog).Contains(toggle), context + " is hit-testable, not clipped by a dialog ancestor");
    }

    private static void ToggleSettingsSwitch(ToggleSwitch toggle) => ((IToggleProvider)new ToggleSwitchAutomationPeer(toggle).GetPattern(PatternInterface.Toggle)).Toggle();

    private void RecordSettingsScrollGeometry(ChatSettingsDialog dialog, ScrollViewer page, ToggleSwitch toggle, string context)
    {
        results.Add($"{context}: viewport={page.ViewportHeight}, extent={page.ExtentHeight}, offset={page.VerticalOffset}, scrollable={page.ScrollableHeight}, toggleY={toggle.TransformToVisual(dialog).TransformPoint(new()).Y}, toggleHeight={toggle.ActualHeight}");
        for (DependencyObject? current = page; current is not null && current != dialog; current = VisualTreeHelper.GetParent(current))
            if (current is FrameworkElement element)
                results.Add($"  {element.GetType().Name}/{element.Name}: height={element.Height}, actual={element.ActualHeight}, desired={element.DesiredSize.Height}, max={element.MaxHeight}, y={element.TransformToVisual(dialog).TransformPoint(new()).Y}");
        File.WriteAllLines(report, results);
    }
}
