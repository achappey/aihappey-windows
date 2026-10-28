using System.Globalization;
using FluentIcons.Common;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using Windows.UI.Core;

namespace AIHappey.Desktop.Core;

public sealed partial class SpeechPage : UserControl
{
    private bool editable = true, hasModel, recording;
    public Func<Task>? SendRequested { get; set; }
    public Func<Task>? ImportRequested { get; set; }
    public Func<Task>? SettingsRequested { get; set; }
    public Func<Task>? FolderRequested { get; set; }
    public Func<Task>? DictationRequested { get; set; }
    public Action? StopRequested { get; set; }
    public Func<DataPackageView, Task>? FilesDropped { get; set; }
    public Func<LibrarySpeech, Task>? DetailsRequested { get; set; }
    public Func<LibrarySpeech, Task>? DownloadRequested { get; set; }
    public Func<LibrarySpeech, Task>? DeleteRequested { get; set; }
    public string PromptText => Prompt.Text.Trim();
    public bool StartDictation() => WindowsVoiceTyping.TryStart(Prompt);
    internal TextBox DictationInput => Prompt;
    internal Button DictationButton => Dictate;
    internal void SetDictating(bool value) { recording = value; Prompt.IsReadOnly = !editable || value; UpdateSend(); }
    public SpeechPage()
    {
        InitializeComponent(); Name = "SpeechPage"; Title.Text = DesktopResources.Get("Speech"); LibraryTitle.Text = DesktopResources.Get("MySpeechFiles");
        Prompt.PlaceholderText = DesktopResources.Get("SpeechPromptPlaceholder"); NoModels.Text = DesktopResources.Get("SpeechNoModels"); EmptyLibrary.Text = DesktopResources.Get("SpeechEmptyLibrary");
        Stop.Content = DesktopResources.Get("Stop"); OpenFolder.Content = DesktopResources.Get("SpeechOpenFolder");
        AutomationProperties.SetHeadingLevel(Title, Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level1); AutomationProperties.SetHeadingLevel(LibraryTitle, Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level2);
        ToolbarControls.Label(Prompt, DesktopResources.Get("SpeechPromptPlaceholder")); ToolbarControls.Label(Settings, DesktopResources.Get("SpeechSettings")); ToolbarControls.Label(Import, DesktopResources.Get("SpeechImport"));
        ToolbarControls.Label(Dictate, DesktopResources.Get("SpeechDictate")); ToolbarControls.Label(Send, DesktopResources.Get("SpeechGenerate"));
        ControlAppearance.Stock(Prompt); foreach (var control in new Control[] { Send, Stop, OpenFolder }) ControlAppearance.Stock(control); ToolbarControls.Subtle(Settings); ToolbarControls.Subtle(Import); ToolbarControls.Subtle(Dictate);
        Prompt.TextChanged += (_, _) => UpdateSend(); Prompt.KeyDown += async (_, args) => { if (args.Key != VirtualKey.Enter || Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down)) return; args.Handled = true; if (Send.IsEnabled && SendRequested is not null) await SendRequested(); };
        Send.Click += async (_, _) => { if (SendRequested is not null) await SendRequested(); }; Import.Click += async (_, _) => { if (ImportRequested is not null) await ImportRequested(); };
        Settings.Click += async (_, _) => { if (SettingsRequested is not null) await SettingsRequested(); }; OpenFolder.Click += async (_, _) => { if (FolderRequested is not null) await FolderRequested(); };
        Dictate.Click += async (_, _) => { if (DictationRequested is not null) await DictationRequested(); }; Stop.Click += (_, _) => StopRequested?.Invoke();
        Root.AddHandler(UIElement.DragEnterEvent, new DragEventHandler(DragOverFiles), true); Root.AddHandler(UIElement.DragOverEvent, new DragEventHandler(DragOverFiles), true); Root.AddHandler(UIElement.DropEvent, new DragEventHandler(DropFiles), true);
        SizeChanged += (_, _) => ResizeGrid(); Loaded += (_, _) => ResizeGrid(); SetModelAvailable(false);
    }
    private void ResizeGrid()
    {
        var width = Math.Max(0, ActualWidth - 48); Header.Width = width; PromptPanel.Width = Math.Min(1056, width);
        var columns = width >= 720 ? 2 : 1;
        if (LibraryGrid.ItemsPanelRoot is ItemsWrapGrid panel) { panel.MaximumRowsOrColumns = columns; panel.ItemWidth = Math.Max(48, width / columns); panel.ItemHeight = 260; }
        foreach (var card in LibraryGrid.Items.OfType<Border>()) { card.Width = Math.Max(40, width / columns - 12); card.Height = 248; }
    }
    private void DragOverFiles(object sender, DragEventArgs args) { args.Handled = true; var accepts = editable && args.DataView.Contains(StandardDataFormats.StorageItems) && args.AllowedOperations.HasFlag(DataPackageOperation.Copy); args.AcceptedOperation = accepts ? DataPackageOperation.Copy : DataPackageOperation.None; DropOutline.Visibility = accepts ? Visibility.Visible : Visibility.Collapsed; }
    private async void DropFiles(object sender, DragEventArgs args) { args.Handled = true; DropOutline.Visibility = Visibility.Collapsed; if (!editable || !args.DataView.Contains(StandardDataFormats.StorageItems)) return; var deferral = args.GetDeferral(); try { if (FilesDropped is not null) await FilesDropped(args.DataView); } finally { deferral.Complete(); } }
    public void SetItems(IReadOnlyList<LibrarySpeech> items, int pending = 0)
    {
        LibraryGrid.Items.Clear();
        for (var i = 0; i < pending; i++) LibraryGrid.Items.Add(Card(new StackPanel { Spacing = 12, Margin = new Thickness(16), VerticalAlignment = VerticalAlignment.Center, Children = { new ProgressRing { IsActive = true, Width = 28, Height = 28 }, new TextBlock { Text = DesktopResources.Get("SpeechGenerating"), HorizontalAlignment = HorizontalAlignment.Center } } }));
        foreach (var item in items) LibraryGrid.Items.Add(ResultCard(item)); EmptyLibrary.Visibility = pending == 0 && items.Count == 0 ? Visibility.Visible : Visibility.Collapsed; ResizeGrid();
    }
    private Border Card(UIElement content) { var card = new Border { Name = "SpeechCard", Child = content, Margin = new Thickness(6) }; NativeCardSurface.Card(card); return card; }
    private Border ResultCard(LibrarySpeech speech)
    {
        var data = speech.Item;
        var layout = new Grid(); layout.RowDefinitions.Add(new() { Height = GridLength.Auto }); layout.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) }); layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var header = new Grid { Margin = new Thickness(16, 16, 16, 0), ColumnSpacing = 12 };
        header.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); header.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var model = OpenAIChatConfig.Text(data.Response["response"]?["modelId"]) ?? data.Model;
        var modelLabel = new TextBlock { Name = "SpeechModel", FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis };
        header.Children.Add(MediaCardIdentity.Artwork(data.Response, model, data.Model, modelLabel, Icon.Speaker2));
        var labels = new StackPanel { Spacing = 6 }; labels.Children.Add(modelLabel);
        var timestamp = new TextBlock { Text = RelativeTime.Format(data.CreatedAt, DateTimeOffset.UtcNow, DesktopResources.Get), FontSize = 12 }; NativeCardSurface.Secondary(timestamp); labels.Children.Add(timestamp);
        var meta = data.Format.ToUpperInvariant(); if (speech.Cost is { } cost) meta += " · " + cost.ToString("C4", CultureInfo.GetCultureInfo("en-US"));
        var badge = new Border { HorizontalAlignment = HorizontalAlignment.Left, Child = new TextBlock { Text = meta, FontSize = 12 } }; NativeCardSurface.Badge(badge); labels.Children.Add(badge);
        Grid.SetColumn(labels, 1); header.Children.Add(labels);
        var more = new Button { Name = "SpeechActions", Content = DesktopIcons.Create(Icon.MoreHorizontal, 18), Width = 36, Height = 36, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Top };
        NativeCardSurface.Action(more); ToolbarControls.Label(more, DesktopResources.Format("ActionForItem", DesktopResources.Get("SpeechActions"), data.Model));
        var menu = new MenuFlyout(); var delete = new MenuFlyoutItem { Text = DesktopResources.Get("Delete"), Icon = DesktopIcons.Create(Icon.Delete) }; ControlAppearance.Stock(delete);
        delete.Click += async (_, _) => { if (editable && DeleteRequested is not null) await DeleteRequested(speech); }; menu.Items.Add(delete); more.Flyout = menu; Grid.SetColumn(more, 2); header.Children.Add(more); layout.Children.Add(header);
        var preview = new TextBlock { Text = data.Text, TextWrapping = TextWrapping.Wrap, MaxLines = 3, TextTrimming = TextTrimming.CharacterEllipsis, IsTextSelectionEnabled = true, FontSize = 13, Margin = new Thickness(16, 18, 16, 16), MinHeight = 36 };
        NativeCardSurface.Secondary(preview); Grid.SetRow(preview, 1); layout.Children.Add(preview);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        void Action(string name, Icon icon, string label, Func<LibrarySpeech, Task>? callback)
        {
            var button = new Button { Name = name, Content = DesktopIcons.Create(icon, 18), Width = 36, Height = 36, Padding = new Thickness(0) };
            NativeCardSurface.Action(button); ToolbarControls.Label(button, DesktopResources.Format("ActionForItem", label, data.Model)); AutomationProperties.SetAutomationId(button, data.Id + ":" + name);
            button.Click += async (_, _) => { if (editable && callback is not null) await callback(speech); }; actions.Children.Add(button);
        }
        Action("SpeechDetails", Icon.Info, DesktopResources.Get("Details"), DetailsRequested); Action("SpeechDownload", Icon.ArrowDownload, DesktopResources.Get("Download"), DownloadRequested);
        var footer = new Border { Name = "SpeechCardFooter", Child = actions, Padding = new Thickness(12, 8, 12, 8), BorderThickness = new Thickness(0, 1, 0, 0) };
        NativeCardSurface.Divider(footer); Grid.SetRow(footer, 2); layout.Children.Add(footer);
        var card = Card(layout); AutomationProperties.SetName(card, data.Model); return card;
    }
    public void SetModelAvailable(bool value) { hasModel = value; NoModels.Visibility = value ? Visibility.Collapsed : Visibility.Visible; UpdateSend(); }
    public void SetBusy(bool value, bool inference) { editable = !value; Prompt.IsReadOnly = value; Settings.IsEnabled = Import.IsEnabled = Dictate.IsEnabled = OpenFolder.IsEnabled = !value; Stop.Visibility = value && inference ? Visibility.Visible : Visibility.Collapsed; Send.Visibility = value && inference ? Visibility.Collapsed : Visibility.Visible; UpdateSend(); }
    public void SetRecording(bool value, string? elapsed = null) { recording = value; Dictate.Content = value ? (object)(elapsed ?? DesktopResources.Get("Stop")) : DesktopIcons.Create(Icon.Mic); UpdateSend(); }
    private void UpdateSend() => Send.IsEnabled = editable && !recording && hasModel && !string.IsNullOrWhiteSpace(Prompt.Text);
    public void AppendPrompt(string text) { if (string.IsNullOrWhiteSpace(text)) return; var separator = Prompt.Text.Length > 0 && !char.IsWhiteSpace(Prompt.Text[^1]) ? " " : ""; Prompt.Text += separator + text.Trim(); Prompt.SelectionStart = Prompt.Text.Length; Prompt.Focus(FocusState.Programmatic); }
    public void ReplacePrompt(string text) { Prompt.Text = text; Prompt.SelectionStart = Prompt.Text.Length; Prompt.Focus(FocusState.Programmatic); }
    public void ClearNotices() => Notices.Children.Clear();
    public void Notice(string message, InfoBarSeverity severity) { var bar = new InfoBar { IsOpen = true, IsClosable = true, Message = message, Severity = severity }; bar.Closed += (_, _) => Notices.Children.Remove(bar); Notices.Children.Add(bar); }
}
