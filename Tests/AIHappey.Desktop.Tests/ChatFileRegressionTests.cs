using System.Runtime.ExceptionServices;
using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Desktop.Core;
using AIHappey.Vercel.Models;

internal static class ChatFileRegressionTests
{
    public static async Task RunAsync(Action<bool, string> check, string root)
    {
        await HistoryAsync(check, root);
        await FramingAsync(check);
        await FilesAsync(check, root);
    }

    private static async Task HistoryAsync(Action<bool, string> check, string root)
    {
        var prepared = await ComposerAttachments.PrepareAsync("hello", [], ServiceKind.Ai, false,
            new DocumentTextExtraction(), default);
        var output = new ConversationMessage
        {
            Message = new UIMessage { Id = "fresh-assistant", Role = Role.assistant }, Status = "streaming"
        };
        var chat = new Conversation { Messages = [new() { Message = prepared.Message }, output] };
        var store = new HistoryStore(Path.Combine(root, "fresh-chat"));
        var partition = HistoryStore.Partition("fresh-chat-account");
        await store.SaveAsync(partition, chat);
        var directory = Path.Combine(root, "fresh-chat", partition);
        var legacy = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(legacy, """{"id":"old-chat","messages":[{"message":{"id":"old-user","role":"user","parts":[]},"status":"complete"}]}""");
        var missingIds = 0;
        void Observe(object? _, FirstChanceExceptionEventArgs args)
        {
            if (args.Exception is JsonException && args.Exception.Message.Contains("missing or empty string id"))
                Interlocked.Increment(ref missingIds);
        }
        AppDomain.CurrentDomain.FirstChanceException += Observe;
        try
        {
            var assembler = new MessageAssembler(output);
            foreach (var payload in new[]
            {
                """{"type":"start","messageMetadata":{"model":"test"}}""",
                """{"type":"text-start","id":"text"}""",
                """{"type":"text-delta","id":"text","delta":"Hello"}""",
                """{"type":"text-end","id":"text"}""",
                """{"type":"finish","messageMetadata":{"usage":{"totalTokens":10}}}"""
            })
            {
                assembler.Apply(StreamEvent.Parse(payload));
                await store.SaveAsync(partition, chat);
                var listed = await store.ListAsync(partition);
                check(listed.Count == 1 && listed[0].Messages.All(m => !string.IsNullOrWhiteSpace(m.Message.Id)),
                    "chat files: fresh streaming checkpoint and history refresh preserve IDs");
            }
            var reopened = (await store.GetAsync(partition, chat.Id))!;
            reopened.Title = "renamed"; // Same read/save path used by the asynchronous naming helper.
            await store.SaveAsync(partition, reopened);
            var restored = PortableConversations.Read(PortableConversations.Write((await store.GetAsync(partition, chat.Id))!));
            check(restored.Messages[0].Message.Id == prepared.Message.Id
                && restored.Messages[1].Message.Id == "fresh-assistant" && restored.Messages[1].Text == "Hello",
                "chat files: plain-text completion, naming save and portable reopen keep exact IDs");
            check(missingIds == 0 && File.Exists(legacy),
                "chat files: legacy filename rejected before deserialization, without first-chance exceptions or deletion");
        }
        finally { AppDomain.CurrentDomain.FirstChanceException -= Observe; }
        foreach (var json in new[] { """{"role":"user","parts":[]}""", """{"id":"","role":"user","parts":[]}""" })
        {
            try { JsonSerializer.Deserialize<UIMessage>(json, PortableConversations.Json); throw new Exception("Expected invalid ID"); }
            catch (JsonException) { check(true, "chat files: missing and empty message IDs remain invalid, never regenerated"); }
        }
    }

    private static async Task FramingAsync(Action<bool, string> check)
    {
        async Task<List<string>> Read(string wire, int limit = SseReader.MaximumEventCharacters, int fragment = 7)
        {
            using var stream = new FragmentedStream(Encoding.UTF8.GetBytes(wire), fragment);
            var result = new List<string>();
            await foreach (var value in SseReader.ReadAsync(stream, default, limit)) result.Add(value);
            return result;
        }
        var framed = await Read("\uFEFF: keepalive\r\nevent: message\r\ndata: {\"text\":\"🌳 café\",\r\ndata: \"ok\":true}\r\n\r\ndata: [DONE]\r\n\r\n");
        check(framed.SequenceEqual(new[] { "{\"text\":\"🌳 café\",\n\"ok\":true}", "[DONE]" }),
            "chat files: fragmented UTF-8, BOM, CRLF, multiline data, comments and DONE framed correctly");
        check((await Read("data: a\r\rdata:b\n\ndata: tail")).SequenceEqual(new[] { "a", "b", "tail" }),
            "chat files: CR, LF, optional space and EOF without delimiter");
        check((await Read("data:\ndata:\n\n")).Single() == "\n",
            "chat files: empty data lines preserve SSE payload newlines");
        check((await Read("data:12345678\n\n", 8)).Single() == "12345678",
            "chat files: exact event safety limit accepted");
        foreach (var wire in new[] { "data:123456789\n\n", "data:1234\ndata:5678\n\n", ":" + new string('x', 32) })
        {
            try { await Read(wire, 8); throw new Exception("Expected event limit"); }
            catch (GatewayException e) { check(e.Message.Contains("8"), "chat files: line and aggregate safety limits remain bounded and actionable"); }
        }
        var binary = Convert.ToBase64String(new byte[1_600_000]);
        var payload = JsonSerializer.Serialize(new { type = "file", mediaType = "image/png", url = "data:image/png;base64," + binary });
        var large = await Read("data: " + payload + "\n\n", fragment: 8191);
        check(large.Single() == payload && StreamEvent.Parse(large.Single()).Type == "file",
            "chat files: generated image event above old two-million-character limit preserved");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        using var source = new MemoryStream("data: never\n\n"u8.ToArray());
        try
        {
            await foreach (var _ in SseReader.ReadAsync(source, canceled.Token)) { }
            throw new Exception("Expected cancellation");
        }
        catch (OperationCanceledException) { check(true, "chat files: SSE cancellation honored"); }
    }

