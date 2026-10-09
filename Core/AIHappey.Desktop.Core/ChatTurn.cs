using AIHappey.Vercel.Models;

namespace AIHappey.Desktop.Core;

/// <summary>One active human-in-the-loop turn, shared by AI and Agents. Never processes historical approvals.</summary>
public static class DesktopChatTurn
{
    public const int MaxRounds = 32;

    public static async Task RunAsync(Conversation conversation, ConversationMessage output,
        Func<List<UIMessage>, CancellationToken, IAsyncEnumerable<StreamEvent>> stream,
        Func<PendingToolApproval, CancellationToken, Task<ToolApprovalDecision>> approve,
        Func<bool, CancellationToken, Task> changed, McpTurnSnapshot mcp, string locale, CancellationToken ct,
        DesktopElicitationHandler? elicit = null)
    {
        var assembler = new MessageAssembler(output);
        var executed = new HashSet<string>(StringComparer.Ordinal);
        var answered = new HashSet<string>(StringComparer.Ordinal);
        var mcpRounds = 0;
        var messages = conversation.Messages.Where(x => x != output && PortableConversations.CanReplay(x)).Select(x => x.Message).ToList();
        try
        {
            for (var round = 0; ; round++)
            {
                ct.ThrowIfCancellationRequested();
                await foreach (var item in stream(messages, ct))
                {
                    assembler.Apply(item);
                    await changed(assembler.Finished, ct);
                    if (assembler.Finished) break;
                }
                if (!assembler.Finished)
                {
                    output.Status = assembler.ApprovalRequired ? "approval required" : "interrupted";
                    break; // An incomplete/failed transport must not silently approve or retry inference.
                }
                if (output.Status is not "complete" and not "approval required") break;
                // Execute captured client routes first. Keep approval-requested with the result so the refreshed
                // pending part passed to the dialog contains both input and output, just like Agents results.
                var calls = await DesktopMcpToolExecution.ExecutePendingAsync(output,
                    mcp, executed, locale, ct, elicit, executeMcp: conversation.Service == ServiceKind.Ai);
                if (calls > 0)
                {
                    await changed(true, ct); // Save the executed result before displaying its review.
                    mcpRounds++;
                }
                var decisions = 0;
                while (DesktopToolApprovals.Pending(output.Message).FirstOrDefault() is { } pending)
                {
                    ct.ThrowIfCancellationRequested();
                    if (answered.Contains(pending.Id)) throw new GatewayException(DesktopResources.Get("ApprovalLoopLimit"));
                    var decision = await approve(pending, ct);
                    ct.ThrowIfCancellationRequested();
                    if (!DesktopToolApprovals.Respond(output.Message, pending, decision))
                        throw new GatewayException(DesktopResources.Get("ApprovalLoopLimit"));
                    answered.Add(pending.Id); decisions++;
                    output.Status = assembler.ApprovalRequired ? "approval required" : "complete";
                    await changed(true, ct); // Persist each decision before requesting continuation.
                }
                if (calls == 0 && decisions == 0) break;
                if (mcpRounds >= DesktopMcpToolExecution.MaxRounds) throw new GatewayException(DesktopResources.Get("McpToolLimit"));
                if (round + 1 >= MaxRounds) throw new GatewayException(DesktopResources.Get("ApprovalLoopLimit"));
                messages = conversation.Messages.Where(x => x != output && PortableConversations.CanReplay(x)).Select(x => x.Message).Append(output.Message).ToList();
                assembler.Continue();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            output.Status = assembler.ApprovalRequired ? "approval required" : "stopped";
            throw;
        }
        catch
        {
            output.Status = assembler.ApprovalRequired ? "approval required" : "failed";
            throw;
        }
    }
}
