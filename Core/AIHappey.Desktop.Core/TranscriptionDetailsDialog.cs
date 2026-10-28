using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AIHappey.Desktop.Core;

public enum TranscriptionDetailAction { None, ExportText, ExportMedia }
public sealed class TranscriptionDetailsDialog : ContentDialog, IResponsiveDialog
{
    private readonly Grid layout = new() { RowSpacing = 12 };
    private readonly MediaDetailsView details = new("TranscriptionDetails");
    public TranscriptionDetailAction Action { get; private set; }
    public TranscriptionDetailsDialog(LibraryTranscription item)
    {
        Name = "TranscriptionDetailsDialog"; Title = item.Item.Filename; CloseButtonText = DesktopResources.Get("Close");
        Resources["ContentDialogMaxWidth"] = 1100d; Resources["ContentDialogMinWidth"] = 0d;
        layout.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) }); layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        layout.Children.Add(details);
        var result = item.Item.Response;
        details.AddTab("Text", MediaDetailsView.Text(OpenAIChatConfig.Text(result["text"]) ?? ""));
        if (result["segments"] is JsonArray { Count: > 0 } segments)
        {
            var panel = new StackPanel { Spacing = 12 };
            foreach (var segment in segments.OfType<JsonObject>())
            {
                var row = new StackPanel { Spacing = 4 }; row.Children.Add(new TextBlock { Text = $"{segment["startSecond"]}s – {segment["endSecond"]}s", FontSize = 12 }); row.Children.Add(MediaDetailsView.Text(OpenAIChatConfig.Text(segment["text"]) ?? "")); panel.Children.Add(row);
            }
            details.AddTab("TranscriptionSegments", panel);
        }
        var input = new JsonObject { ["model"] = item.Item.Model, ["mediaType"] = item.Item.MediaType, ["providerOptions"] = item.Item.Options.DeepClone() };
        details.AddTab("TranscriptionProviderInput", MediaDetailsView.Json(MediaDetailsView.ProviderInput(result, input), "TranscriptionProviderInput"));
        details.AddTab("TranscriptionProviderResult", MediaDetailsView.Json(MediaDetailsView.ProviderResult(result), "TranscriptionProviderResult"));
        var actions = new MessageFooterPanel();
        foreach (var (action, key) in new[] { (TranscriptionDetailAction.ExportText, "TranscriptionExportText"), (TranscriptionDetailAction.ExportMedia, "TranscriptionExportMedia") })
        {
            if (action == TranscriptionDetailAction.ExportMedia && item.Item.Kind == "realtime") continue;
            var button = new Button { Name = "Transcription" + action, Content = DesktopResources.Get(key) }; ControlAppearance.Stock(button); button.Click += (_, _) => { Action = action; Hide(); }; actions.Children.Add(button);
        }
        Grid.SetRow(actions, 1); layout.Children.Add(actions); Content = layout;
        Opened += (_, _) => { SizeToRoot(); XamlRoot.Changed += RootChanged; }; Closed += (_, _) => XamlRoot.Changed -= RootChanged;
    }
    private void RootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => SizeToRoot();
    void IResponsiveDialog.SizeToRoot() => SizeToRoot();
    private void SizeToRoot() { layout.Width = Math.Max(0, Math.Min(1000, XamlRoot.Size.Width - 96)); layout.Height = Math.Max(0, Math.Min(720, XamlRoot.Size.Height - 180)); }
}
