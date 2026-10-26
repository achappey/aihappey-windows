using FluentIcons.Common;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AIHappey.Desktop.Core;

internal sealed record OverviewFilterOption(string Value, string Label, int Count);

/// <summary>A compact native multi-select. The caller owns selection; refreshes keep checkbox focus alive.</summary>
internal sealed class OverviewFilterDropdown : UserControl
{
    private readonly Button button = new() { Height = 40, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock caption = new() { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel choices = new() { Spacing = 4, MinWidth = 200, MaxWidth = 300 };
    private readonly CheckBox all = new();
    private readonly Dictionary<string, CheckBox> boxes = new(StringComparer.Ordinal);
    private Action<string?>? select;

    public OverviewFilterDropdown(string name)
    {
        Name = name;
        var content = new Grid { ColumnSpacing = 8 };
        content.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        content.Children.Add(caption);
        var chevron = DesktopIcons.Create(Icon.ChevronDown, 12); Grid.SetColumn(chevron, 1); content.Children.Add(chevron);
        button.Content = content;
        button.Flyout = new Flyout { Content = new ScrollViewer { Content = choices, MaxHeight = 400, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled } };
        Content = button; ControlAppearance.Native(button); ControlAppearance.Native(all);
        AutomationProperties.SetAutomationId(button, name);
        AutomationProperties.SetAutomationId(all, name + "_all");
        all.Click += (_, _) => select?.Invoke(null); choices.Children.Add(all);
    }

    public void Update(string label, IReadOnlyList<OverviewFilterOption> options, IReadOnlySet<string> selected, int allCount, Action<string?> onSelect)
    {
        select = onSelect;
        var summary = selected.Count == 0 ? DesktopResources.Get("All")
            : string.Join(", ", options.Where(option => selected.Contains(option.Value)).Select(option => option.Label));
        caption.Text = $"{label}: {summary}";
        ToolbarControls.Label(button, caption.Text); ToolTipService.SetToolTip(button, caption.Text);
        all.Content = $"{DesktopResources.Get("All")} ({allCount})"; all.IsChecked = selected.Count == 0;
        foreach (var stale in boxes.Keys.Except(options.Select(option => option.Value)).ToArray())
        { choices.Children.Remove(boxes[stale]); boxes.Remove(stale); }
        for (var index = 0; index < options.Count; index++)
        {
            var option = options[index];
            if (!boxes.TryGetValue(option.Value, out var box))
            {
                var value = option.Value;
                box = new CheckBox(); ControlAppearance.Native(box);
                AutomationProperties.SetAutomationId(box, Name + "_" + value);
                box.Click += (_, _) => select?.Invoke(value); boxes.Add(value, box);
                choices.Children.Insert(index + 1, box);
            }
            var text = $"{option.Label} ({option.Count})";
            box.Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
            AutomationProperties.SetName(box, text);
            box.IsChecked = selected.Contains(option.Value);
            box.IsEnabled = option.Count > 0 || box.IsChecked == true;
        }
    }
}

/// <summary>Search and two facets share one row when possible, two or three rows on narrow windows.</summary>
internal sealed class OverviewSearchFiltersRow : Grid
{
    public OverviewSearchFiltersRow(Control search, Control first, Control second)
    {
        ColumnSpacing = 12; RowSpacing = 8;
        for (var index = 0; index < 3; index++)
        { ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); RowDefinitions.Add(new() { Height = GridLength.Auto }); }
        Children.Add(search); Children.Add(first); Children.Add(second);
        search.MaxWidth = double.PositiveInfinity;
        void ArrangeControls()
        {
            var narrow = ActualWidth < 420; var compact = ActualWidth < 680;
            ColumnDefinitions[0].Width = new GridLength(compact ? 1 : 1.6, GridUnitType.Star);
            ColumnDefinitions[1].Width = new GridLength(narrow ? 0 : 1, GridUnitType.Star);
            ColumnDefinitions[2].Width = new GridLength(compact ? 0 : 1, GridUnitType.Star);
            SetColumnSpan(search, compact && !narrow ? 2 : 1);
            SetRow(first, compact ? 1 : 0); SetColumn(first, compact ? 0 : 1);
            SetRow(second, narrow ? 2 : compact ? 1 : 0); SetColumn(second, narrow ? 0 : compact ? 1 : 2);
        }
        SizeChanged += (_, _) => ArrangeControls(); ArrangeControls();
    }
}
