using System.Globalization;
using System.Reflection;
using System.Xml.Linq;

namespace AIHappey.Desktop.Core;

// The non-visual runner deliberately does not load WinUI/MRT. Use the real English
// resource values, embedded at build time, so production boundary errors remain testable.
public static class DesktopResources
{
    private static readonly Dictionary<string, string> values = XDocument.Load(
        Assembly.GetExecutingAssembly().GetManifestResourceStream("Desktop.Resources.en")!)
        .Root!.Elements("data").ToDictionary(e => e.Attribute("name")!.Value, e => e.Element("value")!.Value);
    public static string Get(string key) => values.TryGetValue(key, out var value) ? value : throw new InvalidOperationException("Missing resource: " + key);
    public static string Format(string key, params object[] args) => string.Format(CultureInfo.InvariantCulture, Get(key), args);
}
