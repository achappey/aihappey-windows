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

    public static JsonObject Build(DesktopChatRoute route, ChatPreferences preferences, IReadOnlyList<UIMessage> messages)
    {
        if (preferences.MaxOutputTokens is < 1) throw new InvalidOperationException(DesktopResources.Get("ChatTokensInvalid"));
        var body = new JsonObject();
        // Only compatible inference settings cross protocols; never leak native tools, service-side state,
        // provider credential bags, or arbitrary gateway execution options into another endpoint.
        var fields = route.Endpoint switch
        {
            "/v1/responses" => new[] { "temperature", "top_p", "reasoning", "text", "service_tier", "include", "metadata", "prompt_cache_key", "prompt_cache_retention" },
            "/v1/messages" => ["temperature", "top_p", "top_k", "thinking", "output_config", "stop_sequences", "service_tier", "metadata", "max_tokens"],
            _ => ["temperature", "top_p", "frequency_penalty", "presence_penalty", "seed", "stop", "reasoning_effort", "response_format", "service_tier", "metadata", "max_completion_tokens"]
        };
        if (route.ProviderKey is { } provider && preferences.ProviderMetadata.TryGetValue(provider, out var config))
            foreach (var field in fields)
                if (config[field] is { } value) body[field] = value.DeepClone();
        body["model"] = route.Model;
        body["stream"] = true;
        var system = string.Join("\n\n", messages.Where(m => m.Role == Role.system).Select(Text).Where(t => t.Length > 0));
        if (route.Endpoint == "/v1/responses")
        {
            body["store"] = false;
            if (system.Length > 0) body["instructions"] = system;
            if (preferences.MaxOutputTokens is { } limit) body["max_output_tokens"] = limit;
            var input = new JsonArray();
            foreach (var message in messages.Where(m => m.Role != Role.system))
            {
                var assistant = message.Role == Role.assistant;
                foreach (var part in message.Parts)
                {
                    var raw = PortableConversations.Node(part);
                    var metadata = Metadata(raw, route.ProviderKey);
                    if (assistant && part.Type == "reasoning" && metadata?["encrypted_content"] is { } encrypted)
                    {
                        var reasoning = new JsonObject { ["type"] = "reasoning", ["encrypted_content"] = encrypted.DeepClone(),
                            ["summary"] = metadata["summary"]?.DeepClone() ?? new JsonArray() };
                        if (metadata["id"] is { } id) reasoning["id"] = id.DeepClone();
                        // Several summary parts may describe the same opaque native reasoning item.
                        if (!input.OfType<JsonObject>().Any(item => item["type"]?.ToString() == "reasoning"
                            && reasoning["id"] is not null && item["id"]?.ToString() == reasoning["id"]?.ToString())) input.Add(reasoning);
                    }
                    else if (part.Type == "text" && PortableConversations.Text(part) is { Length: > 0 } text)
                    {
                        var item = new JsonObject { ["type"] = "message", ["role"] = assistant ? "assistant" : "user",
                            ["content"] = new JsonArray(new JsonObject { ["type"] = assistant ? "output_text" : "input_text", ["text"] = text }) };
                        if (assistant && metadata?["phase"] is { } phase) item["phase"] = phase.DeepClone();
                        input.Add(item);
                    }
                    else if (!assistant && part.Type == "file")
                        input.Add(new JsonObject { ["type"] = "message", ["role"] = "user", ["content"] = new JsonArray(File(raw, route.Endpoint)) });
                }
            }
            body["input"] = input;
            return Contract<ResponseRequest>(body);
        }
        var mapped = new JsonArray();
        if (route.Endpoint == "/v1/messages")
        {
            if (system.Length > 0) body["system"] = system;
            body["max_tokens"] = preferences.MaxOutputTokens ?? body["max_tokens"]?.GetValue<int>() ?? 1024;
            foreach (var message in messages.Where(m => m.Role != Role.system))
            {
                var blocks = new JsonArray();
                foreach (var part in message.Parts)
                {
                    var raw = PortableConversations.Node(part);
                    var metadata = Metadata(raw, route.ProviderKey);
                    if (part.Type == "text" && PortableConversations.Text(part) is { Length: > 0 } text)
                        blocks.Add(new JsonObject { ["type"] = "text", ["text"] = text });
                    else if (message.Role == Role.assistant && part.Type == "reasoning" && metadata is not null)
                    {
                        if (metadata["type"]?.ToString() == "redacted_thinking" && metadata["data"] is { } data)
                            blocks.Add(new JsonObject { ["type"] = "redacted_thinking", ["data"] = data.DeepClone() });
                        else if (metadata["signature"] is { } signature)
                            blocks.Add(new JsonObject { ["type"] = "thinking", ["thinking"] = PortableConversations.Text(part), ["signature"] = signature.DeepClone() });
                    }
                    else if (message.Role == Role.user && part.Type == "file") blocks.Add(File(raw, route.Endpoint));
                }
                if (blocks.Count > 0) mapped.Add(new JsonObject { ["role"] = message.Role == Role.assistant ? "assistant" : "user", ["content"] = blocks });
            }
            body["messages"] = mapped;
            return Contract<MessagesRequest>(body, MessagesJson.Default);
        }
        if (preferences.MaxOutputTokens is { } max)
        {
            body.Remove("max_completion_tokens");
            body["max_tokens"] = max;
        }
        foreach (var message in messages)
        {
            var content = new JsonArray();
            foreach (var part in message.Parts)
                if (part.Type == "text" && PortableConversations.Text(part) is { Length: > 0 } text)
                    content.Add(new JsonObject { ["type"] = "text", ["text"] = text });
                else if (message.Role == Role.user && part.Type == "file") content.Add(File(PortableConversations.Node(part), route.Endpoint));
            if (content.Count > 0) mapped.Add(new JsonObject { ["role"] = message.Role.ToString(),
                ["content"] = content.Count == 1 && content[0]?["type"]?.ToString() == "text" ? JsonValue.Create(content[0]!["text"]!.ToString()) : content });
        }
        body["messages"] = mapped;
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
    private static JsonObject? Metadata(JsonObject part, string? provider) => provider is null ? null : part["providerMetadata"]?[provider] as JsonObject;

    private static JsonObject File(JsonObject part, string endpoint)
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
