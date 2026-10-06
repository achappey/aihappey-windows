using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AIHappey.Desktop.Core;

/// <summary>Native equivalent of the web Link modal. Old URL checks never enable Add for a new URL.</summary>
public sealed class UrlAttachmentDialog : ContentDialog
{
    private readonly Func<string, CancellationToken, Task<string?>> resolve;
    private readonly TextBox url = new() { Name = "AttachmentUrl", Header = "URL (publicly accessible)", PlaceholderText = "https://", TextWrapping = TextWrapping.NoWrap };
    private readonly StackPanel pending = new() { Orientation = Orientation.Horizontal, Spacing = 8, Visibility = Visibility.Collapsed };
    private readonly TextBlock detectedLabel = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Border badge;
    private readonly StackPanel fallback = new() { Spacing = 8, Visibility = Visibility.Collapsed };
    private readonly ComboBox types = new() { Name = "AttachmentMediaType", Header = "MIME type", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox custom = new() { Name = "CustomAttachmentMediaType", Header = "Custom MIME type", Visibility = Visibility.Collapsed };
    private readonly TextBlock error = new() { Name = "AttachmentValidation", TextWrapping = TextWrapping.Wrap };
    private CancellationTokenSource? detection;
    private string? checkedUrl;
    private string? detected;
    private bool closed;
    public ComposerAttachment? Attachment { get; private set; }

    public UrlAttachmentDialog(Func<string, CancellationToken, Task<string?>> resolve)
    {
        this.resolve = resolve;
        Title = "Link"; PrimaryButtonText = "Add"; CloseButtonText = "Cancel";
        DefaultButton = ContentDialogButton.Primary; IsPrimaryButtonEnabled = false;
        var panel = new StackPanel { Spacing = 12, MinWidth = 240, MaxWidth = 540 };
        panel.Children.Add(url);
        pending.Children.Add(new ProgressRing { Width = 20, Height = 20, IsActive = true });
        pending.Children.Add(new TextBlock { Text = "Detecting MIME type…", VerticalAlignment = VerticalAlignment.Center });
        panel.Children.Add(pending);
        var badgeContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        badgeContent.Children.Add(new FontIcon { Glyph = "\uE723", FontSize = 14 }); badgeContent.Children.Add(detectedLabel);
        badge = new Border { Name = "DetectedAttachmentMediaType", Child = badgeContent, Padding = new Thickness(10, 4, 10, 4), CornerRadius = new CornerRadius(16), HorizontalAlignment = HorizontalAlignment.Left, Visibility = Visibility.Collapsed };
        ControlAppearance.TokenBadge(badge); panel.Children.Add(badge);
        types.Items.Add("Choose a MIME type");
        foreach (var type in UrlAttachments.CommonMediaTypes) types.Items.Add(type);
        types.Items.Add("Custom"); types.SelectedIndex = 0;
        fallback.Children.Add(types); fallback.Children.Add(custom);
        fallback.Children.Add(new TextBlock { Text = "MIME type could not be detected. Select the type of content at this URL.", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(fallback); panel.Children.Add(error); Content = panel;
        AutomationProperties.SetName(url, "URL (publicly accessible)");
        AutomationProperties.SetName(types, "MIME type"); AutomationProperties.SetName(custom, "Custom MIME type");
        AutomationProperties.SetLiveSetting(error, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        AutomationProperties.SetLiveSetting(detectedLabel, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        foreach (var control in new Control[] { url, types, custom }) ControlAppearance.Native(control);
        url.TextChanged += async (_, _) => await DetectAsync();
        types.SelectionChanged += (_, _) => UpdateValidation();
        custom.TextChanged += (_, _) => UpdateValidation();
        PrimaryButtonClick += (_, args) =>
        {
            if (!IsPrimaryButtonEnabled || MediaType() is not { } type) { args.Cancel = true; return; }
            Attachment = ComposerAttachment.Link(url.Text, type);
        };
        Closing += (_, _) => { closed = true; detection?.Cancel(); };
        Opened += (_, _) => url.Focus(FocusState.Programmatic);
    }

    private string? MediaType() => checkedUrl == url.Text.Trim() && UrlAttachments.IsHttpUrl(checkedUrl)
        ? detected ?? (types.SelectedItem as string == "Custom" ? UrlAttachments.ValidMediaType(custom.Text)
            : types.SelectedIndex > 0 ? UrlAttachments.ValidMediaType(types.SelectedItem as string) : null) : null;

    private void UpdateValidation()
    {
        custom.Visibility = types.SelectedItem as string == "Custom" ? Visibility.Visible : Visibility.Collapsed;
        IsPrimaryButtonEnabled = !closed && MediaType() is not null;
        error.Text = url.Text.Length > 0 && !UrlAttachments.IsHttpUrl(url.Text) ? "Enter a valid HTTP or HTTPS URL without credentials."
            : custom.Visibility == Visibility.Visible && custom.Text.Length > 0 && UrlAttachments.ValidMediaType(custom.Text) is null ? "Enter a valid MIME type, such as application/pdf." : "";
    }

    private async Task DetectAsync()
    {
        detection?.Cancel();
        checkedUrl = detected = null;
        badge.Visibility = fallback.Visibility = pending.Visibility = Visibility.Collapsed;
        UpdateValidation();
        var value = url.Text.Trim();
        if (closed || !UrlAttachments.IsHttpUrl(value)) return;
        using var lifetime = new CancellationTokenSource(); detection = lifetime;
        pending.Visibility = Visibility.Visible;
        try
        {
            await Task.Delay(400, lifetime.Token);
            var type = await resolve(value, lifetime.Token);
            if (closed || lifetime.IsCancellationRequested || url.Text.Trim() != value) return;
            detected = UrlAttachments.ValidMediaType(type); checkedUrl = value;
            detectedLabel.Text = detected ?? "";
            badge.Visibility = detected is null ? Visibility.Collapsed : Visibility.Visible;
            fallback.Visibility = detected is null ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is HttpRequestException or InvalidOperationException or ArgumentException)
        {
            if (!closed && !lifetime.IsCancellationRequested && url.Text.Trim() == value)
            { checkedUrl = value; fallback.Visibility = Visibility.Visible; }
        }
        finally
        {
            if (ReferenceEquals(detection, lifetime))
            { detection = null; pending.Visibility = Visibility.Collapsed; UpdateValidation(); }
        }
    }
}
