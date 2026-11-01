using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Desktop.Core;
using AIHappey.Vercel.Models;

internal static class DocumentToolRegressionTests
{
    public static async Task RunAsync(Action<bool, string> check, string root)
    {
        Directory.CreateDirectory(root);
        var editor = new AnthropicTextEditorTools(root);
        var canvas = new LocalCanvasTools(root);
        var patch = new OpenAIApplyPatchTools(root);
        Task<JsonElement> Edit(object input) => editor.CallAsync(JsonSerializer.SerializeToElement(input), default);
        Task<JsonElement> Canvas(string operation, object input) => canvas.CallAsync("local_canvas_" + operation, JsonSerializer.SerializeToElement(input), default);
        bool Ok(JsonElement result) => !result.GetProperty("isError").GetBoolean();
        string Text(JsonElement result) => result.GetProperty("content")[0].GetProperty("text").GetString()!;
        void Ack(JsonElement result, string label)
        {
            check(Ok(result) && result.GetProperty("content").EnumerateArray().All(item => item.GetProperty("type").GetString() == "text")
                && !result.TryGetProperty("structuredContent", out _), label + ": acknowledgement only, no document resources");
        }

        Ack(await Edit(new { command = "create", path = "nested/notes.md", file_text = "one\ntwo" }), "Anthropic create");
        check(await File.ReadAllTextAsync(Path.Combine(root, "nested/notes.md")) == "one\ntwo", "editor writes safe relative subfolders");
        check(!Ok(await Edit(new { command = "create", path = "nested/notes.md", file_text = "overwrite" })), "editor create does not overwrite");
        Ack(await Edit(new { command = "str_replace", path = "nested/notes.md", old_str = "one", new_str = "$&" }), "Anthropic replace");
        Ack(await Edit(new { command = "insert", path = "nested/notes.md", insert_line = 0, insert_text = "head\nnext" }), "Anthropic insert");
        check(Text(await Edit(new { command = "view", path = "nested/notes.md", view_range = new[] { 2, -1 } })) == "2: next\n3: $&\n4: two", "editor numbered range and literal replacement match browser");
        check(!Ok(await Edit(new { command = "insert", path = "nested/notes.md", insert_line = 99, insert_text = "no" })), "editor rejects out-of-range insertion");
        await Edit(new { command = "create", path = "duplicates.txt", file_text = "a a" });
        check(!Ok(await Edit(new { command = "str_replace", path = "duplicates.txt", old_str = "a", new_str = "b" }))
            && await File.ReadAllTextAsync(Path.Combine(root, "duplicates.txt")) == "a a", "editor rejects ambiguous replacement without mutation");
        check(!Ok(await Edit(new { command = "str_replace", path = "duplicates.txt", old_str = "", new_str = "b" })), "editor rejects empty search text");
        check(!Ok(await Edit(new { command = "undo_edit", path = "duplicates.txt" })), "editor rejects unsupported commands");
        var limited = new AnthropicTextEditorTools(root, maxCharacters: 5);
        check(Text(await limited.CallAsync(JsonSerializer.SerializeToElement(new { command = "view", path = "duplicates.txt" }), default)) == "1:...", "editor respects configured character limit");
        check(Text(await Edit(new { command = "view", path = "/" })).Contains("duplicates.txt"), "editor supports explicit root listing");

        Ack(await Canvas("create", new { path = "/nested/canvas", file_text = "one\ntwo" }), "Canvas create");
        Ack(await Canvas("insert", new { path = "nested/canvas.md", insert_line = 1, insert_text = "head" }), "Canvas insert");
        Ack(await Canvas("replace", new { path = "nested/canvas", old_str = "one", new_str = "$&" }), "Canvas replace");
        check(Text(await Canvas("read", new { path = "nested/canvas", view_range = new[] { 2, 3 } })) == "$&\ntwo", "Canvas appends extension, preserves directories, and uses one-based insertion/ranges");
        Ack(await Canvas("create", new { path = "nested/canvas", file_text = "overwritten" }), "Canvas overwrite");
        check(await File.ReadAllTextAsync(Path.Combine(root, "nested/canvas.md")) == "overwritten", "Canvas create retains browser overwrite semantics");

        await File.WriteAllBytesAsync(Path.Combine(root, "bom.txt"), [0xef, 0xbb, 0xbf, .. Encoding.UTF8.GetBytes("one\r\ntwo")]);
        Ack(await Edit(new { command = "str_replace", path = "bom.txt", old_str = "one", new_str = "ONE" }), "editor BOM update");
        var bom = await File.ReadAllBytesAsync(Path.Combine(root, "bom.txt"));
        check(bom.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) && Encoding.UTF8.GetString(bom).EndsWith("ONE\r\ntwo"), "editor preserves UTF-8 BOM and CRLF");
        await File.WriteAllBytesAsync(Path.Combine(root, "bad.txt"), [0xff, 0xfe, 0]);
        check(!Ok(await Edit(new { command = "view", path = "bad.txt" })), "editor rejects non-UTF-8 content");
        check(!Ok(await Edit(new { command = "create", path = "image.png", file_text = "not an image" })), "editor rejects non-text extensions");
        check(!Ok(await Canvas("create", new { path = "binary", file_text = "a\0b" })), "Canvas rejects binary text");

