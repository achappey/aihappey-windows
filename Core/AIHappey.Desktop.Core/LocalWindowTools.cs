using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AIHappey.Desktop.Core;

public sealed record LocalWindowInfo(string WindowHandle, int ProcessId, string? ProcessName, string Title, string ClassName, bool Minimized);
public sealed record LocalWindowList(IReadOnlyList<LocalWindowInfo> Windows, bool HasMore);
public sealed record LocalWindowInspectionRequest(string WindowHandle, int ProcessId, int MaxDepth, int MaxNodes);
public sealed record LocalWindowNode(int Id, int? ParentId, int Depth, bool Accessible, bool IsPassword,
    int? ControlType = null, string? Name = null, string? AutomationId = null, string? ClassName = null,
    bool? Enabled = null, bool? Offscreen = null, string? Value = null);
public sealed record LocalWindowInspection(LocalWindowInfo Window, IReadOnlyList<LocalWindowNode> Nodes, bool Truncated, bool Partial);
public sealed record LocalScreenInfo(string ScreenId, int X, int Y, int Width, int Height, bool Primary);
public sealed record LocalWindowCaptureRequest(string Source, string? WindowHandle = null, int? ProcessId = null, string? ScreenId = null);
public sealed record LocalWindowCapture(string Source, string Name, int Width, int Height, byte[] Png, bool PickerFallback);
public sealed record LocalWindowActionRequest(string Action, string WindowHandle, int ProcessId,
    int X = 0, int Y = 0, int? Width = null, int? Height = null);
public sealed record LocalWindowActionResult(bool Submitted, bool Completed, bool Foreground, bool Minimized,
    int X, int Y, int Width, int Height);

