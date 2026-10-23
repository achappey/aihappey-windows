using System.Text;
using System.Text.Json;
using AIHappey.Desktop.Core;
using AIHappey.Vercel.Models;

internal static class WindowsToolRegressionTests
{
    private static JsonElement Args(object value) => JsonSerializer.SerializeToElement(value);
    private static JsonElement Data(JsonElement value) => value.GetProperty("structuredContent");
    private static bool Error(JsonElement value) => value.GetProperty("isError").GetBoolean();

    public static async Task RunAsync(Action<bool, string> check, string root)
    {
        var ids = new[] { DesktopLocalTools.Clipboard, DesktopLocalTools.WindowInspector, DesktopLocalTools.Ocr };
        var definitions = DesktopLocalTools.Plugins.Where(p => ids.Contains(p.Id)).SelectMany(p => p.Tools).ToArray();
        check(definitions.Length == 17 && definitions.Select(t => t.GetProperty("name").GetString()).Distinct().Count() == 17,
            "Windows tools: three existing plugins expose seventeen unique contracts including capture and management");
        check(definitions.All(t => t.GetProperty("name").GetString()!.StartsWith("local_", StringComparison.Ordinal)
            && !t.GetProperty("inputSchema").GetProperty("additionalProperties").GetBoolean()
            && !t.GetProperty("annotations").GetProperty("openWorldHint").GetBoolean()), "Windows tools: local naming, closed schemas and no open-world access");
        var mutations = new[] { LocalClipboardTools.SetTool, LocalClipboardTools.SetImageTool, LocalClipboardTools.SetFilesTool,
            LocalWindowTools.ActivateTool, LocalWindowTools.MinimizeTool, LocalWindowTools.RestoreTool, LocalWindowTools.RepositionTool };
        check(definitions.All(t => t.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean() != mutations.Contains(t.GetProperty("name").GetString()))
            && new[] { LocalClipboardTools.SetTool, LocalClipboardTools.SetImageTool, LocalClipboardTools.SetFilesTool }
                .All(n => DesktopLocalTools.Definition(n).GetProperty("annotations").GetProperty("destructiveHint").GetBoolean()),
            "Windows tools: clipboard replacement and window mutations have accurate annotations");
        check(ids.All(id => !new ChatPreferences().PluginEnabled(id) && !JsonSerializer.Deserialize<ChatPreferences>("{}")!.PluginEnabled(id)),
            "Windows tools: new and migrated preferences default all three off");
        var settingsPath = Path.Combine(root, "windows-tool-settings");
        await SettingsStore.SaveAsync(settingsPath, new() { Chat = new() { ActivePlugins = ids.ToList() } });
        check((await SettingsStore.LoadAsync(settingsPath, new())).Chat.ActivePlugins.SequenceEqual(ids), "Windows tools: opt-in selections persist");
        foreach (var plugin in DesktopLocalTools.Plugins.Where(p => ids.Contains(p.Id)))
        {
            var snapshot = McpTurnSnapshot.Empty; var calls = 0;
            DesktopLocalTools.Register(snapshot, plugin.Id, (_, _, _) => { calls++; return Task.FromResult(DesktopLocalTools.Result(new { ok = true })); });
            check(snapshot.Tools.Count == plugin.Tools.Count && calls == 0, "Windows tools: independent lazy registration for " + plugin.Id);
        }
        await ClipboardAsync(check);
        await WindowsAsync(check);
        await OcrAsync(check, root);
        await ReviewAsync(check);
        await WindowsDesktopToolRegressionTests.RunAsync(check, root);
    }

