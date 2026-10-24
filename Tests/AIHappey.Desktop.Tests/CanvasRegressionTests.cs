using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using AIHappey.Desktop.Core;
using AIHappey.Vercel.Models;

internal static class CanvasRegressionTests
{
    private const string Uri = "https://example.com/documents/plan.md";
    private static readonly DateTimeOffset timestamp = DateTimeOffset.Parse("2026-10-24T12:00:00.1234567Z", CultureInfo.InvariantCulture);

    private static UIMessagePart Part(string json) => PortableConversations.Part(JsonSerializer.Deserialize<JsonElement>(json));
    private static object Resource(string uri, string text, string mime = "text/markdown") => new
        { type = "resource", resource = new { uri, mimeType = mime, text } };
    private static UIMessagePart Tool(object content, string type = "tool-read_resource") => Part(JsonSerializer.Serialize(new
        { type, toolCallId = Guid.NewGuid().ToString(), state = "output-available", output = new { content } }));
    private static ConversationMessage Message(string id, DateTimeOffset time, params UIMessagePart[] parts) => new()
        { Timestamp = time, Message = new UIMessage { Id = id, Role = Role.assistant, Parts = [.. parts] } };

    public static void Run(Action<bool, string> check)
    {
        check(ConversationCanvasProjection.Project([]).Count == 0, "canvas: empty conversation has no documents");
        var conversation = new Conversation { Messages =
        [
            Message("first", timestamp, new TextUIPart { Text = "not a document" },
                Tool(new[] { Resource(Uri, "intermediate") }),
                Tool(new[] { Resource(Uri, "# Original"), Resource("mcp://server/notes.md", "# Notes") })),
            Message("second", timestamp.AddMinutes(1), Tool(Resource(Uri, "# Latest"), "dynamic-tool")),
            Message("third", timestamp.AddMinutes(1), Tool(new[] { Resource(Uri, "# Same time, later message") }))
        ] };
        var before = PortableConversations.Write(conversation);
        var docs = ConversationCanvasProjection.Project(conversation.Messages);
        check(docs.Count == 2 && docs[0].Uri == Uri && docs[1].Uri == "mcp://server/notes.md", "canvas: exact URI grouping preserves document discovery order");
        var plan = docs[0];
        check(plan.Title == "plan" && plan.Versions.Count == 3, "canvas: filename title and one version per message");
        check(plan.Versions.Select(version => version.Text).SequenceEqual(["# Same time, later message", "# Latest", "# Original"]),
            "canvas: newest timestamps first with deterministic message-order ties and last tool part per message");
        check(plan.Versions.Last().PartIndex == 2 && plan.Versions.Last().MessageId == "first", "canvas: retains original part/message identity");
        check(PortableConversations.Write(conversation) == before, "canvas: projection never mutates portable tool results or metadata");
        var reopened = ConversationCanvasProjection.Project(PortableConversations.Read(before).Messages);
        check(reopened.SelectMany(document => document.Versions).Select(version => version.Text)
            .SequenceEqual(docs.SelectMany(document => document.Versions).Select(version => version.Text)), "canvas: document history survives portable round-trip");
        var activity = ConversationActivityProjection.Project(conversation.Messages);
        check(activity.Tools.Count == 4 && activity.Data.Count == 0, "canvas: original Tools/Data projection remains intact");

        var duplicate = ConversationCanvasProjection.Project([Message("duplicate", timestamp,
            Tool(new[] { Resource(Uri, "first block"), Resource(Uri, "second block") }))]).Single();
        check(duplicate.Versions.Single().Text == "first block", "canvas: same-part duplicate resource follows browser first-block behavior");
        var empty = ConversationCanvasProjection.Project([Message("empty", timestamp, Tool(Resource(Uri, "")))]).Single();
        check(empty.Versions.Single().Text == "", "canvas: empty Markdown remains a valid document version");
        check(ConversationCanvasProjection.Project([Message("case", timestamp,
            Tool(new[] { Resource(Uri, "a"), Resource(Uri.Replace("plan.md", "Plan.md"), "b") }))]).Count == 2,
            "canvas: URI grouping is ordinal and does not merge distinct paths");

        foreach (var output in new[]
        {
            "null", "42", "[]", "\"plain output\"", "{}", "{\"content\":null}", "{\"content\":42}",
            """{"content":[null,42,"text",{},[],{"type":"text","text":"# Ordinary tool text"}]}""",
            """{"content":[{"type":"resource_link","uri":"https://example.com/a.md","mimeType":"text/markdown"}]}""",
            """{"content":[{"type":"resource","resource":null},{"type":"resource","resource":42}]}""",
            """{"content":[{"type":"resource","resource":{"uri":"x","mimeType":"text/html","text":"# Not Markdown"}}]}""",
            """{"content":[{"type":"resource","resource":{"mimeType":"text/markdown","text":"no uri"}}]}""",
            """{"content":[{"type":"resource","resource":{"uri":"  ","mimeType":"text/markdown","text":"no uri"}}]}""",
            """{"content":[{"type":"resource","resource":{"uri":42,"mimeType":"text/markdown","text":"no uri"}}]}""",
            """{"content":[{"type":"resource","resource":{"uri":"x","mimeType":"text/markdown","text":42}}]}""",
            """{"content":[{"type":"resource","resource":{"uri":"x","mimeType":"text/markdown","blob":"YWJj"}}]}""",
            """{"structuredContent":{"type":"resource","resource":{"uri":"x","mimeType":"text/markdown","text":"not content"}}}"""
        })
            check(ConversationCanvasProjection.Project([Message("malformed", timestamp, Part("{\"type\":\"tool-test\",\"output\":" + output + "}"))]).Count == 0,
                "canvas: ignore malformed or non-Markdown output " + output);
        foreach (var type in new[] { "text", "data-resource", "tool-call" })
            check(ConversationCanvasProjection.Project([Message(type, timestamp, Tool(Resource(Uri, "not a tool result"), type))]).Count == 0,
                "canvas: non-result part excluded: " + type);
        var malformedTime = PortableConversations.Read("""{"id":"bad-time","messages":[{"id":"m","role":"assistant","metadata":{"timestamp":"not-a-date"},"parts":[{"type":"tool-read","output":{"content":[{"type":"resource","resource":{"uri":"mcp://notes/a.md","mimeType":"text/markdown","text":"# Safe"}}]}}]}]}""");
        check(ConversationCanvasProjection.FormatTimestamp(ConversationCanvasProjection.Project(malformedTime.Messages).Single().Versions[0].Timestamp) == "-",
            "canvas: invalid imported timestamps have a safe display fallback");
        check(ConversationCanvasProjection.Title("folder/notes.md") == "notes"
            && ConversationCanvasProjection.Title("https://example.com/plan.final.md?token=1#part") == "plan.final"
            && ConversationCanvasProjection.Title("https://example.com/") == "https://example.com/", "canvas: titles handle relative paths, query strings, multiple extensions and empty filenames");
        check(ConversationCanvasProjection.FormatTimestamp(default) == "-"
            && ConversationCanvasProjection.FormatTimestamp(timestamp, CultureInfo.GetCultureInfo("nl-NL")) == timestamp.ToLocalTime().ToString("g", CultureInfo.GetCultureInfo("nl-NL")),
            "canvas: localized version timestamps retain safe missing-date fallback");
        foreach (var uri in new[] { "https://example.com/a.md", "http://example.com/a.md" })
            check(ConversationCanvasProjection.ExternalUri(uri) is not null, "canvas: supported external link " + uri);
        foreach (var uri in new[] { "mcp://notes/a.md", "localcanvas:///a.md", "file:///C:/a.md", "javascript:alert(1)", "data:text/html,test", "ms-settings:", "a.md", "https://user:secret@example.com/a", "" })
            check(ConversationCanvasProjection.ExternalUri(uri) is null, "canvas: block unsupported external activation " + uri);

        var state = new ConversationCanvasSelection(); state.SetScope("account-a", "chat-a"); state.Synchronize(docs);
        check(state.Current(plan) == plan.Versions[0], "canvas: initial selection is latest");
        state.Select(plan, plan.Versions.Last()); state.Synchronize(ConversationCanvasProjection.Project(conversation.Messages));
        check(state.Current(plan) == plan.Versions.Last(), "canvas: unchanged refresh retains historical version selection");
        check(!state.SetScope("account-a", "chat-a") && state.Current(plan) == plan.Versions.Last(), "canvas: same scope does not reset selection");
        state.Select(plan, plan.Versions.Last() with { Text = "not a real version" });
        check(state.Current(plan) == plan.Versions.Last(), "canvas: stale menu selection cannot add invented versions");
        conversation.Messages.Add(Message("fourth", timestamp.AddMinutes(2), Tool(Resource(Uri, "# New version"))));
        var next = ConversationCanvasProjection.Project(conversation.Messages); state.Synchronize(next);
        check(state.Current(next[0]).Text == "# New version", "canvas: newly arriving version switches to latest");
        state.Select(next[0], next[0].Versions.Last()); state.SetScope("account-b", "chat-a"); state.Synchronize(next);
        check(state.Current(next[0]) == next[0].Versions[0], "canvas: account change resets history selection even for same chat ID");
        state.Select(next[0], next[0].Versions.Last()); state.SetScope("account-b", "chat-b"); state.Synchronize(next);
        check(state.Current(next[0]) == next[0].Versions[0], "canvas: conversation change resets history selection for same URI");
        state.Select(next[0], next[0].Versions.Last()); state.Synchronize([]); state.Synchronize(next);
        check(state.Current(next[0]) == next[0].Versions[0], "canvas: disappearing documents are pruned from selection state");
        var updated = new CanvasDocument(next[0].Uri, [next[0].Versions[0] with { Text = "# Updated in place" }, .. next[0].Versions.Skip(1)]);
        state.Select(next[0], next[0].Versions.Last()); state.Synchronize([updated]);
        check(state.Current(updated).Text == "# Updated in place", "canvas: changed latest tool output refreshes content and selection");
        var withoutOld = new CanvasDocument(updated.Uri, [updated.Versions[0]]);
        state.Select(updated, updated.Versions.Last()); state.Synchronize([withoutOld]);
        check(state.Current(withoutOld) == withoutOld.Versions[0], "canvas: deleted historical selection falls back to latest");

        foreach (var language in new[] { "en", "nl" })
        {
            using var stream = typeof(CanvasRegressionTests).Assembly.GetManifestResourceStream("Desktop.Resources." + language)!;
            var resources = XDocument.Load(stream).Descendants("data").ToDictionary(item => item.Attribute("name")!.Value, item => item.Element("value")!.Value);
            foreach (var key in new[] { "CanvasCount", "NoCanvasActivities", "CanvasVersions", "CopyCanvasMarkdown", "OpenCanvasLink", "CanvasLinkUnavailable" })
                check(resources.TryGetValue(key, out var translated) && !string.IsNullOrWhiteSpace(translated), "canvas: localized " + language + " resource " + key);
        }
    }
}
