using System.Text;
using System.Text.Json;

namespace AIHappey.Desktop.Core;

/// <summary>Browser Canvas contracts, backed by My Documents rather than browser file storage.</summary>
public sealed class LocalCanvasTools(string? root = null, Func<bool>? isCurrent = null)
{
    private readonly LocalDocumentFiles files = new(root, isCurrent);
    public Task<JsonElement> CallAsync(string name, JsonElement input, CancellationToken ct) => DesktopLocalTools.SafeAsync(async () =>
    {
        await LocalDocumentFiles.Mutations.WaitAsync(ct);
        try
        {
            files.Check(ct);
            if (name is not ("local_canvas_create" or "local_canvas_read" or "local_canvas_insert" or "local_canvas_replace"))
                throw new LocalToolException("Unsupported Canvas tool.");
            // Leading slash is the browser's virtual workspace root, never a filesystem root.
            var relative = DesktopLocalTools.Required(input, "path").Replace('\\', '/').Trim();
            if (relative.StartsWith("//", StringComparison.Ordinal)) throw new LocalToolException("Canvas paths must be relative to My Documents.");
            if (relative.StartsWith('/')) relative = relative[1..];
            if (!relative.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) relative += ".md";
            var path = files.Resolve(relative);
            var create = name == "local_canvas_create";
            using var locks = LocalDocumentFiles.LockParents(path, create);
            files.Check(ct); LocalFilePaths.CheckNoLinks(path);
            if (Directory.Exists(path)) throw new LocalToolException("Canvas operates on files, not directories.");
            var original = create && !File.Exists(path) ? null : await files.ReadAsync(path, ct);
            var text = original is null ? "" : LocalDocumentFiles.Decode(original);
            if (name == "local_canvas_read")
            {
                if (input.TryGetProperty("view_range", out var range) && range.ValueKind != JsonValueKind.Null)
                {
                    if (range.ValueKind != JsonValueKind.Array || range.GetArrayLength() != 2
                        || !range[0].TryGetInt32(out var first) || !range[1].TryGetInt32(out var last))
                        throw new LocalToolException("Invalid view_range. Expected [startLine, endLine].");
                    var lines = text.Split('\n'); var start = Math.Max(1, first); var end = Math.Min(lines.Length, last);
                    if (start <= end) text = string.Join('\n', lines.Skip(start - 1).Take(end - start + 1));
                }
                return AnthropicTextEditorTools.Ok(text);
            }
            var updated = name switch
            {
                "local_canvas_create" => AnthropicTextEditorTools.Text(input, "file_text"),
                "local_canvas_insert" => LocalDocumentEdits.Insert(text, AnthropicTextEditorTools.Line(input), AnthropicTextEditorTools.Text(input, "insert_text"), false),
                _ => LocalDocumentEdits.Replace(text, AnthropicTextEditorTools.Text(input, "old_str"), AnthropicTextEditorTools.Text(input, "new_str"), false)
            };
            await files.WriteTextAsync(path, updated, original, ct);
            return AnthropicTextEditorTools.Ok("OK");
        }
        catch (DecoderFallbackException) { throw new LocalToolException("Only UTF-8 text files are supported."); }
        catch (FileNotFoundException) { throw new LocalToolException("File not found."); }
        catch (DirectoryNotFoundException) { throw new LocalToolException("File not found."); }
        finally { LocalDocumentFiles.Mutations.Release(); }
    }, ct);
}
