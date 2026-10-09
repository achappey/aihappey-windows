using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace AIHappey.Desktop.Core;

public enum TranscriptionDetailAction { None, ExportText, ExportMedia }
public sealed class TranscriptionDetailsDialog : ContentDialog, IResponsiveDialog
{
    private readonly Grid layout = new() { RowSpacing = 12 };
    private readonly MessageFooterPanel tabs = new();
    private readonly ScrollViewer page = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    public TranscriptionDetailAction Action { get; private set; }
    public TranscriptionDetailsDialog(LibraryTranscription item)
    {
        Name = "TranscriptionDetailsDialog"; Title = item.Item.Filename; CloseButtonText = DesktopResources.Get("Close");
        Resources["ContentDialogMaxWidth"] = 1100d; Resources["ContentDialogMinWidth"] = 0d;
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto }); layout.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) }); layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        layout.Children.Add(tabs); Grid.SetRow(page, 1); layout.Children.Add(page);
        var result = item.Item.Response;
        AddTab("Text", Selectable(OpenAIChatConfig.Text(result["text"]) ?? ""));
        if (result["segments"] is JsonArray { Count: > 0 } segments)
        {
            var panel = new StackPanel { Spacing = 12 };
            foreach (var segment in segments.OfType<JsonObject>())
            {
                var row = new StackPanel { Spacing = 4 }; row.Children.Add(new TextBlock { Text = $"{segment["startSecond"]}s – {segment["endSecond"]}s", FontSize = 12 }); row.Children.Add(Selectable(OpenAIChatConfig.Text(segment["text"]) ?? "")); panel.Children.Add(row);
            }
            AddTab("TranscriptionSegments", panel);
        }
        var input = new JsonObject { ["model"] = item.Item.Model, ["mediaType"] = item.Item.MediaType, ["providerOptions"] = item.Item.Options.DeepClone() };
        if (result["request"]?["body"] is JsonValue request && request.TryGetValue<string>(out var raw))
        {
            try { input["providerRequest"] = JsonNode.Parse(raw); } catch (JsonException) { input["providerRequest"] = raw; }
        }
        AddTab("TranscriptionProviderInput", JsonView(input));
        var output = new JsonObject { ["providerMetadata"] = result["providerMetadata"]?.DeepClone(), ["response"] = result["response"]?.DeepClone(), ["warnings"] = result["warnings"]?.DeepClone() };
        // Raw provider JSON sometimes arrives stringified in existing Vercel responses.
        if (output["response"] is JsonObject response && response["body"] is JsonValue body && body.TryGetValue<string>(out var json))
            try { response["body"] = JsonNode.Parse(json); } catch (JsonException) { }
        AddTab("TranscriptionProviderResult", JsonView(output));
        var actions = new MessageFooterPanel();
        foreach (var (action, key) in new[] { (TranscriptionDetailAction.ExportText, "TranscriptionExportText"), (TranscriptionDetailAction.ExportMedia, "TranscriptionExportMedia") })
        {
            var button = new Button { Name = "Transcription" + action, Content = DesktopResources.Get(key) }; ControlAppearance.Stock(button); button.Click += (_, _) => { Action = action; Hide(); }; actions.Children.Add(button);
        }
        Grid.SetRow(actions, 2); layout.Children.Add(actions); Content = layout;
        Opened += (_, _) => { SizeToRoot(); XamlRoot.Changed += RootChanged; }; Closed += (_, _) => XamlRoot.Changed -= RootChanged;
    }
    private static TextBlock Selectable(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
    private static FrameworkElement JsonView(JsonNode node) => new TextBox { Text = node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas") };
    private void AddTab(string key, FrameworkElement content)
    {
        var tab = new ToggleButton { Content = DesktopResources.Get(key), IsChecked = tabs.Children.Count == 0 }; ControlAppearance.Stock(tab); tabs.Children.Add(tab);
        tab.Click += (_, _) => { foreach (var other in tabs.Children.OfType<ToggleButton>()) other.IsChecked = other == tab; page.Content = content; page.ChangeView(null, 0, null); };
        if (tabs.Children.Count == 1) page.Content = content;
    }
    private void RootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => SizeToRoot();
    void IResponsiveDialog.SizeToRoot() => SizeToRoot();
    private void SizeToRoot() { layout.Width = Math.Max(0, Math.Min(1000, XamlRoot.Size.Width - 96)); layout.Height = Math.Max(0, Math.Min(720, XamlRoot.Size.Height - 240)); }
}
