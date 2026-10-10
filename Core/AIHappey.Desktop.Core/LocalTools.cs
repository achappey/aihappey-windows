using System.Text.Json;
using System.Text.Json.Nodes;

namespace AIHappey.Desktop.Core;

public sealed record DesktopToolPlugin(string Id, string ResourceKey, IReadOnlyList<JsonElement> Tools);

/// <summary>Data contracts copied from the browser. No MCP transport/server is needed for local tools.</summary>
public static class DesktopLocalTools
{
    private static readonly JsonSerializerOptions resultJson = new(JsonSerializerDefaults.Web)
        { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };
    public const string Conversations = "local-conversations";
    public const string SkillSearch = "skill-search";
    public const string ArtificialIntelligence = "local-artificial-intelligence";
    public static IReadOnlyList<DesktopToolPlugin> Plugins { get; } = Load();
    private static IReadOnlyList<DesktopToolPlugin> Load()
    {
        using var stream = typeof(DesktopLocalTools).Assembly.GetManifestResourceStream("Desktop.LocalToolDefinitions")
            ?? throw new InvalidDataException("Missing local tool contracts.");
        using var document = JsonDocument.Parse(stream);
        return Array.AsReadOnly(new[] { Conversations, SkillSearch, ArtificialIntelligence }.Select(id => new DesktopToolPlugin(id,
            id switch { Conversations => "LocalPluginConversations", SkillSearch => "LocalPluginSkills", _ => "LocalPluginAi" },
            Array.AsReadOnly(document.RootElement.GetProperty(id).EnumerateArray().Select(t => t.Clone()).ToArray()))).ToArray());
    }
    public static bool Reserved(string name) => Plugins.Any(p => p.Tools.Any(t => CatalogProjection.Text(t, "name") == name));
    public static JsonElement Definition(string name) => Plugins.SelectMany(p => p.Tools).Single(t => CatalogProjection.Text(t, "name") == name);
    public static void Register(McpTurnSnapshot snapshot, string plugin, Func<string, JsonElement, CancellationToken, Task<JsonElement>> call)
    {
        foreach (var tool in Plugins.Single(p => p.Id == plugin).Tools)
        {
            var name = CatalogProjection.Text(tool, "name")!;
            snapshot.AddLocal(tool, (input, ct) => call(name, input, ct));
        }
    }
    public static string Required(JsonElement input, string field) => CatalogProjection.Text(input, field) is { Length: > 0 } text
        ? text : throw new LocalToolException("Missing " + field + ".");
    public static JsonElement Result(object? structured, params JsonObject[] content) => JsonSerializer.SerializeToElement(new JsonObject
    {
        ["isError"] = false, ["structuredContent"] = JsonSerializer.SerializeToNode(structured, resultJson),
        ["content"] = new JsonArray(content.Select(c => (JsonNode)c).ToArray())
    });
    public static JsonObject Text(string text) => new() { ["type"] = "text", ["text"] = text };
    public static JsonElement Error(string text) => JsonSerializer.SerializeToElement(new JsonObject
        { ["isError"] = true, ["content"] = new JsonArray(Text(text)) });
    public static async Task<JsonElement> SafeAsync(Func<Task<JsonElement>> call, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try { var result = await call(); ct.ThrowIfCancellationRequested(); return result; }
        catch (OperationCanceledException) { throw; }
        catch (LocalToolException error) { return Error(error.Message); }
        catch (Exception error) when (error is not OutOfMemoryException) { return Error(DesktopResources.Get("LocalToolFailed")); }
    }
}

// Only deliberate user-safe validation errors may be exposed; arbitrary IO/SDK messages never are.
public sealed class LocalToolException(string message) : Exception(message);
