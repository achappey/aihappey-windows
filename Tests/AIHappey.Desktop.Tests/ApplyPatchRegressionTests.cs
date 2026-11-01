using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Desktop.Core;
using AIHappey.Vercel.Models;

internal static class ApplyPatchRegressionTests
{
    public static async Task RunAsync(Action<bool, string> check, string root)
    {
        Directory.CreateDirectory(root);
        var tools = new OpenAIApplyPatchTools(root);
        Task<JsonElement> Call(string type, string path, string? diff = null) => tools.CallAsync(
            JsonSerializer.SerializeToElement(new { operation = new { type, path, diff } }), default);
        bool Success(JsonElement value) => value.GetProperty("structuredContent").GetProperty("status").GetString() == "completed";
        check(Success(await Call("create_file", "src/test.txt", "+one\n+two")), "patch creates text in relative subfolders");
        check(await File.ReadAllTextAsync(Path.Combine(root, "src/test.txt")) == "one\ntwo", "patch create V4A content");
        check(!Success(await Call("create_file", "src/test.txt", "+overwrite")), "patch create never overwrites");
        check(Success(await Call("update_file", "src/test.txt", "@@\n-one\n+ONE\n two")), "patch updates existing text");
        check(!Success(await Call("update_file", "src/test.txt", "@@\n-not there\n+bad")), "patch conflict returns failed");
        check(await File.ReadAllTextAsync(Path.Combine(root, "src/test.txt")) == "ONE\ntwo", "patch conflict leaves file intact");
        check(Success(await Call("delete_file", "src/test.txt")), "patch deletes exact file");
        check(!Success(await Call("delete_file", "src/test.txt")), "patch missing delete returns failed");
        check(Success(await Call("create_file", "empty.txt", "")), "patch supports empty create diff");
        foreach (var path in new[] { "../escape.txt", "src/../../escape.txt", "C:/escape.txt", "\\\\server\\file.txt", "x:stream", "NUL.txt", "folder/../file.txt", "folder/", " file.txt" })
            check(!Success(await Call("create_file", path, "+no")), "patch rejects unsafe path " + path);
        check(!Success(await Call("delete_file", "src")), "patch refuses directory deletion");

        await File.WriteAllBytesAsync(Path.Combine(root, "utf8.txt"), [0xef, 0xbb, 0xbf, .. Encoding.UTF8.GetBytes("one\r\ntwo\r\n")]);
        check(Success(await Call("update_file", "utf8.txt", "@@\n-one\n+ONE")), "patch updates UTF-8 BOM and CRLF");
        var bytes = await File.ReadAllBytesAsync(Path.Combine(root, "utf8.txt"));
        check(bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) && Encoding.UTF8.GetString(bytes).EndsWith("ONE\r\ntwo\r\n"), "patch preserves BOM and newline style");
        check(V4ADiff.Apply("a\nb\nc\n", "@@\n-a\n+A\n@@\n-c\n+C\n*** End of File") == "A\nb\nC\n", "patch multiple hunks and EOF");
        check(V4ADiff.Apply("a\nb", "@@\n-b\n+B\n*** End of File") == "a\nB", "patch preserves missing trailing newline");
        check(V4ADiff.Apply("one\n\ntwo", "@@ one\n@@ two\n+three") == "one\n\ntwo\nthree", "patch stacked anchors");

