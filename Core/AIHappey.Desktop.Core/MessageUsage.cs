using System.Globalization;
using System.Text.Json;
using AIHappey.Vercel.Models;

namespace AIHappey.Desktop.Core;

/// <summary>Optional footer metadata, shared by live and restored assistant messages.</summary>
internal static class MessageUsage
{
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
