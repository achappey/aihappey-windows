using System.Globalization;
using System.Text;
using MimeKit;
using MsgReader.Outlook;
using RtfPipe;

namespace AIHappey.Desktop.Core;

internal sealed class EmailDocumentTextExtractor(DocumentTextExtraction extraction) : DocumentFormatTextExtractor([".eml"], ["message/rfc822"])
{
    internal override async Task<string?> ExtractAsync(ReadOnlyMemory<byte> content, DocumentExtractionContext context, int depth)
    {
        using var input = new MemoryStream(content.ToArray(), false);
        var options = new ParserOptions { MaxMimeDepth = 32 };
        using var message = await MimeMessage.LoadAsync(options, input, context.CancellationToken);
        return await ReadMessageAsync(message, context, depth);
    }
    private async Task<string> ReadMessageAsync(MimeMessage message, DocumentExtractionContext context, int depth)
    {
        context.Check(depth);
        var text = new DocumentTextBuilder(context);
        text.Line($"From: {DocumentExtractionContext.Label(message.From.ToString())}");
        text.Line($"To: {DocumentExtractionContext.Label(message.To.ToString())}");
        if (message.Cc.Count > 0) text.Line($"Cc: {DocumentExtractionContext.Label(message.Cc.ToString())}");
        text.Line($"Date: {message.Date.ToString("O", CultureInfo.InvariantCulture)}");
        text.Line($"Subject: {DocumentExtractionContext.Label(message.Subject ?? "")}"); text.Line();
        var body = message.TextBody;
        if (string.IsNullOrWhiteSpace(body) && message.HtmlBody is { } html) body = HtmlDocumentTextExtractor.FromHtml(html, context);
        text.Line(body); text.Line();
        foreach (var attachment in message.Attachments)
        {
            context.Check(depth); context.AddEntries(1);
            if (attachment is MessagePart nested)
            {
                context.Check(depth + 1);
                text.Line("[Attached message]"); text.Line(await ReadMessageAsync(nested.Message, context, depth + 1));
            }
            else if (attachment is MimePart part)
            {
                var filename = part.FileName ?? "attachment";
                var mime = part.ContentType.MimeType;
                if (!extraction.Supports(filename, mime))
                { DocumentContainerText.Notice(text, filename, "DocumentEntryUnsupported"); continue; }
                if (part.Content is null) { DocumentContainerText.Notice(text, filename, "DocumentEntryNoText"); continue; }
                using var decoded = new DocumentExtractionWriteStream(context);
                await part.Content.DecodeToAsync(decoded, context.CancellationToken);
                await DocumentContainerText.AppendAsync(text, extraction, filename, mime, decoded.ToArray(), context, depth + 1);
            }
        }
        return text.ToString().Trim();
    }
}

internal sealed class OutlookDocumentTextExtractor(DocumentTextExtraction extraction) : DocumentFormatTextExtractor([".msg"], ["application/vnd.ms-outlook"])
{
    internal override async Task<string?> ExtractAsync(ReadOnlyMemory<byte> content, DocumentExtractionContext context, int depth)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        using var input = new MemoryStream(content.ToArray(), false);
        using var message = new Storage.Message(input, FileAccess.Read, true);
        return await ReadMessageAsync(message, context, depth);
    }
    private async Task<string> ReadMessageAsync(Storage.Message message, DocumentExtractionContext context, int depth)
    {
        context.Check(depth);
        var text = new DocumentTextBuilder(context);
        text.Line($"From: {DocumentExtractionContext.Label(message.Sender?.DisplayName ?? "")} <{DocumentExtractionContext.Label(message.Sender?.Email ?? "")}>");
        foreach (var recipient in message.Recipients)
            text.Line($"{recipient.Type}: {DocumentExtractionContext.Label(recipient.DisplayName ?? "")} <{DocumentExtractionContext.Label(recipient.Email ?? "")}>");
        if (message.SentOn is { } sent) text.Line($"Date: {sent.ToString("O", CultureInfo.InvariantCulture)}");
        text.Line($"Subject: {DocumentExtractionContext.Label(message.Subject ?? "")}"); text.Line();
        var body = message.BodyText;
        if (string.IsNullOrWhiteSpace(body) && message.BodyHtml is { Length: > 0 } html)
            body = HtmlDocumentTextExtractor.FromHtml(html, context);
        if (string.IsNullOrWhiteSpace(body) && message.BodyRtf is { Length: > 0 } rtf)
            body = HtmlDocumentTextExtractor.FromHtml(Rtf.ToHtml(rtf), context);
        text.Line(body); text.Line();
        foreach (var attachment in message.Attachments)
        {
            context.Check(depth); context.AddEntries(1);
            if (attachment is Storage.Message nested)
            {
                context.Check(depth + 1);
                text.Line("[Attached message]"); text.Line(await ReadMessageAsync(nested, context, depth + 1));
            }
            else if (attachment is Storage.Attachment part)
            {
                var filename = part.FileName ?? "attachment";
                // MSG attachments can point at arbitrary local/UNC paths. Never resolve them,
                // even if the supplied path exists and is readable by this process.
                if (part.IsExternalReference)
                { DocumentContainerText.Notice(text, filename, "DocumentEntryExternal"); continue; }
                var mime = part.MimeType ?? UrlAttachments.TypeFromPath(Uri.EscapeDataString(filename)) ?? "application/octet-stream";
                if (!extraction.Supports(filename, mime))
                { DocumentContainerText.Notice(text, filename, "DocumentEntryUnsupported"); continue; }
                var bytes = part.Data;
                ComposerAttachments.ValidateSize(bytes.Length); context.AddBytes(bytes.Length);
                await DocumentContainerText.AppendAsync(text, extraction, filename, mime, bytes, context, depth + 1);
            }
        }
        return text.ToString().Trim();
    }
}

/// <summary>Bounds decoded MIME attachments while a decoder writes, not after allocation.</summary>
internal sealed class DocumentExtractionWriteStream(DocumentExtractionContext context) : MemoryStream
{
    private void Admit(int count)
    {
        context.AddBytes(count);
        if (count > ComposerAttachments.MaximumFileBytes - Length) throw new DocumentExtractionLimitException();
    }
    public override void Write(byte[] buffer, int offset, int count) { Admit(count); base.Write(buffer, offset, count); }
    public override void Write(ReadOnlySpan<byte> buffer) { Admit(buffer.Length); base.Write(buffer); }
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); Write(buffer, offset, count); return Task.CompletedTask; }
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); Write(buffer.Span); return ValueTask.CompletedTask; }
}
