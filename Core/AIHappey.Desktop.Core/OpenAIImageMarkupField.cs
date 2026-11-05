using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace AIHappey.Desktop.Core;

/// <summary>Mask-style input owned entirely by OpenAI provider options, not the Vercel image contract.</summary>
internal sealed class OpenAIImageMarkupField
{
    private readonly JsonObject options;
    private readonly StackPanel group = new() { Name = "ImageOpenAIMarkupField", Spacing = 8, AllowDrop = true };
    private readonly TextBlock name = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock error = new() { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    private readonly Image preview = new() { Name = "ImageOpenAIMarkupPreview", Width = 96, Height = 96, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Button clear;
    public FrameworkElement View => group;
    public bool IsLoading { get; private set; }

    public OpenAIImageMarkupField(JsonObject options)
    {
        this.options = options;
        var label = ChatSettingsFields.L("openai.imageMarkup");
        group.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap });
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var pick = new Button { Name = "ImageOpenAIPickMarkup", Content = label };
        clear = new() { Name = "ImageOpenAIClearMarkup", Content = DesktopResources.Get("Delete"), IsEnabled = options["markup"] is JsonObject };
        ControlAppearance.Stock(pick); ControlAppearance.Stock(clear);
        AutomationProperties.SetName(pick, label); AutomationProperties.SetName(clear, label + " — " + DesktopResources.Get("Delete"));
        row.Children.Add(pick); row.Children.Add(clear); group.Children.Add(row);
        group.Children.Add(name); group.Children.Add(preview);
        group.Children.Add(new TextBlock { Text = ChatSettingsFields.L("openai.imageMarkupHint"), TextWrapping = TextWrapping.Wrap });
        group.Children.Add(error);
        name.Text = options["markup"] is JsonObject markup ? OpenAIChatConfig.Text(markup["file_id"]) ?? "markup.png" : "";
        _ = PreviewAsync(OpenAIChatConfig.Text(options["markup"]?["image_url"]));

        pick.Click += async (_, _) =>
        {
            if (IsLoading) return; IsLoading = true;
            try
            {
                var picker = new FileOpenPicker(); picker.FileTypeFilter.Add(".png");
                WinRT.Interop.InitializeWithWindow.Initialize(picker, Microsoft.UI.Win32Interop.GetWindowFromWindowId(group.XamlRoot.ContentIslandEnvironment.AppWindowId));
                if (await picker.PickSingleFileAsync() is { } file) await LoadAsync(file);
            }
            catch (Exception) { ShowError(); }
            finally { IsLoading = false; }
        };
        group.DragOver += (_, args) =>
        {
            args.Handled = true;
            args.AcceptedOperation = !IsLoading && args.DataView.Contains(StandardDataFormats.StorageItems) ? DataPackageOperation.Copy : DataPackageOperation.None;
        };
        group.Drop += async (_, args) =>
        {
            args.Handled = true;
            if (IsLoading || !args.DataView.Contains(StandardDataFormats.StorageItems)) return;
            var deferral = args.GetDeferral(); IsLoading = true;
            try
            {
                var items = await args.DataView.GetStorageItemsAsync();
                if (items.OfType<StorageFile>().FirstOrDefault() is { } file) await LoadAsync(file);
            }
            catch (Exception) { ShowError(); }
            finally { IsLoading = false; deferral.Complete(); }
        };
        clear.Click += (_, _) =>
        {
            if (IsLoading) return;
            options.Remove("markup"); name.Text = ""; preview.Source = null; clear.IsEnabled = false; error.Visibility = Visibility.Collapsed;
        };
    }

    private async Task LoadAsync(StorageFile file)
    {
        await using var stream = await file.OpenStreamForReadAsync();
        var attachment = await ComposerAttachments.ReadAsync(file.Name, file.ContentType, stream, CancellationToken.None);
        ImageAttachments.Validate(attachment);
        var url = $"data:{attachment.MediaType};base64,{Convert.ToBase64String(attachment.Content.Span)}";
        options["markup"] = new JsonObject { ["image_url"] = url };
        name.Text = attachment.Name; clear.IsEnabled = true; error.Visibility = Visibility.Collapsed;
        await PreviewAsync(url);
    }

    private async Task PreviewAsync(string? url)
    {
        if (string.IsNullOrEmpty(url)) return;
        try
        {
            var bitmap = new BitmapImage();
            if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                var comma = url.IndexOf(',');
                using var stream = new MemoryStream(Convert.FromBase64String(url[(comma + 1)..]));
                await bitmap.SetSourceAsync(stream.AsRandomAccessStream());
            }
            else bitmap.UriSource = new Uri(url);
            preview.Source = bitmap;
        }
        catch (Exception) { ShowError(); }
    }
    private void ShowError() { error.Text = ChatSettingsFields.L("openai.imageMarkupFailed"); error.Visibility = Visibility.Visible; }
}
