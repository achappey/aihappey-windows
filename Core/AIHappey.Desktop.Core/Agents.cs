using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AIHappey.Desktop.Core;

/// <summary>The browser Agent document is the storage/export/wire contract, not a projection
/// into the server SDK's narrower model. Edits affect only the selected JSON properties.</summary>
public sealed class DesktopAgent
{
    private static readonly Lazy<JsonObject> ProviderDefaults = new(() =>
    {
        using var stream = typeof(DesktopAgent).Assembly.GetManifestResourceStream("Desktop.AgentProviderDefaults");
        return stream is null ? new() : JsonNode.Parse(stream) as JsonObject ?? new();
    });
    public JsonObject Definition { get; }
    public string Name => Text(Definition["name"]);
    public string Description => Text(Definition["description"]);
    public IReadOnlyList<JsonObject> Models
    {
        get
        {
            var models = (Definition["models"] as JsonArray)?.OfType<JsonObject>()
                .Where(model => !string.IsNullOrWhiteSpace(Text(model["id"])))
                .DistinctBy(model => Text(model["id"]), StringComparer.Ordinal).ToArray() ?? [];
            return models.Length > 0 ? models : Definition["model"] is JsonObject legacy
                && !string.IsNullOrWhiteSpace(Text(legacy["id"])) ? [legacy] : [];
        }
    }
    public string ModelId => Text((Models.FirstOrDefault(model => Text(model["type"]) != "decision") ?? Models.FirstOrDefault())?["id"]);
    public IReadOnlyList<string> ModelIds => Models.Select(model => Text(model["id"])).ToArray();
    public string SelectionKey => "local:" + Name;
    public DesktopAgent(JsonObject definition) => Definition = (JsonObject)definition.DeepClone();
    public DesktopAgent Clone() => new(Definition);
    public static string Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";
    public static bool Boolean(JsonNode? node) => node is JsonValue value && value.TryGetValue<bool>(out var boolean) && boolean;
    public static JsonObject Object(JsonObject parent, string key)
    {
        if (parent[key] is JsonObject value) return value;
        var result = new JsonObject(); parent[key] = result; return result;
    }
    public bool IsValid => !string.IsNullOrWhiteSpace(Name) && !string.IsNullOrWhiteSpace(Description)
        && !string.IsNullOrWhiteSpace(ModelId) && !string.IsNullOrWhiteSpace(Text(Definition["instructions"]));
    public void Validate()
    {
        if (!IsValid) throw new InvalidDataException(DesktopResources.Get("AgentFieldsRequired"));
        if (Name.Any(char.IsControl)) throw new InvalidDataException(DesktopResources.Get("AgentNameInvalid"));
    }
    internal void ValidateStored()
    {
        // Browser tools require these properties but allow empty descriptions/instructions.
        // Keep native editor/import validation stricter without making such portable agents unreadable.
        if (string.IsNullOrWhiteSpace(Name) || string.IsNullOrWhiteSpace(ModelId)
            || Definition["description"] is not JsonValue d || !d.TryGetValue<string>(out _)
            || Definition["instructions"] is not JsonValue i || !i.TryGetValue<string>(out _))
            throw new InvalidDataException(DesktopResources.Get("AgentFieldsRequired"));
        if (Name.Any(char.IsControl)) throw new InvalidDataException(DesktopResources.Get("AgentNameInvalid"));
    }
    public static DesktopAgent Parse(string json)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaxDocumentBytes) throw new InvalidDataException(DesktopResources.Get("ResponseSizeLimit"));
        var agent = new DesktopAgent(JsonNode.Parse(json) as JsonObject ?? throw new InvalidDataException(DesktopResources.Get("InvalidCatalogDocument")));
        agent.Definition["name"] = agent.Name.Trim(); agent.Validate(); agent.Normalize(); return agent;
    }
    public const int MaxDocumentBytes = 96 * 1024 * 1024;
    public void Normalize()
    {
        if (Definition["models"] is JsonArray collection)
            Definition["models"] = new JsonArray(collection.OfType<JsonObject>()
                .Where(model => !string.IsNullOrWhiteSpace(Text(model["id"])))
                .DistinctBy(model => Text(model["id"]), StringComparer.Ordinal).Select(model => model.DeepClone()).ToArray());
        foreach (var model in Models.Concat(Definition["model"] is JsonObject legacyModel ? new[] { legacyModel } : []).Distinct())
        {
            if (model["providerHeaders"] is not JsonObject headers) continue;
            var provider = Provider(Text(model["id"])).ToLowerInvariant();
            if (headers[provider] is JsonObject legacy) headers = legacy;
            var cleaned = new JsonObject();
            foreach (var (key, value) in headers)
                if (!string.IsNullOrWhiteSpace(key) && value is JsonValue scalar)
                {
                    var text = scalar.TryGetValue<string>(out var s) ? s.Trim() : scalar.ToJsonString();
                    if (text.Length > 0) cleaned[key.Trim()] = text;
                }
            if (cleaned.Count == 0) model.Remove("providerHeaders"); else model["providerHeaders"] = cleaned;
        }
        if (Definition["mcpClient"] is JsonObject client && client["capabilities"] is JsonObject capabilities
            && capabilities.ContainsKey("elicitation") && capabilities["elicitation"] is not JsonObject)
            capabilities.Remove("elicitation");
    }
    public static string Provider(string model) => model.Trim().Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
    /// <summary>Apply the changes made by a native provider form, retaining fields that its
    /// canonical view did not edit or understand.</summary>
    public static void ApplyChanges(JsonObject target, JsonObject before, JsonObject after)
    {
        foreach (var key in before.Select(p => p.Key).Union(after.Select(p => p.Key)).ToArray())
        {
            if (JsonNode.DeepEquals(before[key], after[key]) && before.ContainsKey(key) == after.ContainsKey(key)) continue;
            if (key == "tools" && (before[key] is JsonArray || after[key] is JsonArray))
                ApplyToolChanges(target, before[key] as JsonArray ?? new(), after[key] as JsonArray ?? new());
            else if (!after.ContainsKey(key)) target.Remove(key);
            else if (before[key] is JsonObject oldObject && after[key] is JsonObject newObject && target[key] is JsonObject current)
                ApplyChanges(current, oldObject, newObject);
            else target[key] = after[key]?.DeepClone();
        }
    }
    private static void ApplyToolChanges(JsonObject target, JsonArray before, JsonArray after)
    {
        static string Identity(JsonObject tool) => Text(tool["type"]) + "\n" + Text(tool["name"]);
        var oldTools = before.OfType<JsonObject>().GroupBy(Identity).ToDictionary(g => g.Key, g => g.First());
        var newTools = after.OfType<JsonObject>().GroupBy(Identity).ToDictionary(g => g.Key, g => g.First());
        var current = target["tools"] as JsonArray ?? new JsonArray();
        foreach (var id in oldTools.Keys.Union(newTools.Keys))
        {
            oldTools.TryGetValue(id, out var previous); newTools.TryGetValue(id, out var next);
            if (JsonNode.DeepEquals(previous, next)) continue;
            var existing = current.OfType<JsonObject>().FirstOrDefault(t => Identity(t) == id);
            var type = Text((previous ?? next)?["type"]);
            var legacy = OpenAIChatConfig.ToolTypes.Contains(type) ? target[type] as JsonObject : null;
            if (next is null)
            {
                foreach (var entry in current.OfType<JsonObject>().Where(t => Identity(t) == id).ToArray()) current.Remove(entry);
            }
            else if (existing is not null && previous is not null) ApplyChanges(existing, previous, next);
            else if (legacy is not null && previous is not null)
            {
                var copy = (JsonObject)legacy.DeepClone(); copy["type"] ??= type; ApplyChanges(copy, previous, next); current.Add(copy);
            }
            else current.Add(next.DeepClone());
            if (legacy is not null) target.Remove(type); // Only explicitly edited aliases migrate.
        }
        if (current.Count == 0) target.Remove("tools"); else if (target["tools"] is null) target["tools"] = current;
    }
    public void ChangeModel(string id)
    {
        var model = Object(Definition, "model");
        if (Provider(id).Length > 0 && Provider(ModelId) != Provider(id))
        {
            model["providerMetadata"] = ProviderDefaults.Value[Provider(id)]?.DeepClone() ?? new JsonObject(); model.Remove("providerHeaders");
        }
        model["id"] = id;
    }
    public void SelectModels(IEnumerable<(string Id, string? Type)> selected)
    {
        var previous = Models.ToDictionary(model => Text(model["id"]), StringComparer.Ordinal);
        var next = selected.Where(item => !string.IsNullOrWhiteSpace(item.Id)).DistinctBy(item => item.Id, StringComparer.Ordinal)
            .Select(item =>
            {
                var model = previous.TryGetValue(item.Id, out var existing) ? (JsonObject)existing.DeepClone()
                    : new JsonObject { ["id"] = item.Id, ["providerMetadata"] = ProviderDefaults.Value[Provider(item.Id)]?.DeepClone() ?? new JsonObject() };
                if (item.Type is not null) model["type"] = item.Type;
                return model;
            }).ToArray();
        Definition["models"] = new JsonArray(next.Select(model => (JsonNode)model).ToArray());
        if (Definition["model"] is not null)
            Definition["model"] = next.FirstOrDefault(model => Text(model["type"]) != "decision")?.DeepClone();
    }
    public void SyncLegacyModel()
    {
        // Existing single-model agents remain readable by older clients; collection-only imports stay collection-only.
        if (Definition["model"] is not null)
            Definition["model"] = Models.FirstOrDefault(model => Text(model["type"]) != "decision")?.DeepClone();
    }
    public void ToggleTool(string type, bool enabled)
    {
        var tools = Definition["tools"] as JsonArray;
        if (tools is null) { if (!enabled) return; Definition["tools"] = tools = new JsonArray(); }
        if (enabled)
        {
            if (!tools.Any(t => Text(t?["type"]) == type)) tools.Add(new JsonObject { ["type"] = type });
        }
        else for (var i = tools.Count - 1; i >= 0; i--) if (Text(tools[i]?["type"]) == type) tools.RemoveAt(i);
        if (tools.Count == 0) Definition.Remove("tools");
    }
    public CatalogItem CatalogItem()
    {
        var icons = (Definition["icons"] as JsonArray)?.OfType<JsonObject>().Select(i => new CatalogIcon(Text(i["src"]), Text(i["theme"]))).ToArray() ?? [];
        return new(CatalogKind.Agent, Name, Name, Description) { Origin = CatalogOrigin.Local, Model = ModelId,
            Definition = JsonSerializer.SerializeToElement(Definition), Icons = icons };
    }
    public static DesktopAgent Empty() => new(new JsonObject { ["name"] = "", ["description"] = "", ["instructions"] = "",
        ["model"] = new JsonObject { ["id"] = "" }, ["mcpClient"] = new JsonObject { ["policy"] = new JsonObject
            { ["readOnlyHint"] = false, ["openWorldHint"] = true, ["idempotentHint"] = false, ["destructiveHint"] = true } } });
}

