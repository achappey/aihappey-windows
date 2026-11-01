using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace AIHappey.Desktop.Core;

/// <summary>Native Apply Patch harness, not a function-tool definition. The production
/// root is the OS-resolved Documents known folder; tests may supply an isolated root.</summary>
public sealed class OpenAIApplyPatchTools(string? root = null, Func<bool>? isCurrent = null)
{
    public const string ToolName = "apply_patch";
    private static readonly SemaphoreSlim Mutations = new(1, 1);
    private static readonly UTF8Encoding Utf8 = new(false, true);
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
            var documents = root ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (string.IsNullOrWhiteSpace(documents)) throw new LocalToolException("My Documents is unavailable.");
            var folder = LocalFilePaths.Normalize(documents);
            var path = ResolvePath(folder, name);
            var diff = type == "delete_file" ? "" : operation.TryGetProperty("diff", out var diffValue) && diffValue.ValueKind == JsonValueKind.String
                ? diffValue.GetString()! : throw new LocalToolException("A V4A diff is required.");
            if (diff.Length > 2_000_000) throw new LocalToolException("Patch exceeds the supported size limit.");
            using var locks = LockParents(path, type == "create_file");
            Check(ct); LocalFilePaths.CheckNoLinks(path);
            if (Directory.Exists(path)) throw new LocalToolException("Apply Patch operates on files, not directories.");
            if (type == "create_file")
            {
                if (File.Exists(path)) throw new LocalToolException("File already exists: " + name);
                await CommitAsync(path, Utf8.GetBytes(V4ADiff.Apply("", diff, create: true)), create: true, ct);
            }
            else
            {
                var bytes = await ReadAsync(path, ct);
                var bom = bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf });
                var text = Utf8.GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
                if (text.Contains('\0')) throw new LocalToolException("Only UTF-8 text files are supported by Apply Patch.");
                if (type == "update_file")
                {
                    var updated = Utf8.GetBytes(V4ADiff.Apply(text, diff));
                    if (bom) updated = [0xef, 0xbb, 0xbf, .. updated];
                    // Recheck the current bytes before replacing: never knowingly clobber
                    // a change made since the diff's source was read.
                    var currentBytes = await ReadAsync(path, ct);
                    if (!bytes.AsSpan().SequenceEqual(currentBytes)) throw new LocalToolException("File changed while applying patch. Read it again.");
                    await CommitAsync(path, updated, create: false, ct);
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
    {
        if (string.IsNullOrWhiteSpace(name) || name != name.Trim() || Path.IsPathRooted(name) || name.Contains(':')
            || name.Replace('\\', '/').Split('/').Any(part => part is "" or "." or ".."))
            throw new LocalToolException("Patch paths must be relative file paths inside My Documents; traversal is not allowed.");
        var path = LocalFilePaths.Normalize(Path.Combine(folder, name.Replace('/', Path.DirectorySeparatorChar)));
        if (!LocalFilePaths.IsWithin(path, folder)) throw new LocalToolException("Patch path must remain inside My Documents.");
        return path;
    }

    private async Task<byte[]> ReadAsync(string path, CancellationToken ct)
    {
        Check(ct); LocalFilePaths.CheckNoLinks(path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
        LocalFilePaths.CheckOpenedFile(stream.SafeFileHandle, path);
        if (stream.Length > 10_000_000) throw new LocalToolException("File exceeds the supported size limit.");
        var bytes = new byte[(int)stream.Length]; await stream.ReadExactlyAsync(bytes, ct);
        if (await stream.ReadAsync(new byte[1], ct) != 0) throw new LocalToolException("File changed while reading.");
        Check(ct); return bytes;
    }

    private async Task CommitAsync(string path, byte[] bytes, bool create, CancellationToken ct)
    {
        var temporary = Path.Combine(Path.GetDirectoryName(path)!, ".aihappey-patch-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                LocalFilePaths.CheckOpenedFile(stream.SafeFileHandle, temporary);
                await stream.WriteAsync(bytes, ct); await stream.FlushAsync(ct);
            }
            Check(ct); LocalFilePaths.CheckNoLinks(path);
            if (create) File.Move(temporary, path, overwrite: false);
            else File.Replace(temporary, path, null);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static JsonElement Result(string status, string output) => JsonSerializer.SerializeToElement(new
    {
        isError = status == "failed", content = new[] { new { type = "text", text = output } },
        structuredContent = new { status, output }
    });

    // Keep ancestor directories open without FILE_SHARE_DELETE while resolving,
    // creating and changing files. This prevents junction/parent replacement during
    // the operation, in addition to the existing reparse-point and handle checks.
    private static ParentLocks LockParents(string path, bool create)
    {
        var result = new ParentLocks();
        try
        {
            var parent = Path.GetDirectoryName(path)!; var current = Path.GetPathRoot(parent)!;
            void Lock(string directory)
            {
                if (!Directory.Exists(directory))
                {
                    if (!create) throw new DirectoryNotFoundException();
                    Directory.CreateDirectory(directory);
                }
                LocalFilePaths.CheckNoLinks(directory);
                var handle = CreateFile(directory, 0x80, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
                if (handle.IsInvalid) { handle.Dispose(); throw new IOException("Cannot lock patch directory."); }
                result.Handles.Add(handle);
                LocalFilePaths.CheckOpenedFile(handle, directory);
            }
            Lock(current);
            foreach (var segment in parent[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment); Lock(current);
            }
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    private sealed class ParentLocks : IDisposable
    {
        public List<SafeFileHandle> Handles { get; } = [];
        public void Dispose() { foreach (var handle in Handles.AsEnumerable().Reverse()) handle.Dispose(); }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
}
