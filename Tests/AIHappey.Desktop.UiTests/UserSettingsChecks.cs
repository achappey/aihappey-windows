using AIHappey.Desktop.Core;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AIHappey.Desktop.UiTests;

public partial class App
{
    private async Task CheckUserSettingsAppearanceAsync(ElementTheme theme)
    {
        var context = "User settings appearance / " + theme;
        var root = new Grid { RequestedTheme = theme };
        window!.Content = root; window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1200, 900)); window.Activate();
        await Task.Delay(100);
        var original = new DesktopSettings();
        foreach (var role in Enum.GetValues<AppAgentRole>()) original.SideInferenceAgentNames.Set(role, "");
        original.SideInferenceAgentNames.ToolSearchAgent = "missing-agent";
        var dialog = new SettingsDialog(original, true, "en", aiModels: [], appAgents: [])
            { XamlRoot = root.XamlRoot, RequestedTheme = theme };
        SystemAppearance.PrepareDialog(dialog);
        var shown = dialog.ShowAsync(); await Task.Delay(120);
        var tabs = (NavigationView)dialog.FindName("Tabs");
        Check(tabs.MenuItems.OfType<NavigationViewItem>().Select(item => (string)item.Tag)
                .SequenceEqual(["general", "artificialIntelligence", "modelContext", "chat", "agents", "endpoints", "attachments"])
            && ReferenceEquals(tabs.SelectedItem, dialog.FindName("GeneralTab")),
            context + ": existing vertical tabs match browser order and initially select General");
        foreach (var (tab, page) in new[]
        {
            ("GeneralTab", "GeneralPage"), ("ArtificialIntelligenceTab", "ArtificialIntelligencePage"),
            ("ModelContextTab", "ModelContextPage"), ("ChatTab", "ChatPage"),
            ("AppAgentsTab", "AppAgentsPage"), ("EndpointsTab", "EndpointsPage"), ("AttachmentsTab", "AttachmentsPage")
        })
        {
            tabs.SelectedItem = dialog.FindName(tab); dialog.UpdateLayout();
            Check(((FrameworkElement)dialog.FindName(page)).Visibility == Visibility.Visible,
                context + ": reordered " + tab + " still opens its own page");
        }
        tabs.SelectedItem = dialog.FindName("AppAgentsTab"); dialog.UpdateLayout();
        var agentCards = dialog.AppAgentsView.Children.OfType<SettingsCard>().ToArray();
        Check(agentCards.Length == 5 && agentCards.All(card => card.Content is StackPanel body
                && body.Children.Count == 2 && body.Children[0] is ComboBox && body.Children[1] is TextBlock),
            context + ": all five agent cards retain dropdowns and missing-agent status only, without descriptions or not-active notices");
        foreach (var card in agentCards)
        {
            var body = (StackPanel)card.Content;
            var picker = (ComboBox)body.Children[0];
            var warning = (TextBlock)body.Children[1];
            Check(AutomationProperties.GetName(picker).Length > 0 && picker.IsEnabled
                && warning.Visibility == (picker.Name == "AppAgent_ToolSearch" ? Visibility.Visible : Visibility.Collapsed),
                context + ": " + picker.Name + " stays accessible with genuine missing-agent status");
            picker.SelectedItem = ((IEnumerable<AppAgentsSettingsView.AgentChoice>)picker.ItemsSource).Single(choice => choice.Name == "");
            Check(warning.Visibility == Visibility.Collapsed, context + ": selecting None clears missing-agent status for " + picker.Name);
        }
        tabs.SelectedItem = dialog.FindName("AttachmentsTab"); dialog.UpdateLayout();
        var attachments = (StackPanel)dialog.FindName("AttachmentsPage");
        var extraction = (ToggleSwitch)dialog.FindName("DocumentTextExtraction");
        Check(attachments.Children.Count == 1 && ReferenceEquals(attachments.Children[0], extraction) && extraction.IsEnabled,
            context + ": document extraction retains only its toggle, without a description");
        extraction.IsOn = !original.ConvertAttachmentsToText;
        tabs.SelectedItem = dialog.FindName("ModelContextTab"); dialog.UpdateLayout();
        var modelTabs = (NavigationView)dialog.FindName("ModelContextPage");
        var url = (ToggleSwitch)dialog.FindName("UrlElicitation");
        Check(!url.IsEnabled && !url.IsOn && ((StackPanel)url.Parent).Children.Count == 3,
            context + ": URL elicitation stays disabled/off with its label and no unavailable description");
        modelTabs.SelectedItem = dialog.FindName("McpExtensionsTab"); dialog.UpdateLayout();
        var extensions = (StackPanel)((Border)dialog.FindName("McpExtensionsPage")).Child;
        var apps = (ToggleSwitch)dialog.FindName("EnableMcpApps");
        var skills = (ToggleSwitch)dialog.FindName("EnableMcpSkills");
        Check(extensions.Children.Count == 3 && apps.IsEnabled && skills.IsEnabled,
            context + ": extensions retain their heading and both toggles without warning text");
        apps.IsOn = !original.ModelContext.EnableApps; skills.IsOn = !original.ModelContext.EnableSkills;
        InvokeButton(Descendants(dialog).OfType<Button>().Single(button => button.Name == "PrimaryButton")); await shown;
        Check(dialog.Result is { } saved && saved.ConvertAttachmentsToText == extraction.IsOn
            && saved.ModelContext.EnableApps == apps.IsOn && saved.ModelContext.EnableSkills == skills.IsOn
            && saved.SideInferenceAgentNames.ToolSearchAgent == "" && original.SideInferenceAgentNames.ToolSearchAgent == "missing-agent"
            && saved.ConvertAttachmentsToText != original.ConvertAttachmentsToText
            && saved.ModelContext.EnableApps != original.ModelContext.EnableApps
            && saved.ModelContext.EnableSkills != original.ModelContext.EnableSkills,
            context + ": simplified controls save correctly and leave the original settings isolated");
    }
}
