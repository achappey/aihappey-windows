using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AIHappey.Desktop.Core;

/// <summary>Native top navigation for fixed review pages, rather than draggable document tabs.</summary>
internal sealed class ToolReviewTabs : UserControl
{
    private readonly NavigationView navigation = new()
    {
        PaneDisplayMode = NavigationViewPaneDisplayMode.Top, IsSettingsVisible = false,
        IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed, IsPaneToggleButtonVisible = false,
        AlwaysShowHeader = false, IsTabStop = false
    };
    public int Count => navigation.MenuItems.Count;
    public int SelectedIndex
    {
        get => navigation.MenuItems.IndexOf(navigation.SelectedItem);
        set => navigation.SelectedItem = navigation.MenuItems[value];
    }

    public ToolReviewTabs(string name)
    {
        Name = name; IsTabStop = false; Content = navigation;
        navigation.Name = name + "Navigation";
        navigation.SelectionChanged += (_, args) =>
        {
            if (args.SelectedItem is NavigationViewItem { Tag: UIElement view }) navigation.Content = view;
        };
        ControlAppearance.Native(navigation);
    }

    public void Add(string title, UIElement content, string name)
    {
        // Nested review navigation gets its own bounded viewport, not a second outer ScrollViewer.
        var view = content is ToolReviewTabs ? content : new ScrollViewer { Content = content,
            Padding = new Thickness(4, 12, 4, 8), HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalContentAlignment = HorizontalAlignment.Stretch };
        var item = new NavigationViewItem { Name = name, Content = title, Tag = view };
        ToolbarControls.Label(item, title); ControlAppearance.Native(item);
        navigation.MenuItems.Add(item);
    }
}
