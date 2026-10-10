using System.Globalization;
using System.Net.Http.Json;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AIHappey.Desktop.Core;

public interface IDesktopAppAgentClient
{
    Task<string> InvokeAsync(DesktopAgent? agent, IReadOnlyList<ChatTarget> models, JsonObject input, string fallback, CancellationToken ct);
}

/// <summary>Side inference uses the AI Responses endpoint, not the Agents chat service.
/// No main-chat instructions, tools, headers, token limits or provider preferences leak in.</summary>
public sealed class DesktopAppAgentClient(DesktopSession session, DesktopChatClient client, HttpClient http) : IDesktopAppAgentClient
{
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions InputJson = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public static JsonObject WelcomeInput(string language, string? currentUser, DateTimeOffset now)
    {
        var local = TimeZoneInfo.ConvertTime(now, TimeZoneInfo.Local);
        var zone = TimeZoneInfo.Local.IsDaylightSavingTime(local) ? TimeZoneInfo.Local.DaylightName : TimeZoneInfo.Local.StandardName;
        return new() { ["language"] = language == "nl" ? "Nederlands" : "English", ["currentUser"] = currentUser,
            ["currentDateTime"] = local.ToString("G", CultureInfo.CurrentCulture) + " " + zone };
    }
    public static JsonObject ConversationNameInput(string userMessage, string language) => new() { ["userMessage"] = userMessage, ["language"] = language };

