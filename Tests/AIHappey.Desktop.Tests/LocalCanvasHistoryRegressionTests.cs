using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Desktop.Core;
using AIHappey.Vercel.Models;

internal static class LocalCanvasHistoryRegressionTests
{
    private static UIMessagePart Call(string name, object input, string state = "output-available", bool failed = false, bool? approved = null) =>
        PortableConversations.Part(JsonSerializer.SerializeToElement(new
        {
            type = "tool-" + name, toolCallId = Guid.NewGuid().ToString(), input, state,
            approval = approved is null ? null : new { approved }, output = new { isError = failed, content = new[] { new { type = "text", text = "OK" } } }
        }));
    private static ConversationMessage Message(string id, params UIMessagePart[] parts) => new()
    {
        Timestamp = DateTimeOffset.Parse("2026-11-01T12:00:00Z"),
        Message = new UIMessage { Id = id, Role = Role.assistant, Parts = [.. parts] }
    };
    public static void Run(Action<bool, string> check)
    {
        var conversation = new Conversation { Messages =
        [
            Message("create", new TextUIPart { Text = "text before the tool" }, Call("apply_patch", new { operation = new { type = "create_file", path = "folder/my notes.md", diff = "+one\n+two" } })),
            Message("edit", Call("str_replace_editor", new { command = "str_replace", path = "folder/MY NOTES.md", old_str = "one", new_str = "$&" }),
                Call("local_canvas_insert", new { path = "/folder/my notes", insert_line = 1, insert_text = "head" })),
            Message("append", Call("str_replace_based_edit_tool", new { command = "insert", path = "folder/my notes.md", insert_line = 3, insert_text = "tail" }))
        ] };
        var before = PortableConversations.Write(conversation);
        var doc = ConversationCanvasProjection.Project(conversation.Messages).Single();
        check(doc.Title == "my notes" && doc.Uri == "localcanvas:///folder/my%20notes.md" && doc.Versions.Count == 3, "local Canvas groups Windows path aliases across all three tool families");
        check(doc.Versions.Select(version => version.Text).SequenceEqual(["head\n$&\ntwo\ntail", "head\n$&\ntwo", "one\ntwo"]), "local Canvas replays edits with correct insertion and literal replacement semantics");
        check(doc.Versions.Last().PartIndex == 1 && doc.Versions.First().MimeType == "text/markdown" && doc.Versions.All(version => version.LocalDocument), "local Canvas retains real tool position and rendering classification");
        check(PortableConversations.Write(conversation) == before, "local Canvas does not add resources or mutate native results");
        check(ConversationCanvasProjection.Project(PortableConversations.Read(before).Messages).Single().Versions.Select(v => v.Text).SequenceEqual(doc.Versions.Select(v => v.Text)), "creation-based Canvas survives portable history round-trip");
        check(ConversationCanvasProjection.Project([]).Count == 0, "history reconstruction has no cross-conversation state");

        foreach (var part in new[]
        {
            Call("str_replace_editor", new { command = "str_replace", path = "existing.md", old_str = "a", new_str = "b" }),
            Call("local_canvas_insert", new { path = "existing", insert_line = 1, insert_text = "fragment" }),
            Call("local_canvas_read", new { path = "existing", view_range = new[] { 1, 2 } }),
            Call("str_replace_editor", new { command = "view", path = "existing.md" }),
            Call("apply_patch", new { operation = new { type = "update_file", path = "existing.md", diff = "@@\n-a\n+b" } }),
            Call("local_canvas_unknown", new { path = "existing", file_text = "unknown" }),
            Call("apply_patch", new { operation = new { type = "create_file", path = "unsafe.html", diff = "+<script>" } }),
            Call("str_replace_editor", new { command = "create", path = "../escape.md", file_text = "unsafe" }),
            Call("str_replace_editor", new { command = "create", path = "binary.md", file_text = "a\0b" })
        }) check(ConversationCanvasProjection.Project([Message("exclude", part)]).Count == 0, "local Canvas excludes unseeded, partial, unknown, or unsafe documents");
        foreach (var state in new[] { "input-streaming", "input-available", "approval-requested", "output-error", "output-denied" })
            check(ConversationCanvasProjection.Project([Message(state, Call("local_canvas_create", new { path = "pending", file_text = "not completed" }, state))]).Count == 0, "local Canvas excludes state " + state);
        check(ConversationCanvasProjection.Project([Message("failed", Call("local_canvas_create", new { path = "failed", file_text = "failed" }, failed: true))]).Count == 0, "local Canvas excludes failed result");
        check(ConversationCanvasProjection.Project([Message("denied", Call("local_canvas_create", new { path = "denied", file_text = "denied" }, "approval-responded", approved: false))]).Count == 0, "local Canvas excludes denied result");

        var create = Message("seed", Call("str_replace_editor", new { command = "create", path = "test.txt", file_text = "a" }));
        var update = Message("update", Call("apply_patch", new { operation = new { type = "update_file", path = "test.txt", diff = "@@\n-a\n+b" } }));
        check(ConversationCanvasProjection.Project([create, update]).Single().Versions[0] is { Text: "b", MimeType: "text/plain" }, "local Canvas patches plain text without treating it as Markdown");
        var deletion = Message("delete", Call("apply_patch", new { operation = new { type = "delete_file", path = "test.txt" } }));
        check(ConversationCanvasProjection.Project([create, deletion, update]).Single().Versions.Count == 1, "delete clears replay baseline but retains historical version");
        var conflict = Message("conflict", Call("str_replace_editor", new { command = "str_replace", path = "test.txt", old_str = "missing", new_str = "unknown" }));
        check(ConversationCanvasProjection.Project([create, conflict, update]).Single().Versions.Count == 1, "unreconstructable edit invalidates stale replay baseline");
        var failedEdit = Message("failed", Call("str_replace_editor", new { command = "str_replace", path = "test.txt", old_str = "a", new_str = "unknown" }, failed: true));
        check(ConversationCanvasProjection.Project([create, failedEdit, update]).Single().Versions[0].Text == "b", "failed edit does not change known content");
        var read = Message("read", Call("local_canvas_read", new { path = "test.txt", view_range = new[] { 1, 1 } }));
        check(ConversationCanvasProjection.Project([create, read, update]).Single().Versions[0].Text == "b", "read acknowledgements do not overwrite known complete content");
        var recreate = Message("recreate", Call("str_replace_editor", new { command = "create", path = "test.txt", file_text = "new" }));
        check(ConversationCanvasProjection.Project([create, deletion, recreate]).Single().Versions[0].Text == "new", "recreation starts a new complete baseline");
        check(ConversationCanvasProjection.Project([Message("empty", Call("apply_patch", new { operation = new { type = "create_file", path = "empty.md", diff = "" } }))]).Single().Versions.Single().Text == "", "empty document creation remains previewable");
    }
}
