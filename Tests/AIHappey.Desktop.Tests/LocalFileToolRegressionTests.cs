using System.Text;
using System.Text.Json;
using AIHappey.Desktop.Core;
using AIHappey.Vercel.Models;
using ModelContextProtocol.Protocol;
using Role = AIHappey.Vercel.Models.Role;

internal static class LocalFileToolRegressionTests
{
    private static JsonElement Args(object value) => JsonSerializer.SerializeToElement(value);
    private static JsonElement Data(JsonElement value) => value.GetProperty("structuredContent");
    private static bool Error(JsonElement value) => value.GetProperty("isError").GetBoolean();
    private static void Envelope(Action<bool, string> check, JsonElement result, string label)
    {
        var parsed = JsonSerializer.Deserialize<CallToolResult>(result)!;
        check(parsed.Content.Count > 0 && parsed.Content[0] is TextContentBlock && parsed.IsError != true
            && result.GetProperty("structuredContent").ValueKind == JsonValueKind.Object, label + ": valid SDK CallToolResult with text and structured object");
    }

    public static async Task RunAsync(Action<bool, string> check, string root, byte[] pdf)
    {
        var folder = Path.Combine(root, "local-documents"); Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "notes.txt"); await File.WriteAllTextAsync(path, "héllo\r\n\r\nthree\rfour\nfive\n", new UTF8Encoding(false));
        var tools = new LocalFileTools(new DocumentTextExtraction());
        var first = await tools.CallAsync(LocalFileTools.ToolName, Args(new { path, maxLines = 2 }), default);
        Envelope(check, first, "files");
        var page = Data(first);
        check(page.GetProperty("text").GetString() == "héllo\n" && page.GetProperty("totalLines").GetInt32() == 5
            && page.GetProperty("startLine").GetInt32() == 1 && page.GetProperty("endLine").GetInt32() == 2
            && page.GetProperty("nextLine").GetInt32() == 3 && page.GetProperty("truncated").GetBoolean(),
            "files: 1-based complete-line paging preserves blank lines and normalizes CR/LF without phantom trailing line");
        var next = Data(await tools.CallAsync(LocalFileTools.ToolName, Args(new { path, startLine = 3, maxLines = 20 }), default));
        check(next.GetProperty("text").GetString() == "three\nfour\nfive" && next.GetProperty("endLine").GetInt32() == 5
            && !next.GetProperty("truncated").GetBoolean() && !next.TryGetProperty("nextLine", out _), "files: subsequent line page reaches EOF with no skipped lines");
        var beyond = Data(await tools.CallAsync(LocalFileTools.ToolName, Args(new { path, startLine = 90 }), default));
        check(beyond.GetProperty("lineCount").GetInt32() == 0 && beyond.GetProperty("text").GetString() == ""
            && !beyond.GetProperty("truncated").GetBoolean(), "files: beyond EOF is a successful empty page");
        check(Data(await tools.CallAsync(LocalFileTools.ToolName, Args(new { path, maxLines = 9000 }), default)).GetProperty("maxLines").GetInt32() == 2000,
            "files: maxLines capped at 2000");
        foreach (var arguments in new[] { Args(new { path, startLine = 0 }), Args(new { path, startLine = 1.5 }), Args(new { path, maxLines = "2" }), Args(new { path, maxLines = -1 }) })
            check(Error(await tools.CallAsync(LocalFileTools.ToolName, arguments, default)), "files: malformed paging safely rejected");
        foreach (var extension in new[] { ".md", ".json", ".yaml", ".xml", ".cs", ".log", ".csv" })
        {
            var file = Path.Combine(folder, "document" + extension); await File.WriteAllTextAsync(file, "recognized ✓");
            check(Data(await tools.CallAsync(LocalFileTools.ToolName, Args(new { path = file }), default)).GetProperty("text").GetString() == "recognized ✓",
                "files: shared extraction recognizes " + extension);
        }
        var unicode = Path.Combine(folder, "bom.txt"); await File.WriteAllBytesAsync(unicode, Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("BOM ✓")).ToArray());
        check(Data(await tools.CallAsync(LocalFileTools.ToolName, Args(new { path = unicode }), default)).GetProperty("text").GetString() == "BOM ✓", "files: shared BOM-aware decoding");
        var document = Path.Combine(folder, "report.pdf"); await File.WriteAllBytesAsync(document, pdf);
        check(Data(await tools.CallAsync(LocalFileTools.ToolName, Args(new { path = document }), default)).GetProperty("text").GetString()!.Contains("PDF local tools"),
            "files: real PDF text uses the existing extraction handler");
        var empty = Path.Combine(folder, "empty.txt"); await File.WriteAllBytesAsync(empty, []);
        check(Data(await tools.CallAsync(LocalFileTools.ToolName, Args(new { path = empty }), default)).GetProperty("totalLines").GetInt32() == 0,
            "files: empty text file returns an empty page");
        var invalid = Path.Combine(folder, "invalid.txt");
        // Strict UTF-8 without a BOM must never be decoded with replacement characters.
        await File.WriteAllBytesAsync(invalid, [0xff, 0x00]);
        check(Error(await tools.CallAsync(LocalFileTools.ToolName, Args(new { path = invalid }), default)), "files: invalid UTF-8 rejected, never replacement-decoded");
        var binary = Path.Combine(folder, "binary.bin"); await File.WriteAllBytesAsync(binary, [0, 1, 2]);
        check(Error(await tools.CallAsync(LocalFileTools.ToolName, Args(new { path = binary }), default)), "files: unsupported binary not guessed as text");
        var large = Path.Combine(folder, "large.txt");
        await using (var stream = File.Create(large)) stream.SetLength(ComposerAttachments.MaximumFileBytes + 1L);
        check(Error(await tools.CallAsync(LocalFileTools.ToolName, Args(new { path = large }), default)), "files: oversized input rejected before allocation/extraction");
        var badPaths = new[] { "notes.txt", @"C:notes.txt", @"\notes.txt", @"\\server\share\notes.txt", "https://example.test/notes.txt",
            "file:///C:/notes.txt", @"\\?\C:\notes.txt", @"\\.\pipe\notes.txt", path + ":secret", @"C:\CON.txt", @"C:\NUL.txt", @"C:\AUX.txt" };
        foreach (var bad in badPaths)
            check(Error(await tools.CallAsync(LocalFileTools.ToolName, Args(new { path = bad }), default)), "files: rejects non-local/device/ADS path " + bad);
        check(Error(await tools.CallAsync(LocalFileTools.ToolName, Args(new { path = Path.Combine(folder, "missing.txt") }), default)), "files: safe missing-file result");
        var directory = Path.Combine(folder, "directory.txt"); Directory.CreateDirectory(directory);
        check(Error(await tools.CallAsync(LocalFileTools.ToolName, Args(new { path = directory }), default)), "files: directories cannot be read as files");
        var linked = Path.Combine(folder, "linked.txt");
        try
        {
            Directory.CreateSymbolicLink(linked, folder);
            check(Error(await tools.CallAsync(LocalFileTools.ToolName, Args(new { path = Path.Combine(linked, "notes.txt") }), default)), "files: parent symbolic link cannot bypass local-only policy");
            Directory.Delete(linked);
        }
        catch (UnauthorizedAccessException) { Console.WriteLine("SKIP: symbolic-link creation requires developer mode or elevation"); }

        var bounded = LocalFileTools.Page(new string('x', 30000) + "\n" + new string('y', 30000));
        check(bounded.LineCount == 1 && bounded.NextLine == 2 && bounded.Truncated && LocalFileTools.Page(new string('x', 30000) + "\n" + new string('y', 30000), 2).Text.Length == 30000,
            "files: response ceiling stops before an entire line and continuation does not skip it");
        try { LocalFileTools.Page(new string('x', LocalFileTools.MaximumPageCharacters + 1)); check(false, "files: expected oversized line error"); }
        catch (LocalToolException) { check(true, "files: oversized single line rejected rather than silently partially returned"); }
        var adversarial = DesktopLocalTools.Result(new { text = LocalFileTools.Page(new string('\u0001', LocalFileTools.MaximumPageCharacters)).Text });
        check(adversarial.GetRawText().Length < 2_000_000, "files: worst-case JSON escaping plus text fallback stays under chat result ceiling");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Cancelled(check, () => tools.CallAsync(LocalFileTools.ToolName, Args(new { path }), cancelled.Token), "files: cancellation propagates");
        await Cancelled(check, () => new LocalFileTools(new(), () => false).CallAsync(LocalFileTools.ToolName, Args(new { path }), default), "files: stale account/shutdown guard propagates");
        await SearchAsync(check, folder, path, tools);

        // Opt-in live coverage: requires Windows Search and a functioning local index.
        if (Environment.GetEnvironmentVariable("AIHAPPEY_TEST_WINDOWS_SEARCH") == "1")
        {
            var native = new LocalWindowsSearchTools(new NativeWindowsSearch(Console.WriteLine));
            var result = await native.CallAsync(LocalWindowsSearchTools.ToolName, Args(new { query = "ext:txt", limit = 2 }), default);
            check(!Error(result), "windows search: live native COM/AQS/OLE DB query succeeded");
            if (!Error(result)) Envelope(check, result, "windows search live");
            var scoped = await native.CallAsync(LocalWindowsSearchTools.ToolName, Args(new { query = "kind:document", folder, limit = 2 }), default);
            check(!Error(scoped), "windows search: live recursive SCOPE and native kind keyword succeeded (zero matches is allowed)");
        }
    }

    private static async Task SearchAsync(Action<bool, string> check, string folder, string path, LocalFileTools files)
    {
        LocalWindowsSearchRequest? request = null; var calls = 0;
        var adapter = new SearchStub((value, _) =>
        {
            request = value; calls++;
            return Task.FromResult(new LocalWindowsSearchResults([new(path, "ignored", 12, DateTime.UtcNow, ".txt", 1000),
                new(@"\\server\secret\note.txt", "secret", null, null, null, null), new(Path.Combine(folder + "-other", "foreign.txt"), "foreign", null, null, null, null)], true));
        });
        var tools = new LocalWindowsSearchTools(adapter);
        var result = await tools.CallAsync(LocalWindowsSearchTools.ToolName, Args(new { query = "  kind:document budget  ", folder, limit = 800 }), default);
        Envelope(check, result, "windows search");
        check(request is { Query: "kind:document budget", Limit: 100 } && request.Folder == folder && Data(result).GetProperty("results").GetArrayLength() == 1
            && Data(result).GetProperty("hasMore").GetBoolean() && Data(result).GetProperty("indexedOnly").GetBoolean(),
            "windows search: AQS forwarded, result cap enforced, network/out-of-scope matches filtered, no false exact total");
        await tools.CallAsync(LocalWindowsSearchTools.ToolName, Args(new { query = "ext:pdf" }), default);
        check(request!.Limit == 20 && request.Folder is null, "windows search: unscoped native query defaults to 20");
        var before = calls;
        foreach (var args in new[] { Args(new { query = " " }), Args(new { query = new string('x', 4097) }), Args(new { query = "x\n" }),
            Args(new { query = "x", limit = 1.2 }), Args(new { query = "x", folder = "relative" }), Args(new { query = "x", folder = @"\\server\share" }) })
            check(Error(await tools.CallAsync(LocalWindowsSearchTools.ToolName, args, default)), "windows search: invalid input produces a safe tool error");
        check(calls == before, "windows search: validation failures never invoke the native adapter");
        var escaped = NativeWindowsSearch.Restrictions(Path.Combine(folder, "O'Brien"));
        check(escaped.Contains("SCOPE='file:///") && escaped.Contains("O''Brien") && !escaped.Contains("budget"), "windows search: recursive SQL scope escapes literal apostrophes, never interpolates AQS");
        check(NativeWindowsSearch.Restrictions(null).Contains("System.ItemUrl LIKE 'file:%'"), "windows search: SQL restrictions exclude non-file index protocols");
        var unavailable = new LocalWindowsSearchTools(new SearchStub((_, _) => throw new LocalToolException("Windows Search unavailable.")));
        check(Error(await unavailable.CallAsync(LocalWindowsSearchTools.ToolName, Args(new { query = "x" }), default)), "windows search: unavailable service is an MCP error, not a fabricated empty search");
        var failure = new LocalWindowsSearchTools(new SearchStub((_, _) => throw new IOException("secret internal database")));
        check(!(await failure.CallAsync(LocalWindowsSearchTools.ToolName, Args(new { query = "x" }), default)).GetRawText().Contains("secret"), "windows search: arbitrary provider failure is sanitized");
        using var ct = new CancellationTokenSource(); ct.Cancel();
        await Cancelled(check, () => tools.CallAsync(LocalWindowsSearchTools.ToolName, Args(new { query = "x" }), ct.Token), "windows search: cancelled request never searches");
        await Cancelled(check, () => new LocalWindowsSearchTools(adapter, () => false).CallAsync(LocalWindowsSearchTools.ToolName, Args(new { query = "x" }), default), "windows search: stale turn cannot search");
        var current = true;
        var switching = new LocalWindowsSearchTools(new SearchStub((_, _) => { current = false; return Task.FromResult(new LocalWindowsSearchResults([], false)); }), () => current);
        await Cancelled(check, () => switching.CallAsync(LocalWindowsSearchTools.ToolName, Args(new { query = "x" }), default), "windows search: result withheld when account changes while searching");

        var snapshot = McpTurnSnapshot.Empty;
        DesktopLocalTools.Register(snapshot, DesktopLocalTools.WindowsSearch, tools.CallAsync);
        check(snapshot.Tools.Count == 1 && !snapshot.Contains(LocalFileTools.ToolName), "windows search: enabled plugin does not implicitly grant file reads");
        DesktopLocalTools.Register(snapshot, DesktopLocalTools.Files, files.CallAsync);
        var searchResult = await snapshot.CallAsync(LocalWindowsSearchTools.ToolName, Args(new { query = "notes", folder }), "search", "en", default);
        var foundPath = Data(searchResult).GetProperty("results")[0].GetProperty("path").GetString()!;
        Envelope(check, await snapshot.CallAsync(LocalFileTools.ToolName, Args(new { path = foundPath }), "read", "en", default), "search-to-file");
        var collision = McpTurnSnapshot.Create([(new McpCatalogItem("server", "Server", "", "https://example.test/"),
            new McpDiscovery(Args(new { }), Args(new { }), null, [DesktopLocalTools.Definition(LocalFileTools.ToolName), DesktopLocalTools.Definition(LocalWindowsSearchTools.ToolName)]),
            (Func<string, JsonElement, string, string, CancellationToken, Task<JsonElement>>)((_, _, _, _, _) => Task.FromResult(DesktopLocalTools.Result(new { remote = true }))))]);
        DesktopLocalTools.Register(collision, DesktopLocalTools.WindowsSearch, tools.CallAsync); DesktopLocalTools.Register(collision, DesktopLocalTools.Files, files.CallAsync);
        check(collision.Tools.Count == 8 && collision.Tools.Select(t => t.GetProperty("name").GetString()).Distinct().Count() == 8,
            "windows search/files: native names stay stable and colliding MCP routes are aliased");
        await Continuation(check, snapshot, folder, path);
    }

    private static async Task Continuation(Action<bool, string> check, McpTurnSnapshot snapshot, string folder, string path)
    {
        var chat = new Conversation(); var output = new ConversationMessage { Message = new UIMessage { Id = "output", Role = Role.assistant }, Status = "streaming" };
        chat.Messages.Add(output); var rounds = 0;
        async IAsyncEnumerable<StreamEvent> Stream(List<UIMessage> messages, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield(); rounds++;
            if (rounds == 1) yield return StreamEvent.Parse(JsonSerializer.Serialize(new { type = "tool-input-available", toolCallId = "search", toolName = LocalWindowsSearchTools.ToolName, input = new { query = "notes", folder } }));
            else if (rounds == 2) yield return StreamEvent.Parse(JsonSerializer.Serialize(new { type = "tool-input-available", toolCallId = "read", toolName = LocalFileTools.ToolName, input = new { path, startLine = 3, maxLines = 2 } }));
            else yield return StreamEvent.Parse("""{"type":"text-delta","id":"text","delta":"Read the search result."}""");
            yield return StreamEvent.Parse("""{"type":"finish"}""");
        }
        await DesktopChatTurn.RunAsync(chat, output, Stream, (_, _) => Task.FromResult(new ToolApprovalDecision(true)), (_, _) => Task.CompletedTask, snapshot, "en", default);
        check(rounds == 3 && output.Text == "Read the search result." && output.Message.Parts.Count(p => PortableConversations.IsTool(p)
            && PortableConversations.String(PortableConversations.Element(p), "state") == "output-available") == 2,
            "windows search/files: existing chat pipeline continues search then line-paged file read then answer");
    }
    private static async Task Cancelled(Action<bool, string> check, Func<Task<JsonElement>> call, string label)
    {
        try { await call(); check(false, label); }
        catch (OperationCanceledException) { check(true, label); }
    }
    private sealed class SearchStub(Func<LocalWindowsSearchRequest, CancellationToken, Task<LocalWindowsSearchResults>> call) : ILocalWindowsSearch
    { public Task<LocalWindowsSearchResults> SearchAsync(LocalWindowsSearchRequest request, CancellationToken ct) => call(request, ct); }
}