public static class DesktopAgentTargets
{
    public static IReadOnlyList<ChatTarget> Project(IEnumerable<CatalogItem> items) => items.Select(i =>
        (i.Origin == CatalogOrigin.Local ? new ChatTarget("local:" + i.Id, i.Name + " (" + DesktopResources.Get("Local") + ")") { LocalAgentName = i.Id }
            : new ChatTarget("remote:" + i.Id, i.Name) { RemoteAgentId = i.Id }) with
        { AgentModelId = i.Model, ProviderKey = ChatPreferences.ResolveProvider(ServiceKind.Ai, i.Model ?? ""),
            Description = i.Description, ModelMetadata = i.Definition,
            Tags = i.Definition is { } definition && definition.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array
                ? tags.EnumerateArray().Where(t => t.ValueKind == JsonValueKind.String).Select(t => t.GetString()!).ToArray() : [] }).ToArray();

    public static string DisplayName(string value)
    {
        var prefix = value.StartsWith("remote:", StringComparison.Ordinal) ? 7 : value.StartsWith("local:", StringComparison.Ordinal) ? 6 : 0;
        return prefix > 0 && value.Length > prefix ? value[prefix..] : value;
    }

    public static ChatTarget? Selection(string text, string? selectedId, IReadOnlyList<ChatTarget> targets)
    {
        // A chosen item's identity wins over its label, which need not be unique.
        if (targets.FirstOrDefault(t => t.Id == selectedId) is { } selected && text == selected.Label) return selected;
        var id = Restore(text.Trim(), targets);
        if (targets.FirstOrDefault(t => t.Id == id) is { } exact) return exact;
        var matches = targets.Where(t => t.Label.Equals(text.Trim(), StringComparison.Ordinal)).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    public static string Restore(string value, IReadOnlyList<ChatTarget> targets)
    {
        if (targets.Any(t => t.Id == value)) return value;
        // Older Windows conversations stored the raw backend ID, never a local display name.
        return targets.FirstOrDefault(t => t.RemoteAgentId == value)?.Id ?? value;
    }
}

/// <summary>Account/profile-local portable definitions. A missing file seeds defaults once;
/// an empty existing store remains empty. Failed writes never replace committed storage.</summary>
public sealed class DesktopAgentStore(string directory)
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    private string PathFor(string partition) => Path.Combine(directory, McpValidation.Hash(partition) + ".json");
    private async Task<List<DesktopAgent>> ReadAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path)) return [];
        if (new FileInfo(path).Length > 256L * 1024 * 1024) throw new InvalidDataException(DesktopResources.Get("ResponseSizeLimit"));
        var array = JsonNode.Parse(await File.ReadAllTextAsync(path, ct)) as JsonArray
            ?? throw new InvalidDataException(DesktopResources.Get("InvalidCatalogDocument"));
        var result = new List<DesktopAgent>(); var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var definition in array)
        {
            if (definition is not JsonObject obj) throw new InvalidDataException(DesktopResources.Get("InvalidCatalogDocument"));
            var agent = new DesktopAgent(obj); agent.ValidateStored(); agent.Normalize();
            if (!names.Add(agent.Name)) throw new InvalidDataException(DesktopResources.Get("AgentDuplicateName"));
            result.Add(agent);
        }
        return result;
    }
    private static async Task WriteAsync(string path, IEnumerable<DesktopAgent> agents, CancellationToken ct)
    {
        var array = new JsonArray(agents.Select(a => (JsonNode)a.Definition.DeepClone()).ToArray());
        var bytes = JsonSerializer.SerializeToUtf8Bytes(array);
        if (bytes.Length > 256L * 1024 * 1024) throw new InvalidDataException(DesktopResources.Get("ResponseSizeLimit"));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { await File.WriteAllBytesAsync(temporary, bytes, ct); ct.ThrowIfCancellationRequested(); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public async Task<IReadOnlyList<DesktopAgent>> ListAsync(string partition, IEnumerable<DesktopAgent> defaults, CancellationToken ct)
    {
        var path = PathFor(partition); var gate = Gates.GetOrAdd(path, _ => new(1, 1)); await gate.WaitAsync(ct);
        try
        {
            if (!File.Exists(path))
            {
                var initial = defaults.Select(a => a.Clone()).ToArray();
                foreach (var agent in initial) { agent.Validate(); agent.Normalize(); }
                if (initial.DistinctBy(a => a.Name, StringComparer.Ordinal).Count() != initial.Length)
                    throw new InvalidDataException(DesktopResources.Get("AgentDuplicateName"));
                await WriteAsync(path, initial, ct);
            }
            return await ReadAsync(path, ct);
        }
        finally { gate.Release(); }
    }
    public async Task SaveAsync(string partition, DesktopAgent agent, string? editingName, CancellationToken ct, bool portableTool = false)
    {
        var copy = agent.Clone(); copy.Definition["name"] = copy.Name.Trim();
        if (portableTool) copy.ValidateStored(); else copy.Validate();
        copy.Normalize();
        var path = PathFor(partition); var gate = Gates.GetOrAdd(path, _ => new(1, 1)); await gate.WaitAsync(ct);
        try
        {
            var items = await ReadAsync(path, ct); var index = items.FindIndex(a => a.Name == (editingName ?? copy.Name));
            if (editingName is null && index >= 0) throw new InvalidOperationException(DesktopResources.Get("AgentDuplicateName"));
            if (editingName is not null && (index < 0 || copy.Name != editingName)) throw new InvalidOperationException(DesktopResources.Get("AgentNameImmutable"));
            if (index < 0) items.Add(copy); else items[index] = copy;
            await WriteAsync(path, items, ct);
        }
        finally { gate.Release(); }
    }
    public async Task DeleteAsync(string partition, string name, CancellationToken ct)
    {
        var path = PathFor(partition); var gate = Gates.GetOrAdd(path, _ => new(1, 1)); await gate.WaitAsync(ct);
        try { var items = await ReadAsync(path, ct); items.RemoveAll(a => a.Name == name); await WriteAsync(path, items, ct); }
        finally { gate.Release(); }
    }
}
