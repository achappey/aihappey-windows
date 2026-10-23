using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AIHappey.Desktop.Core;

/// <summary>Native UI Automation COM, not WPF. All COM references belong to the MTA worker.</summary>
internal static class NativeWindowsAutomation
{
    // A provider may ignore cancellation. Keep its permit until the native work really ends,
    // so repeated calls cannot accumulate an unbounded number of blocked workers.
    private static readonly SemaphoreSlim workerGate = new(1, 1);

    internal static async Task<LocalWindowInspection> InspectAsync(LocalWindowInspectionRequest request, CancellationToken ct, Func<bool>? isCurrent = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var token = timeout.Token;
        try
        {
            await workerGate.WaitAsync(token);
            var work = Task.Run(() =>
            {
                try { return Inspect(request, token, isCurrent); }
                finally { workerGate.Release(); }
            });
            _ = work.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            return await work.WaitAsync(token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && timeout.IsCancellationRequested)
        { throw new LocalToolException(DesktopResources.Get("LocalWindowTimeout")); }
        catch (Exception error) when (error is COMException or UnauthorizedAccessException or InvalidComObjectException)
        { throw new LocalToolException(DesktopResources.Get("LocalWindowInspectionUnavailable")); }
    }

    private static LocalWindowInspection Inspect(LocalWindowInspectionRequest request, CancellationToken ct, Func<bool>? isCurrent)
    {
        void Check()
        {
            ct.ThrowIfCancellationRequested();
            if (isCurrent?.Invoke() == false) throw new OperationCanceledException(ct);
        }
        Check();
        var window = NativeWindowsWindowInspector.RequireWindow(request);
        IAutomation? automation = null; IElement? root = null; ITreeWalker? walker = null;
        try
        {
            automation = (IAutomation)Activator.CreateInstance(Type.GetTypeFromCLSID(new("FF48DBA4-60EF-4201-AA87-54103EEF594E"), true)!)!;
            root = automation.ElementFromHandle(LocalWindowTools.ParseHandle(request.WindowHandle));
            walker = automation.ControlViewWalker();
            var nodes = new List<LocalWindowNode>(); var partial = false; var truncated = false; var characters = 0;
            var watch = Stopwatch.StartNew();

            bool AtLimit()
            {
                Check();
                if (nodes.Count < request.MaxNodes && characters < LocalWindowTools.MaximumTextCharacters && watch.Elapsed < TimeSpan.FromSeconds(4)) return false;
                truncated = true; return true;
            }
            string? Text(IElement element, int property, int maximum = 512)
            {
                Check();
                if (element.GetCurrentPropertyValue(property) is not string text) return null;
                var length = Math.Min(text.Length, Math.Min(maximum, LocalWindowTools.MaximumTextCharacters - characters));
                characters += length;
                if (length < text.Length) truncated = true;
                return text[..length];
            }
            void Visit(IElement element, int? parent, int depth)
            {
                if (AtLimit()) return;
                var id = nodes.Count;
                try
                {
                    // Fail closed: do not fetch any text or traverse if password status cannot be established.
                    Check();
                    if (element.GetCurrentPropertyValue(30019 /* IsPassword */) is not bool password)
                    { nodes.Add(new(id, parent, depth, false, false)); partial = true; return; }
                    if (password) { nodes.Add(new(id, parent, depth, true, true)); return; }
                    Check();
                    var type = element.GetCurrentPropertyValue(30003 /* ControlType */) as int?;
                    var node = new LocalWindowNode(id, parent, depth, true, false, type,
                        Text(element, 30005 /* Name */), Text(element, 30011 /* AutomationId */), Text(element, 30012 /* ClassName */),
                        element.GetCurrentPropertyValue(30010 /* IsEnabled */) as bool?,
                        element.GetCurrentPropertyValue(30022 /* IsOffscreen */) as bool?,
                        type == 50004 /* Edit */ ? Text(element, 30045 /* Value.Value */, 2048) : null);
                    nodes.Add(node);
                }
                catch (COMException) { nodes.Add(new(id, parent, depth, false, false)); partial = true; return; }

                if (AtLimit()) return;
                IElement? child = null;
                try
                {
                    child = walker.GetFirstChildElement(element);
                    if (child is not null && depth >= request.MaxDepth) { truncated = true; return; }
                    while (child is not null)
                    {
                        if (AtLimit()) break;
                        Visit(child, id, depth + 1);
                        if (AtLimit()) break;
                        var next = walker.GetNextSiblingElement(child);
                        Release(child); child = next;
                    }
                }
                catch (COMException) { partial = true; }
                finally { Release(child); }
            }
            Visit(root, null, 0);
            Check();
            _ = NativeWindowsWindowInspector.RequireWindow(request);
            return new(window, nodes, truncated, partial);
        }
        finally { Release(walker); Release(root); Release(automation); }
    }

    private static void Release(object? value) { if (value is not null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); }

    // UIAutomationClient.h IUnknown vtable order. Gaps deliberately expose no action methods.
    // https://github.com/microsoft/win32metadata/blob/main/generation/WinSDK/RecompiledIdlHeaders/um/UIAutomationClient.h
    [ComImport, Guid("30CBE57D-D9D0-452A-AB13-7AC5AC4825EE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAutomation
    {
        void _VtblGap1_3(); // CompareElements, CompareRuntimeIds, GetRootElement
        IElement ElementFromHandle(nint handle);
        void _VtblGap2_7(); // ElementFromPoint through CreateTreeWalker
        ITreeWalker ControlViewWalker();
    }

    [ComImport, Guid("D22108AA-8AC5-49A5-837B-37BBB3D7591E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IElement
    {
        void _VtblGap1_7(); // SetFocus through BuildUpdatedCache; none can be called here
        [return: MarshalAs(UnmanagedType.Struct)] object GetCurrentPropertyValue(int propertyId);
    }

    [ComImport, Guid("4042C624-389C-4AFC-A630-9DF854A541FC"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITreeWalker
    {
        void _VtblGap1_1(); // GetParentElement
        IElement? GetFirstChildElement(IElement element);
        void _VtblGap2_1(); // GetLastChildElement
        IElement? GetNextSiblingElement(IElement element);
    }
}
