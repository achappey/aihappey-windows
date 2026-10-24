using Markdig;
using Windows.ApplicationModel.DataTransfer;

namespace AIHappey.Desktop.Core;

/// <summary>Native rich copy: retain original Markdown as text and offer safe formatted HTML to rich editors.</summary>
public static class CanvasClipboard
{
    private static readonly MarkdownPipeline pipeline = new MarkdownPipelineBuilder().UsePipeTables().DisableHtml().Build();

    public static DataPackage Create(string markdown)
    {
        var package = new DataPackage();
        package.SetText(markdown);
        var html = new HtmlAgilityPack.HtmlDocument();
        html.LoadHtml(Markdig.Markdown.ToHtml(markdown, pipeline));
        // Rich copy must not introduce active protocols or remote image loads on paste.
        foreach (var image in html.DocumentNode.Descendants("img").ToArray()) image.Remove();
        foreach (var link in html.DocumentNode.Descendants("a"))
            if (ConversationCanvasProjection.ExternalUri(HtmlAgilityPack.HtmlEntity.DeEntitize(link.GetAttributeValue("href", ""))) is null)
                link.Attributes.Remove("href");
        package.SetHtmlFormat(HtmlFormatHelper.CreateHtmlFormat(html.DocumentNode.InnerHtml));
        return package;
    }
}
