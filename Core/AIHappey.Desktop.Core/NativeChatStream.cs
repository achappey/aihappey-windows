using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.ChatCompletions.Models;
using AIHappey.Messages;
using AIHappey.Responses.Streaming;

namespace AIHappey.Desktop.Core;

/// <summary>Per-response reducer. Only portable UI events leave this boundary, never native SSE packets.</summary>
public sealed partial class NativeChatStream(DesktopChatRoute route)
{
    private sealed class Block(string kind)
    {
        public string Kind = kind;
        public string Text = "";
        public string Id = Guid.NewGuid().ToString("N");
        public bool Closed;
        public JsonObject Metadata = new();
    }
    private readonly Dictionary<string, Block> blocks = [];
    private readonly Dictionary<string, JsonObject> responseItems = [];
    private readonly Dictionary<string, string> messageBlocks = [];
    private readonly HashSet<string> sources = new(StringComparer.Ordinal);
    private readonly Stopwatch watch = Stopwatch.StartNew();
    private readonly JsonObject rawUsage = new();
    private readonly JsonObject providerStreamMetadata = new();
    private JsonObject? gateway;
    private string model = route.Model, reason = "stop";
    private bool started, finished;
    private double? firstToken;

    public IReadOnlyList<StreamEvent> Apply(string payload, string? eventName = null)
    {
        JsonObject raw;
        try { raw = JsonNode.Parse(payload) as JsonObject ?? throw new JsonException(); }
        catch (JsonException) { throw new GatewayException(DesktopResources.Get("InvalidStreamEvent")); }
        var type = raw["type"]?.ToString() ?? eventName ?? "";
        if (raw["error"] is not null || type is "error" or "response.failed" or "response.error")
            throw new GatewayException(GatewayErrors.StreamMessage(JsonSerializer.SerializeToElement(raw)));
        if (raw["model"] is { } nativeModel) model = nativeModel.ToString();
        RememberUsage(raw["usage"] ?? raw["metadata"]?["total_usage"]);
        if ((raw["metadata"]?["gateway"] ?? raw["response"]?["metadata"]?["gateway"]
            ?? raw["metadata"]?["providerMetadata"]?["gateway"]) is JsonObject nested)
        {
            gateway ??= new();
            foreach (var (key, value) in nested) if (value is not null) gateway[key] = value.DeepClone();
        }
        var result = new List<StreamEvent>();
        if (finished)
        {
            if (raw["usage"] is not null) result.Add(Event("message-metadata", ("messageMetadata", MessageMetadata())));
            return result;
        }
        if (!started)
        {
            started = true;
            result.Add(Event("start", ("messageMetadata", MessageMetadata())));
            result.Add(Event("start-step"));
        }
        // Known contracts perform protocol deserialization. The original node remains available for
        // forward-compatible provider metadata that newer contract versions have not modeled yet.
        if (route.Endpoint == "/v1/messages")
        {
            if (raw["type"] is null) raw["type"] = type;
            var part = raw.Deserialize<MessageStreamPart>(MessagesJson.Default) ?? throw new JsonException();
            Messages(part.Type, raw, result);
        }
        else if (route.Endpoint == "/v1/responses")
        {
            if (raw["type"] is null) raw["type"] = type;
            var part = raw.Deserialize<ResponseStreamPart>() ?? throw new JsonException();
            Responses(part.Type, raw, result);
        }
        else
        {
            _ = raw.Deserialize<ChatCompletionUpdate>() ?? throw new JsonException();
            Completions(raw, result);
        }
        return result;
    }

    public void CheckComplete()
    {
        if (!finished) throw new GatewayException(DesktopResources.Get("NativeChatStreamIncomplete"));
    }

