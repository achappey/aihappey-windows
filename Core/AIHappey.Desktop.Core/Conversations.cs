using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AIHappey.Vercel.Models;

namespace AIHappey.Desktop.Core;

[JsonConverter(typeof(ConversationConverter))]
public sealed class Conversation
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Title { get; set; } = DesktopResources.Get("NewChat");
    public DateTimeOffset Updated { get; set; } = DateTimeOffset.UtcNow;
    public ServiceKind Service { get; set; }
    public string Target { get; set; } = "";
    public Dictionary<string, object> Metadata { get; set; } = [];
    public Dictionary<string, JsonElement> Extra { get; set; } = [];
    public List<ConversationMessage> Messages { get; set; } = [];
    public override string ToString() => Title;
}

/// <summary>Runtime presentation state only. The codec writes a direct UIMessage, never this wrapper.</summary>
public sealed class ConversationMessage
{
    public UIMessage Message { get; set; } = new();
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
    public string Status { get; set; } = "complete";
    public string Text => string.Join("\n", Message.Parts.Where(p => p.Type == "text").Select(PortableConversations.Text));
    public string Reasoning => string.Join("\n", Message.Parts.Where(p => p.Type == "reasoning").Select(PortableConversations.Text));
}

/// <summary>Reduces transport chunks into the same ordered, stateful parts that the browser saves.</summary>
public sealed class MessageAssembler(ConversationMessage output)
{
    private readonly Dictionary<string, int> text = [], reasoning = [], tools = [], data = [];
    public bool Finished { get; private set; }
    public bool ApprovalRequired { get; private set; }

    public void Continue()
    {
        if (!Finished || output.Status != "complete") throw new InvalidOperationException("Only completed steps can continue.");
        Finished = false; output.Status = "streaming";
        // Text IDs are stream-local; tool IDs remain turn-wide to prevent repeated execution.
        text.Clear(); reasoning.Clear(); data.Clear();
    }

    public void Apply(StreamEvent item)
    {
        if (Finished || item.Type == "data-aihappey-debug") return;
        var raw = JsonNode.Parse(item.Raw.GetRawText())!.AsObject();
        switch (item.Type)
        {
            case "start":
                if (raw["messageMetadata"] is JsonObject startMetadata) SetMetadata(startMetadata);
                break;
            case "text-start": case "text-delta": case "text-end":
                ApplyText(raw, text, "text", item.Type); break;
            case "reasoning-start": case "reasoning-delta": case "reasoning-end":
                ApplyText(raw, reasoning, "reasoning", item.Type); break;
            case "start-step": output.Message.Parts.Add(PortableConversations.Part(new JsonObject { ["type"] = "step-start" })); break;
            case "finish-step": break;
            case "message-metadata":
                if (raw["messageMetadata"] is JsonObject metadata) SetMetadata(metadata);
                break;
            case "finish":
                if (raw["messageMetadata"] is JsonObject finishMetadata) SetMetadata(finishMetadata);
                foreach (var index in text.Values.Concat(reasoning.Values))
                { var part = PortableConversations.Node(output.Message.Parts[index]); part["state"] = "done"; Store(index, part); }
                Finished = true; output.Status = raw["finishReason"]?.ToString() == "error" ? "failed" : "complete"; break;
            case "error":
                output.Status = "failed";
                throw new GatewayException(DesktopResources.Get("GenerationError"));
            case "abort": output.Status = "stopped"; Finished = true; break;
            case "tool-input-start": case "tool-input-delta": case "tool-input-available": case "tool-call":
            case "tool-input-error": case "tool-output-available": case "tool-output-error": case "tool-output-denied":
            case "tool-approval-request": ApplyTool(raw, item.Type); break;
            default:
                if (raw["transient"]?.ToString() == "true") break;
                if (item.Type == "dynamic-tool" || item.Type.StartsWith("tool-", StringComparison.Ordinal))
                {
                    var id = Required(raw, "toolCallId");
                    var index = Ensure(tools, id, raw); Store(index, raw);
                    if (raw["state"]?.ToString() == "approval-requested") RefuseApproval();
                }
                else if (item.Type.StartsWith("data-", StringComparison.Ordinal) && raw["id"] is not null)
                    Store(Ensure(data, item.Type + ":" + raw["id"], raw), raw);
                else output.Message.Parts.Add(PortableConversations.Part(raw));
                break;
        }
    }

    private void ApplyText(JsonObject raw, Dictionary<string, int> map, string type, string eventType)
    {
        var index = Ensure(map, Required(raw, "id"), new JsonObject { ["type"] = type, ["text"] = "", ["state"] = "streaming" });
        var part = PortableConversations.Node(output.Message.Parts[index]);
        if (eventType.EndsWith("-delta", StringComparison.Ordinal)) part["text"] = (part["text"]?.ToString() ?? "") + (raw["delta"]?.ToString() ?? "");
        if (raw["providerMetadata"] is not null) part["providerMetadata"] = raw["providerMetadata"]!.DeepClone();
        if (eventType.EndsWith("-end", StringComparison.Ordinal)) part["state"] = "done";
        Store(index, part);
    }