    private static async Task ClipboardAsync(Action<bool, string> check)
    {
        string? clipboard = null; var writes = 0; var current = true;
        var tools = new LocalClipboardTools(_ => Task.FromResult(clipboard), (text, _) => { clipboard = text; writes++; return Task.CompletedTask; }, () => current);
        var empty = Data(await tools.CallAsync(LocalClipboardTools.GetTool, Args(new { }), default));
        check(!empty.GetProperty("hasText").GetBoolean() && empty.GetProperty("text").GetString() == "", "clipboard: non-text/empty clipboard is explicit");
        await tools.CallAsync(LocalClipboardTools.SetTool, Args(new { text = "héllo\n世界" }), default);
        check(Data(await tools.CallAsync(LocalClipboardTools.GetTool, Args(new { }), default)).GetProperty("text").GetString() == "héllo\n世界", "clipboard: Unicode and line breaks round trip");
        var copied = await tools.CallAsync(LocalClipboardTools.SetTool, Args(new { text = "" }), default);
        check(!Error(copied) && writes == 2 && clipboard == "", "clipboard: empty string is valid and write executes once");
        foreach (var invalid in new[] { Args(new { text = new string('x', 50_001) }), Args(new { text = "nul\0value" }), Args(new { text = 1 }), Args(new { }) })
            check(Error(await tools.CallAsync(LocalClipboardTools.SetTool, invalid, default)) && writes == 2, "clipboard: invalid input never writes");
        clipboard = new string('x', 60_000);
        var bounded = Data(await tools.CallAsync(LocalClipboardTools.GetTool, Args(new { }), default));
        check(bounded.GetProperty("text").GetString()!.Length == 50_000 && bounded.GetProperty("truncated").GetBoolean(), "clipboard: long read is bounded and marked truncated");
        current = false;
        await CanceledAsync(() => tools.CallAsync(LocalClipboardTools.SetTool, Args(new { text = "stale" }), default), check, "clipboard: stale session cannot write");
        check(writes == 2, "clipboard: stale handler left clipboard unchanged");
        var faulted = new LocalClipboardTools(_ => throw new IOException("private clipboard secret"), (_, _) => Task.CompletedTask);
        var error = await faulted.CallAsync(LocalClipboardTools.GetTool, Args(new { }), default);
        check(Error(error) && !error.GetRawText().Contains("secret"), "clipboard: arbitrary native failure is sanitized");
    }

    private static async Task WindowsAsync(Action<bool, string> check)
    {
        var window = new LocalWindowInfo("0x123", 42, "fixture", "Fixture window", "FixtureClass", false);
        LocalWindowInspectionRequest? observed = null; var inspections = 0; var current = true;
        var tools = new LocalWindowTools(_ => Task.FromResult<LocalWindowInfo?>(window),
            (_, _) => Task.FromResult(new LocalWindowList([window, window], false)),
            (request, _) => { observed = request; inspections++; return Task.FromResult(new LocalWindowInspection(window, [new(0, null, 0, true, false, 50032, "Fixture")], false, false)); }, () => current);
        check(Data(await tools.CallAsync(LocalWindowTools.ActiveTool, Args(new { }), default)).GetProperty("window").GetProperty("windowHandle").GetString() == "0x123", "windows: handles are opaque strings, not lossy JSON numbers");
        var list = Data(await tools.CallAsync(LocalWindowTools.ListTool, Args(new { limit = 1 }), default));
        check(list.GetProperty("count").GetInt32() == 1 && list.GetProperty("hasMore").GetBoolean(), "windows: list obeys requested limit");
        await tools.CallAsync(LocalWindowTools.InspectTool, Args(new { windowHandle = "0x123", processId = 42, maxNodes = 900, maxDepth = 99 }), default);
        check(observed is { MaxDepth: 8, MaxNodes: 500, ProcessId: 42 } && inspections == 1, "windows: inspection requires identity and caps depth/nodes");
        foreach (var invalid in new[] { "123", "0x0", "0xGG", "0xFFFFFFFFFFFFFFFF", "0x10000000000000000" })
            check(Error(await tools.CallAsync(LocalWindowTools.InspectTool, Args(new { windowHandle = invalid, processId = 42 }), default)), "windows: invalid handle rejected " + invalid);
        foreach (var invalid in new[] { Args(new { windowHandle = "0x123" }), Args(new { windowHandle = "0x123", processId = "42" }), Args(new { windowHandle = "0x123", processId = 0 }) })
            check(Error(await tools.CallAsync(LocalWindowTools.InspectTool, invalid, default)) && inspections == 1, "windows: missing/invalid PID never inspects");
        current = false;
        await CanceledAsync(() => tools.CallAsync(LocalWindowTools.ActiveTool, Args(new { }), default), check, "windows: stale session cannot enumerate");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await CanceledAsync(() => tools.CallAsync(LocalWindowTools.ListTool, Args(new { }), canceled.Token), check, "windows: pre-canceled call performs no work");
        // Native negative path never inspects a user's actual window.
        var native = new NativeWindowsWindowInspector();
        try { await native.InspectAsync(new("0x1", int.MaxValue, 1, 1), default); check(false, "windows: invalid native target rejected"); }
        catch (LocalToolException) { check(true, "windows: closed/invalid native target rejected safely"); }
    }

