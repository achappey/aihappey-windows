using AIHappey.Desktop.Core;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AIHappey.Desktop.UiTests;

public partial class App
{
    private async Task CheckToolOutputSettingsAsync(ElementTheme theme)
    {
        var context = "Tool output / " + theme;
        var root = new Grid { RequestedTheme = theme }; window!.Content = root;
        window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1100, 900)); window.Activate(); await Task.Delay(100);
        var original = new DesktopSettings();
        SettingsDialog Dialog(DesktopSettings settings) => new(settings, true, "en", aiModels: []) { XamlRoot = root.XamlRoot, RequestedTheme = theme };
        SettingsExpander[] Cards(SettingsDialog dialog) => ((StackPanel)dialog.FindName("ChatPage")).Children.Cast<SettingsExpander>().ToArray();
        SettingsExpander Card(SettingsDialog dialog) => Cards(dialog).Single(e => e.Name == "ToolOutputContentCard");
        ToggleSwitch[] Toggles(SettingsDialog dialog) => ((StackPanel)dialog.FindName("ToolOutputContentPanel")).Children.OfType<ToggleSwitch>().ToArray();
        async Task ShowChat(SettingsDialog dialog)
        {
            await Task.Delay(100);
            ((NavigationView)dialog.FindName("Tabs")).SelectedItem = dialog.FindName("ChatTab");
            await Task.Delay(60); dialog.UpdateLayout();
            var cards = Cards(dialog);
            Check(cards.Select(card => card.Name).SequenceEqual(["ChatInputCard", "ChatOutputCard", "ToolOutputContentCard"])
                && cards.All(card => !card.IsExpanded), context + ": all three native Chat sections start collapsed in input/output/tool-output order");
            Check(cards.Select(AutomationProperties.GetName).SequenceEqual(["Input", "Output", "Tool output"])
                && cards.Select(card => ((TextBlock)card.Header).Text).SequenceEqual(["Input", "Output", "Tool output"]),
                context + ": native section headings and accessible names resolve localized resources");
            Check(cards.Select(card => ((StackPanel)card.ItemsHeader).Children.OfType<StackPanel>().Single().Name)
                .SequenceEqual(["ChatInputPanel", "ChatOutputPanel", "ToolOutputContentPanel"]), context + ": every settings panel belongs to its matching expander");
            foreach (var card in cards)
            {
                card.IsExpanded = true; await Task.Delay(60); dialog.UpdateLayout();
                Check(cards.Count(section => section.IsExpanded) == 1 && card.IsExpanded, context + ": expanding " + card.Name + " leaves other sections collapsed");
                card.IsExpanded = false;
            }
            Card(dialog).IsExpanded = true;
            await Task.Delay(60); dialog.UpdateLayout();
        }
        var cancelled = Dialog(original); SystemAppearance.PrepareDialog(cancelled);
        var shown = cancelled.ShowAsync(); await ShowChat(cancelled);
        var toggles = Toggles(cancelled);
        Check(toggles.Select(t => t.Name).SequenceEqual(["ToolOutputText", "ToolOutputLinks", "ToolOutputImages", "ToolOutputAudio", "ToolOutputVideo", "ToolOutputDocuments"])
            && toggles.Select(t => t.IsOn).SequenceEqual([true, true, false, false, false, false]), context + ": user Chat tab has all six toggles in browser order/defaults");
        Check(toggles.All(t => AutomationProperties.GetName(t).Length > 0)
            && Descendants(cancelled).OfType<TextBlock>().Any(t => t.Text == "Tool output"), context + ": native resources resolve the shortened title and accessible labels");
        foreach (var toggle in toggles) toggle.IsOn = !toggle.IsOn;
        Card(cancelled).IsExpanded = false; await Task.Delay(60);
        Card(cancelled).IsExpanded = true; await Task.Delay(60);
        Check(Toggles(cancelled).Select(t => t.IsOn).SequenceEqual([false, false, true, true, true, true]), context + ": collapse and expand retains all draft values");
        InvokeButton(Descendants(cancelled).OfType<Button>().Single(b => b.Name == "CloseButton")); await shown;
        Check(cancelled.Result is null && original.Chat.ToolOutputContentSettings.Text && original.Chat.ToolOutputContentSettings.Links && !original.Chat.ToolOutputContentSettings.Images, context + ": Cancel discards edits");

        var saved = Dialog(original); shown = saved.ShowAsync(); await ShowChat(saved);
        foreach (var toggle in Toggles(saved)) toggle.IsOn = !toggle.IsOn;
        foreach (var card in Cards(saved)) card.IsExpanded = true;
        window.AppWindow.Resize(new Windows.Graphics.SizeInt32(500, 900)); await Task.Delay(100); saved.UpdateLayout();
        Check(((Grid)saved.FindName("Layout")).ActualWidth <= root.ActualWidth
            && ((NavigationView)saved.FindName("Tabs")).IsPaneToggleButtonVisible
            && ((ScrollViewer)saved.FindName("PageScroll")).ScrollableWidth < 1
            && Cards(saved).All(card => card.ActualWidth <= ((Grid)saved.FindName("Layout")).ActualWidth), context + ": all expanded cards fit narrow Chat settings with vertical scrolling");
        InvokeButton(Descendants(saved).OfType<Button>().Single(b => b.Name == "PrimaryButton")); await shown;
        var result = saved.Result!;
        Check(!result.Chat.ToolOutputContentSettings.Text && !result.Chat.ToolOutputContentSettings.Links && result.Chat.ToolOutputContentSettings.Images && result.Chat.ToolOutputContentSettings.Audio
            && result.Chat.ToolOutputContentSettings.Video && result.Chat.ToolOutputContentSettings.Documents
            && original.Chat.ToolOutputContentSettings.Text && original.Chat.ToolOutputContentSettings.Links && !original.Chat.ToolOutputContentSettings.Images, context + ": Save returns all six values without mutating original");
        var directory = Path.Combine(Path.GetTempPath(), "AIHappey-tool-output-ui-" + Guid.NewGuid().ToString("N"));
        DesktopSettings loaded;
        try { await SettingsStore.SaveAsync(directory, result); loaded = await SettingsStore.LoadAsync(directory, new()); }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1100, 900)); await Task.Delay(60);
        var reopened = Dialog(loaded); shown = reopened.ShowAsync(); await ShowChat(reopened);
        Check(Toggles(reopened).Select(t => t.IsOn).SequenceEqual([false, false, true, true, true, true]), context + ": reopen after persistence restores every toggle");
        reopened.Hide(); await shown; await Task.Delay(120);
    }
}