    public async Task<string> InvokeAsync(DesktopAgent? agent, IReadOnlyList<ChatTarget> models, JsonObject input, string fallback, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var snapshot = agent?.Clone();
        var model = snapshot is null ? null : models.FirstOrDefault(m => m.Id == snapshot.ModelId);
        if (snapshot is null || model is null || string.IsNullOrWhiteSpace(snapshot.ModelId)) return fallback;
        var partition = ImageLibraryStore.Partition(session);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lifetime.CancelAfter(RequestTimeout);
        try
        {
            using var request = await client.RequestAsync(ServiceKind.Ai, HttpMethod.Post, "v1/responses", lifetime.Token, partition);
            request.Content = JsonContent.Create(RequestBody(snapshot, model, input));
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, lifetime.Token);
            if (!response.IsSuccessStatusCode) return fallback;
            var bytes = await DesktopCatalogClient.ReadBoundedAsync(response.Content, 2_000_000, lifetime.Token);
            using var doc = JsonDocument.Parse(bytes);
            lifetime.Token.ThrowIfCancellationRequested();
            if (partition != ImageLibraryStore.Partition(session)) return fallback;
            var text = ExtractText(doc.RootElement);
            return string.IsNullOrWhiteSpace(text) ? fallback : text;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            // Side-inference failures are deliberately silent and never interrupt the main chat.
            System.Diagnostics.Debug.WriteLine("AIHappey app agent: using localized fallback.");
            return fallback;
        }
    }

    public static JsonObject RequestBody(DesktopAgent agent, ChatTarget model, JsonObject input)
    {
        var body = new JsonObject { ["model"] = agent.ModelId, ["input"] = input.ToJsonString(InputJson), ["stream"] = false };
        if (agent.Definition["instructions"] is JsonValue instructions && instructions.TryGetValue<string>(out var text)) body["instructions"] = text;
        var provider = ChatPreferences.ResolveProvider(ServiceKind.Ai, agent.ModelId, model.ProviderKey);
        var config = SanitizeProviderConfig(agent.Definition["model"]?["providerMetadata"] as JsonObject, provider);
        if (provider is not null && config is { Count: > 0 }) body["metadata"] = new JsonObject { [provider] = config };
        return body;
    }
    public static JsonObject? SanitizeProviderConfig(JsonObject? config, string? provider)
    {
        if (config is null) return null;
        var result = new JsonObject();
        foreach (var (key, value) in config)
        {
            var normalized = key.Trim().ToLowerInvariant();
            if (normalized.Length == 0 || normalized == "headers" || provider == "abliteration" && normalized == "flagged_categories"
                || provider == "anthropic" && normalized == "anthropic-beta" || provider == "openai" && normalized == "openai-beta") continue;
            result[key] = value?.DeepClone();
        }
        if (provider == "anthropic") CanonicalizeAnthropicTools(result);
        return result.Count > 0 ? result : null;
    }
    private static void CanonicalizeAnthropicTools(JsonObject config)
    {
        string[] names = ["advisor", "bash", "code_execution", "memory", "text_editor", "web_fetch", "web_search", "tool_search_tool_bm25", "tool_search_tool_regex"];
        static JsonObject Clean(JsonObject tool)
        {
            var copy = (JsonObject)tool.DeepClone(); var type = DesktopAgent.Text(copy["type"]);
            if (type.StartsWith("web_fetch_", StringComparison.Ordinal) || type.StartsWith("web_search_", StringComparison.Ordinal))
            {
                foreach (var key in new[] { "allowed_domains", "blocked_domains" }) if (copy[key] is null) copy.Remove(key);
                if (type.StartsWith("web_search_", StringComparison.Ordinal) && copy["user_location"] is null) copy.Remove("user_location");
            }
            if (type is not "web_fetch_20260318" and not "web_search_20260318") copy.Remove("response_inclusion");
            if (type is not "web_fetch_20260318" and not "web_fetch_20260309") copy.Remove("use_cache");
            return copy;
        }
        var tools = new JsonArray(); var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in config["tools"] as JsonArray ?? new())
        {
            if (item is null) continue;
            var tool = item as JsonObject; var type = DesktopAgent.Text(tool?["type"]);
            var name = names.FirstOrDefault(n => type == n || DesktopAgent.Text(tool?["name"]) == n
                || type.StartsWith(n + "_", StringComparison.Ordinal) && type.Length == n.Length + 9 && type[(n.Length + 1)..].All(char.IsAsciiDigit));
            if (name is null) tools.Add(item.DeepClone());
            else if (seen.Add(name)) tools.Add(Clean(tool!));
        }
        foreach (var name in names)
        {
            if (!seen.Contains(name) && config[name] is JsonObject alias) tools.Add(Clean(alias));
            config.Remove(name);
        }
        config.Remove("tools"); if (tools.Count > 0) config["tools"] = tools;
    }

    public static string ExtractText(JsonElement response)
    {
        static string Text(JsonElement item, string key) => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(key, out var value)
            && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
        static IEnumerable<JsonElement> Array(JsonElement item, string key) => item.ValueKind == JsonValueKind.Object
            && item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : [];
        static string Join(IEnumerable<string> values) => string.Join("\n\n", values.Where(v => v.Length > 0)).Trim();
        var text = Text(response, "output_text").Trim(); if (text.Length > 0) return text;
        text = Join(Array(response, "output").Where(i => Text(i, "type") == "message").SelectMany(i => Array(i, "content"))
            .Where(i => Text(i, "type") is "output_text" or "text").Select(i => Text(i, "text")));
        if (text.Length > 0) return text;
        text = Join(Array(response, "content").Where(i => Text(i, "type") != "reasoning_text").Select(i => Text(i, "text")));
        if (text.Length > 0) return text;
        var choice = Array(response, "choices").FirstOrDefault();
        if (choice.ValueKind != JsonValueKind.Object || !choice.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object
            || !message.TryGetProperty("content", out var content)) return "";
        if (content.ValueKind == JsonValueKind.String) return content.GetString()!.Trim();
        return content.ValueKind == JsonValueKind.Array ? Join(content.EnumerateArray().Where(i => Text(i, "type") != "reasoning_text")
            .Select(i => Text(i, "text") is { Length: > 0 } value ? value : Text(i, "content"))) : "";
    }
}