    private sealed class FragmentedStream(byte[] bytes, int fragment) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
            => base.ReadAsync(buffer[..Math.Min(buffer.Length, fragment)], ct);
    }

    private static UIMessagePart Part(string json) => PortableConversations.Part(JsonSerializer.Deserialize<JsonElement>(json));

    private static async Task FilesAsync(Action<bool, string> check, string root)
    {
        var png = ImageRegressionTests.Png;
        var largeBinary = Convert.ToBase64String(new byte[1_600_000]);
        var toolOutput = JsonSerializer.Serialize(new { type = "tool-output-available", toolCallId = "image-tool", providerExecuted = true,
            output = new { content = new object[] { new { type = "image", mimeType = "image/png", data = largeBinary },
                new { type = "resource", resource = new { uri = "resource://report", mimeType = "text/plain", text = "report" } },
                new { type = "file", filename = "archive.zip", mimeType = "application/zip", data = "AQID" } } } });
        var direct = JsonSerializer.Serialize(new { type = "file", mediaType = "image/png", url = "data:image/png;base64," + png,
            providerMetadata = new { openai = new { generated_image_id = "generated-1", filename = "house.png" } } });
        using var handler = new WireHandler("data: {\"type\":\"tool-input-available\",\"toolCallId\":\"image-tool\",\"toolName\":\"image_generation\",\"providerExecuted\":true,\"input\":{}}\n\n"
            + "data: " + toolOutput + "\n\ndata: " + direct + "\n\ndata: {\"type\":\"finish\"}\n\ndata: [DONE]\n\n");
        using var http = new HttpClient(handler);
        var session = new DesktopSession(new TestHost(), new TestRuntime(), new());
        var client = new DesktopChatClient(session, http);
        var output = new ConversationMessage { Message = new UIMessage { Id = "image-answer", Role = Role.assistant }, Status = "streaming" };
        var conversation = new Conversation { Messages = [new() { Message = new UIMessage { Id = "image-user", Role = Role.user,
            Parts = [new TextUIPart { Text = "generate" }] } }, output] };
        await DesktopChatTurn.RunAsync(conversation, output,
            (messages, token) => client.StreamAsync(ServiceKind.Ai, "test/model", conversation.Id, messages, token),
            (_, _) => throw new Exception("No approval expected"), (_, _) => Task.CompletedTask, McpTurnSnapshot.Empty, "en", default);
        check(output.Status == "complete" && output.ErrorMessage is null && handler.Requests == 1,
            "chat files: real HTTP client and turn runner accept large embedded tool results and file parts without IDs or retries");
        var tool = output.Message.Parts.Single(PortableConversations.IsTool);
        check(PortableConversations.Element(tool).GetProperty("output").GetProperty("content")[0].GetProperty("data").GetString() == largeBinary,
            "chat files: large embedded result retained byte-for-byte through assembly");
        var rows = MessageDetails.Project(conversation.Messages);
        check(rows.Sum(row => row.Block.Images?.Count ?? 0) == 2 && rows.Any(row => row.Block.Activity),
            "chat files: direct and embedded image previews coexist with the original tool activity");
        var files = rows.SelectMany(row => row.Attachments).ToArray();
        check(files.Length == 4 && files.All(AttachmentDownloads.CanDownload) && files.Any(f => f.Name == "house.png"),
            "chat files: image-only answer has usable attachments and provider-scoped filenames");
        check(AttachmentDownloads.EmbeddedBytes(files.Single(f => f.Name == "archive.zip"))!.SequenceEqual(new byte[] { 1, 2, 3 })
            && Encoding.UTF8.GetString(AttachmentDownloads.EmbeddedBytes(files.Single(f => f.ResourceUri == "resource://report"))!) == "report",
            "chat files: embedded file data and resource text download original bytes");
        var store = new HistoryStore(Path.Combine(root, "image-chat")); var partition = HistoryStore.Partition("image-chat");
        await store.SaveAsync(partition, conversation);
        var restored = (await store.GetAsync(partition, conversation.Id))!;
        check(MessageDetails.Project(restored.Messages).Sum(row => row.Block.Images?.Count ?? 0) == 2
            && restored.Messages[^1].Message.Id == "image-answer",
            "chat files: saved images, embedded files and message identity survive reopen");

        var preview = new ConversationMessage { Message = new UIMessage { Id = "previews", Role = Role.assistant,
            Parts = [Part(direct), new TextUIPart { Text = "done" }] } };
        var first = MessageDetails.Project([preview]).Single(row => row.Block.Images?.Count > 0);
        var final = direct.Replace("generated_image_id", "generatedImageId").Replace("house.png", "final.png");
        preview.Message.Parts.Add(Part(final));
        var previewChat = new Conversation { Messages = [preview] };
        var wireBefore = PortableConversations.Write(previewChat);
        var finalRows = MessageDetails.Project([preview]);
        var imageRow = finalRows.Single(row => row.Block.Images?.Count > 0);
        check(imageRow.Block.Key == first.Block.Key && imageRow.Block.Images![0].Name == "final.png"
            && finalRows.SelectMany(row => row.Attachments).Single().Name == "final.png",
            "chat files: latest generated-image preview replaces display and download while keeping a stable row key");
        check(PortableConversations.Write(previewChat) == wireBefore && preview.Message.Parts.Count == 3,
            "chat files: collapsing previews is presentation-only, raw history and metadata remain unchanged");
        preview.Message.Parts.Add(Part(final.Replace("openai", "other")));
        check(MessageDetails.Project([preview]).Count(row => row.Block.Images?.Count > 0) == 2,
            "chat files: image IDs are scoped to provider, unrelated images never collapse");
        var jpeg = MessageDetails.Attachments(Part("""{"type":"file","mediaType":"image/jpeg","url":"/9j/"}""")).Single();
        check(AttachmentDownloads.CanDownload(jpeg) && AttachmentDownloads.EmbeddedBytes(jpeg)!.SequenceEqual(Convert.FromBase64String("/9j/")),
            "chat files: leading-slash raw JPEG base64 is binary, never a local file path");
        var onlyFile = new ConversationMessage { Message = new UIMessage { Id = "document", Role = Role.assistant,
            Parts = [Part("""{"type":"file","mediaType":"application/pdf","url":"data:application/pdf;base64,AQID","providerMetadata":{"test":{"filename":"report.pdf"}}}""")] } };
        check(MessageDetails.Project([onlyFile]).Single().Attachments.Single().Name == "report.pdf",
            "chat files: non-image file-only response retains a downloadable attachment footer");
        using var downloadHandler = new AttachmentHandler(); using var downloadHttp = new HttpClient(downloadHandler);
        using var destination = new MemoryStream();
        await AttachmentDownloads.WriteAsync(new("remote.txt", "text/plain", "https://files.example/remote.txt"), destination, downloadHttp, default);
        check(!downloadHandler.SawCredentials && Encoding.UTF8.GetString(destination.ToArray()) == "anonymous attachment",
            "chat files: remote downloads are explicit anonymous requests, not authenticated chat requests");
        foreach (var url in new[] { "file:///C:/secret.png", "https://127.0.0.1/image.png", "blob:https://files.example/id", "resource://private" })
            check(!AttachmentDownloads.CanDownload(new("blocked.png", "image/png", url)),
                "chat files: local paths, loopback, blob and MCP-only URLs are not fetched");
        foreach (var invalid in new[] { "[]", "{\"type\":\"\"}", "{\"type\":8}", "{private" })
        {
            try { StreamEvent.Parse(invalid); throw new Exception("Expected malformed event"); }
            catch (GatewayException error) { check(!error.Message.Contains("private"), "chat files: malformed stream errors actionable without payload disclosure"); }
        }
        using var badHandler = new WireHandler("data: {\"type\":\"text-delta\",\"id\":\"partial\",\"delta\":\"Partial answer\"}\n\ndata: {private\n\n");
        using var badHttp = new HttpClient(badHandler); var badClient = new DesktopChatClient(session, badHttp);
        var partial = new ConversationMessage { Message = new UIMessage { Id = "partial", Role = Role.assistant }, Status = "streaming" };
        try
        {
            await DesktopChatTurn.RunAsync(new() { Messages = [partial] }, partial,
                (messages, token) => badClient.StreamAsync(ServiceKind.Ai, "test/model", "partial-chat", messages, token),
                (_, _) => throw new Exception("No approval expected"), (_, _) => Task.CompletedTask, McpTurnSnapshot.Empty, "en", default);
            throw new Exception("Expected malformed stream failure");
        }
        catch (GatewayException)
        {
            check(partial.Status == "failed" && partial.Text == "Partial answer" && partial.ErrorMessage is not null && badHandler.Requests == 1,
                "chat files: malformed event preserves partial answer, records visible error and never retries inference");
        }
    }

    private sealed class WireHandler(string wire) : HttpMessageHandler
    {
        public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StreamContent(new FragmentedStream(Encoding.UTF8.GetBytes(wire), 8191))
                { Headers = { ContentType = new("text/event-stream") } } });
        }
    }
}
