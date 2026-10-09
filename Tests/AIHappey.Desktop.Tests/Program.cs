using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Desktop.Core;
using AIHappey.Vercel.Models;
using AIHappey_Desktop_HeaderAuth;

var tests = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAILED: " + name);
    tests++; Console.WriteLine("PASS: " + name);
}
void Reject(Action action, string name)
{
    try { action(); } catch (Exception e) when (e is InvalidOperationException or ArgumentException) { Check(true, name); return; }
    throw new Exception("FAILED: expected rejection: " + name);
}
var root = Path.Combine(Path.GetTempPath(), "AIHappey.Desktop.Tests", Guid.NewGuid().ToString("N"));
try
{
    if (args.Contains("--images-only"))
    {
        await ImageRegressionTests.RunAsync(Check, root);
        Console.WriteLine($"All {tests} image checks passed."); return;
    }
    if (args.Contains("--models-overview-only"))
    {
        await ModelsOverviewRegressionTests.RunAsync(Check, root);
        await AiModelRegressionTests.RunAsync(Check, root, new DesktopSession(new TestHost(), new TestRuntime(), new()));
        Console.WriteLine($"All {tests} model overview and preference checks passed."); return;
    }
    await ModelsOverviewRegressionTests.RunAsync(Check, root);
    if (args.Contains("--skills-only"))
    {
        await SkillRegressionTests.RunAsync(Check, root);
        Console.WriteLine($"All {tests} skill checks passed."); return;
    }
    if (args.Contains("--elicitation-only"))
    {
        await ElicitationRegressionTests.RunAsync(Check, root);
        Console.WriteLine($"All {tests} elicitation checks passed.");
        return;
    }
    TranscriptRegressionTests.Run(Check);
    await ToolApprovalRegressionTests.RunAsync(Check, root);
    await McpPresentationRegressionTests.RunAsync(Check, root);
    await ElicitationRegressionTests.RunAsync(Check, root);
    await SkillRegressionTests.RunAsync(Check, root);
    await ImageRegressionTests.RunAsync(Check, root);
    var defaults = new DesktopSettings();
    Check(defaults.Ai.Location == RuntimeLocation.Local && defaults.Agents.Location == RuntimeLocation.Local, "public defaults are local");
    Reject(() => defaults.Validate(false), "enterprise rejects local");
    foreach (var url in new[] { "http://remote.example", "https://localhost/", "https://user:secret@remote.example/", "https://remote.example/?secret=x", "file:///test" })
        Reject(() => DesktopSettings.RemoteUri(url), "unsafe remote URI rejected");
    Check(DesktopSettings.RemoteUri("https://example.com/base").AbsoluteUri == "https://example.com/base/", "remote base path preserved");
    var sse = ": heartbeat\r\nevent: message\r\ndata: {\r\ndata: \"type\":\"start\"}\r\n\r\ndata: [DONE]\n\n";
    var payloads = new List<string>();
    await foreach (var payload in SseReader.ReadAsync(new MemoryStream(Encoding.UTF8.GetBytes(sse)))) payloads.Add(payload);
    Check(payloads.Count == 2 && StreamEvent.Parse(payloads[0]).Type == "start" && payloads[1] == "[DONE]", "SSE multiline, CRLF, heartbeat and done");
    using (var canceled = new CancellationTokenSource())
    {
        canceled.Cancel();
        try { await foreach (var _ in SseReader.ReadAsync(new MemoryStream(Encoding.UTF8.GetBytes(sse)), canceled.Token)) { } throw new Exception("Expected cancellation"); }
        catch (OperationCanceledException) { Check(true, "SSE cancellation"); }
    }
    var output = new ConversationMessage { Message = new UIMessage { Id = "assistant", Role = Role.assistant }, Status = "streaming" };
    var assembler = new MessageAssembler(output);
    foreach (var payload in new[]
    {
        "{\"type\":\"start\",\"messageId\":\"server\"}",
        "{\"type\":\"text-delta\",\"id\":\"a\",\"delta\":\"Hi\"}",
        "{\"type\":\"text-delta\",\"id\":\"b\",\"delta\":\"Second\"}",
        "{\"type\":\"text-delta\",\"id\":\"a\",\"delta\":\" there\"}",
        "{\"type\":\"reasoning-delta\",\"id\":\"r\",\"delta\":\"Thinking\"}",
        "{\"type\":\"future-part\",\"content\":{\"x\":1}}",
        "{\"type\":\"data-progress\",\"data\":1,\"transient\":true}",
        "{\"type\":\"finish\"}"
    }) assembler.Apply(StreamEvent.Parse(payload));
    Check(output.Text == "Hi there\nSecond" && output.Reasoning == "Thinking", "assembly matches text/reasoning by part ID");
    Check(assembler.Finished && output.Status == "complete" && output.Message.Parts.Count(p => p.Type == "future-part") == 1, "finish, unknown preservation and transient exclusion");
    Check(output.Message.Parts.All(p => p is not TextDeltaUIMessageStreamPart), "wire deltas are not persisted as message content");
    assembler.Apply(StreamEvent.Parse("{\"type\":\"data-aihappey-debug\",\"data\":{\"secret\":\"excluded\"}}"));
    Check(!JsonSerializer.Serialize(output, JsonSerializerOptions.Web).Contains("excluded"), "provider debug events excluded from history");
    var approvalOutput = new ConversationMessage { Message = new UIMessage { Id = "approval", Role = Role.assistant } };
    var approval = new MessageAssembler(approvalOutput);
    approval.Apply(StreamEvent.Parse("{\"type\":\"tool-approval-request\",\"approvalId\":\"p\",\"toolCallId\":\"t\"}"));
    approval.Apply(StreamEvent.Parse("{\"type\":\"finish\"}"));
    Check(approval.ApprovalRequired && approval.Finished && approvalOutput.Status == "approval required", "approval retained without aborting stream or auto-executing");

    var partition = HistoryStore.Partition("host", "account", "endpoint");
    Check(partition != HistoryStore.Partition("host", "other-account", "endpoint"), "history account isolation");
    var store = new HistoryStore(root);
    var conversation = new Conversation { Messages = [output] };
    await store.SaveAsync(partition, conversation);
    var saved = (await store.ListAsync(partition)).Single();
    Check(saved.Messages[0].Text == output.Text && saved.Messages[0].Message.Parts.Count(p => p.Type == "future-part") == 1, "portable history and unknown payload round trip");
    output.Status = "streaming"; await store.SaveAsync(partition, conversation);
    Check((await store.ListAsync(partition)).Single().Messages[0].Status == "interrupted", "crash-interrupted history recovery");
    Check((await store.ListAsync(HistoryStore.Partition("other"))).Count == 0, "history partition separation");
    Reject(() => store.Delete(partition, "../escape"), "history ID traversal rejection");
    store.Delete(partition, conversation.Id);
    Check((await store.ListAsync(partition)).Count == 0, "history delete");

    var browserJson = """
    {
      "id":"52ed926c-17fa-4458-ab80-0cd9a03a29fd",
      "metadata":{"name":"Portable fixture","temperature":0.7,"mcpServers":["https://tools.example/"],"custom":{"keep":true}},
      "futureEnvelope":{"keep":[1,2]},
      "messages":[
        {"id":"sys_sdk","role":"system","parts":[{"type":"text","text":"Be helpful."}]},
        {"id":"user_sdk","role":"user","parts":[{"type":"text","text":"Find a model."}],"metadata":{"timestamp":"2026-10-06T12:00:00Z","customUser":7}},
        {"id":"assistant_sdk","role":"assistant","futureMessage":{"keep":true},"metadata":{"model":"test/model","timestamp":"2026-10-06T12:00:01Z","usage":{"totalTokens":17},"providerMetadata":{"test":{"keep":true}}},"parts":[
          {"type":"step-start"},
          {"type":"reasoning","text":"Checking available tools.","state":"done","providerMetadata":{"test":{"keep":"reasoning"}}},
          {"type":"tool-search","toolCallId":"named","state":"output-available","input":{"query":"models"},"output":{"items":["test/model"]},"callProviderMetadata":{"test":{"keep":1}},"futureToolField":"kept"},
          {"type":"dynamic-tool","toolName":"custom","toolCallId":"dynamic","state":"output-error","input":{},"errorText":"Not available","futureToolField":2},
          {"type":"text","text":"Here is the result.","state":"done","futureTextField":3},
          {"type":"file","mediaType":"image/png","url":"data:image/png;base64,AA==","filename":"image.png","futureFileField":4},
          {"type":"source-url","sourceId":"source","url":"https://example.com/","title":"Source"},
          {"type":"source-document","sourceId":"doc","mediaType":"application/pdf","title":"Document","filename":"document.pdf"},
          {"type":"data-custom","id":"data","data":{"keep":[1,2,3]}},
          {"type":"future-part","content":{"keep":true}}
        ]}
      ]
    }
    """;
    var browser = PortableConversations.Read(browserJson);
    Check(browser.Title == "Portable fixture" && browser.Target == "test/model" && browser.Messages.Count == 3, "browser name, model, roles and timestamps load without desktop hints");
    var exported = PortableConversations.Write(browser);
    using (var originalDocument = JsonDocument.Parse(browserJson))
    using (var exportedDocument = JsonDocument.Parse(exported))
    {
        var source = originalDocument.RootElement;
        var target = exportedDocument.RootElement;
        Check(target.GetProperty("messages")[2].GetProperty("role").GetString() == "assistant"
            && !target.GetProperty("messages")[2].TryGetProperty("message", out _)
            && !target.TryGetProperty("version", out _), "saved document uses shared direct UI message envelope without desktop wrappers");
        Check(JsonElement.DeepEquals(source.GetProperty("messages")[2].GetProperty("parts"), target.GetProperty("messages")[2].GetProperty("parts")), "all browser parts and arbitrary fields round-trip losslessly");
        Check(JsonElement.DeepEquals(source.GetProperty("futureEnvelope"), target.GetProperty("futureEnvelope"))
            && JsonElement.DeepEquals(source.GetProperty("messages")[2].GetProperty("futureMessage"), target.GetProperty("messages")[2].GetProperty("futureMessage")), "unknown envelope/message fields survive");
        foreach (var property in source.GetProperty("metadata").EnumerateObject())
            Check(JsonElement.DeepEquals(property.Value, target.GetProperty("metadata").GetProperty(property.Name)), "browser conversation metadata survives: " + property.Name);
        foreach (var property in source.GetProperty("messages")[2].GetProperty("metadata").EnumerateObject())
            Check(JsonElement.DeepEquals(property.Value, target.GetProperty("messages")[2].GetProperty("metadata").GetProperty(property.Name)), "browser message metadata survives: " + property.Name);
    }
    await store.SaveAsync(partition, browser);
    var reopenedBrowser = (await store.ListAsync(partition)).Single();
    Check(reopenedBrowser.Id == browser.Id && reopenedBrowser.Messages[2].Message.Parts.Count == 10, "browser UUID and named/dynamic tools reopen from atomic local history");
    reopenedBrowser.Title = "Renamed portable chat"; await store.SaveAsync(partition, reopenedBrowser);
    Check(PortableConversations.Read(PortableConversations.Write(reopenedBrowser)).Title == "Renamed portable chat", "rename uses shared metadata name");
    store.Delete(partition, browser.Id);
    var projected = TranscriptProjection.Project(browser.Messages[2]);
    Check(projected[0].Activity && projected[0].Parts.Count == 3 && projected[1].Parts[0].Type == "text", "reasoning/named/dynamic tool run groups before text in original order");
    Check(browser.Messages[2].Message.Parts.Count == 10 && PortableConversations.CanReplay(browser.Messages[0]) && PortableConversations.CanReplay(browser.Messages[2]), "projection leaves original message boundaries intact and completed portable history replays");

    var toolReply = new ConversationMessage { Message = new UIMessage { Id = "tools", Role = Role.assistant }, Status = "streaming" };
    var toolAssembler = new MessageAssembler(toolReply);
    foreach (var payload in new[]
    {
        "{\"type\":\"start-step\"}",
        "{\"type\":\"text-delta\",\"id\":\"intro\",\"delta\":\"I will check.\"}",
        "{\"type\":\"text-end\",\"id\":\"intro\",\"providerMetadata\":{\"test\":{\"end\":true}}}",
        "{\"type\":\"reasoning-delta\",\"id\":\"r\",\"delta\":\"Thinking\"}",
        "{\"type\":\"reasoning-end\",\"id\":\"r\",\"providerMetadata\":{\"test\":{\"end\":true}}}",
        "{\"type\":\"tool-input-start\",\"toolCallId\":\"t\",\"toolName\":\"search\",\"providerExecuted\":true}",
        "{\"type\":\"tool-input-delta\",\"toolCallId\":\"t\",\"inputTextDelta\":\"{\\\"query\\\":\"}",
        "{\"type\":\"tool-input-available\",\"toolCallId\":\"t\",\"toolName\":\"search\",\"input\":{\"query\":\"models\"},\"providerMetadata\":{\"test\":{\"call\":true}}}",
        "{\"type\":\"tool-output-available\",\"toolCallId\":\"t\",\"output\":{\"items\":[1]},\"preliminary\":true}",
        "{\"type\":\"tool-output-available\",\"toolCallId\":\"t\",\"output\":{\"items\":[1,2]},\"preliminary\":false,\"providerMetadata\":{\"test\":{\"result\":true}}}",
        "{\"type\":\"tool-input-available\",\"toolCallId\":\"d\",\"toolName\":\"dynamicSearch\",\"dynamic\":true,\"input\":{}}",
        "{\"type\":\"tool-output-denied\",\"toolCallId\":\"d\"}",
        "{\"type\":\"finish-step\"}",
        "{\"type\":\"start-step\"}",
        "{\"type\":\"text-delta\",\"id\":\"answer\",\"delta\":\"Done.\"}",
        "{\"type\":\"data-custom\",\"id\":\"data\",\"data\":1}",
        "{\"type\":\"data-custom\",\"id\":\"data\",\"data\":2}",
        "{\"type\":\"finish\",\"messageMetadata\":{\"model\":\"test/model\",\"usage\":{\"totalTokens\":42},\"timestamp\":\"2026-10-06T12:00:01Z\",\"futureMetadata\":{\"keep\":true}}}"
    }) toolAssembler.Apply(StreamEvent.Parse(payload));
    var toolParts = toolReply.Message.Parts.Where(PortableConversations.IsTool).ToArray();
    var namedTool = PortableConversations.Element(toolParts[0]);
    Check(toolParts.Length == 2 && toolParts[0].Type == "tool-search" && toolParts[1].Type == "dynamic-tool", "tool lifecycle consolidates by call ID into named/dynamic SDK parts");
    Check(namedTool.GetProperty("input").GetProperty("query").GetString() == "models" && namedTool.GetProperty("output").GetProperty("items").GetArrayLength() == 2
        && !namedTool.TryGetProperty("inputText", out _) && namedTool.GetProperty("state").GetString() == "output-available", "tool input delta and preliminary results replaced by final input/output");
    Check(namedTool.TryGetProperty("callProviderMetadata", out _) && namedTool.TryGetProperty("resultProviderMetadata", out _), "tool call/result provider metadata retained");
    Check(toolReply.Message.Parts.All(p => p.Type is not "text-delta" and not "reasoning-delta" and not "tool-input-start" and not "tool-output-available")
        && toolReply.Message.Parts.Count(p => p.Type == "step-start") == 2, "saved parts contain step boundaries but no transport events");
    Check(toolReply.Message.Parts.Count(p => p.Type == "data-custom") == 1 && PortableConversations.Element(toolReply.Message.Parts.Single(p => p.Type == "data-custom")).GetProperty("data").GetInt32() == 2, "persistent data IDs update in place");
    var mixedBlocks = TranscriptProjection.Project(toolReply);
    Check(mixedBlocks.Count == 4 && mixedBlocks[0].Parts[0].Type == "text" && mixedBlocks[1].Activity && mixedBlocks[1].Parts.Count == 3
        && mixedBlocks[2].Parts[0].Type == "text", "activity remains grouped between assistant text blocks across visual-only step markers");
    Check(PortableConversations.CanReplay(toolReply) && !PortableConversations.CanReplay(approvalOutput), "complete tool results replay; approval-required turns do not");
    toolReply.Status = "stopped";
    Check(!PortableConversations.CanReplay(toolReply), "stopped partial assistant output does not replay");
    toolReply.Status = "complete";
    var partial = PortableConversations.Read("{\"id\":\"sdk_opaque\",\"messages\":[{\"id\":\"p\",\"role\":\"assistant\",\"parts\":[{\"type\":\"text\",\"text\":\"Partial\",\"state\":\"streaming\"}]}]}");
    Check(partial.Messages[0].Status == "interrupted" && !PortableConversations.CanReplay(partial.Messages[0]), "browser partial state is safe even without desktop metadata");
    await store.SaveAsync(partition, partial);
    Check((await store.ListAsync(partition)).Single().Id == "sdk_opaque", "opaque browser IDs supported without treating them as filesystem paths");
    store.Delete(partition, partial.Id);

    var detailsFixture = PortableConversations.Read("""
    {"id":"details_fixture","messages":[
      {"id":"u","role":"user","parts":[{"type":"text","text":"Make files."}]},
      {"id":"a","role":"assistant","parts":[
        {"type":"text","text":"First answer."},
        {"type":"source-url","sourceId":"s1","title":"First source","url":"https://first.example/story"},
        {"type":"reasoning","text":"Preparing attachments."},
        {"type":"tool-generate","toolCallId":"t","state":"output-available","input":{"format":"txt"},"output":{
          "content":[
            {"type":"image","mimeType":"image/png","data":"aGVsbG8="},
            {"type":"audio","mimeType":"audio/mpeg","data":"aGVsbG8="},
            {"type":"resource","resource":{"uri":"mcp://reports/report.pdf","mimeType":"application/pdf","blob":"aGVsbG8="}},
            {"type":"resource","resource":{"uri":"mcp://reports/readme.txt","mimeType":"text/plain","text":"Download text."}},
            {"type":"resource_link","uri":"https://files.example/report.docx","name":"Report.docx","mimeType":"application/vnd.openxmlformats-officedocument.wordprocessingml.document"},
            {"type":"resource_link","uri":"mcp://reports/private.bin","name":"Private.bin"}
          ],"structuredContent":{"keep":{"everything":true}}
        }},
        {"type":"file","mediaType":"image/png","url":"data:image/png;base64,aGVsbG8=","filename":"direct-duplicate.png"}
      ]},
      {"id":"b","role":"assistant","parts":[
        {"type":"text","text":"Final answer."},
        {"type":"source-url","sourceId":"s2","url":"https://second.example/story"},
        {"type":"source-document","sourceId":"s3","title":"Document source","filename":"report.pdf","mediaType":"application/pdf"},
        {"type":"file","filename":"direct.txt","mediaType":"text/plain","url":"data:text/plain,hello%20world"}
      ]},
      {"id":"u2","role":"user","parts":[{"type":"text","text":"Next turn."}]},
      {"id":"c","role":"assistant","parts":[{"type":"text","text":"Unrelated answer."}]}
    ]}
    """);
    var beforeProjection = PortableConversations.Write(detailsFixture);
    var detailRows = MessageDetails.Project(detailsFixture.Messages);
    var firstAnswer = detailRows.Single(row => row.Block.Parts.FirstOrDefault()?.Type == "text" && PortableConversations.Text(row.Block.Parts[0]) == "First answer.");
    var finalAnswer = detailRows.Single(row => row.Block.Parts.FirstOrDefault()?.Type == "text" && PortableConversations.Text(row.Block.Parts[0]) == "Final answer.");
    var unrelated = detailRows.Single(row => row.Message.Message.Id == "c");
    Check(firstAnswer.Sources.Count == 0 && finalAnswer.Sources.Count == 3 && finalAnswer.Sources[0].Host == "first.example", "sources follow their next assistant answer and trailing sources join final answer");
    Check(finalAnswer.Attachments.Count == 7 && firstAnswer.Attachments.Count == 0 && unrelated.Attachments.Count == 0 && unrelated.Sources.Count == 0,
        "direct/MCP attachments deduplicate and attach only to final assistant answer in their turn");
    Check(detailRows.SelectMany(row => row.Block.Parts).All(part => part.Type is not "file" and not "source-url" and not "source-document"), "files and sources never render as raw transcript cards");
    Check(beforeProjection == PortableConversations.Write(detailsFixture), "footer extraction leaves original parts, MCP content and structuredContent unchanged");
    var blobAttachment = finalAnswer.Attachments.Single(file => file.MediaType == "application/pdf");
    Check(Encoding.UTF8.GetString(AttachmentDownloads.EmbeddedBytes(blobAttachment)!) == "hello", "MCP resource blob decodes for download without external resource execution");
    Check(Encoding.UTF8.GetString(AttachmentDownloads.EmbeddedBytes(finalAnswer.Attachments.Single(file => file.Text is not null))!) == "Download text.", "MCP text resource is downloadable as UTF-8 attachment");
    Check(Encoding.UTF8.GetString(AttachmentDownloads.EmbeddedBytes(finalAnswer.Attachments.Single(file => file.Name == "direct.txt"))!) == "hello world", "non-base64 data URL downloads decode correctly");
    Check(AttachmentDownloads.EmbeddedBytes(new("binary.bin", "application/octet-stream", "data:application/octet-stream,%FF%00"))!.SequenceEqual(new byte[] { 255, 0 }), "percent-encoded binary data URL retains arbitrary bytes");
    Check(!AttachmentDownloads.CanDownload(finalAnswer.Attachments.Single(file => file.Name == "Private.bin")), "MCP-only resource links give explicit unsupported download state");
    Check(AttachmentDownloads.SafeName("../../evil.txt", "text/plain") == "evil.txt" && AttachmentDownloads.SafeName("CON", "text/plain") == "_CON.txt", "attachment filenames remove traversal and reserved Windows names");
    foreach (var url in new[] { "file:///C:/secret.txt", "https://user:secret@files.example/report", "https://127.0.0.1/report", "https://192.168.1.1/report", "https://10.0.0.1/report", "https://169.254.169.254/latest", "https://[::1]/report", "https://internal.local/report" })
        Check(AttachmentDownloads.RemoteUri(url) is null, "unsafe attachment/source URL blocked: " + url);
    Reject(() => AttachmentDownloads.EmbeddedBytes(new("bad.bin", "application/octet-stream", "data:application/octet-stream;base64,!!!")), "invalid embedded binary data reports an error rather than crashing");
    using (var downloadHandler = new AttachmentHandler())
    using (var downloadHttp = new HttpClient(downloadHandler))
    using (var downloaded = new MemoryStream())
    {
        await AttachmentDownloads.WriteAsync(new("report.txt", "text/plain", "https://files.example/report.txt"), downloaded, downloadHttp, CancellationToken.None);
        Check(Encoding.UTF8.GetString(downloaded.ToArray()) == "anonymous attachment" && !downloadHandler.SawCredentials, "explicit remote download has no gateway/provider credentials");
        downloadHandler.Redirect = true;
        try { await AttachmentDownloads.WriteAsync(new("report.txt", "text/plain", "https://files.example/redirect"), downloaded, downloadHttp, CancellationToken.None); throw new Exception("Expected redirect refusal"); }
        catch (InvalidOperationException) { Check(true, "attachment redirects refused rather than automatically followed"); }
    }

    var fakeHost = new TestHost();
    var fakeRuntime = new TestRuntime();
    var handler = new TestHandler();
    using var http = new HttpClient(handler);
    var session = new DesktopSession(fakeHost, fakeRuntime, new DesktopSettings());
    var client = new DesktopChatClient(session, http);
    await ComposerAttachmentRegressionTests.RunAsync(Check, Reject, session);
    await CatalogRegressionTests.RunAsync(Check, Reject, root, session);
    await AiModelRegressionTests.RunAsync(Check, root, session);
    Check((await client.ListAsync(ServiceKind.Agents, CancellationToken.None)).Single().Id == "agent", "typed agent catalog");
    var user = new UIMessage { Id = "user", Role = Role.user, Parts = [new TextUIPart { Text = "hello" }] };
    foreach (var service in new[] { ServiceKind.Ai, ServiceKind.Agents })
    {
        var events = new List<StreamEvent>();
        await foreach (var item in client.StreamAsync(service, "target", "conversation", [user], CancellationToken.None)) events.Add(item);
        Check(events.Count == 2 && handler.LastBody!.Contains("\"messages\"") && !handler.LastBody.Contains("test-secret"), "shared typed request and credentials excluded from body: " + service);
        Check(fakeRuntime.LastService == service && fakeHost.LastService == service && handler.SawAuth, "location and host authentication are independent: " + service);
    }
    await foreach (var _ in client.StreamAsync(ServiceKind.Ai, "target", browser.Id, browser.Messages.Where(PortableConversations.CanReplay).Select(m => m.Message).ToList(), CancellationToken.None)) { }
    using (var body = JsonDocument.Parse(handler.LastBody!))
        Check(body.RootElement.GetProperty("messages")[0].GetProperty("role").GetString() == "system"
            && body.RootElement.GetProperty("messages")[2].GetProperty("parts")[2].GetProperty("type").GetString() == "tool-search", "normal completed browser conversation continues using canonical messages");
    handler.Failure = true;
    try { await foreach (var _ in client.StreamAsync(ServiceKind.Ai, "target", "conversation", [user], CancellationToken.None)) { } throw new Exception("Expected failure"); }
    catch (GatewayException e) { Check(!e.Message.Contains("sensitive") && handler.FailureCount == 1, "sanitized failure without inference retry"); }

    var missing = new ManagedLocalRuntime(Path.Combine(root, "missing"), root);
    try { await missing.ResolveAsync(ServiceKind.Ai, new(), CancellationToken.None); throw new Exception("Expected missing runtime"); }
    catch (InvalidOperationException) { Check(true, "missing managed runtime diagnosis"); }
    await missing.DisposeAsync();
    if (args.Length == 1)
    {
        var local = new ManagedLocalRuntime(Path.GetFullPath(args[0]), root);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var ai = await local.ResolveAsync(ServiceKind.Ai, new(), timeout.Token);
            var agents = await local.ResolveAsync(ServiceKind.Agents, new(), timeout.Token);
            Check(ai.IsLoopback && agents.IsLoopback && ai.Port != agents.Port, "bundled runtime startup and independent loopback ports");
            using var probe = new HttpClient();
            var catalog = await probe.GetStringAsync(new Uri(agents, "v1/models"), timeout.Token);
            Check(catalog.Contains("OpenAIAgent"), "bundled existing agent definitions");
            var localSession = new DesktopSession(new NoAuthHost(), local, new DesktopSettings());
            var realClient = new DesktopChatClient(localSession, probe);
            var turns = new List<UIMessage>();
            foreach (var prompt in new[] { "Desktop integration first turn", "Desktop integration second turn" })
            {
                turns.Add(new UIMessage { Id = Guid.NewGuid().ToString("N"), Role = Role.user, Parts = [new TextUIPart { Text = prompt }] });
                var reply = new ConversationMessage { Message = new UIMessage { Id = Guid.NewGuid().ToString("N"), Role = Role.assistant } };
                var realAssembler = new MessageAssembler(reply);
                await foreach (var item in realClient.StreamAsync(ServiceKind.Ai, "echo/Echo", "desktop-integration", turns, timeout.Token)) realAssembler.Apply(item);
                Check(realAssembler.Finished && reply.Text == prompt, "actual bundled gateway typed streaming turn");
                turns.Add(reply.Message);
            }
            await local.DisposeAsync();
            await Task.Delay(200);
            try { await probe.GetAsync(new Uri(ai, "v1/models"), timeout.Token); throw new Exception("AI process survived shutdown"); }
            catch (HttpRequestException) { Check(true, "owned runtime cleanup"); }
        }
        finally { await local.DisposeAsync(); }
    }
    Console.WriteLine($"{tests} checks passed.");
}
finally { if (Directory.Exists(root)) Directory.Delete(root, true); }