/// <summary>Bounded window inspection, capture and explicit Win32 window management. No input simulation.</summary>
public sealed class LocalWindowTools(
    Func<CancellationToken, Task<LocalWindowInfo?>> active,
    Func<int, CancellationToken, Task<LocalWindowList>> list,
    Func<LocalWindowInspectionRequest, CancellationToken, Task<LocalWindowInspection>> inspect,
    Func<bool>? isCurrent = null,
    Func<CancellationToken, Task<IReadOnlyList<LocalScreenInfo>>>? screens = null,
    Func<LocalWindowCaptureRequest, CancellationToken, Task<LocalWindowCapture?>>? capture = null,
    Func<LocalWindowActionRequest, CancellationToken, Task<LocalWindowActionResult>>? manage = null)
{
    public const string ActiveTool = "local_windows_get_active_window";
    public const string ListTool = "local_windows_list_windows";
    public const string InspectTool = "local_windows_inspect_window";
    public const string ScreensTool = "local_windows_list_screens";
    public const string CaptureTool = "local_windows_capture";
    public const string ActivateTool = "local_windows_activate_window";
    public const string MinimizeTool = "local_windows_minimize_window";
    public const string RestoreTool = "local_windows_restore_window";
    public const string RepositionTool = "local_windows_reposition_window";
    public const int MaximumNodes = 500;
    public const int MaximumTextCharacters = 50_000;
    public const int MaximumCapturePixels = 16_777_216;
    public const int MaximumCaptureBytes = 20 * 1024 * 1024;

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
            case ScreensTool:
                if (screens is null) throw new LocalToolException(DesktopResources.Get("LocalCaptureUnavailable"));
                var displays = await screens(ct); Check(ct);
                // New outputs use content blocks only: structuredContent is not covered by the request's Text filter.
                return DesktopLocalTools.Result(null, DesktopLocalTools.Text(JsonSerializer.Serialize(new { screens = displays.Take(32).ToArray(), truncated = displays.Count > 32 }, JsonSerializerOptions.Web)));
            case CaptureTool:
                var target = CaptureRequest(input);
                if (capture is null) throw new LocalToolException(DesktopResources.Get("LocalCaptureUnavailable"));
                var image = await capture(target, ct); Check(ct);
                if (image is null) return DesktopLocalTools.Result(null, DesktopLocalTools.Text("{\"canceled\":true}"));
                if (image.Width < 1 || image.Height < 1 || (long)image.Width * image.Height > MaximumCapturePixels
                    || image.Png.Length is < 8 or > MaximumCaptureBytes || !image.Png.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
                    throw new LocalToolException(DesktopResources.Get("LocalCaptureTooLarge"));
                return DesktopLocalTools.Result(null,
                    DesktopLocalTools.Text(JsonSerializer.Serialize(new { image.Source, image.Name, image.Width, image.Height, image.PickerFallback, mimeType = "image/png" }, JsonSerializerOptions.Web)),
                    new JsonObject { ["type"] = "image", ["mimeType"] = "image/png", ["data"] = Convert.ToBase64String(image.Png) });
            case ActivateTool:
            case MinimizeTool:
            case RestoreTool:
            case RepositionTool:
                var action = ActionRequest(name, input);
                if (manage is null) throw new LocalToolException(DesktopResources.Get("LocalWindowActionUnavailable"));
                Check(ct); var outcome = await manage(action, ct); Check(ct);
                return DesktopLocalTools.Result(null, DesktopLocalTools.Text(JsonSerializer.Serialize(outcome, JsonSerializerOptions.Web)));
            default: throw new LocalToolException("Unsupported window tool.");
        }
    }, ct);

    private static LocalWindowActionRequest ActionRequest(string name, JsonElement input)
    {
        var handle = DesktopLocalTools.Required(input, "windowHandle"); _ = ParseHandle(handle);
        if (!input.TryGetProperty("processId", out var pid) || pid.ValueKind != JsonValueKind.Number || !pid.TryGetInt32(out var processId) || processId < 1)
            throw new LocalToolException(DesktopResources.Get("LocalWindowInvalid"));
        var action = name switch { ActivateTool => "activate", MinimizeTool => "minimize", RestoreTool => "restore", _ => "reposition" };
        if (action != "reposition") return new(action, handle, processId);
        int Coordinate(string field)
        {
            if (!input.TryGetProperty(field, out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number))
                throw new LocalToolException(DesktopResources.Get("LocalWindowBoundsInvalid"));
            return number;
        }
        var x = Coordinate("x"); var y = Coordinate("y");
        int? width = null, height = null;
        if (input.TryGetProperty("width", out _) || input.TryGetProperty("height", out _))
        {
            width = Coordinate("width"); height = Coordinate("height");
            if (width is < 1 or > 32768 || height is < 1 or > 32768
                || (long)x + width > int.MaxValue || (long)y + height > int.MaxValue)
                throw new LocalToolException(DesktopResources.Get("LocalWindowBoundsInvalid"));
        }
        return new(action, handle, processId, x, y, width, height);
    }

    private static LocalWindowCaptureRequest CaptureRequest(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object) throw new LocalToolException(DesktopResources.Get("LocalCaptureInvalidSource"));
        var source = "picker";
        if (input.TryGetProperty("source", out var value))
        {
            if (value.ValueKind != JsonValueKind.String) throw new LocalToolException(DesktopResources.Get("LocalCaptureInvalidSource"));
            source = value.GetString()!;
        }
        // Models may populate every optional schema field. Source is authoritative: never validate
        // or forward unused selectors (especially stale HWNDs when the user will choose in the picker).
        if (source == "picker") return new(source);
        if (source == "window")
        {
            var handle = DesktopLocalTools.Required(input, "windowHandle"); _ = ParseHandle(handle);
            if (!input.TryGetProperty("processId", out var pid)
                || pid.ValueKind != JsonValueKind.Number || !pid.TryGetInt32(out var processId) || processId < 1) throw new LocalToolException(DesktopResources.Get("LocalWindowInvalid"));
            return new(source, handle, processId);
        }
        if (source == "screen")
        {
            if (!input.TryGetProperty("screenId", out var screenValue) || screenValue.ValueKind != JsonValueKind.String
                || screenValue.GetString() is not { Length: > 0 and <= 128 } screen || string.IsNullOrWhiteSpace(screen) || screen.Any(char.IsControl))
                throw new LocalToolException(DesktopResources.Get("LocalCaptureInvalidScreen"));
            return new(source, ScreenId: screen);
        }
        throw new LocalToolException(DesktopResources.Get("LocalCaptureInvalidSource"));
    }
}
