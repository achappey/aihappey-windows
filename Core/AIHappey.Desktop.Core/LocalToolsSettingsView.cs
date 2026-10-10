using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AIHappey.Desktop.Core;

/// <summary>Native plugin toggles edit the isolated chat-settings draft only.</summary>
public sealed class LocalToolsSettingsView : StackPanel
{
    private readonly Grid cards = new() { Name = "ChatPluginGrid", ColumnSpacing = 12, RowSpacing = 12 };
    private int columns;
    private bool arrangePending;

    public LocalToolsSettingsView(ChatPreferences draft)
    {
        Name = "ChatToolsView"; Spacing = 16;
        Children.Add(new TextBlock { Text = DesktopResources.Get("BuiltInLocalTools"), FontSize = 20,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        Children.Add(cards);
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
            var card = NativeSettingsSurface.ToggleCard(cards, "ChatPluginCard", title, toggle);
            card.HorizontalAlignment = HorizontalAlignment.Stretch;
        }
        ArrangeCards(1);
        Loaded += (_, _) => QueueArrange();
        SizeChanged += (_, args) => { if (args.NewSize.Width > 0) QueueArrange(); };
    }

    private void QueueArrange()
    {
        if (arrangePending) return;
        arrangePending = DispatcherQueue.TryEnqueue(() =>
        {
            arrangePending = false;
            // Reflow after the layout pass, using the viewport-constrained view width.
            // Changing grid definitions in the grid's own SizeChanged can cycle on reopen.
            if (IsLoaded && ActualWidth > 0) ArrangeCards(ActualWidth >= 600 ? 2 : 1);
        });
    }

    private void ArrangeCards(int count)
    {
        if (columns == count) return;
        columns = count;
        cards.ColumnDefinitions.Clear(); cards.RowDefinitions.Clear();
        for (var column = 0; column < count; column++)
            cards.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        for (var row = 0; row < (cards.Children.Count + count - 1) / count; row++)
            cards.RowDefinitions.Add(new() { Height = GridLength.Auto });
        for (var index = 0; index < cards.Children.Count; index++)
        {
            var card = (FrameworkElement)cards.Children[index];
            Grid.SetRow(card, index / count);
            Grid.SetColumn(card, index % count);
        }
    }
}
