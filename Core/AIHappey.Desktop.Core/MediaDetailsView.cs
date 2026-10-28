using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AIHappey.Desktop.Core;

/// <summary>The same native top navigation and bounded scrolling for both saved-audio dialogs.</summary>
internal sealed class MediaDetailsView : Grid
{
    private readonly NavigationView tabs = new()
    {
        PaneDisplayMode = NavigationViewPaneDisplayMode.Top, IsSettingsVisible = false,
        IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed, IsPaneToggleButtonVisible = false,
        AlwaysShowHeader = false, Height = 56
    };
    private readonly ScrollViewer page = new()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        HorizontalScrollMode = ScrollMode.Disabled, HorizontalContentAlignment = HorizontalAlignment.Stretch,
        Padding = new Thickness(0, 4, 4, 8)
    };

    internal MediaDetailsView(string name)
    {
        Name = name; tabs.Name = name + "Tabs"; page.Name = name + "Page";
        RowDefinitions.Add(new() { Height = GridLength.Auto });
        RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        Children.Add(tabs); SetRow(page, 1); Children.Add(page);
        tabs.SelectionChanged += (_, args) =>
        {
            if (args.SelectedItem is not NavigationViewItem { Tag: FrameworkElement content }) return;
            page.Content = content; page.ChangeView(null, 0, null, true);
        };
    }

    internal void AddTab(string key, FrameworkElement content)
    {
        var label = DesktopResources.Get(key);
        var tab = new NavigationViewItem { Name = key + "Tab", Content = label, Tag = content };
        ToolbarControls.Label(tab, label); AutomationProperties.SetAutomationId(tab, tab.Name);
        tabs.MenuItems.Add(tab);
        if (tabs.MenuItems.Count == 1) { tabs.SelectedItem = tab; page.Content = content; }
    }

    internal static TextBlock Text(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };

    internal static FrameworkElement Json(JsonNode node, string name)
    {
        var view = new StackPanel { Name = name, Spacing = 12, HorizontalAlignment = HorizontalAlignment.Stretch };
        // Use data-only native expanders, not an editable TextBox or a browser control.
        view.Children.Add(SystemContextDialog.JsonTree(JsonSerializer.SerializeToElement(node), null, 0));
        AutomationProperties.SetName(view, DesktopResources.Get(name));
        return view;
    }

    internal static JsonNode ProviderInput(JsonObject response, JsonObject fallback) =>
        response["request"] is JsonObject request && request["body"] is { } body ? ParseBody(body) : fallback.DeepClone();

    internal static JsonNode ParseBody(JsonNode node)
    {
        if (node is JsonValue value && value.TryGetValue<string>(out var text))
            try { return JsonNode.Parse(text) ?? node.DeepClone(); } catch (JsonException) { }
        return node.DeepClone();
    }

    internal static JsonObject ProviderResult(JsonObject result)
    {
        var output = new JsonObject();
        foreach (var key in new[] { "providerMetadata", "response", "warnings" })
            if (result[key] is { } value) output[key] = value.DeepClone();
        if (output["response"] is JsonObject response && response["body"] is { } body) response["body"] = ParseBody(body);
        return output;
    }
}
