using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AIHappey.Desktop.Core;

/// <summary>Safe, read-only presentation shared by approvals and conversation activity.</summary>
internal static class ToolOutputView
{
    public static UIElement Create(JsonElement part, string tabsName = "ToolOutputTabs")
    {
        if (!DesktopToolApprovals.HasOutput(part))
            return Text(PortableConversations.String(part, "errorText") ?? DesktopResources.Get("NoToolOutputResult"));
        var output = part.GetProperty("output");
        if (output.ValueKind != JsonValueKind.Object) return Value(output);
        var sections = new ToolReviewTabs(tabsName);
        if (output.TryGetProperty("structuredContent", out var structured) && structured.ValueKind != JsonValueKind.Null)
            sections.Add(DesktopResources.Get("StructuredContent"), Value(structured), "ToolStructuredOutput");
        if (output.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            var i = 0;
            foreach (var block in content.EnumerateArray())
            {
                var kind = PortableConversations.String(block, "type") ?? "unknown";
                var view = kind == "text" && PortableConversations.String(block, "text") is { } text
                    ? (UIElement)new ChatMarkdown { Text = text } : kind == "image" ? ImageView(block) : Value(block);
                var label = DesktopResources.Get(kind switch { "text" => "ToolContentText", "image" => "ToolContentImage",
                    "audio" => "ToolContentAudio", "resource" => "ToolContentResource", "resource_link" => "ToolContentLink", _ => "ToolContentOther" });
                sections.Add($"{label} {++i}", view, "ToolOutputBlock");
            }
        }
        if (sections.Count == 0) return Value(output);
        sections.Add(DesktopResources.Get("RawOutput"), Value(output), "ToolRawOutput");
        sections.SelectedIndex = 0;
        return sections;
    }

    public static TextBlock Text(string value) => new() { Text = value, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
    private static UIElement ImageView(JsonElement block)
        => ImageView(new MessageAttachment(PortableConversations.String(block, "name") ?? DesktopResources.Get("ImagePreview"),
            PortableConversations.String(block, "mimeType") ?? "image/png", Base64: PortableConversations.String(block, "data")), "ToolOutputImage");

    // Shared with inline chat images. Decode embedded bytes locally; remote media is loaded
    // only after an explicit click using the same anonymous, bounded download policy.
    internal static UIElement ImageView(MessageAttachment file, string name = "ChatGeneratedImage")
    {
        var panel = new StackPanel { Spacing = 8 };
        var image = new Image { Name = name, MaxHeight = 420, Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(image, file.Name);
        panel.Children.Add(image);
        var notice = Text(file.MediaType); panel.Children.Add(notice);
        var embedded = file.Base64 is not null || file.Text is not null || file.Url?.StartsWith("data:", StringComparison.OrdinalIgnoreCase) == true;
        var load = new Button { Name = "LoadChatImagePreview", Content = DesktopResources.Get("LoadImagePreview"),
            Visibility = embedded ? Visibility.Collapsed : Visibility.Visible, IsEnabled = AttachmentDownloads.RemoteUri(file.Url) is not null };
        ControlAppearance.Native(load); panel.Children.Add(load);
        CancellationTokenSource? lifetime = null;
        var loading = false;
        async Task LoadAsync()
        {
            var active = lifetime;
            if (active is null || loading || image.Source is not null) return;
            loading = true; load.IsEnabled = false;
            try
            {
                var bytes = await Task.Run(() => AttachmentDownloads.EmbeddedBytes(file), active.Token);
                if (bytes is null)
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(active.Token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(30));
                    using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseDefaultCredentials = false });
                    using var download = new MemoryStream();
                    await AttachmentDownloads.WriteAsync(file, download, http, timeout.Token);
                    bytes = download.ToArray();
                }
                using var memory = new MemoryStream(bytes); using var stream = memory.AsRandomAccessStream();
                var bitmap = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage { DecodePixelWidth = 1200 };
                await bitmap.SetSourceAsync(stream).AsTask(active.Token);
                if (ReferenceEquals(lifetime, active) && image.IsLoaded) { image.Source = bitmap; notice.Text = file.MediaType; }
            }
            catch (OperationCanceledException) when (active.IsCancellationRequested) { }
            catch (Exception e) when (e is not OutOfMemoryException)
            { if (ReferenceEquals(lifetime, active)) notice.Text = DesktopResources.Get("ImagePreviewFailed"); }
            finally
            {
                if (ReferenceEquals(lifetime, active))
                { loading = false; load.IsEnabled = AttachmentDownloads.RemoteUri(file.Url) is not null; }
            }
        }
        load.Click += async (_, _) => await LoadAsync();
        image.Loaded += async (_, _) =>
        {
            lifetime ??= new CancellationTokenSource();
            if (embedded) await LoadAsync();
        };
        image.Unloaded += (_, _) =>
        {
            var previous = lifetime; lifetime = null;
            previous?.Cancel(); previous?.Dispose();
            loading = false; image.Source = null;
        };
        return panel;
    }
    public static UIElement Value(JsonElement value) => Text(value.ValueKind == JsonValueKind.String
        ? value.GetString() ?? "" : JsonSerializer.Serialize(value, PortableConversations.Json));
}

public sealed class ToolOutputDialog : ContentDialog, IResponsiveDialog
{
    private readonly Grid layout = new();

    public ToolOutputDialog(string toolName, JsonElement part)
    {
        Name = "ToolOutputDialog";
        Title = DesktopResources.Format("ToolOutputTitle", toolName);
        CloseButtonText = DesktopResources.Get("Close");
        DefaultButton = ContentDialogButton.Close;
        Resources["ContentDialogMaxWidth"] = 840d;
        Resources["ContentDialogMinWidth"] = 0d;
        layout.Children.Add(ToolOutputView.Create(part));
        Content = layout;
        Opened += (_, _) => { SizeToRoot(); XamlRoot.Changed += RootChanged; };
        Closed += (_, _) => XamlRoot.Changed -= RootChanged;
    }

    private void RootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => SizeToRoot();
    void IResponsiveDialog.SizeToRoot() => SizeToRoot();
    private void SizeToRoot()
    {
        if (XamlRoot is null) return;
        layout.Width = Math.Max(0, Math.Min(760, XamlRoot.Size.Width - 96));
        layout.Height = Math.Max(80, Math.Min(520, XamlRoot.Size.Height - 240));
    }
}
