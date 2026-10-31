using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AIHappey.Desktop.Core;

public record McpRegistryInput
{
    public string[]? Choices { get; init; }
    public string? Default { get; init; }
    public string? Description { get; init; }
    public string? Placeholder { get; init; }
    public string? Value { get; init; }
    public bool IsSecret { get; init; }
    public bool IsRequired { get; init; }
    public string? Format { get; init; }
}

public sealed record McpRegistryHeader : McpRegistryInput
{
    public string Name { get; init; } = "";
    public Dictionary<string, McpRegistryInput>? Variables { get; init; }
}

public sealed class McpHeaderField(string header, string label, McpRegistryInput input, string value, string? variable = null, bool fixedValue = false)
{
    public string Header { get; } = header;
    public string Label { get; } = label;
    public McpRegistryInput Input { get; } = input;
    public string? Variable { get; } = variable;
    public bool Fixed { get; } = fixedValue;
    public string Value { get; set; } = value;
}

/// <summary>Registry hints configure presentation only; OAuth endpoints and trust remain discovery/transport-owned.</summary>
public static class McpHeaderConfiguration
{
    private static readonly Regex Variables = new(@"\{([^{}]+)\}", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    public static bool HasOAuthHint(JsonElement entry)
    {
        static bool Has(JsonElement block) => block.ValueKind == JsonValueKind.Object && block.EnumerateObject().Any(property =>
            property.Name.EndsWith("/auth", StringComparison.Ordinal) && property.Value.ValueKind == JsonValueKind.Object
            && CatalogProjection.Text(property.Value, "type") == "oauth2");
        if (entry.ValueKind != JsonValueKind.Object) return false;
        if (entry.TryGetProperty("server", out var server) && server.ValueKind == JsonValueKind.Object
            && server.TryGetProperty("_meta", out var metadata) && metadata.ValueKind == JsonValueKind.Object)
        {
            if (metadata.TryGetProperty("io.modelcontextprotocol.registry/publisher-provided", out var publisher) && Has(publisher)) return true;
            if (Has(metadata)) return true;
        }
        return entry.TryGetProperty("_meta", out var legacy) && Has(legacy);
    }

    public static bool Managed(McpRegistryHeader header, bool oauth) => oauth && header.Name.Equals("Authorization", StringComparison.OrdinalIgnoreCase);
    private static string? Find(IReadOnlyDictionary<string, string> headers, string name) =>
        headers.FirstOrDefault(pair => pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;

    private static Dictionary<string, string>? Extract(McpRegistryHeader header, string value)
    {
        var template = header.Value ?? ""; var pattern = new StringBuilder("\\A"); var offset = 0; var names = new List<string>();
        foreach (Match match in Variables.Matches(template))
        {
            pattern.Append(Regex.Escape(template[offset..match.Index])).Append("(.*?)");
            names.Add(match.Groups[1].Value); offset = match.Index + match.Length;
        }
        pattern.Append(Regex.Escape(template[offset..])).Append("\\z");
        var actual = Regex.Match(value, pattern.ToString(), RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        if (!actual.Success) return null;
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < names.Count; index++)
        {
            var part = actual.Groups[index + 1].Value;
            if (values.TryGetValue(names[index], out var previous) && previous != part) return null;
            values[names[index]] = part;
        }
        return values;
    }

    public static List<McpHeaderField> Fields(IReadOnlyList<McpRegistryHeader> definitions, bool oauth, IReadOnlyDictionary<string, string> headers)
    {
        var fields = new List<McpHeaderField>();
        foreach (var header in definitions)
        {
            if (Managed(header, oauth)) continue;
            var saved = Find(headers, header.Name);
            var extracted = saved is not null && header.Variables?.Count > 0 ? Extract(header, saved) : null;
            if (header.Variables?.Count > 0 && (saved is null || extracted is not null))
                foreach (var (name, input) in header.Variables)
                    fields.Add(new(header.Name, header.Name + " · " + name,
                        input with { IsSecret = input.IsSecret || header.IsSecret, IsRequired = input.IsRequired || header.IsRequired },
                        extracted?.GetValueOrDefault(name) == "{" + name + "}" ? "" : extracted?.GetValueOrDefault(name) ?? input.Value ?? input.Default ?? "", name));
            else fields.Add(new(header.Name, header.Name, header.Variables?.Count > 0
                    ? new McpRegistryInput { IsRequired = header.IsRequired, IsSecret = header.IsSecret || header.Variables.Values.Any(input => input.IsSecret), Description = header.Description }
                    : header, saved ?? header.Value ?? header.Default ?? "", fixedValue: saved is null && header.Value is not null
                        && !Variables.IsMatch(header.Value) && header.Variables?.Count is not > 0));
        }
        return fields;
    }

    private static void ValidateInput(McpRegistryInput input, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) { if (input.IsRequired) throw Invalid(); return; }
        if (input.Choices?.Length > 0 && !input.Choices.Contains(value, StringComparer.Ordinal)
            || input.Format == "number" && (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number))
            || input.Format == "boolean" && value is not "true" and not "false") throw Invalid();
    }
    private static InvalidOperationException Invalid() => new(DesktopResources.Get("McpInvalidHeaderInputs"));