        foreach (var path in new[] { "../escape.md", "nested/../../escape.md", "C:/escape.md", "\\\\server\\share\\file.md", "x:stream.md", "NUL.md", "nested/../file.md" })
        {
            check(!Ok(await Edit(new { command = "create", path, file_text = "no" })), "editor rejects unsafe path " + path);
            check(!Ok(await Canvas("create", new { path, file_text = "no" })), "Canvas rejects unsafe path " + path);
        }
        var outside = root + "-outside"; Directory.CreateDirectory(outside);
        try
        {
            var link = Path.Combine(root, "junction");
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{outside}\"")
                { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true })!;
            await process.WaitForExitAsync(); check(process.ExitCode == 0, "document junction setup");
            check(!Ok(await Edit(new { command = "create", path = "junction/editor.md", file_text = "no" }))
                && !Ok(await Canvas("create", new { path = "junction/canvas", file_text = "no" }))
                && Directory.GetFiles(outside).Length == 0, "both document handlers reject junction escapes");
            Directory.Delete(link);
        }
        finally { Directory.Delete(outside, true); }

        var preferences = new ChatPreferences(); var snapshot = McpTurnSnapshot.Empty;
        check(!AnthropicTextEditorTools.Register(snapshot, preferences, "anthropic", root: root), "native editor is disabled without provider configuration");
        preferences.ProviderMetadata["anthropic"] = JsonNode.Parse("""{"tools":[{"type":"text_editor_20250728","name":"str_replace_based_edit_tool"}]}""")!.AsObject();
        check(!AnthropicTextEditorTools.Register(snapshot, preferences, "openai", root: root), "native editor is gated to Anthropic");
        check(AnthropicTextEditorTools.Register(snapshot, preferences, "anthropic", root: root)
            && AnthropicTextEditorTools.ToolNames.All(snapshot.Contains) && snapshot.Tools.Count == 0, "both editor aliases route natively without function declarations");
        var output = new ConversationMessage { Message = new UIMessage { Id = "editor-turn", Role = Role.assistant } };
        output.Message.Parts.Add(PortableConversations.Part(JsonNode.Parse("""
            {"type":"tool-str_replace_editor","toolCallId":"native-editor","state":"approval-requested","approval":{"id":"review-editor"},
             "input":{"command":"create","path":"native.md","file_text":"# Native"}}
            """)!.AsObject()));
        check(await DesktopMcpToolExecution.ExecutePendingAsync(output, snapshot, new(), "en", default) == 1, "editor uses existing execution/review pipeline");
        var pending = DesktopToolApprovals.Pending(output.Message).Single();
        check(File.Exists(Path.Combine(root, "native.md")) && pending.Part.TryGetProperty("output", out _), "editor executes before result review as existing tools do");
        DesktopToolApprovals.Respond(output.Message, pending, new(true));
        check(ConversationCanvasProjection.Project([output]).Single().Versions.Single().Text == "# Native", "reviewed native result feeds Canvas without modifying result contract");
        check(DesktopLocalTools.Plugins.Single(plugin => plugin.Id == DesktopLocalTools.Canvas).Tools.Count == 4, "Canvas plugin includes four synced browser contracts");
        var local = McpTurnSnapshot.Empty; DesktopLocalTools.Register(local, DesktopLocalTools.Canvas, canvas.CallAsync);
        check(local.Tools.Count == 4 && local.Contains("local_canvas_create"), "Canvas plugin advertises and routes its function tools");

        var shared = await patch.CallAsync(JsonSerializer.SerializeToElement(new { operation = new { type = "create_file", path = "shared.md", diff = "+first" } }), default);
        check(Ok(shared) && Ok(await Edit(new { command = "str_replace", path = "shared.md", old_str = "first", new_str = "second" }))
            && Text(await Canvas("read", new { path = "shared.md" })) == "second", "patch, editor, and Canvas share the same Documents root");
        try
        {
            await new AnthropicTextEditorTools(root, () => false).CallAsync(JsonSerializer.SerializeToElement(new { command = "create", path = "stale.md", file_text = "no" }), default);
            check(false, "stale account must cancel");
        }
        catch (OperationCanceledException) { check(!File.Exists(Path.Combine(root, "stale.md")), "account-lifetime guard blocks document mutation"); }
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { await canvas.CallAsync("local_canvas_create", JsonSerializer.SerializeToElement(new { path = "cancelled", file_text = "no" }), cancelled.Token); check(false, "cancelled Canvas must cancel"); }
        catch (OperationCanceledException) { check(!File.Exists(Path.Combine(root, "cancelled.md")), "cancelled Canvas leaves no document"); }
        check(!Directory.EnumerateFiles(root, ".aihappey-patch-*.tmp", SearchOption.AllDirectories).Any(), "document operations leave no temporary files");
    }
}
