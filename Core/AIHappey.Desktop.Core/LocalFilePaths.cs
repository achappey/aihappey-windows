using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AIHappey.Desktop.Core;

/// <summary>Shared local-only policy for file reads, search scopes, and indexed paths.</summary>
public static class LocalFilePaths
{
    public static string Normalize(string value)
    {
        // Never resolve a relative path against the app's working directory. Reject Win32
        // namespaces/ADS and DOS device names before opening anything (including named pipes).
        if (value.Length is < 3 or > 32700 || !char.IsAsciiLetter(value[0]) || value[1] != ':' || value[2] is not ('\\' or '/')
            || value[2..].Contains(':') || value.Any(char.IsControl)) throw Invalid();
        var segments = value[3..].Replace('/', '\\').Split('\\');
        foreach (var segment in segments)
        {
            if (segment.IndexOfAny(['<', '>', '"', '|', '?', '*']) >= 0 || segment.EndsWith(' ') || segment.EndsWith('.') && segment is not ("." or ".."))
                throw Invalid();
            var stem = segment.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
            if (stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$"
                || stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && (stem[3] is >= '1' and <= '9' or '¹' or '²' or '³'))
                throw Invalid();
        }
        try
        {
            var path = Path.GetFullPath(value);
            var drive = new DriveInfo(Path.GetPathRoot(path)!);
            if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable or DriveType.CDRom or DriveType.Ram)) throw Invalid();
            return path;
        }
        catch (ArgumentException) { throw Invalid(); }
        catch (NotSupportedException) { throw Invalid(); }
    }

    public static void CheckNoLinks(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new LocalToolException(DesktopResources.Get("LocalFileLinksUnsupported"));
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    public static void CheckOpenedFile(SafeFileHandle handle, string expectedPath)
    {
        if (GetFileType(handle) != 1) throw Invalid(); // FILE_TYPE_DISK only.
        var buffer = new StringBuilder(32768);
        var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0 || length >= buffer.Capacity) throw Invalid();
        var final = buffer.ToString();
        if (final.StartsWith(@"\\?\", StringComparison.Ordinal)) final = final[4..];
        // The handle is authoritative; recheck after opening to catch path/link replacement.
        if (!string.Equals(Normalize(final), expectedPath, StringComparison.OrdinalIgnoreCase)) throw Invalid();
        CheckNoLinks(expectedPath);
    }

    public static bool IsWithin(string path, string folder) => path.StartsWith(Path.TrimEndingDirectorySeparator(folder) + Path.DirectorySeparatorChar,
        StringComparison.OrdinalIgnoreCase);

    private static LocalToolException Invalid() => new(DesktopResources.Get("LocalFilePathRequired"));
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint length, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetFileType(SafeFileHandle file);
}