    public static Dictionary<string, string> Resolve(IReadOnlyList<McpRegistryHeader> definitions, IEnumerable<McpHeaderField> fields,
        IEnumerable<KeyValuePair<string, string>> extra, bool validate = true)
    {
        var rows = new List<KeyValuePair<string, string>>(); var all = fields.ToArray();
        foreach (var header in definitions)
        {
            var inputs = all.Where(field => field.Header == header.Name).ToArray();
            if (inputs.Length == 0) continue;
            if (!header.IsRequired && inputs.All(field => field.Value.Length == 0)) continue;
            if (validate) foreach (var field in inputs) ValidateInput(field.Input, field.Value);
            var direct = inputs.FirstOrDefault(field => field.Variable is null);
            if (direct is not null)
            {
                if (validate && Variables.IsMatch(direct.Value)) throw Invalid();
                if (direct.Value.Length > 0) rows.Add(new(header.Name, direct.Value)); continue;
            }
            if (validate && inputs.All(field => field.Value.Length == 0)) continue;
            var value = Variables.Replace(header.Value ?? "", match =>
                inputs.FirstOrDefault(field => field.Variable == match.Groups[1].Value)?.Value is { Length: > 0 } part ? part : match.Value);
            if (validate && (inputs.Any(field => field.Value.Length == 0) || Variables.IsMatch(value))) throw Invalid();
            rows.Add(new(header.Name, value));
        }
        rows.AddRange(extra.Where(pair => pair.Key.Length > 0 || pair.Value.Length > 0));
        return McpValidation.Headers(rows);
    }

    public static Dictionary<string, string> Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) throw Invalid();
            return McpValidation.Headers(doc.RootElement.EnumerateObject().Select(property => new KeyValuePair<string, string>(property.Name,
                property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString()! : throw Invalid())));
        }
        catch (JsonException) { throw Invalid(); }
    }

    public static void Validate(IReadOnlyList<McpRegistryHeader> definitions, bool oauth, IReadOnlyDictionary<string, string> headers)
    {
        foreach (var header in definitions)
        {
            if (Managed(header, oauth)) continue;
            var value = Find(headers, header.Name);
            if (string.IsNullOrWhiteSpace(value)) { if (header.IsRequired) throw Invalid(); continue; }
            if (Variables.IsMatch(value)) throw Invalid();
            if (header.Variables?.Count > 0)
            {
                if (Extract(header, value) is { } extracted)
                    foreach (var (name, input) in header.Variables)
                        ValidateInput(input with { IsRequired = input.IsRequired || header.IsRequired }, extracted.GetValueOrDefault(name) ?? "");
            }
            else ValidateInput(header, value);
        }
    }

    public static Dictionary<string, string> Initial(McpCatalogItem item) => Resolve(item.HeaderDefinitions, Fields(item.HeaderDefinitions, item.OAuthManaged, new Dictionary<string, string>()), []);
    public static bool RequiresConfiguration(McpCatalogItem item)
    {
        try { Validate(item.HeaderDefinitions, item.OAuthManaged, Initial(item)); return false; }
        catch (InvalidOperationException) { return true; }
    }

    public static bool CanConnect(DesktopMcpServer server)
    {
        try { Validate(server.HeaderDefinitions, server.OAuthManaged, server.Headers); return true; }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or RegexMatchTimeoutException) { return false; }
    }

    public static bool HasManualCredentials(DesktopMcpServer server) => server.Headers.Any(pair => !string.IsNullOrWhiteSpace(pair.Value)
        && (pair.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase) || pair.Key.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase)
            || server.HeaderDefinitions.Any(header => header.Name.Equals(pair.Key, StringComparison.OrdinalIgnoreCase)
                && !Managed(header, server.OAuthManaged) && (header.IsSecret || header.Variables?.Values.Any(input => input.IsSecret) == true))
            // Legacy URL-only installations have no declarations; retain common explicit key/token headers.
            || pair.Key.EndsWith("-key", StringComparison.OrdinalIgnoreCase) || pair.Key.EndsWith("-token", StringComparison.OrdinalIgnoreCase)
            || pair.Key.Equals("api_key", StringComparison.OrdinalIgnoreCase)));
}
