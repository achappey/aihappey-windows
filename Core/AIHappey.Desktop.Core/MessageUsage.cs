using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Vercel.Models;

namespace AIHappey.Desktop.Core;

/// <summary>Optional footer metadata, shared by live and restored assistant messages.</summary>
internal static class MessageUsage
{
    public static (string? Ttft, string? Tps) ReadPerformance(Dictionary<string, object>? metadata)
    {
        var nested = Property(Property(metadata, "providerMetadata"), "gateway");
        var legacy = Property(metadata, "gateway");
        return (FormatMetric(Property(nested, "ttft") ?? Property(legacy, "ttft"), true),
            FormatMetric(Property(nested, "tps") ?? Property(legacy, "tps"), false));
    }

    private static object? Property(object? value, string name)
    {
        object? result = value switch
        {
            JsonElement { ValueKind: JsonValueKind.Object } element when element.TryGetProperty(name, out var property) => property,
            JsonObject node => node[name],
            System.Collections.IDictionary dictionary => dictionary.Contains(name) ? dictionary[name] : null,
            _ => null
        };
        return result is JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } ? null : result;
    }

    // Validate each field independently. Bad optional metrics must never hide valid cost,
    // token or sibling metric badges, and strings are not coerced to numbers.
    internal static string? FormatMetric(object? raw, bool firstToken)
    {
        if (raw is null) return null;
        try
        {
            var element = raw is JsonElement json ? json : JsonSerializer.SerializeToElement(raw);
            if (element.ValueKind != JsonValueKind.Number || !element.TryGetDouble(out var value) || !double.IsFinite(value) || value < 0) return null;
            var rounded = Math.Round(value, firstToken ? 2 : 0, MidpointRounding.AwayFromZero);
            return (rounded == 0 ? 0 : rounded).ToString(firstToken ? "#,0.##" : "N0", CultureInfo.CurrentCulture) + (firstToken ? " s" : " tok/s");
        }
        catch (Exception error) when (error is JsonException or ArgumentException or NotSupportedException) { return null; }
    }

    public static (string? Tokens, string? Price) Read(Dictionary<string, object> metadata, string model, DateTimeOffset timestamp)
    {
        try
        {
            var finish = FinishMessageMetadata.FromDictionary(metadata, model, timestamp);
            var tokens = finish.Usage.TotalTokens is > 0 ? finish.Usage.TotalTokens.Value.ToString(CultureInfo.CurrentCulture) : null;
            string? price = null;
            // Read cost independently of the gateway's other, extensible fields. An invalid
            // cost must not remove an otherwise valid token badge or break the transcript.
            if (finish.ProviderMetadata is not null && finish.ProviderMetadata.TryGetValue("gateway", out var gateway)
                && gateway.ValueKind == JsonValueKind.Object && gateway.TryGetProperty("cost", out var cost)
                && cost.ValueKind == JsonValueKind.Number && cost.TryGetDouble(out var value))
                price = FormatCost(value);
            return (tokens, price);
        }
        catch (JsonException) { return (null, null); }
    }

    public static string? FormatCost(double cost)
    {
        if (!double.IsFinite(cost)) return null;
        // Match the browser's Math.round((value + Number.EPSILON) * 100) / 100.
        var scaled = (cost + 2.220446049250313e-16) * 100;
        var rounded = double.IsFinite(scaled) ? Math.Floor(scaled + .5) / 100 : cost;
        return rounded == 0 && cost > 0 ? "0.00>" : (rounded == 0 ? 0 : rounded).ToString("F2", CultureInfo.InvariantCulture);
    }
}
