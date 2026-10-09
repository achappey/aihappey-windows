using System.Globalization;
using System.Net.Mail;
using System.Text.Json;
using System.Text.RegularExpressions;
using ModelContextProtocol.Protocol;

namespace AIHappey.Desktop.Core;

public delegate Task<ElicitResult> DesktopElicitationHandler(string origin, ElicitRequestParams request, CancellationToken ct);

public sealed record ElicitationOption(string Value, string Title);

/// <summary>A visual-neutral projection of the SDK's constrained form schema, shared with provider input requests.</summary>
public sealed class ElicitationField
{
    public string Name { get; }
    public string Title { get; }
    public string? Description { get; }
    public string Type { get; }
    public string? Format { get; }
    public bool Required { get; }
    public bool IsEnum { get; }
    public IReadOnlyList<ElicitationOption> Options { get; }
    public JsonElement? Default { get; }
    private readonly JsonElement schema;

    internal ElicitationField(string name, ElicitRequestParams.PrimitiveSchemaDefinition definition, bool required)
    {
        Name = name; Required = required;
        schema = JsonSerializer.SerializeToElement(definition);
        Title = Text(schema, "title") ?? name;
        Description = Text(schema, "description"); Type = Text(schema, "type") ?? ""; Format = Text(schema, "format");
        IsEnum = schema.TryGetProperty("enum", out _) || schema.TryGetProperty("oneOf", out _) || Type == "array";
        var source = Type == "array" && schema.TryGetProperty("items", out var items) ? items : schema;
        var options = new List<ElicitationOption>();
        if (source.TryGetProperty("enum", out var values))
        {
            var titles = schema.TryGetProperty("enumNames", out var names) && names.ValueKind == JsonValueKind.Array
                ? names.EnumerateArray().Select(n => n.GetString()).ToArray() : [];
            var i = 0;
            foreach (var value in values.EnumerateArray())
            {
                var text = value.GetString() ?? throw InvalidSchema();
                options.Add(new(text, i < titles.Length ? titles[i] ?? text : text)); i++;
            }
        }
        else if (source.TryGetProperty(Type == "array" ? "anyOf" : "oneOf", out var titled))
            foreach (var value in titled.EnumerateArray())
            {
                var text = Text(value, "const") ?? throw InvalidSchema();
                options.Add(new(text, Text(value, "title") ?? text));
            }
        Options = options;
        if (Type is not ("string" or "number" or "integer" or "boolean" or "array")
            || IsEnum && (options.Count == 0 || options.Count > 1000 || options.Select(o => o.Value).Distinct().Count() != options.Count))
            throw InvalidSchema();
        Default = schema.TryGetProperty("default", out var initial)
            && initial.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null) ? initial.Clone() : null;
    }

    public string? Validate(JsonElement? value)
    {
        if (value is null || value.Value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return Required ? "ElicitationRequired" : null;
        var v = value.Value;
        if (Type == "boolean") return v.ValueKind is JsonValueKind.True or JsonValueKind.False ? null : "ElicitationInvalidValue";
        if (Type is "number" or "integer")
        {
            if (v.ValueKind != JsonValueKind.Number || !v.TryGetDouble(out var number) || !double.IsFinite(number)
                || Type == "integer" && (v.TryGetDecimal(out var exact) ? exact != decimal.Truncate(exact) : number != Math.Truncate(number)))
                return "ElicitationInvalidNumber";
            if (Bound("minimum") is { } min && number < min || Bound("maximum") is { } max && number > max)
                return "ElicitationNumberBounds";
            return null;
        }
        if (Type == "array")
        {
            if (v.ValueKind != JsonValueKind.Array) return "ElicitationInvalidValue";
            var selected = v.EnumerateArray().ToArray();
            if (selected.Any(x => x.ValueKind != JsonValueKind.String || !Options.Any(o => o.Value == x.GetString()))
                || selected.Select(x => x.GetString()).Distinct().Count() != selected.Length) return "ElicitationInvalidChoice";
            if (Bound("minItems") is { } min && selected.Length < min || Bound("maxItems") is { } max && selected.Length > max)
                return "ElicitationSelectionBounds";
            return null;
        }
        if (v.ValueKind != JsonValueKind.String) return "ElicitationInvalidValue";
        var text = v.GetString()!;
        if (IsEnum) return Options.Any(o => o.Value == text) ? null : "ElicitationInvalidChoice";
        // JSON Schema measures Unicode code points, not UTF-16 code units.
        var length = text.EnumerateRunes().Count();
        if (Bound("minLength") is { } lower && length < lower || Bound("maxLength") is { } upper && length > upper)
            return "ElicitationStringLength";
        var valid = Format switch
        {
            "email" => !text.Any(char.IsWhiteSpace) && MailAddress.TryCreate(text, out var mail) && mail.Address == text,
            "uri" => !text.Any(char.IsWhiteSpace) && Uri.TryCreate(text, UriKind.Absolute, out _),
            "date" => DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
            "date-time" => Regex.IsMatch(text, @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?(Z|[+-]\d{2}:\d{2})$", RegexOptions.IgnoreCase,
                    TimeSpan.FromMilliseconds(100))
                && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
            _ => true
        };
        return valid ? null : "ElicitationInvalidFormat";
    }

    public double? Bound(string name) => schema.TryGetProperty(name, out var v) && v.TryGetDouble(out var n) ? n : null;
    internal static string? Text(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    internal static InvalidOperationException InvalidSchema() => new(DesktopResources.Get("ElicitationInvalidSchema"));
}

public sealed class DesktopElicitationForm
{
    public IReadOnlyList<ElicitationField> Fields { get; }
    public Dictionary<string, JsonElement> Values { get; } = new(StringComparer.Ordinal);
    public DesktopElicitationForm(ElicitRequestParams request)
    {
        if (request.Mode is not (null or "form") || request.RequestedSchema is not { } schema
            || schema.Properties is null || schema.Properties.Count > 200 || request.Message?.Length > 100_000)
            throw ElicitationField.InvalidSchema();
        var required = (schema.Required ?? []).ToHashSet(StringComparer.Ordinal);
        if (required.Any(name => !schema.Properties.ContainsKey(name))) throw ElicitationField.InvalidSchema();
        Fields = schema.Properties.Select(p => new ElicitationField(p.Key, p.Value, required.Contains(p.Key))).ToArray();
        foreach (var field in Fields)
            if (field.Default is { } value) Values[field.Name] = value.Clone();
    }
    public Dictionary<string, string> Errors() => Fields.Select(f => (f.Name, Error: f.Validate(Values.TryGetValue(f.Name, out var value) ? value : null)))
        .Where(p => p.Error is not null).ToDictionary(p => p.Name, p => DesktopResources.Get(p.Error!), StringComparer.Ordinal);
    public ElicitResult Accept()
    {
        if (Errors().Count > 0) throw new InvalidOperationException(DesktopResources.Get("ElicitationCheckFields"));
        return new() { Action = "accept", Content = Values.Where(p => Fields.Any(f => f.Name == p.Key))
            .ToDictionary(p => p.Key, p => p.Value.Clone(), StringComparer.Ordinal) };
    }
    public static ElicitRequestParams ParseProviderRequest(JsonElement input)
    {
        if (ElicitationField.Text(input, "method") != "elicitation/create"
            || !input.TryGetProperty("params", out var parameters) || parameters.ValueKind != JsonValueKind.Object
            || ElicitationField.Text(parameters, "mode") is not (null or "form")) throw ElicitationField.InvalidSchema();
        return parameters.Deserialize<ElicitRequestParams>() ?? throw ElicitationField.InvalidSchema();
    }
}