sealed class TestHost : IDesktopHost
{
    public string ProfileId => "test";
    public bool AllowLocal => true;
    public string AccountLabel => "test";
    public string HistoryIdentity => "test";
    public ServiceKind LastService;
    public Task AuthenticateAsync(HttpRequestMessage request, ServiceKind service, CancellationToken ct)
    { LastService = service; request.Headers.Add("X-Test-Key", "test-secret"); return Task.CompletedTask; }
    public Task ManageAccountAsync(object root, CancellationToken ct) => Task.CompletedTask;
}
sealed class NoAuthHost : IDesktopHost
{
    public string ProfileId => "integration";
    public bool AllowLocal => true;
    public string AccountLabel => "integration";
    public string HistoryIdentity => "integration";
    public Task AuthenticateAsync(HttpRequestMessage request, ServiceKind service, CancellationToken ct) => Task.CompletedTask;
    public Task ManageAccountAsync(object root, CancellationToken ct) => Task.CompletedTask;
}
sealed class TestRuntime : IRuntimeResolver
{
    public ServiceKind LastService;
    public Task<Uri> ResolveAsync(ServiceKind service, DesktopSettings settings, CancellationToken ct)
    { LastService = service; return Task.FromResult(new Uri("http://127.0.0.1:9876/")); }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
sealed class TestHandler : HttpMessageHandler
{
    public string? LastBody;
    public bool SawAuth, Failure;
    public int FailureCount;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        SawAuth = request.Headers.Contains("X-Test-Key");
        LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
        if (Failure) { FailureCount++; return new(HttpStatusCode.InternalServerError) { Content = new StringContent("sensitive-provider-error") }; }
        return new(HttpStatusCode.OK)
        {
            Content = request.Method == HttpMethod.Get
                ? new StringContent("{\"data\":[{\"id\":\"agent\",\"name\":\"Agent\"}]}", Encoding.UTF8, "application/json")
                : new StringContent("data: {\"type\":\"text-delta\",\"id\":\"x\",\"delta\":\"hello\"}\n\ndata: {\"type\":\"finish\"}\n\ndata: [DONE]\n\n", Encoding.UTF8, "text/event-stream")
        };
    }
}

sealed class AttachmentHandler : HttpMessageHandler
{
    public bool SawCredentials, Redirect;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        SawCredentials = request.Headers.Any(header => header.Key.Contains("Key", StringComparison.OrdinalIgnoreCase) || header.Key == "Authorization" || header.Key == "Cookie");
        return Task.FromResult(new HttpResponseMessage(Redirect ? HttpStatusCode.Redirect : HttpStatusCode.OK) { Content = new StringContent("anonymous attachment") });
    }
}
