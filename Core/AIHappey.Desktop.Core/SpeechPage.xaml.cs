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
    public Func<LibrarySpeech, Task>? PlayRequested { get; set; }
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
    private void ResizeGrid() { var width = Math.Max(0, ActualWidth - 48); Header.Width = width; PromptPanel.Width = Math.Min(1056, width); if (LibraryGrid.ItemsPanelRoot is ItemsWrapGrid panel) { var columns = width >= 720 ? 2 : 1; panel.MaximumRowsOrColumns = columns; panel.ItemWidth = Math.Max(280, width / columns); } }
    private void DragOverFiles(object sender, DragEventArgs args) { args.Handled = true; var accepts = editable && args.DataView.Contains(StandardDataFormats.StorageItems) && args.AllowedOperations.HasFlag(DataPackageOperation.Copy); args.AcceptedOperation = accepts ? DataPackageOperation.Copy : DataPackageOperation.None; DropOutline.Visibility = accepts ? Visibility.Visible : Visibility.Collapsed; }
    private async void DropFiles(object sender, DragEventArgs args) { args.Handled = true; DropOutline.Visibility = Visibility.Collapsed; if (!editable || !args.DataView.Contains(StandardDataFormats.StorageItems)) return; var deferral = args.GetDeferral(); try { if (FilesDropped is not null) await FilesDropped(args.DataView); } finally { deferral.Complete(); } }
    public void SetItems(IReadOnlyList<LibrarySpeech> items, int pending = 0)
    {
        LibraryGrid.Items.Clear();
        for (var i = 0; i < pending; i++) LibraryGrid.Items.Add(Card(new StackPanel { Children = { new ProgressRing { IsActive = true, Width = 28, Height = 28 }, new TextBlock { Text = DesktopResources.Get("SpeechGenerating") } } }));
        foreach (var item in items) LibraryGrid.Items.Add(ResultCard(item)); EmptyLibrary.Visibility = pending == 0 && items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private Border Card(UIElement content) { var card = new Border { Name = "SpeechCard", Child = content, Margin = new Thickness(6), Padding = new Thickness(16), MinHeight = 140 }; NativeCardSurface.Card(card); return card; }
    private Border ResultCard(LibrarySpeech speech)
    {
        var data = speech.Item; var panel = new StackPanel { Spacing = 10 }; var header = new Grid { ColumnSpacing = 10 }; header.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var labels = new StackPanel { Spacing = 3 }; labels.Children.Add(new TextBlock { Text = data.Model, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        labels.Children.Add(new TextBlock { Text = RelativeTime.Format(data.CreatedAt, DateTimeOffset.UtcNow, DesktopResources.Get), Opacity = .7 }); header.Children.Add(labels);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        void Action(string name, Icon icon, string label, Func<LibrarySpeech, Task>? callback) { var button = new Button { Name = name, Content = DesktopIcons.Create(icon, 18), Width = 36, Height = 36, Padding = new Thickness(0) }; NativeCardSurface.Action(button); ToolbarControls.Label(button, label); button.Click += async (_, _) => { if (editable && callback is not null) await callback(speech); }; actions.Children.Add(button); }
        Action("SpeechPlay", Icon.Play, DesktopResources.Get("SpeechPlay"), PlayRequested); Action("SpeechDetails", Icon.Info, DesktopResources.Get("Details"), DetailsRequested); Action("SpeechDownload", Icon.ArrowDownload, DesktopResources.Get("Download"), DownloadRequested); Action("SpeechDelete", Icon.Delete, DesktopResources.Get("Delete"), DeleteRequested);
        Grid.SetColumn(actions, 1); header.Children.Add(actions); panel.Children.Add(header); panel.Children.Add(new TextBlock { Text = data.Text, TextWrapping = TextWrapping.Wrap, MaxLines = 3, TextTrimming = TextTrimming.CharacterEllipsis });
        var meta = data.Format.ToUpperInvariant(); if (speech.Cost is { } cost) meta += " · " + cost.ToString("C4", CultureInfo.GetCultureInfo("en-US")); panel.Children.Add(new TextBlock { Text = meta, Opacity = .7 }); return Card(panel);
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
