using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace AIHappey.Desktop.Core;

internal sealed class NativeWindowsWindowInspector(Func<bool>? isCurrent = null)
{
    private void Check(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (isCurrent?.Invoke() == false) throw new OperationCanceledException(ct);
    }
    public Task<LocalWindowInfo?> ActiveAsync(CancellationToken ct) => Task.Run(() =>
    {
        Check(ct);
        var handle = GetForegroundWindow();
        return handle == 0 ? null : Describe(handle);
    }, ct);

    public Task<LocalWindowList> ListAsync(int limit, CancellationToken ct) => Task.Run(() =>
    {
        var windows = new List<LocalWindowInfo>(); var more = false;
        // Never let managed exceptions unwind through the unmanaged EnumWindows callback.
        Check(ct);
        var enumerated = EnumWindows((handle, _) =>
        {
            if (ct.IsCancellationRequested || isCurrent?.Invoke() == false) return false;
            if (!Visible(handle)) return true;
            if (Describe(handle) is { } window)
            {
                if (windows.Count == limit) { more = true; return false; }
                windows.Add(window);
            }
            return true;
        }, 0);
        Check(ct);
        if (!enumerated && !more) throw new LocalToolException(DesktopResources.Get("LocalWindowUnavailable"));
        return new LocalWindowList(windows, more);
    }, ct);

    public Task<LocalWindowInspection> InspectAsync(LocalWindowInspectionRequest request, CancellationToken ct) => NativeWindowsAutomation.InspectAsync(request, ct, isCurrent);

    public Task<LocalWindowActionResult> ManageAsync(LocalWindowActionRequest request, CancellationToken ct) => Task.Run(async () =>
    {
        Check(ct); RequireWindow(request.WindowHandle, request.ProcessId);
        var handle = LocalWindowTools.ParseHandle(request.WindowHandle);
        if (request.Action is not ("activate" or "minimize" or "restore" or "reposition"))
            throw new LocalToolException(DesktopResources.Get("LocalWindowActionUnavailable"));
        if (!GetWindowRect(handle, out var original)) throw new LocalToolException(DesktopResources.Get("LocalWindowActionUnavailable"));
        var width = request.Width ?? ((long)original.Right - original.Left);
        var height = request.Height ?? ((long)original.Bottom - original.Top);
        if (request.Action == "reposition" && (request.Width.HasValue != request.Height.HasValue
            || width is < 1 or > 32768 || height is < 1 or > 32768
            || (long)request.X + width > int.MaxValue || (long)request.Y + height > int.MaxValue))
            throw new LocalToolException(DesktopResources.Get("LocalWindowBoundsInvalid"));
        Check(ct); RequireWindow(request.WindowHandle, request.ProcessId);
        // Asynchronous show/position calls do not block on a foreign window's message loop.
        // No AttachThreadInput, synthetic keys, retries, elevation, or foreground-lock bypass.
        var submitted = request.Action switch
        {
            "activate" => SetForegroundWindow(handle),
            "minimize" => ShowWindowAsync(handle, 6 /* SW_MINIMIZE */),
            "restore" => ShowWindowAsync(handle, 9 /* SW_RESTORE */),
            _ => SetWindowPos(handle, 0, request.X, request.Y, (int)width, (int)height,
                0x4000 /* ASYNCWINDOWPOS */ | 0x0010 /* NOACTIVATE */ | 0x0004 /* NOZORDER */ | 0x0200 /* NOOWNERZORDER */
                | (request.Width is null ? 0x0001u /* NOSIZE */ : 0))
        };
        var watch = Stopwatch.StartNew();
        while (true)
        {
            Check(ct); RequireWindow(request.WindowHandle, request.ProcessId);
            if (!GetWindowRect(handle, out var bounds)) throw new LocalToolException(DesktopResources.Get("LocalWindowActionUnavailable"));
            var foreground = GetForegroundWindow() == handle; var minimized = IsIconic(handle);
            var completed = request.Action switch
            {
                "activate" => foreground,
                "minimize" => minimized,
                "restore" => !minimized && !IsZoomed(handle),
                _ => bounds.Left == request.X && bounds.Top == request.Y
                    && (request.Width is null || (long)bounds.Right - bounds.Left == width && (long)bounds.Bottom - bounds.Top == height)
            };
            if (!submitted || completed || watch.ElapsedMilliseconds >= 1000)
                return new LocalWindowActionResult(submitted, completed, foreground, minimized,
                    bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top);
            await Task.Delay(25, ct);
        }
    }, ct);

