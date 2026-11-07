using System.Text.Json.Nodes;
using AIHappey.Vercel.Models;

namespace AIHappey.Desktop.Core;

/// <summary>Projects ordered portable parts, including multiple model/tool steps in one assistant message.</summary>
internal static class NativeChatHistory
{
    private static IEnumerable<JsonObject[]> Steps(UIMessage message)
    {
        var parts = new List<JsonObject>();
        foreach (var part in message.Parts.Select(PortableConversations.Node))
        {
            if (part["type"]?.ToString() == "step-start")
            {
                if (parts.Count > 0) yield return parts.ToArray();
                parts.Clear();
            }
            else parts.Add(part);
        }
        if (parts.Count > 0) yield return parts.ToArray();
    }

    internal static JsonArray Responses(IReadOnlyList<UIMessage> messages, DesktopChatRoute route)
    {
        var input = new JsonArray();
        foreach (var message in messages.Where(m => m.Role != Role.system))
        foreach (var step in Steps(message))
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var entries = new List<(JsonObject Item, JsonObject? Metadata)>();
            foreach (var part in step)
            {
                var type = part["type"]?.ToString();
                var meta = NativeChatTools.Metadata(part, route.ProviderKey);
                if (message.Role == Role.assistant && type == "reasoning" && meta?["encrypted_content"] is { } encrypted
                    && meta["type"]?.ToString() is not ("thinking" or "redacted_thinking"))
                {
                    var item = new JsonObject { ["type"] = "reasoning", ["encrypted_content"] = encrypted.DeepClone(),
                        ["summary"] = meta["summary"]?.DeepClone() ?? new JsonArray() };
                    if ((meta["id"] ?? meta["item_id"]) is { } id) item["id"] = id.DeepClone();
                    if (item["id"] is null || seen.Add("reasoning:" + item["id"])) entries.Add((item, meta));
                }
                else if (type == "text" && part["text"]?.ToString() is { Length: > 0 } text)
                {
                    var item = new JsonObject { ["type"] = "message", ["role"] = message.Role == Role.assistant ? "assistant" : "user",
                        ["content"] = new JsonArray(new JsonObject { ["type"] = message.Role == Role.assistant ? "output_text" : "input_text", ["text"] = text }) };
                    if (message.Role == Role.assistant && meta?["phase"] is { } phase) item["phase"] = phase.DeepClone();
                    entries.Add((item, meta));
                }
                else if (message.Role == Role.user && type == "file") entries.Add((new JsonObject { ["type"] = "message", ["role"] = "user",
                    ["content"] = new JsonArray(NativeChatRequests.File(part, route.Endpoint)) }, null));
                else if (message.Role == Role.assistant && NativeChatTools.IsTool(part) && NativeChatTools.Resolved(part))
                {
                    var call = NativeChatTools.Call(part, route.ProviderKey);
                    var result = NativeChatTools.Result(part, route.ProviderKey);
                    if (NativeChatTools.Provider(part))
                    {
                        foreach (var metadata in new[] { call, result })
                        {
                            var native = ResponseNative(metadata);
                            if (native is not null && seen.Add(native["type"] + ":" + (native["id"] ?? native["call_id"]))) entries.Add((native, metadata));
                        }
                        continue;
                    }
                    var callId = call?["call_id"]?.ToString() ?? part["toolCallId"]?.ToString();
                    if (string.IsNullOrEmpty(callId) || !seen.Add("client:" + callId)) continue;
                    var nativeType = call?["type"]?.ToString();
                    var item = new JsonObject { ["type"] = "function_call", ["call_id"] = callId,
                        ["name"] = NativeChatTools.Name(part), ["arguments"] = NativeChatTools.Stringify(part["input"] ?? new JsonObject()) };
                    if (nativeType == "custom_tool_call")
                    {
                        item["type"] = nativeType; item.Remove("arguments");
                        item["input"] = NativeChatTools.Stringify(part["input"]);
                    }
                    else if (nativeType == "apply_patch_call")
                    {
                        item["type"] = nativeType; item.Remove("name"); item.Remove("arguments");
                        item["operation"] = (part["input"] as JsonObject)?["operation"]?.DeepClone(); item["status"] = "completed";
                    }
                    if (nativeType is "function_call" or "custom_tool_call" or "apply_patch_call")
                        foreach (var field in new[] { "id", "namespace", "caller", "created_by" })
                            if (call?[field] is { } value) item[field] = value.DeepClone();
                    entries.Add((item, call));
                    var output = new JsonObject { ["type"] = nativeType == "custom_tool_call" ? "custom_tool_call_output"
                            : nativeType == "apply_patch_call" ? "apply_patch_call_output" : "function_call_output",
                        ["call_id"] = callId, ["output"] = NativeChatTools.Output(part) };
                    if (nativeType == "apply_patch_call")
                    {
                        output["status"] = NativeChatTools.Failed(part) ? "failed" : "completed";
                        if (!NativeChatTools.Failed(part) && (part["output"] as JsonObject)?["structuredContent"] is JsonObject patch)
                        { output["status"] = patch["status"]?.DeepClone() ?? JsonValue.Create("completed"); output["output"] = patch["output"]?.DeepClone() ?? JsonValue.Create(""); }
                    }
                    entries.Add((output, null));
                }
            }
            // Native hosted results update their original UI card in place. Restore only indexed runs;
            // unindexed client results remain immediately after their calls, with stable step ordering.
            foreach (var entry in Ordered(entries, "output_index", "response_id")) input.Add(entry);
        }
        return input;
    }

    private static JsonObject? ResponseNative(JsonObject? metadata)
    {
        if (metadata is null || metadata["native_endpoint"]?.ToString() is { } endpoint && endpoint != "/v1/responses") return null;
        if (metadata["native_item"] is JsonObject item) return item.DeepClone().AsObject();
        var type = metadata["type"]?.ToString();
        if (type is not ("web_search_call" or "file_search_call" or "code_interpreter_call" or "image_generation_call" or "shell_call"
            or "shell_call_output" or "mcp_call" or "mcp_list_tools" or "tool_search_call" or "tool_search_output" or "program_call" or "program_call_output")) return null;
        var copy = metadata.DeepClone().AsObject();
        foreach (var field in new[] { "native_endpoint", "output_index", "item_id", "response_id" }) copy.Remove(field);
        return copy;
    }

    internal static JsonArray Messages(IReadOnlyList<UIMessage> messages, DesktopChatRoute route)
    {
        var mapped = new JsonArray();
        foreach (var message in messages.Where(m => m.Role != Role.system))
        foreach (var step in Steps(message))
        {
            var blocks = new List<(JsonObject Item, JsonObject? Metadata)>();
            var results = new JsonArray();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            void Flush()
            {
                if (blocks.Count > 0) mapped.Add(new JsonObject { ["role"] = message.Role == Role.assistant ? "assistant" : "user",
                    ["content"] = new JsonArray(Ordered(blocks, "block_index", "message_id").Select(b => (JsonNode)b).ToArray()) });
                if (results.Count > 0) mapped.Add(new JsonObject { ["role"] = "user", ["content"] = results });
                blocks.Clear(); results = new();
            }
            foreach (var part in step)
            {
                var type = part["type"]?.ToString();
                var meta = NativeChatTools.Metadata(part, route.ProviderKey);
                if (type == "text" && part["text"]?.ToString() is { Length: > 0 } text)
                {
                    if (results.Count > 0) Flush(); // Legacy history without step markers.
                    var block = new JsonObject { ["type"] = "text", ["text"] = text };
                    if (meta?["type"]?.ToString() == "text" && meta["citations"] is { } citations) block["citations"] = citations.DeepClone();
                    blocks.Add((block, meta));
                }
                else if (message.Role == Role.assistant && type == "reasoning" && meta is not null)
                {
                    JsonObject? block = meta["type"]?.ToString() == "redacted_thinking" && meta["data"] is { } data
                        ? new JsonObject { ["type"] = "redacted_thinking", ["data"] = data.DeepClone() }
                        : meta["signature"] is { } signature ? new JsonObject { ["type"] = "thinking", ["thinking"] = part["text"]?.ToString() ?? "", ["signature"] = signature.DeepClone() } : null;
                    if (block is not null) { if (results.Count > 0) Flush(); blocks.Add((block, meta)); }
                }
                else if (message.Role == Role.user && type == "file") blocks.Add((NativeChatRequests.File(part, route.Endpoint), null));
                else if (message.Role == Role.assistant && NativeChatTools.IsTool(part) && NativeChatTools.Resolved(part))
                {
                    var call = NativeChatTools.Call(part, route.ProviderKey);
                    var result = NativeChatTools.Result(part, route.ProviderKey);
                    var id = part["toolCallId"]?.ToString();
                    if (NativeChatTools.Provider(part))
                    {
                        if (call?["type"]?.ToString() is "server_tool_use" or "mcp_tool_use" && call["id"] is { } callId && seen.Add("call:" + callId))
                        {
                            var block = new JsonObject { ["type"] = call["type"]!.DeepClone(), ["id"] = callId.DeepClone(),
                                ["name"] = call["name"]?.ToString() ?? NativeChatTools.Name(part), ["input"] = part["input"]?.DeepClone() ?? new JsonObject() };
                            foreach (var field in new[] { "caller", "server_name" }) if (call[field] is { } value) block[field] = value.DeepClone();
                            blocks.Add((block, call));
                        }
                        if (result?["tool_use_id"] is { } useId && result["content"] is { } content && seen.Add("result:" + useId))
                        {
                            var block = new JsonObject { ["type"] = result["type"]!.DeepClone(), ["tool_use_id"] = useId.DeepClone(), ["content"] = content.DeepClone() };
                            foreach (var field in new[] { "is_error", "caller" }) if (result[field] is { } value) block[field] = value.DeepClone();
                            blocks.Add((block, result));
                        }
                    }
                    else if (!string.IsNullOrEmpty(id) && seen.Add("client:" + id))
                    {
                        var block = new JsonObject { ["type"] = "tool_use", ["id"] = id, ["name"] = NativeChatTools.Name(part),
                            ["input"] = part["input"]?.DeepClone() ?? new JsonObject() };
                        if (call?["caller"] is { } caller && call["type"]?.ToString() == "tool_use") block["caller"] = caller.DeepClone();
                        blocks.Add((block, call));
                        var output = new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = id, ["content"] = NativeChatTools.Output(part) };
                        if (NativeChatTools.Failed(part)) output["is_error"] = true;
                        results.Add(output);
                    }
                }
            }
            Flush();
        }
        return mapped;
    }

    internal static JsonArray Completions(IReadOnlyList<UIMessage> messages, DesktopChatRoute route)
    {
        var mapped = new JsonArray();
        foreach (var message in messages)
        foreach (var step in Steps(message))
        {
            var content = new JsonArray(); var calls = new JsonArray(); var results = new JsonArray();
            var reasoning = ""; string? reasoningField = null;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var part in step)
            {
                var type = part["type"]?.ToString();
                if (type == "text" && part["text"]?.ToString() is { Length: > 0 } text) content.Add(new JsonObject { ["type"] = "text", ["text"] = text });
                else if (message.Role == Role.user && type == "file") content.Add(NativeChatRequests.File(part, route.Endpoint));
                else if (message.Role == Role.assistant && type == "reasoning")
                {
                    var meta = NativeChatTools.Metadata(part, route.ProviderKey);
                    if (meta?["reasoning_field"]?.ToString() is "reasoning" or "reasoning_content")
                    { reasoningField = meta["reasoning_field"]!.ToString(); reasoning += part["text"]?.ToString(); }
                }
                else if (message.Role == Role.assistant && NativeChatTools.IsTool(part) && !NativeChatTools.Provider(part) && NativeChatTools.Resolved(part))
                {
                    var id = part["toolCallId"]?.ToString();
                    if (string.IsNullOrEmpty(id) || !seen.Add(id)) continue;
                    var call = new JsonObject { ["id"] = id, ["type"] = "function", ["function"] = new JsonObject {
                        ["name"] = NativeChatTools.Name(part), ["arguments"] = NativeChatTools.Stringify(part["input"] ?? new JsonObject()) } };
                    if (NativeChatTools.Call(part, route.ProviderKey)?["type"]?.ToString() == "custom")
                    { call["type"] = "custom"; call.Remove("function"); call["custom"] = new JsonObject { ["name"] = NativeChatTools.Name(part), ["input"] = NativeChatTools.Stringify(part["input"]) }; }
                    calls.Add(call);
                    results.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = id, ["content"] = NativeChatTools.Output(part) });
                }
            }
            if (content.Count == 0 && calls.Count == 0) continue;
            var item = new JsonObject { ["role"] = message.Role.ToString(), ["content"] = content.Count == 0 ? null
                : content.Count == 1 && content[0]?["type"]?.ToString() == "text" ? content[0]!["text"]!.DeepClone() : content };
            if (calls.Count > 0) item["tool_calls"] = calls;
            if (reasoningField is not null) item[reasoningField] = reasoning;
            mapped.Add(item);
            foreach (var result in results) mapped.Add(result!.DeepClone());
        }
        return mapped;
    }

    private static IEnumerable<JsonObject> Ordered(List<(JsonObject Item, JsonObject? Metadata)> entries, string index, string group)
    {
        var run = new List<(JsonObject Item, JsonObject? Metadata)>();
        foreach (var entry in entries)
        {
            if (entry.Metadata?[index] is not JsonValue value || !value.TryGetValue<int>(out _)
                || run.Count > 0 && run[0].Metadata?[group]?.ToString() != entry.Metadata?[group]?.ToString())
            {
                foreach (var previous in run.OrderBy(e => e.Metadata![index]!.GetValue<int>())) yield return previous.Item;
                run.Clear();
            }
            if (entry.Metadata?[index] is JsonValue number && number.TryGetValue<int>(out _)) run.Add(entry);
            else yield return entry.Item;
        }
        foreach (var entry in run.OrderBy(e => e.Metadata![index]!.GetValue<int>())) yield return entry.Item;
    }
}
