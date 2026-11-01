using System.Text.Json;

namespace AIHappey.Desktop.Core;

internal sealed record LocalCanvasSnapshot(string Uri, string MimeType, string Text);

/// <summary>Creation-based replay only. No filesystem access, tool execution, or result mutation.</summary>
internal sealed class LocalCanvasHistory
{
    private const int MaximumCharacters = 2_000_000;
    private readonly Dictionary<string, string> known = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> identities = new(StringComparer.OrdinalIgnoreCase);

    internal LocalCanvasSnapshot? Apply(JsonElement part)
    {
        var name = PortableConversations.String(part, "toolName") ?? PortableConversations.String(part, "type") ?? "";
        if (name.StartsWith("tool-", StringComparison.Ordinal)) name = name[5..];
        var editor = name is "str_replace_based_edit_tool" or "str_replace_editor";
        var canvas = name is "local_canvas_create" or "local_canvas_insert" or "local_canvas_replace" or "local_canvas_read";
        if (!editor && !canvas && name != "apply_patch") return null;
        var input = Input(part);
        var operation = name == "apply_patch" && input.TryGetProperty("operation", out var op) ? op : input;
        if (operation.ValueKind != JsonValueKind.Object) return null;
        var path = DocumentPath(name, PortableConversations.String(operation, "path"));
        if (path is null || LocalDocumentEdits.CanvasMimeType(path) is not { } mimeType) return null;
        if (!identities.TryGetValue(path, out var uri))
            identities[path] = uri = "localcanvas:///" + string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
        var command = name == "apply_patch" ? PortableConversations.String(operation, "type")
            : editor ? PortableConversations.String(input, "command") : name;
        if (command is "view" or "local_canvas_read") return null;
        if (!Successful(part))
        {
            if (!Failed(part)) known.Remove(uri); // A mutation may have executed before denial/interruption.
            return null;
        }
        if (command == "delete_file") { known.Remove(uri); return null; }
        try
        {
            string text;
            if (command == "create_file") text = V4ADiff.Apply("", RequiredText(operation, "diff"), create: true);
            else if (command is "create" or "local_canvas_create") text = RequiredText(input, "file_text");
            else
            {
                if (!known.TryGetValue(uri, out var previous)) return null;
                text = command switch
                {
                    "update_file" => V4ADiff.Apply(previous, RequiredText(operation, "diff")),
                    "str_replace" or "local_canvas_replace" => LocalDocumentEdits.Replace(previous,
                        RequiredText(input, "old_str"), RequiredText(input, "new_str"), command == "str_replace"),
                    "insert" or "local_canvas_insert" => LocalDocumentEdits.Insert(previous,
                        AnthropicTextEditorTools.Line(input), RequiredText(input, "insert_text"), command == "insert"),
                    _ => throw new LocalToolException("Unrecognized document edit.")
                };
            }
            if (text.Length > MaximumCharacters || text.Contains('\0')) throw new LocalToolException("Unsupported preview.");
            known[uri] = text;
            return new(uri, mimeType, text);
        }
        catch (Exception error) when (error is LocalToolException or InvalidOperationException or ArgumentException)
        {
            known.Remove(uri); return null;
        }
    }

    private static string RequiredText(JsonElement input, string field)
    {
        var text = PortableConversations.String(input, field);
        return text is not null && text.Length <= MaximumCharacters ? text : throw new LocalToolException("Missing document text.");
    }
    private static JsonElement Input(JsonElement part)
    {
        if (part.TryGetProperty("input", out var input))
        {
            if (input.ValueKind == JsonValueKind.Object) return input;
            if (input.ValueKind == JsonValueKind.String && input.GetString() is { Length: <= MaximumCharacters } json)
                try
                {
                    using var document = JsonDocument.Parse(json);
                    if (document.RootElement.ValueKind == JsonValueKind.Object) return document.RootElement.Clone();
                }
                catch (JsonException) { }
        }
        return JsonSerializer.SerializeToElement(new { });
    }
    private static bool Failed(JsonElement part) => part.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Object
        && (output.TryGetProperty("isError", out var error) && error.ValueKind == JsonValueKind.True
            || output.TryGetProperty("structuredContent", out var structured) && structured.ValueKind == JsonValueKind.Object
                && PortableConversations.String(structured, "status") == "failed");
    private static bool Successful(JsonElement part)
    {
        if (!part.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Object || Failed(part)
            || PortableConversations.String(part, "state") is not (null or "output-available" or "approval-responded")
            || DesktopToolApprovals.Approved(part) == false
            || part.TryGetProperty("providerExecuted", out var provider) && provider.ValueKind == JsonValueKind.True) return false;
        return output.TryGetProperty("isError", out var error) && error.ValueKind == JsonValueKind.False
            || output.TryGetProperty("structuredContent", out var structured) && structured.ValueKind == JsonValueKind.Object
                && PortableConversations.String(structured, "status") == "completed";
    }
    private static string? DocumentPath(string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 32700 || value.Contains(':') || value.Any(char.IsControl)) return null;
        var path = value.Replace('\\', '/').Trim();
        if (path.StartsWith("//", StringComparison.Ordinal)) return null;
        if (name == "apply_patch") { if (path != value || path.StartsWith('/')) return null; }
        else
        {
            if (path.StartsWith('/')) path = path[1..];
            if (name.StartsWith("local_canvas_", StringComparison.Ordinal))
            {
                if (!path.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) path += ".md";
            }
            else if (path.StartsWith("./", StringComparison.Ordinal)) path = path[2..];
        }
        if (path.Split('/').Any(segment => segment is "" or "." or ".." || segment.EndsWith(' ') || segment.EndsWith('.')
            || segment.IndexOfAny(['<', '>', '"', '|', '?', '*']) >= 0)) return null;
        return path;
    }
}