    private void Messages(string type, JsonObject raw, List<StreamEvent> result)
    {
        var index = raw["index"]?.ToString() ?? "0";
        var id = "messages-" + index;
        if (type == "message_start")
        {
            model = raw["message"]?["model"]?.ToString() ?? model;
            RememberUsage(raw["message"]?["usage"]);
            if (raw["message"] is JsonObject message) Copy(message, providerStreamMetadata, "id", "container");
        }
        else if (type == "content_block_start" && raw["content_block"] is JsonObject block)
        {
            var kind = block["type"]?.ToString();
            if (kind is not ("text" or "thinking" or "redacted_thinking"))
            { MessageToolStart(index, block, result); return; }
            messageBlocks[index] = kind!;
            Add(id, kind == "text" ? "text" : "reasoning", block[kind == "text" ? "text" : "thinking"]?.ToString() ?? "", false, result);
            var meta = blocks[id].Metadata;
            meta["type"] = kind;
            meta["block_index"] = int.Parse(index, System.Globalization.CultureInfo.InvariantCulture);
            if (providerStreamMetadata["id"] is { } messageId) meta["message_id"] = messageId.DeepClone();
            Copy(block, meta, "signature", "data", "citations");
            if (block["citations"] is JsonArray citations) foreach (var citation in citations.OfType<JsonObject>()) Source(citation, result);
        }
        else if (type == "content_block_delta" && raw["delta"] is JsonObject delta)
        {
            if (messageTools.TryGetValue(index, out var tool))
            {
                if (delta["type"]?.ToString() != "input_json_delta") throw new GatewayException(DesktopResources.Get("InvalidStreamEvent"));
                ToolDelta(tool, delta["partial_json"]?.ToString() ?? "", result); return;
            }
            if (!messageBlocks.TryGetValue(index, out var kind)) throw new GatewayException(DesktopResources.Get("InvalidStreamEvent"));
            if (delta["type"]?.ToString() == "signature_delta") blocks[id].Metadata["signature"] = (blocks[id].Metadata["signature"]?.ToString() ?? "") + delta["signature"]?.ToString();
            else if (delta["type"]?.ToString() == "citations_delta")
            {
                var citations = blocks[id].Metadata["citations"] as JsonArray ?? new JsonArray();
                if (citations.Parent is null) blocks[id].Metadata["citations"] = citations;
                citations.Add(delta["citation"]?.DeepClone());
                if (delta["citation"] is JsonObject citation) Source(citation, result);
            }
            else Add(id, kind == "text" ? "text" : "reasoning", delta[kind == "text" ? "text" : "thinking"]?.ToString() ?? "", false, result);
        }
        else if (type == "content_block_stop")
        {
            if (messageTools.TryGetValue(index, out var tool)) { MessageToolStop(tool, result); closedMessageTools.Add(index); }
            else Close(id, result);
        }
        else if (type == "message_delta")
        {
            if (raw["delta"] is JsonObject stopDelta) Copy(stopDelta, providerStreamMetadata, "stop_reason", "stop_sequence", "container");
            reason = raw["delta"]?["stop_reason"]?.ToString() switch { "max_tokens" => "length", "tool_use" => "tool-calls", _ => "stop" };
            nativeContinuation = raw["delta"]?["stop_reason"]?.ToString() == "pause_turn";
        }
        else if (type == "message_stop")
        {
            if (messageBlocks.Keys.Any(key => !blocks["messages-" + key].Closed) || messageTools.Keys.Any(key => !closedMessageTools.Contains(key)))
                throw new GatewayException(DesktopResources.Get("NativeChatStreamIncomplete"));
            Finish(result);
        }
    }

