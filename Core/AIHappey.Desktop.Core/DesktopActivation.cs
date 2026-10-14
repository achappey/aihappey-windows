using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace AIHappey.Desktop.Core;

/// <summary>Shared by both hosts and distributions; registers before WinUI or child services start.</summary>
public static class DesktopActivation
{
    private static readonly object gate = new();
    private static readonly Queue<string> pending = new();
    private static AppInstance? primary;
    private static FileStream? profileLock;
    private static DispatcherQueue? dispatcher;
    private static Window? window;
    private static ChatShell? shell;
    private static string host = "";
    private static bool stopped;

    public static int Run(string profile, Func<Application> createApp)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        host = profile;
        try
        {
            var arguments = AppInstance.GetCurrent().GetActivatedEventArgs();
            var key = DesktopPaths.InstanceKey(profile, DesktopPaths.PackageFamily,
                WindowsIdentity.GetCurrent().User!.Value, Process.GetCurrentProcess().SessionId);
            for (var attempt = 0; attempt < 3; attempt++)
            {
                primary = AppInstance.FindOrRegisterForKey(key);
                if (primary.IsCurrent) break;
                try { Redirect(primary, arguments); return 0; }
                catch when (attempt < 2 && !IsAlive(primary.ProcessId)) { Thread.Sleep(100); }
            }
            primary!.Activated += OnActivated;
            // AppInstance registration may be executable-scoped for unpackaged copies. Protect the shared profile too.
            var data = DesktopPaths.ProfileDirectory(profile); Directory.CreateDirectory(data);
            profileLock = new FileStream(Path.Combine(data, ".instance.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            Queue(arguments);
            Application.Start(parameters =>
            {
                dispatcher = DispatcherQueue.GetForCurrentThread();
                SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(dispatcher));
                _ = createApp();
            });
            return 0;
        }
        catch (Exception error)
        {
            Trace.WriteLine($"Desktop activation failed: {error}");
            MessageBox(IntPtr.Zero, "The application could not be activated. Close other copies and try again. No files have been sent.", DesktopBranding.AppName, 0x10);
            return 1;
        }
        finally { Stop(); profileLock?.Dispose(); }
    }
    private static bool IsAlive(uint pid)
    { try { return !Process.GetProcessById((int)pid).HasExited; } catch (ArgumentException) { return false; } }
    private static void Redirect(AppInstance target, AppActivationArguments arguments)
    {
        AllowSetForegroundWindow(target.ProcessId);
        using var signal = new ManualResetEvent(false);
        Exception? failure = null;
        _ = Task.Run(async () =>
        {
            try { await target.RedirectActivationToAsync(arguments); }
            catch (Exception error) { failure = error; }
            finally { try { signal.Set(); } catch (ObjectDisposedException) { } }
        });
        // Pump COM while waiting on an STA; never synchronously wait on a WinRT async call here.
        var result = CoWaitForMultipleObjects(0, 15000, 1, [signal.SafeWaitHandle.DangerousGetHandle()], out _);
        if (result != 0) Marshal.ThrowExceptionForHR(result);
        if (failure is not null) throw new InvalidOperationException("Activation redirection failed.", failure);
    }
    private static void OnActivated(object? sender, AppActivationArguments arguments) => Queue(arguments);
    private static void Queue(AppActivationArguments arguments)
    {
        var text = arguments.Kind == ExtendedActivationKind.Launch
            && arguments.Data is Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs launch ? launch.Arguments : "";
        lock (gate)
        {
            if (stopped) return;
            if (pending.Count >= 32) { MessageBox(IntPtr.Zero, "Too many pending activations. Try again after the application is ready.", DesktopBranding.AppName, 0x30); return; }
            pending.Enqueue(text);
        }
        dispatcher?.TryEnqueue(Drain);
    }
    public static void Bind(Window target, ChatShell composer)
    {
        window = target; shell = composer;
        target.Closed += (_, _) => Stop();
        if (DesktopPaths.PackageFamily is { } family)
        {
            try
            {
                var store = new ExplorerRequestStore(DesktopPaths.PackageState, host, family);
                store.Cleanup(DateTimeOffset.UtcNow);
                if (store.RecoverInterrupted()) composer.ExplorerDeliveryFailed();
            }
            catch { composer.ExplorerDeliveryFailed(); }
        }
        Drain();
    }
    private static void Drain()
    {
        if (window is null || shell is null || stopped) return;
        while (true)
        {
            string arguments;
            lock (gate) { if (!pending.TryDequeue(out arguments!)) return; }
            Restore(window);
            _ = DeliverAsync(arguments);
        }
    }
    private static async Task DeliverAsync(string arguments)
    {
        ExplorerRequestStore? store = null; string? id = null;
        try
        {
            id = ExplorerRequest.ParseActivation(arguments);
            if (id is null) return;
            store = new ExplorerRequestStore(DesktopPaths.PackageState, host, DesktopPaths.PackageFamily!);
            store.Cleanup(DateTimeOffset.UtcNow);
            var request = store.Claim(id, DateTimeOffset.UtcNow);
            if (request is null) return;
            var outcome = await shell!.EnqueueExplorerFilesAsync(request.Paths);
            store.Complete(id, outcome);
        }
        catch (Exception error)
        {
            Trace.WriteLine($"Explorer delivery failed: {error.GetType().Name}");
            shell?.ExplorerDeliveryFailed();
        }
    }
    public static void Stop()
    {
        lock (gate) { stopped = true; pending.Clear(); }
        if (primary is { IsCurrent: true }) { primary.Activated -= OnActivated; primary.UnregisterKey(); }
    }
    private static void Restore(Window target)
    {
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(target);
        if (IsIconic(handle)) ShowWindow(handle, 9);
        target.Activate();
        if (!SetForegroundWindow(handle))
        {
            var flash = new FlashInfo { Size = (uint)Marshal.SizeOf<FlashInfo>(), Window = handle, Flags = 3, Count = 3 };
            FlashWindowEx(ref flash);
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct FlashInfo { public uint Size; public IntPtr Window; public uint Flags, Count, Timeout; }
    [DllImport("ole32.dll")] private static extern int CoWaitForMultipleObjects(uint flags, uint timeout, uint count, IntPtr[] handles, out uint index);
    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(uint pid);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr handle);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr handle);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr handle, int command);
    [DllImport("user32.dll")] private static extern bool FlashWindowEx(ref FlashInfo info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int MessageBox(IntPtr handle, string text, string caption, uint type);
}
