using System.Reflection;

namespace AIHappey.Desktop.Core;

public static class DesktopBranding
{
    public static string AppName { get; } = ResolveName(Assembly.GetEntryAssembly()?
        .GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(value => value.Key == "DesktopAppName")?.Value);

    public static string ResolveName(string? name) => string.IsNullOrWhiteSpace(name) ? "aihappey" : name.Trim();

    public static string StorageFolderName { get; } = ResolveStorageFolderName(AppName);

    /// <summary>Keep display branding intact while making its disk folder a single safe Windows component.</summary>
    public static string ResolveStorageFolderName(string? name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var folder = new string(ResolveName(name).Select(c => char.IsControl(c) || invalid.Contains(c) ? '_' : c).ToArray());
        folder = folder[..Math.Min(folder.Length, 120)].TrimEnd(' ', '.');
        if (folder.Length == 0) return ResolveName(null);
        var stem = folder.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$"
            || stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && "123456789¹²³".Contains(stem[3]))
            folder = "_" + folder;
        return folder;
    }
}
