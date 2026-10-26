using System.IO.Compression;
using System.Text;
using HtmlAgilityPack;
using RtfPipe;
using VersOne.Epub;

namespace AIHappey.Desktop.Core;

internal sealed class HtmlDocumentTextExtractor() : DocumentFormatTextExtractor([".html", ".htm", ".xhtml"], ["text/html", "application/xhtml+xml"])
{
    internal override Task<string?> ExtractAsync(ReadOnlyMemory<byte> content, DocumentExtractionContext context, int depth)
    {
        using var input = new MemoryStream(content.ToArray(), false);
        var document = NewDocument();
        document.Load(input, Encoding.UTF8, true);
        return Task.FromResult<string?>(ReadableText(document, context));
    }
    private static HtmlDocument NewDocument() => new() { OptionMaxNestedChildNodes = 256 };
    internal static string FromHtml(string html, DocumentExtractionContext context)
    {
        context.Check();
        var document = NewDocument(); document.LoadHtml(html);
        return ReadableText(document, context);
    }
    private static string ReadableText(HtmlDocument document, DocumentExtractionContext context)
    {
        // DOM parsing only: never navigate, execute scripts, resolve links, or fetch images/CSS.
        var text = new DocumentTextBuilder(context);
        var nodes = new Stack<(HtmlNode Node, bool Closing)>();
        nodes.Push((document.DocumentNode, false));
        var whitespace = false;
        while (nodes.TryPop(out var entry))
        {
            context.Check();
            var node = entry.Node;
            if (node.NodeType == HtmlNodeType.Comment || node.Name is "head" or "script" or "style" or "template" or "noscript" or "iframe" or "object") continue;
            if (node.Attributes.Contains("hidden") || node.GetAttributeValue("aria-hidden", "").Equals("true", StringComparison.OrdinalIgnoreCase)) continue;
            if (entry.Closing)
            {
                if (node.Name is "td" or "th") { text.Append("\t"); whitespace = true; }
                else if (IsBlock(node.Name)) { text.Line(); whitespace = true; }
                continue;
            }
            if (node.NodeType == HtmlNodeType.Text)
            {
                var decoded = HtmlEntity.DeEntitize(node.InnerText);
                var run = new StringBuilder();
                foreach (var c in decoded)
                {
                    if (char.IsWhiteSpace(c)) { if (!whitespace) run.Append(' '); whitespace = true; }
                    else { run.Append(c); whitespace = false; }
                }
                text.Append(run.ToString());
            }
            else
            {
                if (node.Name == "br" || IsBlock(node.Name)) { text.Line(); whitespace = true; }
                nodes.Push((node, true));
                for (var i = node.ChildNodes.Count - 1; i >= 0; i--) nodes.Push((node.ChildNodes[i], false));
            }
        }
        // Keep meaningful line boundaries without returning empty runs from nested block elements.
        var result = new DocumentTextBuilder(context);
        using var reader = new StringReader(text.ToString());
        while (reader.ReadLine() is { } line)
            if (!string.IsNullOrWhiteSpace(line)) result.Line(line.Trim());
        return result.ToString().Trim();
    }
    private static bool IsBlock(string name) => name is "p" or "div" or "section" or "article" or "header" or "footer"
        or "h1" or "h2" or "h3" or "h4" or "h5" or "h6" or "li" or "ul" or "ol" or "tr" or "table" or "blockquote" or "pre" or "hr";
}

internal sealed class RtfDocumentTextExtractor() : DocumentFormatTextExtractor([".rtf"], ["application/rtf", "text/rtf"])
{
    internal override Task<string?> ExtractAsync(ReadOnlyMemory<byte> content, DocumentExtractionContext context, int depth)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        using var input = new MemoryStream(content.ToArray(), false);
        context.Check();
        var html = Rtf.ToHtml(input);
        context.Check();
        return Task.FromResult<string?>(HtmlDocumentTextExtractor.FromHtml(html, context));
    }
}

internal sealed class EpubDocumentTextExtractor() : DocumentFormatTextExtractor([".epub"], ["application/epub+zip"])
{
    internal override async Task<string?> ExtractAsync(ReadOnlyMemory<byte> content, DocumentExtractionContext context, int depth)
    {
        context.ValidatePackage(content);
        using var input = new MemoryStream(content.ToArray(), false);
        using (var archive = new ZipArchive(input, ZipArchiveMode.Read, true))
            if (archive.GetEntry("META-INF/encryption.xml") is not null)
                throw new InvalidDataException("Encrypted EPUB packages are not supported.");
        input.Position = 0;
        var book = await EpubReader.ReadBookAsync(input);
        var text = new DocumentTextBuilder(context);
        foreach (var chapter in book.ReadingOrder)
        {
            context.Check();
            var body = HtmlDocumentTextExtractor.FromHtml(chapter.Content, context);
            if (!string.IsNullOrWhiteSpace(body)) { text.Line(body); text.Line(); }
        }
        return text.ToString().Trim();
    }
}
