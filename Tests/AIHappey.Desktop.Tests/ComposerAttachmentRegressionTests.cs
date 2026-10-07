using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Desktop.Core;
using AIHappey.Vercel.Models;

internal static class ComposerAttachmentRegressionTests
{
    public static async Task RunAsync(Action<bool, string> check, Action<Action, string> reject, DesktopSession session)
    {
        var draft = new List<ComposerAttachment>();
        var names = new[] { "first.txt", "large.pdf", "denied.txt", "broken.txt", "last.png" };
        var rejected = await ComposerAttachments.AdmitAsync(names, name => name, async (name, ct) =>
        {
            if (name == "large.pdf") ComposerAttachments.ValidateSize(ComposerAttachments.MaximumFileBytes + 1L);
            if (name == "denied.txt") throw new UnauthorizedAccessException();
            if (name == "broken.txt") throw new IOException();
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(name));
            return await ComposerAttachments.ReadAsync(name, "application/octet-stream", stream, ct);
        }, draft.Add, () => true, CancellationToken.None);
        check(draft.Select(file => file.Name).SequenceEqual(new[] { "first.txt", "last.png" })
            && rejected.SequenceEqual(names.Skip(1).Take(3)), "picker/drop batch keeps readable files and aggregates oversize/access/read failures");
        check(draft[0].MediaType == "text/plain" && draft[1].MediaType == "image/png", "picker/drop use the same extension MIME fallback");
        reject(() => ComposerAttachments.ValidateSize(-1), "negative attachment size rejected");
        ComposerAttachments.ValidateSize(ComposerAttachments.MaximumFileBytes);
        check(true, "attachment limit accepts exactly 25 MB");

        var original = new byte[] { 0, 255, 7 };
        var immutable = ComposerAttachment.Local("C:\\private\\payload.bin", null, original);
        original[0] = 99;
        check(immutable.Name == "payload.bin" && immutable.Content.Span[0] == 0, "local file admission snapshots bytes and strips local paths");

        var reads = 0;
        var current = false;
        await ComposerAttachments.AdmitAsync(new[] { "stale.txt" }, name => name,
            (name, _) => { reads++; return Task.FromResult(immutable); }, draft.Add, () => current, CancellationToken.None);
        check(reads == 0 && draft.Count == 2, "stale draft/account blocked before reading dropped files");
        current = true;
        await ComposerAttachments.AdmitAsync(new[] { "stale.txt", "next.txt" }, name => name,
            (name, _) => { reads++; current = false; return Task.FromResult(immutable); }, draft.Add, () => current, CancellationToken.None);
        check(reads == 1 && draft.Count == 2, "draft/account change while reading prevents admission and remaining reads");

        using (var canceled = new CancellationTokenSource())
        {
            try
            {
                await ComposerAttachments.AdmitAsync(new[] { "cancel.txt" }, name => name,
                    (_, _) => { canceled.Cancel(); return Task.FromResult(immutable); }, draft.Add, () => true, canceled.Token);
                throw new Exception("Expected canceled admission");
            }
            catch (OperationCanceledException) { check(draft.Count == 2, "cancellation after read never admits the canceled file"); }
        }
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel();
            try
            {
                await ComposerAttachments.AdmitAsync(new[] { "cancel.txt" }, name => name,
                    (_, _) => { reads++; return Task.FromResult(immutable); }, draft.Add, () => true, canceled.Token);
                throw new Exception("Expected canceled admission");
            }
            catch (OperationCanceledException) { check(reads == 1, "pre-canceled batch does not read files"); }
        }

        draft.RemoveAt(0); // Same removal performed by the chip's X button.
        draft.Add(immutable);
        draft.Add(ComposerAttachment.Link("https://files.example/report.pdf", "application/pdf"));
        var extractor = new FixtureExtractor();
        var prepared = await ComposerAttachments.PrepareAsync("", draft, ServiceKind.Ai, false, extractor, CancellationToken.None);
        check(prepared.Message.Parts.Count == 3 && prepared.Message.Parts.All(part => part is FileUIPart)
            && !prepared.Message.Parts.OfType<FileUIPart>().Any(part => part.Filename == "first.txt"), "attachment-only message sends remaining chips, not removed files");
        check(prepared.Message.Parts.OfType<FileUIPart>().Single(file => file.Filename == "payload.bin").Url
            == "data:application/octet-stream;base64,AP8H", "binary file part contains exact snapshotted base64 bytes");

        using var handler = new PayloadHandler();
        using var http = new HttpClient(handler);
        var client = new DesktopChatClient(session, http);
        foreach (var service in new[] { ServiceKind.Ai, ServiceKind.Agents })
        {
            await foreach (var _ in client.StreamAsync(service, "fixture", "conversation", [prepared.Message], CancellationToken.None)) { }
            using var body = JsonDocument.Parse(handler.Body!);
            var parts = body.RootElement.GetProperty("messages")[0].GetProperty("parts");
            check(parts.GetArrayLength() == 3 && parts[0].GetProperty("type").GetString() == "file"
                && parts[0].GetProperty("filename").GetString() == "last.png"
                && parts[0].GetProperty("mediaType").GetString() == "image/png"
                && parts[1].GetProperty("url").GetString() == "data:application/octet-stream;base64,AP8H"
                && parts[2].GetProperty("url").GetString() == "https://files.example/report.pdf"
                && !handler.Body!.Contains("private"), service + ": dropped files and links retain filenames/MIME/content on actual outgoing HTTP request");
        }

        var pdf = ComposerAttachment.Local("report.pdf", "application/pdf", Encoding.UTF8.GetBytes("PDF fixture"));
        var extracted = await ComposerAttachments.PrepareAsync(" Summarize ", [pdf], ServiceKind.Ai, true, extractor, CancellationToken.None);
        check(extracted.Message.Parts[0] is TextUIPart text && text.Text.Contains("Extracted fixture")
            && extracted.Message.Parts[1] is FileUIPart && extracted.Message.Parts[2] is TextUIPart prompt && prompt.Text == "Summarize",
            "dropped documents honor extraction preference and still send original file");
        extractor.Fail = true;
        var fallback = await ComposerAttachments.PrepareAsync("", [pdf], ServiceKind.Ai, true, extractor, CancellationToken.None);
        check(fallback.Warnings.Count == 1 && fallback.Message.Parts.Single() is FileUIPart, "extraction failure keeps original attachment with warning");
        var agent = await ComposerAttachments.PrepareAsync("", [pdf], ServiceKind.Agents, true, extractor, CancellationToken.None);
        check(agent.Warnings.Count == 0 && agent.Message.Parts.Single() is FileUIPart, "agent attachments bypass document extraction");
    }

    private sealed class FixtureExtractor : IDocumentTextExtractor
    {
        public bool Fail { get; set; }
        public bool Supports(string filename, string mediaType) => mediaType == "application/pdf";
        public Task<string?> ExtractAsync(ReadOnlyMemory<byte> content, CancellationToken ct)
            => Fail ? throw new InvalidOperationException() : Task.FromResult<string?>("Extracted fixture");
    }

    private sealed class PayloadHandler : HttpMessageHandler
    {
        public string? Body { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Body = await request.Content!.ReadAsStringAsync(ct);
            return new(HttpStatusCode.OK) { Content = new StringContent("data: [DONE]\n\n", Encoding.UTF8, "text/event-stream") };
        }
    }
}
