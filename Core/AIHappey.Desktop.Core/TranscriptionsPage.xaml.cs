using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace AIHappey.Desktop.Core;

public sealed partial class TranscriptionsPage : UserControl
{
    private readonly ObservableCollection<FrameworkElement> cards = [];
    private bool editable = true, hasModel;
    public Func<Task>? PickRequested { get; set; }
    public Func<Task>? SettingsRequested { get; set; }
    public Action? StopRequested { get; set; }
    public Func<Task>? FolderRequested { get; set; }
    public Func<DataPackageView, Task>? FilesDropped { get; set; }
    public Func<LibraryTranscription, Task>? ViewRequested { get; set; }
    public Func<LibraryTranscription, Task>? DeleteRequested { get; set; }
    public TranscriptionsPage()
    {
        InitializeComponent(); Name = "TranscriptionsPage";
        Title.Text = DesktopResources.Get("Transcriptions"); DropHint.Text = DesktopResources.Get("TranscriptionDropHint"); FileHint.Text = DesktopResources.Get("TranscriptionFileHint");
        LibraryTitle.Text = DesktopResources.Get("MyTranscriptions"); NoModels.Text = DesktopResources.Get("TranscriptionNoModels"); EmptyLibrary.Text = DesktopResources.Get("TranscriptionEmptyLibrary");
        Stop.Content = DesktopResources.Get("Stop"); OpenFolder.Content = DesktopResources.Get("TranscriptionOpenFolder");
        AutomationProperties.SetHeadingLevel(Title, Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level1);
        AutomationProperties.SetHeadingLevel(LibraryTitle, Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level2);
        ToolbarControls.Label(AddFiles, DesktopResources.Get("TranscriptionAddFiles")); ToolbarControls.Label(Settings, DesktopResources.Get("TranscriptionSettings"));
        foreach (var control in new Control[] { AddFiles, Settings, Stop, OpenFolder }) ControlAppearance.Stock(control);
        AddFiles.Click += async (_, _) => { if (editable && hasModel && PickRequested is not null) await PickRequested(); };
        Settings.Click += async (_, _) => { if (editable && SettingsRequested is not null) await SettingsRequested(); };
        Stop.Click += (_, _) => StopRequested?.Invoke(); OpenFolder.Click += async (_, _) => { if (editable && FolderRequested is not null) await FolderRequested(); };
        LibraryGrid.ItemsSource = cards;
        Root.AddHandler(UIElement.DragEnterEvent, new DragEventHandler(DragOverFiles), true);
        Root.AddHandler(UIElement.DragOverEvent, new DragEventHandler(DragOverFiles), true);
        Root.AddHandler(UIElement.DropEvent, new DragEventHandler(DropFiles), true);
        Root.DragLeave += (_, args) => { var point = args.GetPosition(Root); if (point.X <= 0 || point.Y <= 0 || point.X >= Root.ActualWidth || point.Y >= Root.ActualHeight) DropOutline.Visibility = Visibility.Collapsed; };
        SizeChanged += (_, _) => ResizeGrid(); Loaded += (_, _) => ResizeGrid(); SetModelAvailable(false);
    }
    private void ResizeGrid()
    {
        var width = Math.Max(0, ActualWidth - 48); Header.Width = width; InputPanel.Width = Math.Min(1056, width);
        if (LibraryGrid.ItemsPanelRoot is ItemsWrapGrid panel)
        {
            var columns = width >= 720 ? 2 : 1; panel.MaximumRowsOrColumns = columns; panel.ItemWidth = Math.Max(48, width / columns); panel.ItemHeight = 260;
        }
        foreach (var card in cards) { card.Width = Math.Max(40, width / (width >= 720 ? 2 : 1) - 12); card.Height = 248; }
    }
    public void SetModelAvailable(bool value) { hasModel = value; NoModels.Visibility = value ? Visibility.Collapsed : Visibility.Visible; AddFiles.IsEnabled = editable && value; }
    public void SetBusy(bool value, bool inference)
    {
        editable = !value; AddFiles.IsEnabled = !value && hasModel; Settings.IsEnabled = OpenFolder.IsEnabled = !value;
        Stop.Visibility = value && inference ? Visibility.Visible : Visibility.Collapsed; DropOutline.Visibility = Visibility.Collapsed;
        foreach (var card in cards) card.IsHitTestVisible = !value;
    }
    public void SetItems(IReadOnlyList<LibraryTranscription> items, int pending = 0)
    {
        cards.Clear();
        for (var i = 0; i < pending; i++)
        {
            var panel = new StackPanel { Spacing = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16) };
            panel.Children.Add(new ProgressRing { IsActive = true, Width = 28, Height = 28 }); panel.Children.Add(new TextBlock { Text = DesktopResources.Get("TranscriptionProcessing"), HorizontalAlignment = HorizontalAlignment.Center });
            cards.Add(Card(panel));
        }
        foreach (var item in items) cards.Add(ResultCard(item));
        EmptyLibrary.Visibility = cards.Count == 0 ? Visibility.Visible : Visibility.Collapsed; ResizeGrid();
    }
    private static Border Card(UIElement content)
    {
        var card = new Border { Child = content, Name = "TranscriptionCard", Margin = new Thickness(6), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1) };
        ControlAppearance.Apply(card, (_, _) => { }, palette => { card.Background = new SolidColorBrush(palette.Panel); card.BorderBrush = new SolidColorBrush(palette.Stroke); }); return card;
    }
    private FrameworkElement ResultCard(LibraryTranscription item)
    {
        var data = item.Item; var response = data.Response;
        var layout = new Grid(); layout.RowDefinitions.Add(new() { Height = GridLength.Auto }); layout.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) }); layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var header = new Grid { Margin = new Thickness(16, 16, 16, 0), ColumnSpacing = 12 };
        header.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); header.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var icon = new Grid { Width = 32, Height = 32, VerticalAlignment = VerticalAlignment.Top };
        icon.Children.Add(new FontIcon { Glyph = "\uE720", FontSize = 24 }); header.Children.Add(icon);
        var labels = new StackPanel { Spacing = 6 };
        var title = new TextBlock { Text = data.Filename, FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis }; ToolTipService.SetToolTip(title, data.Filename); labels.Children.Add(title);
        Grid.SetColumn(labels, 1); header.Children.Add(labels);
        var more = new Button { Content = new FontIcon { Glyph = "\uE712", FontSize = 18 }, Width = 36, Height = 36, Padding = new Thickness(0), Name = "TranscriptionActions", VerticalAlignment = VerticalAlignment.Top }; ToolbarControls.Subtle(more); ToolbarControls.Label(more, DesktopResources.Format("ActionForItem", DesktopResources.Get("TranscriptionActions"), data.Filename));
        var menu = new MenuFlyout(); var delete = new MenuFlyoutItem { Text = DesktopResources.Get("Delete"), Icon = new SymbolIcon(Symbol.Delete) }; ControlAppearance.Stock(delete);
        delete.Click += async (_, _) => { if (editable && DeleteRequested is not null) await DeleteRequested(item); }; menu.Items.Add(delete); more.Flyout = menu; Grid.SetColumn(more, 2); header.Children.Add(more); layout.Children.Add(header);
        var badges = new MessageFooterPanel();
        void Badge(string text) { if (string.IsNullOrWhiteSpace(text)) return; var badge = new Border { Padding = new Thickness(10, 4, 10, 4), CornerRadius = new CornerRadius(16), HorizontalAlignment = HorizontalAlignment.Left, Child = new TextBlock { Text = text, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 320 } }; ControlAppearance.TokenBadge(badge); ToolTipService.SetToolTip(badge, text); badges.Children.Add(badge); }
        Badge(OpenAIChatConfig.Text(response["response"]?["modelId"]) ?? data.Model);
        Badge(OpenAIChatConfig.Text(response["language"]) ?? "");
        if (response["providerMetadata"] is JsonObject metadata)
            foreach (var provider in metadata.Where(p => p.Key != "gateway"))
                if (provider.Value is JsonObject options && options["languages"] is JsonArray languages)
                    foreach (var language in languages.OfType<JsonObject>()) Badge(OpenAIChatConfig.Text(language["code"]) ?? "");
        if (Number(response["durationInSeconds"]) is { } duration && duration >= 0) Badge(DesktopResources.Format("TranscriptionSeconds", duration.ToString("0.#", CultureInfo.CurrentCulture)));
        if (Number(response["providerMetadata"]?["gateway"]?["cost"]) is { } cost) Badge(cost.ToString("C4", CultureInfo.GetCultureInfo("en-US")));
        labels.Children.Add(badges);
        var preview = new TextBlock { Text = OpenAIChatConfig.Text(response["text"]) ?? "", TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 3, IsTextSelectionEnabled = true, FontSize = 13, Margin = new Thickness(16, 18, 16, 16), MinHeight = 36 };
        ControlAppearance.Apply(preview, (_, _) => { }, palette => preview.Foreground = new SolidColorBrush(palette.Disabled)); Grid.SetRow(preview, 1); layout.Children.Add(preview);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        var view = new Button { Name = "TranscriptionView", Content = new FontIcon { Glyph = "\uE890", FontSize = 18 }, Width = 36, Height = 36, Padding = new Thickness(0) };
        ToolbarControls.Subtle(view); ToolbarControls.Label(view, DesktopResources.Format("ActionForItem", DesktopResources.Get("TranscriptionView"), data.Filename)); AutomationProperties.SetAutomationId(view, data.Id + ":Details");
        view.Click += async (_, _) => { if (editable && ViewRequested is not null) await ViewRequested(item); }; actions.Children.Add(view);
        var footer = new Border { Child = actions, Padding = new Thickness(12, 8, 12, 8), BorderThickness = new Thickness(0, 1, 0, 0) };
        ControlAppearance.Separator(footer); Grid.SetRow(footer, 2); layout.Children.Add(footer);
        var result = Card(layout); AutomationProperties.SetName(result, data.Filename + " · " + data.Model); return result;
    }
    private static double? Number(JsonNode? node) => node is JsonValue value && value.TryGetValue<double>(out var result) && double.IsFinite(result) ? result : null;
    public void ClearNotices() => Notices.Children.Clear();
    public void Notice(string message, InfoBarSeverity severity)
    {
        var bar = new InfoBar { IsOpen = true, IsClosable = true, Message = message, Severity = severity }; bar.Closed += (_, _) => Notices.Children.Remove(bar); Notices.Children.Add(bar);
    }
    private void DragOverFiles(object sender, DragEventArgs args)
    {
        args.Handled = true; var accepts = editable && hasModel && args.DataView.Contains(StandardDataFormats.StorageItems) && args.AllowedOperations.HasFlag(DataPackageOperation.Copy);
        args.AcceptedOperation = accepts ? DataPackageOperation.Copy : DataPackageOperation.None; args.DragUIOverride.Caption = accepts ? DesktopResources.Get("TranscriptionAddFiles") : ""; DropOutline.Visibility = accepts ? Visibility.Visible : Visibility.Collapsed;
    }
    private async void DropFiles(object sender, DragEventArgs args)
    {
        args.Handled = true; DropOutline.Visibility = Visibility.Collapsed;
        if (!editable || !hasModel || !args.DataView.Contains(StandardDataFormats.StorageItems) || !args.AllowedOperations.HasFlag(DataPackageOperation.Copy)) { args.AcceptedOperation = DataPackageOperation.None; return; }
        var deferral = args.GetDeferral();
        try { args.AcceptedOperation = DataPackageOperation.Copy; if (FilesDropped is not null) await FilesDropped(args.DataView); }
        finally { deferral.Complete(); }
    }
}
