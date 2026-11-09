using System.Text.Json;
using System.Text.Json.Nodes;

namespace AIHappey.Desktop.Core;

/// <summary>Native shell adapter. Only the envelope changes; execution belongs to PowerShellExecutor.</summary>
public sealed class NativeShellTools(PowerShellExecutor executor)
{
    public const string ToolName = "shell_call";
    public static bool Enabled(ChatPreferences preferences, string? provider) => provider is not null && preferences.ProviderMetadata.TryGetValue(provider, out var config)
        && OpenAIChatConfig.Resolve(config)["shell"]?["environment"]?["type"]?.ToString() == "local";

    public static void Register(McpTurnSnapshot snapshot, ChatPreferences preferences, string? provider, string? endpoint, Func<bool> current)
    {
        var executor = new PowerShellExecutor(current);
        if ((endpoint is null or "/v1/responses") && Enabled(preferences, provider))
            snapshot.AddNative(ToolName, new NativeShellTools(executor).CallAsync);
    }

    public Task<JsonElement> CallAsync(JsonElement input, CancellationToken ct) => DesktopLocalTools.SafeAsync(async () =>
    {
        if (!input.TryGetProperty("commands", out var commands) || commands.ValueKind != JsonValueKind.Array
            || commands.EnumerateArray().Any(c => c.ValueKind != JsonValueKind.String)) throw new LocalToolException("Missing shell commands.");
        var maximum = Limit(input, "max_output_length");
        var results = await executor.ExecuteAsync(commands.EnumerateArray().Select(c => c.GetString()!).ToArray(),
            Limit(input, "timeout_ms"), maximum, null, ct);
        var result = new JsonObject { ["output"] = new JsonArray(results.Select(r => (JsonNode)new JsonObject
        {
            ["stdout"] = r.Stdout, ["stderr"] = r.Stderr,
            ["outcome"] = r.TimedOut ? new JsonObject { ["type"] = "timeout" }
                : new JsonObject { ["type"] = "exit", ["exit_code"] = r.ExitCode }
        }).ToArray()) };
        if (maximum.HasValue) result["max_output_length"] = maximum.Value;
        return DesktopLocalTools.Result(result);
    }, ct);

    internal static int? Limit(JsonElement input, string field)
    {
        if (!input.TryGetProperty(field, out var value)) return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number) || number <= 0) throw new LocalToolException(field + " must be a positive integer.");
        return number;
    }

    // Native error/denied results must still be shell_call_output, without releasing withheld stdout/stderr.
    internal static JsonObject ReplayOutput(JsonObject part, string callId, string? provider)
    {
        var output = new JsonObject { ["type"] = "shell_call_output", ["call_id"] = callId };
        if (!NativeChatTools.Failed(part) && part["output"]?["structuredContent"] is JsonObject value && value["output"] is JsonArray chunks)
        {
            output["output"] = chunks.DeepClone();
            if (value["max_output_length"] is { } maximum) output["max_output_length"] = maximum.DeepClone();
        }
        else output["output"] = new JsonArray(new JsonObject { ["stdout"] = "", ["stderr"] = NativeChatTools.Output(part),
            ["outcome"] = new JsonObject { ["type"] = "exit", ["exit_code"] = 1 } });
        if (output["max_output_length"] is null && NativeChatTools.Call(part, provider)?["action"]?["max_output_length"] is { } limit)
            output["max_output_length"] = limit.DeepClone();
        return output;
    }
}
