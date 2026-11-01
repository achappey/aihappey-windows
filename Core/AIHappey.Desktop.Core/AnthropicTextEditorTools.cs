using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AIHappey.Desktop.Core;

/// <summary>Native Anthropic editor harness. Mutations return acknowledgements, not embedded documents.</summary>
public sealed class AnthropicTextEditorTools(string? root = null, Func<bool>? isCurrent = null, int? maxCharacters = null)
{
    public static readonly string[] ToolNames = ["str_replace_based_edit_tool", "str_replace_editor"];
    private readonly LocalDocumentFiles files = new(root, isCurrent);
    public static bool Register(McpTurnSnapshot snapshot, ChatPreferences preferences, string? provider,
        Func<bool>? isCurrent = null, string? root = null)
    {
        if (provider != "anthropic" || !preferences.ProviderMetadata.TryGetValue("anthropic", out var config)
            || AnthropicChatConfig.Resolve(config)["text_editor"] is not JsonObject editor) return false;
        var maximum = editor["max_characters"] is JsonValue value && value.TryGetValue<int>(out var number) && number > 0 ? (int?)number : null;
        var runtime = new AnthropicTextEditorTools(root, isCurrent, maximum);
        foreach (var name in ToolNames) snapshot.AddNative(name, runtime.CallAsync);
        return true;
    }

    public Task<JsonElement> CallAsync(JsonElement input, CancellationToken ct) => DesktopLocalTools.SafeAsync(async () =>
    {
        await LocalDocumentFiles.Mutations.WaitAsync(ct);
        try
        {
            files.Check(ct);
            var command = DesktopLocalTools.Required(input, "command");
            var name = CatalogProjection.Text(input, "path") ?? "";
            if (command == "view" && name is "" or "/")
            {
                var folder = files.Root;
                using var folderLocks = LocalDocumentFiles.LockParents(Path.Combine(folder, ".aihappey-list"), false);
                files.Check(ct);
                var names = Directory.EnumerateFiles(folder).Where(LocalDocumentEdits.IsTextFile)
                    .Where(path => (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0)
                    .Select(Path.GetFileName).Order(StringComparer.OrdinalIgnoreCase).Take(512).ToArray();
                files.Check(ct);
                return Ok(names.Length == 0 ? "No text files available." : "My Documents text files:\n" + string.Join('\n', names.Select(n => "- " + n)));
            }
            if (command is not ("view" or "create" or "str_replace" or "insert")) throw new LocalToolException("Unsupported Anthropic text editor command.");
            var path = files.Resolve(name);
            if (!LocalDocumentEdits.IsTextFile(name)) throw new LocalToolException("Only text file types are supported by the Anthropic text editor.");
            using var locks = LocalDocumentFiles.LockParents(path, command == "create");
            files.Check(ct); LocalFilePaths.CheckNoLinks(path);
            if (Directory.Exists(path)) throw new LocalToolException("The editor operates on files, not directories.");
            if (command == "create")
            {
                if (File.Exists(path)) throw new LocalToolException("File already exists.");
                await files.WriteTextAsync(path, Text(input, "file_text"), null, ct);
                return Ok("Successfully created file " + name + ".");
            }
            var original = await files.ReadAsync(path, ct);
            var text = LocalDocumentFiles.Decode(original);
            if (command == "view")
            {
                var lines = text.Length == 0 ? [] : text.Split('\n');
                var start = 1; var end = lines.Length;
                if (input.TryGetProperty("view_range", out var range) && range.ValueKind != JsonValueKind.Null)
                {
                    if (range.ValueKind != JsonValueKind.Array || range.GetArrayLength() != 2
                        || !range[0].TryGetInt32(out start) || !range[1].TryGetInt32(out end)
                        || start < 1 || end != -1 && end < start) throw new LocalToolException("Invalid view_range. Expected [startLine, endLine], with -1 for EOF.");
                    if (end == -1) end = lines.Length;
                }
                var numbered = string.Join('\n', lines.Skip(start - 1).Take(Math.Max(0, end - start + 1)).Select((line, index) => $"{start + index}: {line}"));
                return Ok(maxCharacters is > 0 && numbered.Length > maxCharacters.Value
                    ? maxCharacters.Value <= 3 ? numbered[..maxCharacters.Value] : numbered[..(maxCharacters.Value - 3)] + "..." : numbered);
            }
            var updated = command == "str_replace"
                ? LocalDocumentEdits.Replace(text, Text(input, "old_str"), Text(input, "new_str"), true)
                : LocalDocumentEdits.Insert(text, Line(input), Text(input, "insert_text"), true);
            await files.WriteTextAsync(path, updated, original, ct);
            return Ok(command == "str_replace" ? "Successfully replaced text at exactly one location." : $"Successfully inserted text after line {Line(input)}.");
        }
        catch (DecoderFallbackException) { throw new LocalToolException("Only UTF-8 text files are supported."); }
        catch (FileNotFoundException) { throw new LocalToolException("File not found."); }
        catch (DirectoryNotFoundException) { throw new LocalToolException("File not found."); }
        finally { LocalDocumentFiles.Mutations.Release(); }
    }, ct);

    internal static string Text(JsonElement input, string field) => CatalogProjection.Text(input, field) ?? "";
    internal static int Line(JsonElement input) => input.TryGetProperty("insert_line", out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var line)
        ? line : throw new LocalToolException("insert_line must be an integer.");
    internal static JsonElement Ok(string text) => DesktopLocalTools.Result(null, DesktopLocalTools.Text(text));
}