    private void Responses(string type, JsonObject raw, List<StreamEvent> result)
    {
        if (raw["response"] is JsonObject response)
        {
            model = response["model"]?.ToString() ?? model;
            RememberUsage(response["usage"]);
            Copy(response, providerStreamMetadata, "id");
        }
        if (type == "response.incomplete")
            throw new GatewayException(DesktopResources.Get("NativeChatStreamIncomplete"));
        if (type is "response.output_item.added" or "response.output_item.done")
        {
            if (raw["item"] is JsonObject item) ResponseItem(item, raw["output_index"]?.ToString() ?? "0", result,
                type == "response.output_item.done");
        }
        else if (type is "response.output_text.delta" or "response.refusal.delta" or "response.reasoning_summary_text.delta" or "response.reasoning_text.delta"
            or "response.output_text.done" or "response.refusal.done" or "response.reasoning_summary_text.done" or "response.reasoning_text.done")
        {
            var reasoning = type.Contains("reasoning", StringComparison.Ordinal);
            var itemId = raw["item_id"]?.ToString() ?? raw["output_index"]?.ToString() ?? "0";
            var content = reasoning ? raw["summary_index"]?.ToString() ?? "0" : raw["content_index"]?.ToString() ?? "0";
            var id = (reasoning ? "reasoning-" : "text-") + itemId + "-" + content;
            Add(id, reasoning ? "reasoning" : "text", raw[type.EndsWith(".delta", StringComparison.Ordinal) ? "delta" : type.Contains("refusal", StringComparison.Ordinal) ? "refusal" : "text"]?.ToString() ?? "",
                type.EndsWith(".done", StringComparison.Ordinal), result);
            blocks[id].Metadata["id"] = itemId;
            blocks[id].Metadata["item_id"] = itemId;
            if (responseItems.TryGetValue(itemId, out var known)) Copy(known, blocks[id].Metadata, "phase", "encrypted_content", "summary", "output_index");
            if (type.EndsWith(".done", StringComparison.Ordinal)) Close(id, result);
        }
        else if (type == "response.completed")
        {
            var completedResponse = raw["response"] as JsonObject;
            if (completedResponse?["status"]?.ToString() is "failed" or "cancelled" or "incomplete")
                throw new GatewayException(DesktopResources.Get("NativeChatStreamIncomplete"));
            if (completedResponse?["output"] is JsonArray output)
                for (var i = 0; i < output.Count; i++)
                    if (output[i] is JsonObject item) ResponseItem(item, i.ToString(System.Globalization.CultureInfo.InvariantCulture), result, true);
            if (pendingResponseResults.Count > 0 || nativeTools.Values.Distinct().Any(tool => !tool.Complete || tool.Provider && !tool.Output))
                throw new GatewayException(DesktopResources.Get("NativeChatStreamIncomplete"));
            Finish(result);
        }
        else if (type == "response.output_text.annotation.added" && raw["annotation"] is JsonObject annotation) Source(annotation, result);
        else if (type is "response.function_call_arguments.delta" or "response.custom_tool_call_input.delta"
            or "response.function_call_arguments.done" or "response.custom_tool_call_input.done")
        {
            var key = raw["item_id"]?.ToString() ?? raw["output_index"]?.ToString() ?? "";
            if (!nativeTools.TryGetValue(key, out var tool)) throw new GatewayException(DesktopResources.Get("InvalidStreamEvent"));
            if (type.EndsWith(".delta", StringComparison.Ordinal)) ToolDelta(tool, raw["delta"]?.ToString() ?? "", result);
            else if (!tool.Complete) tool.Arguments = raw["arguments"]?.ToString() ?? raw["input"]?.ToString() ?? tool.Arguments;
        }
    }

