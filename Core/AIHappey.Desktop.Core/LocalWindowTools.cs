using System.Globalization;
using System.Text.Json;

namespace AIHappey.Desktop.Core;

public sealed record LocalWindowInfo(string WindowHandle, int ProcessId, string? ProcessName, string Title, string ClassName, bool Minimized);
public sealed record LocalWindowList(IReadOnlyList<LocalWindowInfo> Windows, bool HasMore);
public sealed record LocalWindowInspectionRequest(string WindowHandle, int ProcessId, int MaxDepth, int MaxNodes);
public sealed record LocalWindowNode(int Id, int? ParentId, int Depth, bool Accessible, bool IsPassword,
    int? ControlType = null, string? Name = null, string? AutomationId = null, string? ClassName = null,
    bool? Enabled = null, bool? Offscreen = null, string? Value = null);
public sealed record LocalWindowInspection(LocalWindowInfo Window, IReadOnlyList<LocalWindowNode> Nodes, bool Truncated, bool Partial);

/// <summary>Only metadata and read-only control inspection; no automation actions are exposed.</summary>
public sealed class LocalWindowTools(
    Func<CancellationToken, Task<LocalWindowInfo?>> active,
    Func<int, CancellationToken, Task<LocalWindowList>> list,
    Func<LocalWindowInspectionRequest, CancellationToken, Task<LocalWindowInspection>> inspect,
    Func<bool>? isCurrent = null)
{
    public const string ActiveTool = "local_windows_get_active_window";
    public const string ListTool = "local_windows_list_windows";
    public const string InspectTool = "local_windows_inspect_window";
    public const int MaximumNodes = 500;
    public const int MaximumTextCharacters = 50_000;

    private void Check(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (isCurrent?.Invoke() == false) throw new OperationCanceledException(ct);
    }

    public static nint ParseHandle(string handle)
    {
        if (handle.Length is < 3 or > 18 || !handle.StartsWith("0x", StringComparison.Ordinal)
            || !ulong.TryParse(handle.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var number)
            || number == 0 || number > (Environment.Is64BitProcess ? (ulong)long.MaxValue : int.MaxValue))
            throw new LocalToolException(DesktopResources.Get("LocalWindowInvalid"));
        return (nint)number;
    }

    public Task<JsonElement> CallAsync(string name, JsonElement input, CancellationToken ct) => DesktopLocalTools.SafeAsync(async () =>
    {
        Check(ct);
        switch (name)
        {
            case ActiveTool:
                var window = await active(ct); Check(ct);
                return DesktopLocalTools.Result(new { window });
            case ListTool:
                var limit = DesktopLocalTools.Integer(input, "limit", 50, 100);
                var windows = await list(limit, ct); Check(ct);
                return DesktopLocalTools.Result(new { windows = windows.Windows.Take(limit).ToArray(), count = Math.Min(limit, windows.Windows.Count),
                    hasMore = windows.HasMore || windows.Windows.Count > limit });
            case InspectTool:
                var handle = DesktopLocalTools.Required(input, "windowHandle");
                _ = ParseHandle(handle);
                if (!input.TryGetProperty("processId", out var pid) || pid.ValueKind != JsonValueKind.Number || !pid.TryGetInt32(out var processId) || processId < 1)
                    throw new LocalToolException(DesktopResources.Get("LocalWindowInvalid"));
                var request = new LocalWindowInspectionRequest(handle, processId,
                    DesktopLocalTools.Integer(input, "maxDepth", 3, 8), DesktopLocalTools.Integer(input, "maxNodes", 200, MaximumNodes));
                var result = await inspect(request, ct); Check(ct);
                return DesktopLocalTools.Result(result);
            default: throw new LocalToolException("Unsupported window tool.");
        }
    }, ct);
}
