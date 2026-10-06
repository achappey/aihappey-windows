using System.Reflection;

namespace AIHappey.Desktop.Core;

public static class DesktopBranding
{
    public static string AppName { get; } = ResolveName(Assembly.GetEntryAssembly()?
        .GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(value => value.Key == "DesktopAppName")?.Value);

    public static string ResolveName(string? name) => string.IsNullOrWhiteSpace(name) ? "aihappey" : name.Trim();
}
