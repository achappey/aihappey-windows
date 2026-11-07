using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AIHappey.Desktop.Core;

public sealed partial class NativeChatStream
{
    private sealed class Tool
    {
        public string Id = "", Name = "", Arguments = "";
        public bool Provider, Started, Complete, HasDeltas, Output;
        public JsonNode? Input;
        public JsonObject Metadata = new();
        public JsonObject? Result;
    }
    private readonly Dictionary<string, Tool> nativeTools = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Tool> messageTools = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Tool> completionTools = new(StringComparer.Ordinal);
    private readonly HashSet<string> closedMessageTools = new(StringComparer.Ordinal);
    private bool nativeContinuation;

    private JsonObject? ToolMetadata(JsonObject metadata) => route.ProviderKey is null ? null
        : new JsonObject { [route.ProviderKey] = metadata.DeepClone() };

    private void StartTool(Tool tool, List<StreamEvent> result)
    {
        if (tool.Started || tool.Id.Length == 0 || tool.Name.Length == 0) return;
        tool.Started = true;
        result.Add(Event("tool-input-start", ("toolCallId", JsonValue.Create(tool.Id)), ("toolName", JsonValue.Create(tool.Name)),
            ("providerExecuted", JsonValue.Create(tool.Provider)), ("providerMetadata", ToolMetadata(tool.Metadata))));
    }

    private void ToolDelta(Tool tool, string delta, List<StreamEvent> result)
    {
        if (tool.Complete) throw new GatewayException(DesktopResources.Get("InvalidStreamEvent"));
        tool.HasDeltas = true; tool.Arguments += delta;
        StartTool(tool, result);
        if (tool.Started && delta.Length > 0) result.Add(Event("tool-input-delta", ("toolCallId", JsonValue.Create(tool.Id)), ("inputTextDelta", JsonValue.Create(delta))));
    }

    private void CompleteTool(Tool tool, List<StreamEvent> result)
    {
        if (tool.Complete) return;
        StartTool(tool, result);
        if (!tool.Started) throw new GatewayException(DesktopResources.Get("InvalidStreamEvent"));
        JsonNode? input = tool.Input?.DeepClone();
        if (tool.HasDeltas || input is null)
        {
            if (tool.Metadata["type"]?.ToString() is "custom" or "custom_tool_call") input = JsonValue.Create(tool.Arguments);
            else
            {
                try { input = tool.Arguments.Length == 0 ? new JsonObject() : JsonNode.Parse(tool.Arguments); }
                catch (JsonException) { throw new GatewayException(DesktopResources.Get("McpInvalidArguments")); }
                if (input is not JsonObject) throw new GatewayException(DesktopResources.Get("McpInvalidArguments"));
            }
        }
        tool.Input = input;
        result.Add(Event("tool-input-available", ("toolCallId", JsonValue.Create(tool.Id)), ("toolName", JsonValue.Create(tool.Name)),
            ("input", input?.DeepClone()), ("providerExecuted", JsonValue.Create(tool.Provider)), ("providerMetadata", ToolMetadata(tool.Metadata))));
        if (!tool.Provider)
            result.Add(Event("tool-approval-request", ("toolCallId", JsonValue.Create(tool.Id)), ("approvalId", JsonValue.Create(tool.Id))));
        tool.Complete = true;
    }

