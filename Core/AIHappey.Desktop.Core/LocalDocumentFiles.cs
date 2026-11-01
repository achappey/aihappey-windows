using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AIHappey.Desktop.Core;

/// <summary>Shared Documents boundary for native editors and Canvas. Never used by Canvas projection.</summary>
internal sealed class LocalDocumentFiles(string? root = null, Func<bool>? isCurrent = null)
{
    internal static readonly SemaphoreSlim Mutations = new(1, 1);
    internal static readonly UTF8Encoding Utf8 = new(false, true);
    internal const int MaximumBytes = 10_000_000;
    internal void Check(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (isCurrent?.Invoke() == false) throw new OperationCanceledException(ct);
    }

    internal string Root
    {
        get
        {
            var folder = root ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (string.IsNullOrWhiteSpace(folder)) throw new LocalToolException("My Documents is unavailable.");
            return LocalFilePaths.Normalize(folder);
        }
    }

    internal string Resolve(string name) => ResolvePath(Root, name);
    internal static string ResolvePath(string folder, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name != name.Trim() || Path.IsPathRooted(name) || name.Contains(':')
            || name.Replace('\\', '/').Split('/').Any(part => part is "" or "." or ".."))
            throw new LocalToolException("Paths must be relative file paths inside My Documents; traversal is not allowed.");
        var path = LocalFilePaths.Normalize(Path.Combine(folder, name.Replace('/', Path.DirectorySeparatorChar)));
        if (!LocalFilePaths.IsWithin(path, folder)) throw new LocalToolException("Path must remain inside My Documents.");
        return path;
    }

    internal async Task<byte[]> ReadAsync(string path, CancellationToken ct)
    {
        Check(ct); LocalFilePaths.CheckNoLinks(path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
        LocalFilePaths.CheckOpenedFile(stream.SafeFileHandle, path);
        if (stream.Length > MaximumBytes) throw new LocalToolException("File exceeds the supported size limit.");
        var bytes = new byte[(int)stream.Length]; await stream.ReadExactlyAsync(bytes, ct);
        if (await stream.ReadAsync(new byte[1], ct) != 0) throw new LocalToolException("File changed while reading.");
        Check(ct); return bytes;
    }

    internal static bool HasBom(byte[] bytes) => bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf });
    internal static string Decode(byte[] bytes)
    {
        var offset = HasBom(bytes) ? 3 : 0;
        var text = Utf8.GetString(bytes, offset, bytes.Length - offset);
        if (text.Contains('\0')) throw new LocalToolException("Only UTF-8 text files are supported.");
        return text;
    }

    internal async Task CommitAsync(string path, byte[] bytes, bool create, CancellationToken ct)
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

    internal async Task WriteTextAsync(string path, string text, byte[]? original, CancellationToken ct)
    {
        if (text.Contains('\0')) throw new LocalToolException("Only UTF-8 text files are supported.");
        var bytes = Utf8.GetBytes(text);
        if (original is not null && HasBom(original)) bytes = [0xef, 0xbb, 0xbf, .. bytes];
        if (bytes.Length > MaximumBytes) throw new LocalToolException("File exceeds the supported size limit.");
        if (original is not null)
        {
            var current = await ReadAsync(path, ct);
            if (!original.AsSpan().SequenceEqual(current)) throw new LocalToolException("File changed while editing. Read it again.");
        }
        await CommitAsync(path, bytes, original is null, ct);
    }

    // Keep ancestor directories open without FILE_SHARE_DELETE, including during creation.
    internal static IDisposable LockParents(string path, bool create)
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
                if (handle.IsInvalid) { handle.Dispose(); throw new IOException("Cannot lock document directory."); }
                result.Handles.Add(handle); LocalFilePaths.CheckOpenedFile(handle, directory);
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
        internal List<SafeFileHandle> Handles { get; } = [];
        public void Dispose() { foreach (var handle in Handles.AsEnumerable().Reverse()) handle.Dispose(); }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
}
