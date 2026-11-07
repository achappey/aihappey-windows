using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Vercel.Models;

namespace AIHappey.Desktop.Core;

/// <summary>Endpoint-local tool declarations and replay metadata. No server mapper dependency.</summary>
internal static class NativeChatTools
{
    internal static bool IsTool(JsonObject part) => part["type"]?.ToString() == "dynamic-tool"
        || part["type"]?.ToString().StartsWith("tool-", StringComparison.Ordinal) == true;
    internal static string Name(JsonObject part) => part["toolName"]?.ToString() ?? part["type"]!.ToString()[5..];
    internal static bool Provider(JsonObject part) => part["providerExecuted"]?.ToString() == "true";
    internal static bool Resolved(JsonObject part) => part["preliminary"]?.ToString() != "true"
        && part["state"]?.ToString() is "output-available" or "output-error" or "output-denied" or "approval-responded"
        && (part.ContainsKey("output") || part["state"]?.ToString() is "output-error" or "output-denied" || part["approval"]?["approved"]?.ToString() == "false");
    internal static JsonObject? Metadata(JsonObject part, string? provider, string channel = "providerMetadata") =>
        provider is null ? null : part[channel]?[provider] as JsonObject;
    internal static JsonObject? Call(JsonObject part, string? provider) => Metadata(part, provider, "callProviderMetadata") ?? Metadata(part, provider);
    internal static JsonObject? Result(JsonObject part, string? provider) => Metadata(part, provider, "resultProviderMetadata");
    internal static string Stringify(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : node?.ToJsonString() ?? "";
    internal static bool Failed(JsonObject part) => part["state"]?.ToString() is "output-error" or "output-denied"
        || part["approval"]?["approved"]?.ToString() == "false" || (part["output"] as JsonObject)?["isError"]?.ToString() == "true";
    internal static string Output(JsonObject part) => part["approval"]?["approved"]?.ToString() == "false" || part["state"]?.ToString() == "output-denied"
        ? part["approval"]?["reason"]?.ToString() ?? "The user denied release of this tool result."
        : part["state"]?.ToString() == "output-error" ? part["errorText"]?.ToString() ?? "Tool execution failed." : Stringify(part["output"]);

    internal static void Declarations(JsonObject body, DesktopChatRoute route, ChatPreferences preferences,
        IReadOnlyList<UIMessage> messages, McpTurnSnapshot? mcp)
    {
        var choice = preferences.EffectiveToolChoice(messages, mcp);
        var tools = new JsonArray();
        if (route.ProviderKey is { } provider && preferences.ProviderMetadata.TryGetValue(provider, out var raw))
        {
            var config = provider == "openai" ? OpenAIChatConfig.Canonical(raw)
                : provider == "anthropic" ? AnthropicChatConfig.Canonical(raw) : raw;
            if (config["tools"] is JsonArray native)
                foreach (var tool in native.OfType<JsonObject>())
                {
                    var type = tool["type"]?.ToString() ?? "";
                    if (type == "function")
                    {
                        var function = (tool["function"] as JsonObject ?? tool).DeepClone().AsObject();
                        function.Remove("type"); function.Remove("function");
                        if (function["name"] is null) throw new InvalidOperationException("Native function is missing its name.");
                        if (route.Endpoint == "/v1/messages")
                        {
                            function["input_schema"] = (function["parameters"] ?? function["input_schema"])?.DeepClone() ?? new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() };
                            function.Remove("parameters"); function.Remove("allowed_callers"); tools.Add(function);
                        }
                        else if (route.Endpoint == "/v1/responses") { function["type"] = "function"; tools.Add(function); }
                        else tools.Add(new JsonObject { ["type"] = "function", ["function"] = function });
                        continue;
                    }
                    // Do not send a provider's Responses tools to its Messages/Completions surface.
                    var compatible = route.Endpoint == "/v1/responses" ? type is "function" or "custom" or "namespace" or "web_search" or "web_search_preview"
                            or "file_search" or "code_interpreter" or "image_generation" or "shell" or "apply_patch" or "tool_search" or "mcp" or "programmatic_tool_calling"
                        : route.Endpoint == "/v1/messages" ? tool["name"] is not null && (tool["input_schema"] is not null
                            || AnthropicChatConfig.ToolTypes.Any(name => type == name || type.StartsWith(name + "_", StringComparison.Ordinal)))
                        : type is "function" or "custom";
                    if (compatible) tools.Add(tool.DeepClone());
                }
        }
        if (choice != "none" && mcp is not null)
        {
            // Namespaces are a Responses contract. Other endpoints receive ordinary aliased functions.
            var shaping = preferences.Clone();
            if (route.Endpoint != "/v1/responses") shaping.UseToolNamespaces = false;
            var shaped = ChatToolConfiguration.Shape(mcp.Tools.Where(preferences.ToolAnnotations.Allows), shaping,
                name => route.Endpoint == "/v1/responses" ? mcp.SourceForTool(name) : mcp.SourceForTool(name) with { Namespace = false });
            foreach (var tool in shaped.OfType<JsonObject>())
            {
                if (tool["type"]?.ToString() == "namespace") { tools.Add(tool.DeepClone()); continue; }
                var name = tool["name"]!.ToString();
                if (tools.OfType<JsonObject>().Any(t => (t["name"] ?? t["function"]?["name"])?.ToString() == name))
                    throw new InvalidOperationException("Duplicate native and client tool name: " + name);
                var function = new JsonObject { ["name"] = name, ["parameters"] = (tool["inputSchema"] ?? tool["parameters"])?.DeepClone()
                    ?? new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() } };
                var description = tool["description"] ?? tool["title"] ?? tool["annotations"]?["title"];
                if (description is not null) function["description"] = description.DeepClone();
                if (route.Endpoint == "/v1/messages")
                {
                    function["input_schema"] = function["parameters"]!.DeepClone(); function.Remove("parameters");
                    if (tool["defer_loading"] is { } deferred) function["defer_loading"] = deferred.DeepClone();
                    tools.Add(function);
                }
                else if (route.Endpoint == "/v1/responses")
                {
                    function["type"] = "function";
                    foreach (var field in new[] { "allowed_callers", "defer_loading", "strict", "output_schema" })
                        if (tool[field] is { } value) function[field] = value.DeepClone();
                    if (tool["outputSchema"] is { } outputSchema) function["output_schema"] = outputSchema.DeepClone();
                    tools.Add(function);
                }
                else tools.Add(new JsonObject { ["type"] = "function", ["function"] = function });
            }
        }
        if (tools.Count > 0) body["tools"] = tools;
        if (route.Endpoint == "/v1/messages" && body["tool_choice"] is JsonValue nativeChoice && nativeChoice.TryGetValue<string>(out var choiceText))
            body["tool_choice"] = new JsonObject { ["type"] = choiceText == "required" ? "any" : choiceText };
        else if (route.Endpoint != "/v1/messages" && body["tool_choice"] is JsonObject nativeObject && nativeObject["type"]?.ToString() is "auto" or "any" or "none")
            body["tool_choice"] = nativeObject["type"]!.ToString() == "any" ? "required" : nativeObject["type"]!.ToString();
        // An explicit client budget/stop decision must override even a configured forced tool choice.
        if (choice == "none" || !body.ContainsKey("tool_choice") && tools.Count > 0 && choice is not null)
            body["tool_choice"] = route.Endpoint == "/v1/messages"
                ? new JsonObject { ["type"] = choice == "required" ? "any" : choice } : JsonValue.Create(choice);
        if (preferences.MaxToolCalls is > 0 && route.Endpoint == "/v1/responses") body["max_tool_calls"] = preferences.MaxToolCalls.Value;
    }
}