    private void ResponseItem(JsonObject item, string index, List<StreamEvent> result, bool complete = false)
    {
        var type = item["type"]?.ToString();
        if (type is not ("message" or "reasoning")) { ResponseTool(item, index, complete, result); return; }
        var id = item["id"]?.ToString() ?? index;
        var metadata = responseItems.GetValueOrDefault(id) ?? new JsonObject();
        Copy(item, metadata, "id", "phase", "summary", "encrypted_content");
        metadata["item_id"] = id;
        metadata["output_index"] = int.Parse(index, System.Globalization.CultureInfo.InvariantCulture);
        if (providerStreamMetadata["id"] is { } responseId) metadata["response_id"] = responseId.DeepClone();
        responseItems[id] = metadata;
        var content = item[type == "reasoning" ? "summary" : "content"] as JsonArray;
        if (content is not null)
            for (var i = 0; i < content.Count; i++)
            {
                var key = (type == "reasoning" ? "reasoning-" : "text-") + id + "-" + i;
                var text = content[i]?["text"]?.ToString() ?? content[i]?["refusal"]?.ToString() ?? "";
                if (text.Length > 0 || type == "reasoning" && item["encrypted_content"] is not null)
                    Add(key, type == "reasoning" ? "reasoning" : "text", text, true, result);
                if (content[i]?["annotations"] is JsonArray annotations)
                    foreach (var annotation in annotations.OfType<JsonObject>()) Source(annotation, result);
            }
        if (type == "reasoning" && item["encrypted_content"] is not null && !blocks.Keys.Any(key => key.StartsWith("reasoning-" + id + "-", StringComparison.Ordinal)))
            Add("reasoning-" + id + "-0", "reasoning", "", true, result);
        foreach (var pair in blocks.Where(pair => pair.Key.StartsWith((type == "reasoning" ? "reasoning-" : "text-") + id + "-", StringComparison.Ordinal)))
        {
            Copy(metadata, pair.Value.Metadata, "id", "item_id", "phase", "summary", "encrypted_content", "output_index", "response_id");
            // Metadata can arrive in output_item.done after text/reasoning end.
            result.Add(Event(pair.Value.Kind + (pair.Value.Closed ? "-end" : "-start"), ("id", JsonValue.Create(pair.Value.Id)), ("providerMetadata", ProviderMetadata(pair.Value))));
        }
    }

    private void Completions(JsonObject raw, List<StreamEvent> result)
    {
        if (raw["choices"] is not JsonArray choices) return;
        foreach (var choice in choices.OfType<JsonObject>())
        {
            if (choice["index"]?.GetValue<int>() is > 0) continue; // Basic chat deliberately requests one choice.
            RememberUsage(choice["usage"] ?? choice["delta"]?["usage"]);
            var delta = choice["delta"] as JsonObject;
            CompletionTools(delta, result);
            var reasoning = delta?["reasoning_content"]?.ToString() ?? delta?["reasoning"]?.ToString();
            if (!string.IsNullOrEmpty(reasoning))
            {
                Add("completion-reasoning", "reasoning", reasoning, false, result);
                blocks["completion-reasoning"].Metadata["reasoning_field"] = delta?["reasoning_content"] is not null ? "reasoning_content" : "reasoning";
            }
            if (delta?["content"] is JsonValue text && text.TryGetValue<string>(out var value) && value.Length > 0)
                Add("completion-text", "text", value, false, result);
            if (delta?["refusal"] is JsonValue refusal && refusal.TryGetValue<string>(out var refused) && refused.Length > 0)
                Add("completion-text", "text", refused, false, result);
            if (choice["finish_reason"] is { } finish)
            {
                reason = finish.ToString();
                if (reason is "tool_calls" or "function_call") reason = "tool-calls";
                FinishCompletionTools(result);
                Finish(result);
            }
        }
    }