        var outside = root + "-outside"; Directory.CreateDirectory(outside);
        try
        {
            // Junctions do not require developer-mode symbolic link privileges.
            var link = Path.Combine(root, "junction");
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{outside}\"")
                { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true })!;
            await process.WaitForExitAsync();
            check(process.ExitCode == 0, "patch junction test setup");
            check(!Success(await Call("create_file", "junction/escape.txt", "+no")) && !File.Exists(Path.Combine(outside, "escape.txt")), "patch rejects junction escapes");
            Directory.Delete(link);
        }
        finally { Directory.Delete(outside, true); }

        var config = new OpenAIChatConfig(new JsonObject());
        check(!config.Enabled("apply_patch"), "patch default disabled");
        config.Toggle("apply_patch", true);
        var canonical = OpenAIChatConfig.Canonical(config.Root);
        check(canonical["tools"] is JsonArray array && array.Count == 1 && array[0]!.ToJsonString() == "{\"type\":\"apply_patch\"}", "patch minimal native config");
        var reloaded = new OpenAIChatConfig(canonical); reloaded.Toggle("apply_patch", false);
        check(OpenAIChatConfig.Canonical(reloaded.Root)["tools"] is null, "patch disable does not resurrect canonical tool");

        var snapshot = McpTurnSnapshot.Empty;
        snapshot.AddNative(OpenAIApplyPatchTools.ToolName, tools.CallAsync);
        check(snapshot.Contains("apply_patch") && snapshot.Tools.Count == 0, "patch runtime route has no function declaration");
        var output = new ConversationMessage { Message = new UIMessage { Id = "patch", Role = Role.assistant } };
        output.Message.Parts.Add(PortableConversations.Part(JsonNode.Parse("""
            {"type":"tool-apply_patch","toolCallId":"call_patch","state":"approval-requested","approval":{"id":"approval_patch"},
             "input":{"operation":{"type":"create_file","path":"review.txt","diff":"+reviewed"}}}
            """)!.AsObject()));
        var executed = new HashSet<string>();
        check(await DesktopMcpToolExecution.ExecutePendingAsync(output, snapshot, executed, "en", default) == 1, "patch uses existing executor");
        var pending = DesktopToolApprovals.Pending(output.Message).Single();
        check(File.Exists(Path.Combine(root, "review.txt")) && pending.Part.TryGetProperty("input", out _) && pending.Part.TryGetProperty("output", out _), "patch executes before input/output review");
        check(await DesktopMcpToolExecution.ExecutePendingAsync(output, snapshot, executed, "en", default) == 0, "patch review does not execute again");
        DesktopToolApprovals.Respond(output.Message, pending, new(false, "Denied result"));
        check(File.Exists(Path.Combine(root, "review.txt")), "patch denial does not pretend to roll back execution");

        var conversation = new Conversation();
        var turnOutput = new ConversationMessage { Message = new UIMessage { Id = "full-turn", Role = Role.assistant }, Status = "streaming" };
        conversation.Messages.Add(turnOutput);
        var turn = 0; var reviews = 0;
        async IAsyncEnumerable<StreamEvent> Stream(List<UIMessage> messages, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            var current = turn++;
            if (current > 0)
                check(messages.Last().Parts.Where(PortableConversations.IsTool).All(part => DesktopToolApprovals.HasOutput(PortableConversations.Element(part))), "patch continuation contains executed outputs");
            if (current < 3)
            {
                var type = new[] { "create_file", "update_file", "delete_file" }[current];
                var diff = new[] { "+first", "@@\n-first\n+second", "" }[current];
                yield return StreamEvent.Parse(JsonSerializer.Serialize(new { type = "tool-input-available", toolCallId = "native_" + current,
                    toolName = "apply_patch", providerExecuted = false, input = new { operation = new { type, path = "flow/file.txt", diff } } }));
                yield return StreamEvent.Parse(JsonSerializer.Serialize(new { type = "tool-approval-request", toolCallId = "native_" + current, approvalId = "review_" + current }));
            }
            else yield return StreamEvent.Parse("""{"type":"text-delta","id":"done","delta":"All patches completed."}""");
            yield return StreamEvent.Parse("""{"type":"finish"}""");
            await Task.CompletedTask;
        }
        await DesktopChatTurn.RunAsync(conversation, turnOutput, Stream, (review, cancellation) =>
        {
            reviews++;
            check(review.Part.TryGetProperty("input", out _) && review.Part.TryGetProperty("output", out _), "patch turn review contains input and output");
            return Task.FromResult(new ToolApprovalDecision(true));
        }, (_, _) => Task.CompletedTask, snapshot, "en", default);
        check(turn == 4 && reviews == 3 && turnOutput.Text == "All patches completed." && !File.Exists(Path.Combine(root, "flow/file.txt")),
            "patch end-to-end native turn executes create/update/delete with review and model continuation");
    }
}
