using System.Runtime.InteropServices;
using System.Text;

namespace AIHappey.Desktop.Core;

/// <summary>Explicit storage boundaries; never rely on MSIX file-system virtualization.</summary>
public static class DesktopPaths
{
    private static readonly Lazy<string?> family = new(ReadPackageFamily);
    public static string? PackageFamily => family.Value;
    public static string ProfileDirectory(string profile) => ResolveProfile(profile, PackageFamily,
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
    public static string PackageState => PackageFamily is { } name
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages", name, "LocalState")
        : throw new InvalidOperationException("Explorer activation requires an installed package.");

    public static string ResolveProfile(string profile, string? packageFamily, string localAppData)
    {
        if (string.IsNullOrWhiteSpace(profile) || profile is "." or ".." || profile.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("Invalid desktop profile.");
        return packageFamily is null ? Path.Combine(localAppData, "AIHappey", "Desktop", profile)
            : Path.Combine(localAppData, "Packages", packageFamily, "LocalState", "Desktop", profile);
    }

    public static string InstanceKey(string profile, string? packageFamily, string user, int session)
        => $"AIHappey.Desktop.{profile}.{packageFamily ?? "portable"}.{user}.{session}";

    private static string? ReadPackageFamily()
    {
        uint length = 0;
        var result = GetCurrentPackageFamilyName(ref length, null);
        if (result == 15700) return null; // APPMODEL_ERROR_NO_PACKAGE
        if (result != 122) throw new InvalidOperationException($"Package identity lookup failed ({result}).");
        var buffer = new StringBuilder((int)length);
        result = GetCurrentPackageFamilyName(ref length, buffer);
        if (result != 0) throw new InvalidOperationException($"Package identity lookup failed ({result}).");
        return buffer.ToString();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFamilyName(ref uint length, StringBuilder? name);
}
