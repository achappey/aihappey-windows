using System.Text.Json;
using System.Text.RegularExpressions;

namespace AIHappey.Desktop.Core;

/// <summary>Display the gateway's explicit error message, not debug events, headers or an arbitrary response dump.</summary>
public static class GatewayErrors
{
    public const int MaximumMessageCharacters = 16_384;
    public static string? Message(JsonElement error)
        => Message(error, 0);
    private static string? Message(JsonElement error, int depth)
    {
        if (depth > 5) return null;
        if (error.ValueKind == JsonValueKind.String) return Display(error.GetString());
        if (error.ValueKind != JsonValueKind.Object) return null;
        foreach (var key in new[] { "errorText", "errorMessage", "message", "detail", "description" })
            foreach (var property in error.EnumerateObject().Where(p => p.Name.Equals(key, StringComparison.OrdinalIgnoreCase)))
                if (property.Value.ValueKind == JsonValueKind.String && Display(property.Value.GetString()) is { } text) return text;
        foreach (var key in new[] { "error", "data", "response" })
            foreach (var property in error.EnumerateObject().Where(p => p.Name.Equals(key, StringComparison.OrdinalIgnoreCase)))
                if (Message(property.Value, depth + 1) is { } message) return message;
        return null;
    }
    public static string StreamMessage(JsonElement error) => Message(error) ??
        "The service returned an error event without an error message. Event fields: "
        + string.Join(", ", error.EnumerateObject().Select(p => p.Name + " (" + p.Value.ValueKind + ")")) + ".";
    public static string? Display(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        // Preserve normal provider error text verbatim. Only credential-shaped values are redacted.
        text = Regex.Replace(text, @"(?i)\b(Bearer\s+)[a-z0-9._~+/=-]+", "$1[redacted]");
        text = Regex.Replace(text, @"\bsk-[A-Za-z0-9_-]{12,}", "[redacted]");
        text = Regex.Replace(text, @"(?i)((?:api[-_ ]?key|authorization|access[-_ ]?token)\s*[:=]\s*[""']?)[^\s,""'}]+", "$1[redacted]");
        return text.Length <= MaximumMessageCharacters ? text : text[..MaximumMessageCharacters] + "…";
    }
    public static async Task CheckResponseAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        string? message = null;
        try
        {
            // Error envelopes only. Never display arbitrary HTML, stack traces, or the full HTTP body.
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            var buffer = new byte[64 * 1024 + 1]; var length = 0;
            while (length < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(length), ct); if (read == 0) break; length += read;
            }
            if (length < buffer.Length)
            {
                using var document = JsonDocument.Parse(buffer.AsMemory(0, length));
                message = Message(document.RootElement);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is JsonException or IOException or HttpRequestException) { }
        if (message is not null) throw new GatewayException($"HTTP {(int)response.StatusCode}: {message}");
        DesktopChatClient.CheckResponse(response);
    }
}