    private static async Task OcrAsync(Action<bool, string> check, string root)
    {
        var tools = new LocalOcrTools(); var directory = Path.Combine(root, "ocr"); Directory.CreateDirectory(directory);
        foreach (var path in new[] { "relative.png", @"\\server\share\private.png", @"\\?\C:\private.png", "https://example.com/private.png", Path.Combine(directory, "document.pdf") })
            check(Error(await tools.CallAsync(LocalOcrTools.RecognizeTool, Args(new { path }), default)), "OCR: rejects unsupported/non-local input " + path);
        check(Error(await tools.CallAsync(LocalOcrTools.RecognizeTool, Args(new { path = Path.Combine(directory, "missing.png") }), default)), "OCR: missing file returns safe error");
        var oversized = Path.Combine(directory, "large.png");
        using (var file = File.Create(oversized)) file.SetLength(LocalOcrTools.MaximumFileBytes + 1);
        check(Error(await tools.CallAsync(LocalOcrTools.RecognizeTool, Args(new { path = oversized }), default)), "OCR: file size is rejected before decode");
        var broken = Path.Combine(directory, "broken.png"); await File.WriteAllTextAsync(broken, "not an image");
        check(Error(await tools.CallAsync(LocalOcrTools.RecognizeTool, Args(new { path = broken }), default)), "OCR: corrupt image produces a safe error");
        foreach (var tag in new object[] { "", new string('x', 86), 123 })
            check(Error(await tools.CallAsync(LocalOcrTools.RecognizeTool, Args(new { path = broken, language = tag }), default)), "OCR: malformed language input rejected");
        await CanceledAsync(() => new LocalOcrTools(() => false).CallAsync(LocalOcrTools.LanguagesTool, Args(new { }), default), check, "OCR: stale session cannot access native APIs");
        var languagesResult = await tools.CallAsync(LocalOcrTools.LanguagesTool, Args(new { }), default);
        check(!Error(languagesResult), "OCR: installed recognition languages queried locally");
        var languages = Data(languagesResult).GetProperty("languages");
        var image = Path.Combine(directory, "text.bmp"); await File.WriteAllBytesAsync(image, TextBitmap());
        if (languages.GetArrayLength() == 0)
        {
            check(Error(await tools.CallAsync(LocalOcrTools.RecognizeTool, Args(new { path = image }), default)), "OCR: missing language packs fail without an external fallback");
            return;
        }
        var tagName = languages.EnumerateArray().Select(l => l.GetProperty("language").GetString()!).FirstOrDefault(l => l.StartsWith("en", StringComparison.OrdinalIgnoreCase))
            ?? languages[0].GetProperty("language").GetString()!;
        var recognized = await tools.CallAsync(LocalOcrTools.RecognizeTool, Args(new { path = image, language = tagName }), default);
        check(!Error(recognized), "OCR: native Windows engine recognizes a generated local bitmap");
        var data = Data(recognized);
        check(data.GetProperty("width").GetInt32() == 420 && data.GetProperty("height").GetInt32() == 100
            && data.GetProperty("lines").GetArrayLength() > 0 && data.GetProperty("text").GetString()!.Length > 0,
            "OCR: native result has text, pixel dimensions and line/word geometry");
        if (tagName.StartsWith("en", StringComparison.OrdinalIgnoreCase))
            check(data.GetProperty("text").GetString()!.Contains("HELLO", StringComparison.OrdinalIgnoreCase), "OCR: generated HELLO text is actually recognized locally");
        check(Error(await tools.CallAsync(LocalOcrTools.RecognizeTool, Args(new { path = image, language = "zz-ZZ" }), default)), "OCR: unavailable language never falls back remotely");
    }

