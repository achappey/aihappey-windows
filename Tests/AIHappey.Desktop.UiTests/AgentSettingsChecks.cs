using AIHappey.Desktop.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AIHappey.Desktop.UiTests;

public partial class App
{
    private async Task CheckAgentSettingsAsync(ElementTheme theme)
    {
        var context = "Agent settings / " + theme;
        var root = new Grid { RequestedTheme = theme }; window!.Content = root; window.Activate(); await Task.Delay(80);
        var scale = root.XamlRoot.RasterizationScale;
        window.AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(1100 * scale), (int)(900 * scale))); await Task.Delay(100);
        ChatTarget[] selected = [new("agent-one", "First agent") { RemoteAgentId = "one" }, new("agent-two", "Second agent") { RemoteAgentId = "two" }];
        var original = new AgentWorkflowPreferences(); var tools = new ChatPreferences();
        AgentWorkflowPreferences? saved = null; IReadOnlyList<string>? savedIds = null; var failSave = true;
        var dialog = new AgentWorkflowDialog(original, selected, [], tools) { XamlRoot = root.XamlRoot, RequestedTheme = theme };
        dialog.SaveAsync = (next, ids) => { if (failSave) throw new IOException("Fixture save failure"); saved = next; savedIds = ids; return Task.CompletedTask; };
        SystemAppearance.PrepareDialog(dialog); var shown = dialog.ShowAsync(); await Task.Delay(120); dialog.UpdateLayout();
        var navigation = Descendants(dialog).OfType<NavigationView>().Single(t => t.Name == "AgentSettingsTabs");
        var tabs = navigation.MenuItems.OfType<NavigationViewItem>().ToArray();
        var type = Descendants(dialog).OfType<ComboBox>().Single(c => c.Name == "WorkflowType");
        Button Button(string name) => Descendants(dialog).OfType<Button>().Single(b => b.Name == name);
        var layout = (Grid)dialog.Content;
        Check(dialog.Title as string == DesktopResources.Get("AgentSettings") && type.Header as string == DesktopResources.Get("AgentWorkflow")
            && tabs.Select(t => t.Name).SequenceEqual(["AgentGeneralTab", "AgentToolsTab"]) && ReferenceEquals(navigation.SelectedItem, tabs[0])
            && navigation.PaneDisplayMode == NavigationViewPaneDisplayMode.Top && tabs.All(t => AutomationProperties.GetAutomationId(t) == t.Name),
            context + ": localized title and native accessible General/Tools navigation retain workflow field label");
        Check(layout.ActualWidth > 700 && double.IsNaN(layout.Height), context + ": dialog uses wide responsive content and shrinkable height");
        type.SelectedIndex = AgentWorkflowPreferences.Types.ToList().IndexOf("groupchat"); await Task.Delay(40);
        Descendants(dialog).OfType<NumberBox>().Single().Value = 7;
        navigation.SelectedItem = tabs[1]; await Task.Delay(80);
        var toggle = Descendants(dialog).OfType<ToggleSwitch>().First(t => t.Name == "ChatPluginToggle"); var toolId = (string)toggle.Tag; toggle.IsOn = true;
        Check(original.Type == "sequential" && tools.ActivePlugins.Count == 0, context + ": both tabs edit isolated drafts");
        var page = Descendants(dialog).OfType<ScrollViewer>().Single(s => s.Name == "AgentSettingsPage");
        foreach (var (width, height) in new[] { (500, 900), (500, 600), (1100, 900) })
        {
            window.AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(width * scale), (int)(height * scale))); await Task.Delay(180); dialog.UpdateLayout();
            Check(layout.ActualWidth <= root.ActualWidth - 80 && navigation.ActualWidth <= layout.ActualWidth + 1
                && Descendants(dialog).OfType<ScrollViewer>().Where(s => s.HorizontalScrollMode == ScrollMode.Disabled).All(s => s.ScrollableWidth < 1)
                && Within(toggle, dialog) && ReferenceEquals(navigation.SelectedItem, tabs[1]), context + $": {width}x{height} fits tabs and tool toggles without changing selection");
            var footerTop = Button("PrimaryButton").TransformToVisual(dialog).TransformPoint(new()).Y;
            Check(page.TransformToVisual(dialog).TransformPoint(new()).Y + page.ActualHeight <= footerTop
                && page.ScrollableHeight > 0 && Button("CloseButton").ActualHeight > 0, context + $": {width}x{height} tools scroll above accessible Save/Cancel footer");
            page.ChangeView(null, page.ScrollableHeight, null, true); await Task.Delay(50);
            Check(page.VerticalOffset > 0, context + ": tools remain scrollable to the end");
        }
        InvokeButton(Button("PrimaryButton")); await Task.Delay(100);
        Check(shown.Status == Windows.Foundation.AsyncStatus.Started && ReferenceEquals(navigation.SelectedItem, tabs[0]) && saved is null
            && type.SelectedIndex == AgentWorkflowPreferences.Types.ToList().IndexOf("groupchat"), context + ": save failure returns to General without discarding edits");
        navigation.SelectedItem = tabs[1]; await Task.Delay(80);
        Check(Descendants(dialog).OfType<ToggleSwitch>().Single(t => t.Tag as string == toolId).IsOn, context + ": failed save preserves tool selection");
        failSave = false; InvokeButton(Button("PrimaryButton")); await shown;
        Check(saved is { Type: "groupchat", MaximumIterationCount: 7 } && savedIds!.SequenceEqual(selected.Select(t => t.Id))
            && dialog.ToolPreferences.ActivePlugins.Contains(toolId), context + ": Save commits workflow, ordering, and tool draft");

        var canceled = new AgentWorkflowDialog(original, selected, [], tools) { XamlRoot = root.XamlRoot, RequestedTheme = theme };
        var cancelSaved = false; canceled.SaveAsync = (_, _) => { cancelSaved = true; return Task.CompletedTask; };
        shown = canceled.ShowAsync(); await Task.Delay(100);
        Descendants(canceled).OfType<ComboBox>().Single(c => c.Name == "WorkflowType").SelectedIndex = 0;
        InvokeButton(Descendants(canceled).OfType<Button>().Single(b => b.Name == "CloseButton")); await shown;
        Check(!cancelSaved && original.Type == "sequential" && tools.ActivePlugins.Count == 0, context + ": Cancel discards drafts");

        var invalid = new AgentWorkflowDialog(new() { Type = "decisions" }, selected, [], tools) { XamlRoot = root.XamlRoot, RequestedTheme = theme };
        shown = invalid.ShowAsync(); await Task.Delay(100);
        navigation = Descendants(invalid).OfType<NavigationView>().Single(); navigation.SelectedItem = navigation.MenuItems[1]; await Task.Delay(60);
        InvokeButton(Descendants(invalid).OfType<Button>().Single(b => b.Name == "PrimaryButton")); await Task.Delay(100);
        Check(shown.Status == Windows.Foundation.AsyncStatus.Started && ReferenceEquals(navigation.SelectedItem, navigation.MenuItems[0])
            && Descendants(invalid).OfType<TextBlock>().Any(t => t.Text == DesktopResources.Get("RoutingModelRequired")), context + ": validation returns native navigation to General and exposes error");
        invalid.Hide(); await shown;
    }
}
