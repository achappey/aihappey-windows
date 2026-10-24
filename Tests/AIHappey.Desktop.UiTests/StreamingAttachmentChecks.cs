using System.Text.Json;
using AIHappey.Desktop.Core;
using AIHappey.Vercel.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AIHappey.Desktop.UiTests;

public partial class App
{
    private async Task CheckStreamingAttachmentsAsync(ElementTheme theme)
    {
        var session = new DesktopSession(new UiHost(true), new UiRuntime(), new());
        var shell = new ChatShell(session) { RequestedTheme = theme };
        try
        {
            window!.Content = shell; window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1100, 900)); window.Activate();
            for (var attempt = 0; attempt < 100 && !Field<bool>(shell, "initialized"); attempt++) await Task.Delay(20);
            await HistoryIdleAsync(shell);
            var transcript = Field<StackPanel>(shell, "transcript");
            ProgressBar[] Indicators() => Descendants(transcript).OfType<ProgressBar>().Where(p => p.Name == "AssistantStreamingProgress").ToArray();
            foreach (var count in new[] { 0, 1, 4 })
            {
                var context = $"Streaming attachments / {theme} / {count} resources";
                var output = new ConversationMessage { Status = "streaming", Message = new UIMessage { Id = "streaming-answer", Role = Role.assistant } };
                var chat = new Conversation { Messages = [output] };
                await (Task)Call(shell, "OpenConversationAsync", chat)!;
                await Task.Delay(80); shell.UpdateLayout();
                Check(ReferenceEquals(Field<Conversation>(shell, "current"), chat), context + ": fixture is the active initialized conversation");
                var assembler = new MessageAssembler(output);
                async Task Apply(object value)
                {
                    assembler.Apply(StreamEvent.Parse(JsonSerializer.Serialize(value)));
                    var original = PortableConversations.Write(chat);
                    Call(shell, "RenderTranscript");
                    await Task.Delay(40); shell.UpdateLayout();
                    Check(PortableConversations.Write(chat) == original, context + ": rendering preserves portable tool results and text");
                    File.WriteAllLines(report, results);
                }
                await Apply(new { type = "tool-input-available", toolCallId = "read", toolName = "read_resource", input = new { uri = "resource://document" } });
                await Apply(new { type = "tool-output-available", toolCallId = "read", output = new { content = Enumerable.Range(0, count).Select(index => new
                {
                    type = "resource", resource = new { uri = $"resource://document/{index}", mimeType = "text/plain", text = "Tool document content " + index }
                }).ToArray() } });
                Check(Indicators().Length == 1, context + ": tool result has exactly one live indicator");
                if (count > 0)
                    Check(Descendants(transcript).OfType<Button>().Count(b => b.Name == "AttachmentsButton") == 1,
                        context + ": attachment-only details row exposes its footer before text");
                results.Add(context + ": transitioning from tool results to the first assistant token");
                File.WriteAllLines(report, results);
                // The production renderer may coalesce text-start and the first delta into one frame.
                assembler.Apply(StreamEvent.Parse("""{"type":"text-start","id":"answer"}"""));
                await Apply(new { type = "text-delta", id = "answer", delta = "First token" });
                var markdown = Descendants(transcript).OfType<ChatMarkdown>().Single(m => m.Name == "MessageMarkdown");
                var indicator = Indicators().Single();
                Check(markdown.Text == "First token" && Descendants(transcript.Children.Last()).Contains(indicator),
                    context + ": first token retains one indicator on the final text row");
                await Apply(new { type = "text-delta", id = "answer", delta = ", then the complete answer." });
                Check(ReferenceEquals(markdown, Descendants(transcript).OfType<ChatMarkdown>().Single(m => m.Name == "MessageMarkdown"))
                    && ReferenceEquals(indicator, Indicators().Single()) && markdown.Text == output.Text,
                    context + ": subsequent tokens update the existing text and indicator without interruption");
                Check(MessageDetails.Project(chat.Messages).Last().Attachments.Count == count
                    && !Descendants(transcript).OfType<TextBlock>().Any(t => t.Text.StartsWith("Tool document content")),
                    context + ": attachments follow the answer without leaking tool document text into it");
                await Apply(new { type = "finish" });
                Check(output.Status == "complete" && output.ErrorMessage is null && Indicators().Length == 0,
                    context + ": completion removes the indicator and leaves the answer successful");
            }
            foreach (var ending in new[] { "complete", "cancel", "cancel-before-text", "error" })
            {
                var context = $"Streaming attachment turn / {theme} / {ending}";
                var output = new ConversationMessage { Status = "streaming", Message = new UIMessage { Id = "tool-turn", Role = Role.assistant } };
                var chat = new Conversation { Messages = [output] };
                await (Task)Call(shell, "OpenConversationAsync", chat)!;
                using var cancellation = new CancellationTokenSource();
                var requests = 0;
                var renders = 0;
                var originalTools = new List<UIMessagePart>();
                async IAsyncEnumerable<StreamEvent> Stream(List<UIMessage> _,
                    [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
                {
                    requests++;
                    // Two tool/text cycles exercise repeated indicator moves within a real turn.
                    for (var cycle = 0; cycle < 2; cycle++)
                    {
                        yield return StreamEvent.Parse(JsonSerializer.Serialize(new { type = "tool-input-available", toolCallId = "read-" + cycle,
                            toolName = "read_resource", providerExecuted = true, input = new { uri = "resource://report/" + cycle } }));
                        yield return StreamEvent.Parse(JsonSerializer.Serialize(new { type = "tool-output-available", toolCallId = "read-" + cycle,
                            output = new { content = Enumerable.Range(0, 2).Select(index => new { type = "resource",
                                resource = new { uri = $"resource://report/{cycle}/{index}", mimeType = "text/plain", text = "Original tool resource " + index } }).ToArray() } }));
                        originalTools.Add(output.Message.Parts.Single(p => PortableConversations.IsTool(p)
                            && PortableConversations.Element(p).GetProperty("toolCallId").GetString() == "read-" + cycle));
                        if (ending == "cancel-before-text") { cancellation.Cancel(); ct.ThrowIfCancellationRequested(); }
                        yield return StreamEvent.Parse(JsonSerializer.Serialize(new { type = "text-start", id = "answer-" + cycle }));
                        yield return StreamEvent.Parse(JsonSerializer.Serialize(new { type = "text-delta", id = "answer-" + cycle, delta = "First token " }));
                        yield return StreamEvent.Parse(JsonSerializer.Serialize(new { type = "text-delta", id = "answer-" + cycle, delta = "and the rest of the answer." }));
                        yield return StreamEvent.Parse(JsonSerializer.Serialize(new { type = "text-end", id = "answer-" + cycle }));
                    }
                    await Task.Yield();
                    if (ending == "cancel") { cancellation.Cancel(); ct.ThrowIfCancellationRequested(); }
                    yield return StreamEvent.Parse(ending == "error" ? """{"type":"error","errorText":"Fixture stream failed"}""" : """{"type":"finish"}""");
                }
                try
                {
                    await DesktopChatTurn.RunAsync(chat, output, Stream,
                        (_, _) => throw new InvalidOperationException("No approval expected"), async (_, _) =>
                        {
                            var original = PortableConversations.Write(chat);
                            Call(shell, "RenderTranscript"); await Task.Delay(30); shell.UpdateLayout(); renders++;
                            Check(PortableConversations.Write(chat) == original, context + ": live turn render does not mutate the message");
                            Check(Indicators().Length == (output.Status == "streaming" ? 1 : 0), context + ": each event has the correct number of live indicators");
                        }, McpTurnSnapshot.Empty, "en", cancellation.Token);
                    Check(ending == "complete", context + ": only completion returns successfully");
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                    Check(ending.StartsWith("cancel") && output.Status == "stopped", context + ": cancellation retains stopped state");
                }
                catch (GatewayException error)
                {
                    Check(ending == "error" && error.Message == "Fixture stream failed" && output.Status == "failed"
                        && output.ErrorMessage == error.Message, context + ": genuine stream error is still propagated");
                }
                // Same final render as the production shell's turn-finally block.
                Call(shell, "RenderTranscript"); await Task.Delay(40); shell.UpdateLayout();
                Check(requests == 1 && renders > 0 && Indicators().Length == 0, context + ": termination clears indicators without retrying inference");
                Check(output.Message.Parts.Where(PortableConversations.IsTool).SequenceEqual(originalTools)
                    && MessageDetails.Project(chat.Messages).Last().Attachments.Count == (ending == "cancel-before-text" ? 2 : 4),
                    context + ": original tool results and attachment footers survive termination");
                Check(ending == "cancel-before-text" ? output.Text.Length == 0 : output.Text.Contains("and the rest of the answer."),
                    context + ": completed or partial assistant text is retained");
            }
            File.WriteAllLines(report, results);
        }
        finally { await shell.ShutdownAsync(); window!.Content = null; }
    }
}
