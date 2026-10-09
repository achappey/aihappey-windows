using System.Text.Json;
using AIHappey.Vercel.Models;

namespace AIHappey.Desktop.Core;

public sealed record McpPromptCompletion(IReadOnlyList<string> Values, int? Total = null, bool HasMore = false)
{
    public static McpPromptCompletion Empty => new([]);
    public bool CanAutofill => Values.Count == 1 && !HasMore && Total is null or <= 1;
}

/// <summary>Identity includes the originating connection epoch, not just the server/name pair.</summary>
public sealed record McpPromptEntry(string ServerId, string ServerName, JsonElement Prompt,
    Func<string, IReadOnlyDictionary<string, string>, CancellationToken, Task<JsonElement>> Get,
    Func<string, string, string, IReadOnlyDictionary<string, string>, CancellationToken, Task<McpPromptCompletion>>? Complete,
    Func<bool> IsCurrent)
{
    public string Name => CatalogProjection.Text(Prompt, "name") ?? "";
    public string Title => CatalogProjection.Text(Prompt, "title") ?? Name;
    public string Description => CatalogProjection.Text(Prompt, "description") ?? CatalogProjection.Text(Prompt, "text") ?? "";
    public IReadOnlyList<McpPromptArgument> Arguments => DesktopMcpPrompts.Arguments(Prompt);
}

public sealed record McpPromptArgument(string Name, string Description, bool Required);
public sealed record McpSelectedPrompt(McpPromptEntry Entry, IReadOnlyList<UIMessagePart> Parts);

public static class DesktopMcpPrompts
{
    public static bool Capability(JsonElement capabilities, string name) => capabilities.ValueKind == JsonValueKind.Object
        && capabilities.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object;

    public static IReadOnlyList<McpPromptArgument> Arguments(JsonElement prompt)
    {
        if (!prompt.TryGetProperty("arguments", out var list) || list.ValueKind is JsonValueKind.Null) return [];
        if (list.ValueKind != JsonValueKind.Array) throw new JsonException("Invalid MCP prompt arguments.");
        var result = list.EnumerateArray().Select(a => new McpPromptArgument(
            CatalogProjection.Text(a, "name") ?? throw new JsonException("Missing MCP prompt argument name."),
            CatalogProjection.Text(a, "description") ?? "", a.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.True)).ToArray();
        if (result.Any(a => string.IsNullOrWhiteSpace(a.Name)) || result.Select(a => a.Name).Distinct(StringComparer.Ordinal).Count() != result.Length)
            throw new JsonException("Invalid MCP prompt argument names.");
        return result;
    }

    public static bool MissingRequired(IEnumerable<McpPromptArgument> arguments, IReadOnlyDictionary<string, string> values) =>
        arguments.Any(a => a.Required && (!values.TryGetValue(a.Name, out var value) || string.IsNullOrWhiteSpace(value)));

    public static JsonElement ValidateResult(JsonElement result)
    {
        if (result.GetRawText().Length > DesktopMcpResources.MaximumResultCharacters)
            throw new InvalidOperationException(DesktopResources.Get("McpResultTooLarge"));
        if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array)
            throw new JsonException("Invalid MCP prompt result.");
        return result;
    }

    /// <summary>Match the browser: flatten all prompt roles into ordered text parts in one user message.
    /// Unsupported image/audio blocks are empty text, not a new multimodal behavior. Never copy _meta.</summary>
    public static IReadOnlyList<UIMessagePart> MessageParts(JsonElement result)
    {
        ValidateResult(result);
        return result.GetProperty("messages").EnumerateArray().Select(message =>
        {
            if (!message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Object)
                throw new JsonException("Invalid MCP prompt content.");
            var text = CatalogProjection.Text(content, "text");
            if (text is null && content.TryGetProperty("resource", out var resource) && resource.ValueKind == JsonValueKind.Object)
                text = DesktopMcpResources.Markdown(CatalogProjection.Text(resource, "uri") ?? "",
                    CatalogProjection.Text(resource, "text") ?? "", CatalogProjection.Text(resource, "mimeType") ?? "text/plain");
            return (UIMessagePart)new TextUIPart { Text = text ?? "" };
        }).ToArray();
    }
}

/// <summary>Prompt form logic independent of WinUI. Each argument request is canceled on replacement;
/// a context revision rejects replies based on obsolete sibling values, including servers ignoring cancellation.</summary>
public sealed class McpPromptArgumentsState : IDisposable
{
    private readonly McpPromptEntry entry;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Dictionary<string, CancellationTokenSource> requests = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> values;
    private readonly Dictionary<string, IReadOnlyList<string>> suggestions = new(StringComparer.Ordinal);
    private int revision;
    private bool disposed;
    public event Action? Changed;
    public IReadOnlyList<McpPromptArgument> Arguments { get; }
    public IReadOnlyDictionary<string, string> Values => values;
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Suggestions => suggestions;
    public bool MissingRequired => DesktopMcpPrompts.MissingRequired(Arguments, values);

    public McpPromptArgumentsState(McpPromptEntry entry)
    {
        this.entry = entry; Arguments = entry.Arguments;
        values = Arguments.ToDictionary(a => a.Name, _ => "", StringComparer.Ordinal);
    }

    public Task InitializeAsync() => Task.WhenAll(Arguments.Select(a => RequestAsync(a.Name, "", false, false)));

    public Task ChangeAsync(string name, string value)
    {
        if (disposed || !values.ContainsKey(name)) return Task.CompletedTask;
        values[name] = value; revision++; CancelRequests(); Changed?.Invoke();
        // Browser ordinary Inputs do not filter. Only completion-backed selects filter as typed.
        var refresh = Arguments.Where(a => a.Name != name && string.IsNullOrWhiteSpace(values[a.Name]))
            .Select(a => RequestAsync(a.Name, "", false, true)).ToList();
        if (suggestions.ContainsKey(name)) refresh.Add(RequestAsync(name, value, true, false));
        return Task.WhenAll(refresh);
    }

    public Task FilterAsync(string name, string query) => RequestAsync(name, query, true, false);

    private async Task RequestAsync(string name, string query, bool delay, bool autofill)
    {
        if (disposed || entry.Complete is null || !entry.IsCurrent()) return;
        if (requests.Remove(name, out var old)) old.Cancel();
        using var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        requests[name] = request;
        var version = revision; var originalValue = values[name];
        try
        {
            if (delay) await Task.Delay(500, request.Token);
            var context = values.Where(p => p.Key != name && !string.IsNullOrWhiteSpace(p.Value)).ToDictionary(p => p.Key, p => p.Value);
            var result = await entry.Complete(entry.Name, name, query, context, request.Token);
            if (disposed || request.IsCancellationRequested || version != revision || values[name] != originalValue || !entry.IsCurrent()) return;
            if (result.Values.Count > 0 || suggestions.ContainsKey(name)) suggestions[name] = result.Values.Take(100).ToArray();
            if (autofill && string.IsNullOrWhiteSpace(values[name]) && result.CanAutofill) values[name] = result.Values[0];
            Changed?.Invoke();
        }
        catch (Exception) { /* Optional completion cannot block freeform entry or expose server errors. */ }
        finally { if (requests.TryGetValue(name, out var current) && current == request) requests.Remove(name); }
    }

    private void CancelRequests() { foreach (var request in requests.Values) request.Cancel(); requests.Clear(); }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; lifetime.Cancel(); CancelRequests(); Changed = null; lifetime.Dispose();
    }
}
