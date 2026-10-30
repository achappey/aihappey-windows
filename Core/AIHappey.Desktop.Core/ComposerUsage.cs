using System.Globalization;
using System.Text.Json;
using AIHappey.Vercel.Models;

namespace AIHappey.Desktop.Core;

/// <summary>Composer totals use the same metadata boundaries as the browser, not transcript projections.</summary>
internal readonly record struct ComposerUsage(double? Tokens, double? Cost)
{
    public static ComposerUsage Read(IEnumerable<ConversationMessage> messages)
    {
        double? tokens = null;
        double cost = 0;
        var costEntries = 0;
        foreach (var row in messages)
        {
            var message = row.Message;
            if (message.Role != Role.assistant) continue;
            var metadata = message.Metadata;
            if (Number(MessageUsage.Property(MessageUsage.Property(metadata, "usage"), "totalTokens")
                ?? MessageUsage.Property(metadata, "totalTokens")) is >= 0 and var latest) tokens = latest;
            AddCost(MessageUsage.Property(MessageUsage.Property(MessageUsage.Property(metadata, "providerMetadata"), "gateway"), "cost")
                ?? MessageUsage.Property(MessageUsage.Property(metadata, "gateway"), "cost")
                ?? MessageUsage.Property(metadata, "cost"));
            foreach (var part in message.Parts)
                AddCost(MessageUsage.Property(MessageUsage.Property(MessageUsage.Property(PortableConversations.Element(part), "output"), "_meta"), "gateway") is { } gateway
                    ? MessageUsage.Property(gateway, "cost") : null);
        }
        return new(tokens, costEntries > 1 && double.IsFinite(cost) ? cost : null);

        void AddCost(object? value)
        {
            if (Number(value, allowString: true) is not { } parsed) return;
            cost += parsed; costEntries++;
        }
    }

    private static double? Number(object? raw, bool allowString = false)
    {
        if (raw is null) return null;
        try
        {
            var json = raw is JsonElement element ? element : JsonSerializer.SerializeToElement(raw);
            if (json.ValueKind == JsonValueKind.Number && json.TryGetDouble(out var number) && double.IsFinite(number)) return number;
            if (allowString && json.ValueKind == JsonValueKind.String && double.TryParse(json.GetString(), NumberStyles.Float,
                CultureInfo.InvariantCulture, out number) && double.IsFinite(number)) return number;
        }
        catch (Exception error) when (error is JsonException or ArgumentException or NotSupportedException) { }
        return null;
    }
}