    public Task<IReadOnlyList<LocalScreenInfo>> ScreensAsync(CancellationToken ct) => Task.Run<IReadOnlyList<LocalScreenInfo>>(() =>
    {
        Check(ct);
        var screens = EnumerateScreens().Select(s => s.Info).ToArray();
        Check(ct); return screens;
    }, ct);

    internal static LocalWindowInfo RequireWindow(LocalWindowInspectionRequest request) => RequireWindow(request.WindowHandle, request.ProcessId);

    internal static LocalWindowInfo RequireWindow(string windowHandle, int processId)
    {
        var handle = LocalWindowTools.ParseHandle(windowHandle);
        if (!Visible(handle) || GetAncestor(handle, 2) != handle || Describe(handle) is not { } window || window.ProcessId != processId)
            throw new LocalToolException(DesktopResources.Get("LocalWindowUnavailable"));
        return window;
    }

    internal static nint RequireScreen(string screenId) => EnumerateScreens().FirstOrDefault(s => s.Info.ScreenId == screenId).Handle is var handle && handle != 0
        ? handle : throw new LocalToolException(DesktopResources.Get("LocalCaptureUnavailable"));

    private static List<(nint Handle, LocalScreenInfo Info)> EnumerateScreens()
    {
        var screens = new List<(nint, LocalScreenInfo)>();
        if (!EnumDisplayMonitors(0, 0, (nint monitor, nint _, ref Rect bounds, nint state) =>
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>(), Device = "" };
            if (GetMonitorInfo(monitor, ref info)) screens.Add((monitor, new(info.Device, info.Monitor.Left, info.Monitor.Top,
                info.Monitor.Right - info.Monitor.Left, info.Monitor.Bottom - info.Monitor.Top, (info.Flags & 1) != 0)));
            return screens.Count < 32;
        }, 0) && screens.Count == 0) throw new LocalToolException(DesktopResources.Get("LocalCaptureUnavailable"));
        return screens;
    }

    private static bool Visible(nint handle) => IsWindow(handle) && IsWindowVisible(handle)
        && (DwmGetWindowAttribute(handle, 14 /* DWMWA_CLOAKED */, out var cloaked, sizeof(int)) != 0 || cloaked == 0);

    private static LocalWindowInfo? Describe(nint handle)
    {
        if (!IsWindow(handle)) return null;
        GetWindowThreadProcessId(handle, out var pid);
        if (pid == 0 || pid > int.MaxValue) return null;
        // GetWindowText does not send WM_GETTEXT to a different process. No control-text messages are sent here.
        var title = new StringBuilder(513); GetWindowText(handle, title, title.Capacity);
        var className = new StringBuilder(257); GetClassName(handle, className, className.Capacity);
        string? processName = null;
        try { using var process = Process.GetProcessById((int)pid); processName = process.ProcessName; }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
        GetWindowThreadProcessId(handle, out var currentPid);
        if (currentPid != pid || !IsWindow(handle)) return null;
        return new("0x" + ((ulong)handle).ToString("X", CultureInfo.InvariantCulture), (int)pid, processName,
            title.ToString(), className.ToString(), IsIconic(handle));
    }

    private delegate bool EnumWindowCallback(nint handle, nint state);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public int Size; public Rect Monitor, Work; public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }
    private delegate bool MonitorCallback(nint monitor, nint dc, ref Rect bounds, nint state);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumDisplayMonitors(nint dc, nint clip, MonitorCallback callback, nint state);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(nint handle);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShowWindowAsync(nint handle, int command);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(nint handle, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(nint handle, out Rect bounds);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsZoomed(nint handle);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumWindows(EnumWindowCallback callback, nint state);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(nint handle);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint handle);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(nint handle);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint handle, uint flags);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint handle, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint handle, StringBuilder text, int maximum);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint handle, StringBuilder text, int maximum);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint handle, uint attribute, out int value, int size);
}
