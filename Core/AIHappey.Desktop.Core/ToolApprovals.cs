using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Vercel.Models;

namespace AIHappey.Desktop.Core;

public enum ToolApprovalMode { Once, ThisTool, AllTools }
public sealed record ToolApprovalDecision(bool Approved, string? Reason = null, ToolApprovalMode Mode = ToolApprovalMode.Once);
public sealed record PendingToolApproval(string Id, string ToolCallId, string ToolName, string Title, JsonElement Part);

/// <summary>Approval is a history mutation, not another execution of a tool result.</summary>
public static class DesktopToolApprovals
{
    public static string CanonicalName(UIMessagePart part) => part.Type == "dynamic-tool"
        ? PortableConversations.String(PortableConversations.Element(part), "toolName") ?? ""
        : part.Type.StartsWith("tool-", StringComparison.Ordinal) ? part.Type[5..] : "";

    public static IReadOnlyList<PendingToolApproval> Pending(UIMessage message)
    {
        var result = new List<PendingToolApproval>();
        foreach (var part in message.Parts.Where(PortableConversations.IsTool))
        {
            var raw = PortableConversations.Element(part);
            if (PortableConversations.String(raw, "state") != "approval-requested") continue;
            if (!raw.TryGetProperty("approval", out var approval)
                || PortableConversations.String(approval, "id") is not { Length: > 0 } id
                || PortableConversations.String(raw, "toolCallId") is not { Length: > 0 } call)
                throw new JsonException("Missing tool approval identity.");
            result.Add(new(id, call, CanonicalName(part), PortableConversations.ToolName(part), raw));
        }
        return result;
    }

    public static bool Respond(UIMessage message, PendingToolApproval pending, ToolApprovalDecision decision)
    {
        var changed = false;
        for (var i = 0; i < message.Parts.Count; i++)
        {
            var part = message.Parts[i];
            if (!PortableConversations.IsTool(part)) continue;
            var node = PortableConversations.Node(part);
            if (node["state"]?.ToString() != "approval-requested" || node["approval"] is not JsonObject approval
                || approval["id"]?.ToString() != pending.Id || node["toolCallId"]?.ToString() != pending.ToolCallId) continue;
            approval["approved"] = decision.Approved;
            var reason = decision.Reason?.Trim();
            if (string.IsNullOrEmpty(reason)) approval.Remove("reason"); else approval["reason"] = reason;
            node["state"] = "approval-responded";
            message.Parts[i] = PortableConversations.Part(node); changed = true;
        }
        return changed;
    }

    public static bool HasOutput(JsonElement raw) => raw.TryGetProperty("output", out var value)
        && value.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined;

    public static bool? Approved(JsonElement raw) => raw.TryGetProperty("approval", out var approval)
        && approval.ValueKind == JsonValueKind.Object && approval.TryGetProperty("approved", out var value)
        ? value.ValueKind == JsonValueKind.True ? true : value.ValueKind == JsonValueKind.False ? false : null : null;
}

/// <summary>Allow-all is intentionally not part of persisted settings. Names are ordinal wire names, never titles.</summary>
public sealed class DesktopToolApprovalPolicy
{
    public bool ApproveAll { get; set; }
    public ToolApprovalDecision? Automatic(PendingToolApproval pending, DesktopSettings settings) => ApproveAll
        ? new(true, "BRRR")
        : pending.ToolName.Length > 0 && settings.AllowedToolList.Contains(pending.ToolName, StringComparer.Ordinal)
            ? new(true, pending.ToolName) : null;
}