    private void Add(string id, string kind, string text, bool snapshot, List<StreamEvent> result)
    {
        if (!blocks.TryGetValue(id, out var block))
        {
            block = new(kind); blocks.Add(id, block);
            result.Add(Event(kind + "-start", ("id", JsonValue.Create(block.Id))));
        }
        var delta = snapshot ? text.StartsWith(block.Text, StringComparison.Ordinal) ? text[block.Text.Length..] : "" : text;
        if (block.Closed && delta.Length > 0) throw new GatewayException(DesktopResources.Get("InvalidStreamEvent"));
        if (delta.Length > 0)
        {
            firstToken ??= watch.Elapsed.TotalSeconds;
            block.Text += delta;
            result.Add(Event(kind + "-delta", ("id", JsonValue.Create(block.Id)), ("delta", JsonValue.Create(delta))));
        }
    }
    private void Close(string id, List<StreamEvent> result)
    {
        if (!blocks.TryGetValue(id, out var block) || block.Closed) return;
        block.Closed = true;
        result.Add(Event(block.Kind + "-end", ("id", JsonValue.Create(block.Id)), ("providerMetadata", ProviderMetadata(block))));
    }
    private void Finish(List<StreamEvent> result)
    {
        if (finished) return;
        foreach (var id in blocks.Keys) Close(id, result);
        result.Add(Event("finish-step"));
        result.Add(Event("finish", ("finishReason", JsonValue.Create(reason)), ("messageMetadata", MessageMetadata()),
            ("nativeContinuation", JsonValue.Create(nativeContinuation))));
        finished = true;
    }
    private JsonObject? ProviderMetadata(Block block) => route.ProviderKey is null || block.Metadata.Count == 0 ? null
        : new JsonObject { [route.ProviderKey] = block.Metadata.DeepClone() };
    private void RememberUsage(JsonNode? usage)
    {
        if (usage is JsonObject fields) foreach (var (key, value) in fields) rawUsage[key] = value?.DeepClone();
    }
    private JsonObject MessageMetadata()
    {
        double? Number(params string[] keys)
        {
            foreach (var key in keys)
                if (rawUsage[key] is JsonValue value && value.TryGetValue<double>(out var number) && double.IsFinite(number) && number >= 0) return number;
            return null;
        }
        var input = Number("input_tokens", "prompt_tokens");
        var output = Number("output_tokens", "completion_tokens");
        // Messages excludes cache reads/writes from input_tokens; normalized usage must include them.
        if (route.Endpoint == "/v1/messages" && input is not null)
            input += (Number("cache_read_input_tokens") ?? 0) + (Number("cache_creation_input_tokens") ?? 0);
        var usage = new JsonObject();
        if (input is not null) usage["promptTokens"] = input;
        if (output is not null) usage["completionTokens"] = output;
        if ((Number("total_tokens") ?? (input is not null || output is not null ? (input ?? 0) + (output ?? 0) : null)) is { } total) usage["totalTokens"] = total;
        var providers = new JsonObject();
        if (route.ProviderKey is { } provider)
        {
            var metadata = (JsonObject)providerStreamMetadata.DeepClone();
            metadata["usage"] = rawUsage.DeepClone(); providers[provider] = metadata;
        }
        var metrics = gateway is null ? new JsonObject() : (JsonObject)gateway.DeepClone();
        if (firstToken is { } ttft && metrics["ttft"] is null) metrics["ttft"] = ttft;
        if (output is > 0 && watch.Elapsed.TotalSeconds > 0 && metrics["tps"] is null) metrics["tps"] = output / watch.Elapsed.TotalSeconds;
        providers["gateway"] = metrics;
        // Direct transport uses provider-local IDs; portable history and UI selection remain canonical.
        return new JsonObject { ["model"] = route.IsDirect ? route.CanonicalModel : model, ["usage"] = usage, ["providerMetadata"] = providers,
            ["timestamp"] = DateTimeOffset.UtcNow.ToString("O") };
    }
    private static void Copy(JsonObject source, JsonObject target, params string[] fields)
    {
        foreach (var field in fields) if (source[field] is { } value) target[field] = value.DeepClone();
    }
    private void Source(JsonObject citation, List<StreamEvent> result)
    {
        var url = citation["url"]?.ToString();
        if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
            || !string.IsNullOrEmpty(uri.UserInfo) || !sources.Add(url)) return;
        result.Add(Event("source-url", ("sourceId", JsonValue.Create("source-" + sources.Count)), ("url", JsonValue.Create(url)),
            ("title", JsonValue.Create(citation["title"]?.ToString() ?? url))));
    }
    private static StreamEvent Event(string type, params (string Name, JsonNode? Value)[] fields)
    {
        var raw = new JsonObject { ["type"] = type };
        foreach (var (name, value) in fields) if (value is not null) raw[name] = value;
        return StreamEvent.Parse(raw.ToJsonString());
    }
    private static void UnsupportedTool() => throw new GatewayException(DesktopResources.Get("NativeChatToolsUnsupported"));
}
