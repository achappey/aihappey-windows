using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AIHappey.Desktop.Core;

/// <summary>Native plugin toggles edit the isolated chat-settings draft only.</summary>
public sealed class LocalToolsSettingsView : StackPanel
{
    public LocalToolsSettingsView(ChatPreferences draft)
    {
        Name = "ChatToolsView"; Spacing = 16;
        Children.Add(new TextBlock { Text = DesktopResources.Get("BuiltInLocalTools"), FontSize = 20,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        foreach (var plugin in DesktopLocalTools.Plugins)
        {
            var title = DesktopResources.Get(plugin.ResourceKey);
            var toggle = new ToggleSwitch { Name = "ChatPluginToggle", Tag = plugin.Id, IsOn = draft.PluginEnabled(plugin.Id),
                OnContent = "", OffContent = "", MinWidth = 0, HorizontalAlignment = HorizontalAlignment.Right };
            ControlAppearance.Stock(toggle); ToolbarControls.Label(toggle, title);
            AutomationProperties.SetAutomationId(toggle, "ChatPlugin_" + plugin.Id);
            toggle.Toggled += (_, _) =>
            {
                draft.ActivePlugins.RemoveAll(id => id == plugin.Id);
                if (toggle.IsOn) draft.ActivePlugins.Add(plugin.Id);
            };
            NativeSettingsSurface.ToggleCard(this, "ChatPluginCard", title, toggle);
        }
    }
}