    private static async Task ReviewAsync(Action<bool, string> check)
    {
        var calls = 0; var reviews = 0; var snapshot = McpTurnSnapshot.Empty;
        var tools = new LocalClipboardTools(_ => Task.FromResult<string?>("fixture"), (_, _) => { calls++; return Task.CompletedTask; });
        DesktopLocalTools.Register(snapshot, DesktopLocalTools.Clipboard, tools.CallAsync);
        var conversation = new Conversation();
        var output = new ConversationMessage { Message = new UIMessage { Id = "output", Role = Role.assistant }, Status = "streaming" };
        conversation.Messages.Add(output); var rounds = 0;
        async IAsyncEnumerable<StreamEvent> Stream(List<UIMessage> _, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield(); rounds++;
            if (rounds == 1)
            {
                yield return StreamEvent.Parse("""{"type":"tool-input-available","toolCallId":"clipboard","toolName":"local_clipboard_set","input":{"text":"fixture"}}""");
                yield return StreamEvent.Parse("""{"type":"tool-approval-request","approvalId":"review","toolCallId":"clipboard"}""");
            }
            else yield return StreamEvent.Parse("""{"type":"text-delta","id":"text","delta":"done"}""");
            yield return StreamEvent.Parse("""{"type":"finish"}""");
        }
        await DesktopChatTurn.RunAsync(conversation, output, Stream, (_, _) =>
        {
            reviews++; check(calls == 1, "Windows tools: clipboard mutation occurs before existing result review, not a new access prompt");
            return Task.FromResult(new ToolApprovalDecision(true));
        }, (_, _) => Task.CompletedTask, snapshot, "en", default);
        check(calls == 1 && reviews == 1 && rounds == 2 && output.Text == "done", "Windows tools: review and continuation never execute clipboard mutation twice");
    }

    private static async Task CanceledAsync(Func<Task> call, Action<bool, string> check, string name)
    {
        try { await call(); check(false, name); }
        catch (OperationCanceledException) { check(true, name); }
    }

    private static byte[] TextBitmap()
    {
        const int width = 420, height = 100, scale = 7;
        var pixels = Enumerable.Repeat((byte)255, width * height * 4).ToArray();
        var glyphs = new Dictionary<char, string>
        {
            ['H'] = "10001100011000111111100011000110001", ['E'] = "11111100001000011110100001000011111",
            ['L'] = "10000100001000010000100001000011111", ['O'] = "01110100011000110001100011000101110",
            ['1'] = "00100011000010000100001000010001110", ['2'] = "01110100010000100010001000100011111", ['3'] = "11110000010000101110000010000111110"
        };
        var x = 20;
        foreach (var c in "HELLO 123")
        {
            if (glyphs.TryGetValue(c, out var glyph))
                for (var row = 0; row < 7; row++) for (var col = 0; col < 5; col++)
                    if (glyph[row * 5 + col] == '1')
                        for (var dy = 0; dy < scale; dy++) for (var dx = 0; dx < scale; dx++)
                        {
                            var offset = ((height - 1 - (20 + row * scale + dy)) * width + x + col * scale + dx) * 4;
                            pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = 0;
                        }
            x += 6 * scale;
        }
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        writer.Write((ushort)0x4D42); writer.Write(54 + pixels.Length); writer.Write(0); writer.Write(54);
        writer.Write(40); writer.Write(width); writer.Write(height); writer.Write((ushort)1); writer.Write((ushort)32);
        writer.Write(0); writer.Write(pixels.Length); writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(pixels);
        return stream.ToArray();
    }
}
