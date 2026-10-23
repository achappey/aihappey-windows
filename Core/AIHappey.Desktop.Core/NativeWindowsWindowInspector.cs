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

    internal static LocalWindowInfo RequireWindow(LocalWindowInspectionRequest request)
    {
        var handle = LocalWindowTools.ParseHandle(request.WindowHandle);
        if (!Visible(handle) || GetAncestor(handle, 2) != handle || Describe(handle) is not { } window || window.ProcessId != request.ProcessId)
            throw new LocalToolException(DesktopResources.Get("LocalWindowUnavailable"));
        return window;
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
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
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
