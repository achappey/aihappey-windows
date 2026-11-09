using System.Text.Json;

namespace AIHappey.Desktop.Core;

public sealed class LocalPowerShellTools(PowerShellExecutor executor)
{
    public const string ToolName = "local_powershell_execute";
    public Task<JsonElement> CallAsync(string name, JsonElement input, CancellationToken ct) => DesktopLocalTools.SafeAsync(async () =>
    {
        if (name != ToolName) throw new LocalToolException("Unknown PowerShell tool.");
        var results = await executor.ExecuteAsync(new[] { DesktopLocalTools.Required(input, "script") },
            NativeShellTools.Limit(input, "timeoutMs"), NativeShellTools.Limit(input, "maxOutputLength"),
            input.TryGetProperty("workingDirectory", out _) ? DesktopLocalTools.Required(input, "workingDirectory") : null, ct);
        return DesktopLocalTools.Result(results[0]);
    }, ct);
}
