using System.Globalization;
using Microsoft.Windows.ApplicationModel.Resources;

namespace AIHappey.Desktop.Core;

/// <summary>Native MRT Core lookup for programmatic UI. Translation and fallback are handled by the PRI, not by this class.</summary>
public static class DesktopResources
{
    private static readonly Lazy<ResourceLoader> loader = new(() => new ResourceLoader());

    public static string Get(string resource)
    {
        try { return loader.Value.GetString(resource); }
        catch (Exception error)
        {
            System.Diagnostics.Trace.WriteLine($"Native resource lookup failed: key={resource}; base={AppContext.BaseDirectory}; application PRI exists={File.Exists(Path.Combine(AppContext.BaseDirectory, "resources.pri"))}; error={error}");
            throw;
        }
    }
    public static string Format(string resource, params object?[] values)
        => string.Format(CultureInfo.CurrentCulture, Get(resource), values);
}
