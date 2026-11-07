using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AIHappey.ChatCompletions.Models;
using AIHappey.Messages;
using AIHappey.Responses;
using AIHappey.Vercel.Models;

namespace AIHappey.Desktop.Core;

/// <summary>Client-side projections of portable UI messages into the three standalone wire contracts.</summary>
public static class NativeChatRequests
{
    private static readonly JsonSerializerOptions json = new(JsonSerializerDefaults.Web)
        { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public static JsonObject Build(DesktopChatRoute route, ChatPreferences preferences, IReadOnlyList<UIMessage> messages,
        McpTurnSnapshot? mcp = null)
    {
        if (preferences.MaxOutputTokens is < 1) throw new InvalidOperationException(DesktopResources.Get("ChatTokensInvalid"));
        var body = new JsonObject();
        // Only compatible inference settings cross protocols. Tool declarations are projected separately;
        // service-side state, credential bags and gateway execution options never cross this boundary.
        var fields = route.Endpoint switch
        {
            "/v1/responses" => new[] { "temperature", "top_p", "reasoning", "text", "service_tier", "include", "metadata", "prompt_cache_key", "prompt_cache_retention", "parallel_tool_calls", "max_tool_calls", "tool_choice" },
            "/v1/messages" => ["temperature", "top_p", "top_k", "thinking", "output_config", "stop_sequences", "service_tier", "metadata", "max_tokens", "tool_choice", "mcp_servers", "container"],
            _ => ["temperature", "top_p", "frequency_penalty", "presence_penalty", "seed", "stop", "reasoning_effort", "response_format", "service_tier", "metadata", "max_completion_tokens", "parallel_tool_calls", "tool_choice", "web_search_options"]
        };
        if (route.ProviderKey is { } provider && preferences.ProviderMetadata.TryGetValue(provider, out var config))
            foreach (var field in fields)
                if (config[field] is { } value) body[field] = value.DeepClone();
        body["model"] = route.Model;
        body["stream"] = true;
        NativeChatTools.Declarations(body, route, preferences, messages, mcp);
        // Output-content settings operate on a deep request copy, never the stored conversation.
        var portable = new JsonObject { ["messages"] = JsonSerializer.SerializeToNode(messages, PortableConversations.Json) };
        preferences.ToolOutputContentSettings.ApplyToRequest(portable);
        messages = portable["messages"]!.Deserialize<List<UIMessage>>(PortableConversations.Json)!;
        var system = string.Join("\n\n", messages.Where(m => m.Role == Role.system).Select(Text).Where(t => t.Length > 0));
        if (route.Endpoint == "/v1/responses")
        {
            body["store"] = false;
            if (system.Length > 0) body["instructions"] = system;
            if (preferences.MaxOutputTokens is { } limit) body["max_output_tokens"] = limit;
            var include = body["include"] as JsonArray ?? new JsonArray();
            if (!include.Any(value => value?.ToString() == "reasoning.encrypted_content")) include.Add("reasoning.encrypted_content");
            body["include"] = include.DeepClone();
            body["input"] = NativeChatHistory.Responses(messages, route);
            return Contract<ResponseRequest>(body);
        }
        if (route.Endpoint == "/v1/messages")
        {
            if (system.Length > 0) body["system"] = system;
            body["max_tokens"] = preferences.MaxOutputTokens ?? body["max_tokens"]?.GetValue<int>() ?? 1024;
            body["messages"] = NativeChatHistory.Messages(messages, route);
            return Contract<MessagesRequest>(body, MessagesJson.Default);
        }
        if (preferences.MaxOutputTokens is { } max)
        {
            body.Remove("max_completion_tokens");
            body["max_tokens"] = max;
        }
        body["messages"] = NativeChatHistory.Completions(messages, route);
        return Contract<ChatCompletionOptions>(body);
    }
    private static JsonObject Contract<T>(JsonObject body, JsonSerializerOptions? options = null)
    {
        var result = JsonSerializer.SerializeToNode(body.Deserialize<T>(options ?? json)!, options ?? json)!.AsObject();
        // Model defaults (such as an empty tools collection) must not turn basic chat into a
        // tool-enabled request or introduce fields that the selected protocol did not request.
        foreach (var key in result.Select(pair => pair.Key).Where(key => !body.ContainsKey(key)).ToArray()) result.Remove(key);
        return result;
    }
    private static string Text(UIMessage message) => string.Join("\n", message.Parts.Where(p => p.Type == "text").Select(PortableConversations.Text));
    internal static JsonObject File(JsonObject part, string endpoint)
    {
        var url = part["url"]?.ToString() ?? "";
        var media = part["mediaType"]?.ToString() ?? "application/octet-stream";
        var name = part["filename"]?.ToString() ?? "attachment";
        var image = media.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
        var inline = url.StartsWith("data:", StringComparison.OrdinalIgnoreCase);
        var comma = url.IndexOf(',');
        var base64 = inline && comma > 0 && url[..comma].EndsWith(";base64", StringComparison.OrdinalIgnoreCase);
        var web = Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "https" && string.IsNullOrEmpty(uri.UserInfo);
        if ((!base64 && !web) || !image && endpoint == "/v1/messages" && media != "application/pdf")
            throw new InvalidOperationException(DesktopResources.Get("NativeChatAttachmentUnsupported"));
        if (endpoint == "/v1/messages") return new JsonObject { ["type"] = image ? "image" : "document",
            ["source"] = base64 ? new JsonObject { ["type"] = "base64", ["media_type"] = media, ["data"] = url[(comma + 1)..] }
                : new JsonObject { ["type"] = "url", ["url"] = url } };
        if (endpoint == "/v1/responses") return image ? new JsonObject { ["type"] = "input_image", ["image_url"] = url }
            : base64 ? new JsonObject { ["type"] = "input_file", ["file_data"] = url, ["filename"] = name }
            : new JsonObject { ["type"] = "input_file", ["file_url"] = url };
        if (!image && !base64) throw new InvalidOperationException(DesktopResources.Get("NativeChatAttachmentUnsupported"));
        return image ? new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = url } }
            : new JsonObject { ["type"] = "file", ["file"] = new JsonObject { ["file_data"] = url, ["filename"] = name } };
    }
}
