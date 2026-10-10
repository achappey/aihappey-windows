using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AIHappey.Desktop.Core;

/// <summary>Pin every parent without write/delete sharing while using a path. This prevents ancestor
/// rename/replacement and reparse mutation between validation and open/create. Never follow links.</summary>
public static class SharedFileAccess
{
    public sealed class DirectoryPins : IDisposable
    {
        private readonly List<SafeFileHandle> handles = [];
        internal void Add(SafeFileHandle handle) => handles.Add(handle);
        public void Dispose() { for (var i = handles.Count - 1; i >= 0; i--) handles[i].Dispose(); handles.Clear(); }
    }
    public static DirectoryPins PinParents(string path, bool includeTarget = false)
    {
        path = LocalFilePaths.Normalize(path);
        var folder = includeTarget ? path : Path.GetDirectoryName(path)!;
        var chain = new Stack<string>();
        for (string? current = folder; current is not null; current = Path.GetDirectoryName(current)) chain.Push(current);
        var pins = new DirectoryPins();
        try
        {
            foreach (var current in chain)
            {
                var handle = Open(current, 0x80, 1, 3, 0x02000000 | 0x00200000); // attributes, share-read, OPEN_EXISTING, backup/open-reparse
                pins.Add(handle); LocalFilePaths.CheckOpenedFile(handle, current);
                if (!File.GetAttributes(current).HasFlag(FileAttributes.Directory)) throw new DirectoryNotFoundException();
            }
            return pins;
        }
        catch { pins.Dispose(); throw; }
    }
    public static string Resolve(SharedFileReference item, string? relativePath, bool requireFolder = false)
    {
        if (requireFolder && !item.IsFolder) throw new LocalToolException(DesktopResources.Get("FilesFolderRequired"));
        var relative = relativePath ?? "";
        if (relative == ".") relative = "";
        if (relative.Length == 0) return item.Path;
        if (!item.IsFolder) throw new LocalToolException(DesktopResources.Get("FilesRelativePathInvalid"));
        var parts = relative.Replace('\\', '/').Split('/');
        if (parts.Any(p => p.Length == 0 || p is "." or "..") || Path.IsPathRooted(relative) || relative.Contains(':'))
            throw new LocalToolException(DesktopResources.Get("FilesRelativePathInvalid"));
        var result = LocalFilePaths.Normalize(Path.Combine(item.Path, Path.Combine(parts)));
        if (!LocalFilePaths.IsWithin(result, item.Path)) throw new LocalToolException(DesktopResources.Get("FilesRelativePathInvalid"));
        return result;
    }
    public static SafeFileHandle CreateNew(string path)
    {
        LocalFilePaths.CheckNoLinks(path);
        // DELETE access allows cleanup by *this handle*, never by a possibly replaced pathname.
        var handle = Open(path, 0x40000000 | 0x00010000, 0, 1, 0x80); // GENERIC_WRITE|DELETE, no sharing, CREATE_NEW
        try { LocalFilePaths.CheckOpenedFile(handle, path); return handle; }
        catch { DeleteOwned(handle); handle.Dispose(); throw; }
    }
    public static bool DeleteOwned(SafeFileHandle handle)
    {
        var disposition = new FileDisposition { DeleteFile = true };
        return SetFileInformationByHandle(handle, 4, ref disposition, (uint)Marshal.SizeOf<FileDisposition>());
    }
    private static SafeFileHandle Open(string path, uint access, uint share, uint disposition, uint flags)
    {
        var handle = CreateFile(path, access, share, IntPtr.Zero, disposition, flags, IntPtr.Zero);
        if (!handle.IsInvalid) return handle;
        var error = Marshal.GetLastWin32Error(); handle.Dispose();
        throw error switch
        {
            2 => new FileNotFoundException(), 3 => new DirectoryNotFoundException(), 5 => new UnauthorizedAccessException(),
            80 or 183 => new LocalToolException(DesktopResources.Get("FilesAlreadyExists")),
            _ => new IOException(new Win32Exception(error).Message)
        };
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct FileDisposition { [MarshalAs(UnmanagedType.Bool)] public bool DeleteFile; }
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int informationClass, ref FileDisposition information, uint size);
}
