using System.Text;
using System.Text.Json;
using static AIHappey.Desktop.Core.LocalDocumentFiles;

namespace AIHappey.Desktop.Core;

/// <summary>Native Apply Patch harness, not a function-tool definition. The production
/// root is the OS-resolved Documents known folder; tests may supply an isolated root.</summary>
public sealed class OpenAIApplyPatchTools(string? root = null, Func<bool>? isCurrent = null)
{
    public const string ToolName = "apply_patch";
    private readonly LocalDocumentFiles files = new(root, isCurrent);
    private void Check(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (isCurrent?.Invoke() == false) throw new OperationCanceledException(ct);
    }

    public static bool Enabled(ChatPreferences preferences) => preferences.ProviderMetadata.TryGetValue("openai", out var config)
        && OpenAIChatConfig.Resolve(config)["apply_patch"] is System.Text.Json.Nodes.JsonObject;

    public async Task<JsonElement> CallAsync(JsonElement input, CancellationToken ct)
    {
        await Mutations.WaitAsync(ct);
        try
        {
            Check(ct);
            if (!input.TryGetProperty("operation", out var operation) || operation.ValueKind != JsonValueKind.Object)
                throw new LocalToolException("Missing Apply Patch operation.");
            var type = DesktopLocalTools.Required(operation, "type");
            if (type is not ("create_file" or "update_file" or "delete_file")) throw new LocalToolException("Unsupported Apply Patch operation.");
            var name = DesktopLocalTools.Required(operation, "path");
            var path = files.Resolve(name);
            var diff = type == "delete_file" ? "" : operation.TryGetProperty("diff", out var diffValue) && diffValue.ValueKind == JsonValueKind.String
                ? diffValue.GetString()! : throw new LocalToolException("A V4A diff is required.");
            if (diff.Length > 2_000_000) throw new LocalToolException("Patch exceeds the supported size limit.");
            using var locks = LockParents(path, type == "create_file");
            Check(ct); LocalFilePaths.CheckNoLinks(path);
            if (Directory.Exists(path)) throw new LocalToolException("Apply Patch operates on files, not directories.");
            if (type == "create_file")
            {
                if (File.Exists(path)) throw new LocalToolException("File already exists: " + name);
                await files.CommitAsync(path, Utf8.GetBytes(V4ADiff.Apply("", diff, create: true)), create: true, ct);
            }
            else
            {
                var bytes = await files.ReadAsync(path, ct);
                var bom = bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf });
                var text = Utf8.GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
                if (text.Contains('\0')) throw new LocalToolException("Only UTF-8 text files are supported by Apply Patch.");
                if (type == "update_file")
                {
                    var updated = Utf8.GetBytes(V4ADiff.Apply(text, diff));
                    if (bom) updated = [0xef, 0xbb, 0xbf, .. updated];
                    // Recheck the current bytes before replacing: never knowingly clobber
                    // a change made since the diff's source was read.
                    var currentBytes = await files.ReadAsync(path, ct);
                    if (!bytes.AsSpan().SequenceEqual(currentBytes)) throw new LocalToolException("File changed while applying patch. Read it again.");
                    await files.CommitAsync(path, updated, create: false, ct);
                }
                else
                {
                    Check(ct); LocalFilePaths.CheckNoLinks(path);
                    File.Delete(path);
                }
            }
            return Result("completed", type + ": " + name);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested || isCurrent?.Invoke() == false) { throw; }
        catch (Exception error)
        {
            var message = error switch
            {
                LocalToolException => error.Message,
                FileNotFoundException or DirectoryNotFoundException => "File not found.",
                UnauthorizedAccessException => "File access denied.",
                DecoderFallbackException => "Only UTF-8 text files are supported by Apply Patch.",
                IOException => "File could not be changed. It may be in use, changed, or unavailable.",
                _ => "Invalid Apply Patch request."
            };
            return Result("failed", message);
        }
        finally { Mutations.Release(); }
    }

    public static string ResolvePath(string folder, string name)
        => LocalDocumentFiles.ResolvePath(folder, name);

    private static JsonElement Result(string status, string output) => JsonSerializer.SerializeToElement(new
    {
        isError = status == "failed", content = new[] { new { type = "text", text = output } },
        structuredContent = new { status, output }
    });

}
