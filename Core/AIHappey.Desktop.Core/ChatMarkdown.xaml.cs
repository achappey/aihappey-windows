using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Windows.System;

namespace AIHappey.Desktop.Core;

/// <summary>Toolkit integration only: native resource adaptation and read-only content policy.</summary>
public sealed partial class ChatMarkdown : UserControl
{
    private bool refreshQueued;
    public string Text { get => Markdown.Text; set => Markdown.Text = value; }

    public ChatMarkdown()
    {
        InitializeComponent();
        Markdown.Config = new MarkdownConfig { ImageProvider = new NoRemoteImages() };
        Markdown.OnLinkClicked += async (_, args) =>
        {
            args.Handled = true;
            // Labs clears NavigateUri after a handled event. Refresh restores the links
            // for subsequent clicks without allowing default protocol activation.
            QueueRefresh();
            if (args.Uri is not { IsAbsoluteUri: true } uri || uri.Scheme is not ("https" or "http")) return;
            try { await Launcher.LaunchUriAsync(uri); }
            catch (Exception error) { System.Diagnostics.Trace.WriteLine($"Markdown link activation failed: {error.Message}"); }
        };
        foreach (var (element, property) in new (DependencyObject, DependencyProperty)[]
        {
            (Primary, TextBlock.ForegroundProperty), (Link, TextBlock.ForegroundProperty),
            (Card, Border.BackgroundProperty), (Card, Border.BorderBrushProperty),
            (Code, Border.BackgroundProperty), (Code, Border.BorderBrushProperty)
        }) element.RegisterPropertyChangedCallback(property, (_, _) => QueueRefresh());
        Loaded += (_, _) => QueueRefresh();
        ActualThemeChanged += (_, _) => QueueRefresh();
    }

    private void QueueRefresh()
    {
        if (refreshQueued) return;
        refreshQueued = DispatcherQueue.TryEnqueue(() =>
        {
            refreshQueued = false;
            if (!IsLoaded) return;
            Markdown.Config = new MarkdownConfig
            {
                ImageProvider = new NoRemoteImages(),
                Themes = new MarkdownThemes
                {
                    H1Foreground = Primary.Foreground, H2Foreground = Primary.Foreground,
                    H3Foreground = Primary.Foreground, H4Foreground = Primary.Foreground,
                    H5Foreground = Primary.Foreground, H6Foreground = Primary.Foreground,
                    BorderBrush = Card.BorderBrush, TableHeadingBackground = Card.Background,
                    InlineCodeBackground = Code.Background, InlineCodeForeground = Primary.Foreground,
                    InlineCodeBorderBrush = Card.BorderBrush, InlineCodeFontSize = 14,
                    CodeBlockBackground = Code.Background, CodeBlockForeground = Primary.Foreground,
                    CodeBlockBorderBrush = Card.BorderBrush, HorizontalRuleBrush = Code.BorderBrush,
                    LinkForeground = Link.Foreground, QuoteBorderBrush = Card.BorderBrush,
                    QuoteForeground = Primary.Foreground, TableBorderBrush = Card.BorderBrush,
                    YamlBorderBrush = Card.BorderBrush
                }
            };
            var text = Text;
            Markdown.Text = "";
            Markdown.Text = text;
            foreach (var richText in ControlAppearance.Descendants(Markdown).OfType<RichTextBlock>())
                richText.SetBinding(RichTextBlock.ForegroundProperty, new Binding { Source = Primary, Path = new PropertyPath("Foreground") });
        });
    }

    // Conversation content must not trigger network/local-file reads merely by being rendered.
    private sealed class NoRemoteImages : IImageProvider
    {
        public bool ShouldUseThisProvider(string url) => true;
        public Task<Image> GetImage(string url) => Task.FromResult(new Image());
    }
}