    private void ToolOutput(Tool tool, JsonObject metadata, JsonNode? output, string? error, List<StreamEvent> result)
    {
        if (tool.Output) return;
        tool.Output = true;
        var wrapped = output is JsonObject obj && (obj.ContainsKey("content") || obj.ContainsKey("structuredContent") || obj.ContainsKey("isError"))
            ? output.DeepClone() : output is JsonValue value && value.TryGetValue<string>(out var text)
            ? new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }) }
            : new JsonObject { ["content"] = new JsonArray(), ["structuredContent"] = output?.DeepClone() ?? new JsonObject() };
        result.Add(Event(error is null ? "tool-output-available" : "tool-output-error", ("toolCallId", JsonValue.Create(tool.Id)),
            ("providerExecuted", JsonValue.Create(true)), ("providerMetadata", ToolMetadata(metadata)),
            ("output", wrapped), ("errorText", error is null ? null : JsonValue.Create(GatewayErrors.Display(error)))));
    }

    private void MessageToolStart(string index, JsonObject block, List<StreamEvent> result)
    {
        var type = block["type"]?.ToString() ?? "";
        var isCall = type is "tool_use" or "server_tool_use" or "mcp_tool_use";
        var isResult = type is "web_search_tool_result" or "web_fetch_tool_result" or "code_execution_tool_result"
            or "bash_code_execution_tool_result" or "text_editor_code_execution_tool_result" or "tool_search_tool_result" or "mcp_tool_result";
        if (!isCall && !isResult) UnsupportedTool();
        var id = (block[isCall ? "id" : "tool_use_id"]?.ToString()) ?? throw new GatewayException(DesktopResources.Get("InvalidStreamEvent"));
        if (!nativeTools.TryGetValue(id, out var tool))
        {
            tool = new() { Id = id, Name = block["name"]?.ToString() ?? type.Replace("_tool_result", "", StringComparison.Ordinal), Provider = type != "tool_use" };
            nativeTools.Add(id, tool);
        }
        var metadata = block.DeepClone().AsObject(); metadata.Remove("input");
        metadata["native_endpoint"] = "/v1/messages"; metadata["block_index"] = int.Parse(index, CultureInfo.InvariantCulture);
        if (providerStreamMetadata["id"] is { } messageId) metadata["message_id"] = messageId.DeepClone();
        if (isCall)
        {
            tool.Metadata = metadata; tool.Input = block["input"]?.DeepClone() ?? new JsonObject(); StartTool(tool, result);
        }
        else tool.Result = metadata;
        messageTools[index] = tool;
    }

    private void MessageToolStop(Tool tool, List<StreamEvent> result)
    {
        CompleteTool(tool, result);
        if (tool.Result is not { } metadata) return;
        var content = metadata["content"];
        var errorContent = content as JsonObject;
        var error = metadata["is_error"]?.ToString() == "true" || errorContent?["type"]?.ToString().EndsWith("_error", StringComparison.Ordinal) == true
            ? errorContent?["error_code"]?.ToString() ?? errorContent?["message"]?.ToString() ?? "Provider tool execution failed." : null;
        ToolOutput(tool, metadata, content, error, result);
        if (content is JsonArray array) foreach (var citation in array.OfType<JsonObject>()) Source(citation, result);
    }

    private void ResponseTool(JsonObject item, string index, bool complete, List<StreamEvent> result)
    {
        var type = item["type"]?.ToString() ?? "";
        var client = type is "function_call" or "custom_tool_call" or "apply_patch_call";
        var isResult = type is "shell_call_output" or "tool_search_output" or "program_call_output";
        var hosted = type is "web_search_call" or "file_search_call" or "code_interpreter_call" or "image_generation_call" or "shell_call"
            or "mcp_call" or "mcp_list_tools" or "tool_search_call" or "program_call";
        if (!client && !hosted && !isResult) UnsupportedTool();
        var nativeId = item["id"]?.ToString() ?? throw new GatewayException(DesktopResources.Get("InvalidStreamEvent"));
        var callId = item["call_id"]?.ToString();
        var key = isResult ? "call:" + callId : nativeId;
        if (!nativeTools.TryGetValue(key, out var tool))
        {
            tool = new() { Id = client ? callId ?? nativeId : nativeId, Name = item["name"]?.ToString() ?? type.Replace("_call", "", StringComparison.Ordinal), Provider = !client };
            nativeTools.Add(key, tool);
        }
        if (callId is not null && !isResult) nativeTools["call:" + callId] = tool;
        var metadata = item.DeepClone().AsObject();
        metadata["native_endpoint"] = "/v1/responses"; metadata["native_item"] = item.DeepClone(); metadata["item_id"] = nativeId;
        metadata["output_index"] = int.Parse(index, CultureInfo.InvariantCulture);
        if (providerStreamMetadata["id"] is { } responseId) metadata["response_id"] = responseId.DeepClone();
        if (isResult) tool.Result = metadata;
        else
        {
            tool.Metadata = metadata;
            if (type == "apply_patch_call") tool.Input = new JsonObject { ["operation"] = item["operation"]?.DeepClone() };
            else if (client)
            {
                if ((item[type == "custom_tool_call" ? "input" : "arguments"]?.ToString()) is { } arguments)
                { if (complete || arguments.Length > 0) tool.Arguments = arguments; }
            }
            else tool.Input = item["arguments"]?.DeepClone() ?? item["action"]?.DeepClone() ?? new JsonObject();
            StartTool(tool, result);
        }
        if (!complete) return;
        CompleteTool(tool, result);
        if (tool.Provider && !tool.Output && (type is not ("shell_call" or "program_call") || tool.Result is not null))
        {
            var output = tool.Result ?? metadata;
            var raw = output["native_item"] as JsonObject ?? item;
            var error = raw["status"]?.ToString() == "failed" || raw["error"] is not null
                ? raw["error"] is JsonObject errorObject ? errorObject["message"]?.ToString() ?? "Provider tool execution failed." : raw["error"]?.ToString() ?? "Provider tool execution failed." : null;
            ToolOutput(tool, output, raw["output"] ?? raw["results"] ?? raw["outputs"] ?? raw["result"] ?? raw, error, result);
            if (raw["action"]?["sources"] is JsonArray sources) foreach (var source in sources.OfType<JsonObject>()) Source(source, result);
            if (type == "image_generation_call" && raw["result"]?.ToString() is { Length: > 0 } image)
            {
                var format = raw["output_format"]?.ToString() ?? "png";
                result.Add(Event("file", ("mediaType", JsonValue.Create("image/" + format)), ("url", JsonValue.Create("data:image/" + format + ";base64," + image))));
            }
        }
        if (!tool.Provider) reason = "tool-calls";
    }

    private void CompletionTools(JsonObject? delta, List<StreamEvent> result)
    {
        var calls = delta?["tool_calls"] as JsonArray;
        if (delta?["function_call"] is JsonObject legacy)
            calls = new JsonArray(new JsonObject { ["index"] = 0, ["id"] = "legacy-" + Guid.NewGuid().ToString("N"), ["function"] = legacy.DeepClone() });
        if (calls is null) return;
        foreach (var call in calls.OfType<JsonObject>())
        {
            var index = call["index"]?.ToString() ?? "0";
            if (!completionTools.TryGetValue(index, out var tool)) completionTools.Add(index, tool = new());
            if (tool.Complete) throw new GatewayException(DesktopResources.Get("InvalidStreamEvent"));
            if (call["id"] is { } id && tool.Id.Length == 0) tool.Id = id.ToString();
            var function = call["function"] as JsonObject ?? call["custom"] as JsonObject;
            if (function?["name"] is { } name) tool.Name += name.ToString();
            tool.Metadata["type"] = call["type"]?.ToString() ?? "function";
            tool.Metadata["native_endpoint"] = route.Endpoint;
            StartTool(tool, result);
            if ((function?[call["custom"] is null ? "arguments" : "input"]?.ToString()) is { } arguments) ToolDelta(tool, arguments, result);
        }
    }
}
