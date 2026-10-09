using System.Reflection;
using System.Text.Json.Nodes;
using AIHappey.Desktop.Core;

namespace AIHappey.Desktop.Samples;

internal static class SampleAgents
{
    public static IReadOnlyList<DesktopAgent> Load()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Desktop.SampleAgents")
            ?? throw new InvalidOperationException("Missing sample agent definitions.");
        var array = JsonNode.Parse(stream) as JsonArray ?? throw new InvalidDataException("Invalid sample agents.");
        return array.OfType<JsonObject>().Select(value => DesktopAgent.Parse(value.ToJsonString())).ToArray();
    }
}