    private void ApplyTool(JsonObject raw, string eventType)
    {
        var id = Required(raw, "toolCallId");
        var name = raw["toolName"]?.ToString();
        var index = Ensure(tools, id, new JsonObject { ["type"] = name is null ? "dynamic-tool" : "tool-" + name,
            ["toolCallId"] = id, ["toolName"] = name ?? "Unknown tool", ["state"] = "input-streaming" });
        var part = PortableConversations.Node(output.Message.Parts[index]);
        if (name is not null)
        {
            part["type"] = raw["dynamic"]?.ToString() == "true" ? "dynamic-tool" : "tool-" + name;
            if (part["type"]?.ToString() == "dynamic-tool") part["toolName"] = name; else part.Remove("toolName");
        }
        foreach (var key in new[] { "title", "providerExecuted", "preliminary" })
            if (raw[key] is not null) part[key] = raw[key]!.DeepClone();
        if (raw["providerMetadata"] is not null)
            part[eventType.StartsWith("tool-output", StringComparison.Ordinal) ? "resultProviderMetadata" : "callProviderMetadata"] = raw["providerMetadata"]!.DeepClone();
        switch (eventType)
        {
            case "tool-input-start": part["state"] = "input-streaming"; break;
            case "tool-input-delta":
                part["inputText"] = (part["inputText"]?.ToString() ?? "") + raw["inputTextDelta"]?.ToString(); break;
            case "tool-input-available": case "tool-call":
                part["input"] = raw["input"]?.DeepClone(); part["state"] = "input-available"; part.Remove("inputText"); break;
            case "tool-input-error": case "tool-output-error":
                if (raw["input"] is not null) part["input"] = raw["input"]!.DeepClone();
                part["errorText"] = raw["errorText"]?.DeepClone(); part["state"] = "output-error"; part.Remove("inputText"); break;
            case "tool-output-available": part["output"] = raw["output"]?.DeepClone(); part["state"] = "output-available"; break;
            case "tool-output-denied": part["state"] = "output-denied"; break;
            case "tool-approval-request":
                part["approval"] = new JsonObject { ["id"] = raw["approvalId"]?.DeepClone() };
                part["state"] = "approval-requested"; Store(index, part); RefuseApproval(); break;
        }
        Store(index, part);
    }

    private void RefuseApproval()
    {
        ApprovalRequired = true; output.Status = "approval required";
        throw new GatewayException(DesktopResources.Get("ApprovalUnsupported"));
    }
    private static string Required(JsonObject raw, string key) => raw[key]?.ToString() is { Length: > 0 } value ? value : throw new JsonException("Missing stream part ID.");
    private int Ensure(Dictionary<string, int> map, string id, JsonObject initial)
    {
        if (map.TryGetValue(id, out var index)) return index;
        index = output.Message.Parts.Count; map[id] = index; output.Message.Parts.Add(PortableConversations.Part(initial)); return index;
    }
    private void Store(int index, JsonObject part) => output.Message.Parts[index] = PortableConversations.Part(part);
    private void SetMetadata(JsonObject metadata)
    {
        var merged = output.Message.Metadata?.ToDictionary(p => p.Key, p => p.Value) ?? [];
        foreach (var (key, value) in metadata) if (value is not null) merged[key] = JsonSerializer.SerializeToElement(value);
        output.Message = PortableConversations.WithMetadata(output.Message, merged);
    }
}

public sealed class HistoryStore(string root)
{
    public static string Partition(params string[] values) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(values))));
    private string DirectoryFor(string partition)
    {
        if (partition.Length != 64 || !partition.All(Uri.IsHexDigit)) throw new ArgumentException(DesktopResources.Get("InvalidHistoryPartition"));
        return Path.Combine(root, partition);
    }
    public async Task<IReadOnlyList<Conversation>> ListAsync(string partition, CancellationToken ct = default)
    {
        var directory = DirectoryFor(partition);
        if (!Directory.Exists(directory)) return [];
        var items = new List<Conversation>();
        foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await using var stream = File.OpenRead(file);
                var item = await JsonSerializer.DeserializeAsync<Conversation>(stream, PortableConversations.Json, ct);
                if (item is null || Path.GetFileName(file) != Path.GetFileName(PathFor(partition, item.Id))) continue;
                foreach (var message in item.Messages.Where(x => x.Status == "streaming")) message.Status = "interrupted";
                items.Add(item);
            }
            catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or ArgumentException) { /* Isolate invalid documents. No legacy migration. */ }
        }
        return items.OrderByDescending(x => x.Updated).ToArray();
    }
    public async Task SaveAsync(string partition, Conversation conversation, CancellationToken ct = default)
    {
        var path = PathFor(partition, conversation.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        conversation.Updated = DateTimeOffset.UtcNow;
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await JsonSerializer.SerializeAsync(stream, conversation, PortableConversations.Json, ct);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public void Delete(string partition, string id) => File.Delete(PathFor(partition, id));
    private string PathFor(string partition, string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 2048 || id.Contains('/') || id.Contains('\\') || id.Any(char.IsControl)) throw new ArgumentException(DesktopResources.Get("InvalidConversationId"));
        // Browser IDs are opaque (UUIDs and SDK IDs). Never interpret them as paths.
        return Path.Combine(DirectoryFor(partition), Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id))) + ".json");
    }
}
