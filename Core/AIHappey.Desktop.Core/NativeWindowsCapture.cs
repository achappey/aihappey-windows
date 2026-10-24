using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace AIHappey.Desktop.Core;

/// <summary>One WGC frame per invocation. No GDI fallback, files, global device, or retained capture item.</summary>
internal sealed class NativeWindowsCapture(DispatcherQueue dispatcher, Func<nint> owner, Func<bool> isCurrent)
{
    private void Check(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!isCurrent()) throw new OperationCanceledException(ct);
    }

    public async Task<LocalWindowCapture?> CaptureAsync(LocalWindowCaptureRequest request, CancellationToken ct)
    {
        Check(ct);
        if (!GraphicsCaptureSession.IsSupported()) throw Unavailable();
        GraphicsCaptureItem? item = null; var fallback = false;
        // Validate a window's identity before the fallback boundary. Never capture a reused HWND.
        if (request.Source == "window") NativeWindowsWindowInspector.RequireWindow(request.WindowHandle!, request.ProcessId!.Value);
        if (request.Source != "picker")
        {
            try
            {
                item = request.Source == "window"
                    ? CreateItem(LocalWindowTools.ParseHandle(request.WindowHandle!), false)
                    : CreateItem(NativeWindowsWindowInspector.RequireScreen(request.ScreenId!), true);
                Check(ct);
                return await FrameAsync(item, request.Source, false, ct);
            }
            catch (Exception error) when (error is COMException or UnauthorizedAccessException or LocalToolException)
            {
                Check(ct); fallback = true;
            }
        }
        item = await PickAsync(ct); Check(ct);
        return item is null ? null : await FrameAsync(item, "picker", fallback, ct);
    }

    private async Task<GraphicsCaptureItem?> PickAsync(CancellationToken ct)
    {
        var completion = new TaskCompletionSource<GraphicsCaptureItem?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = completion.Task.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        if (!dispatcher.TryEnqueue(async () =>
        {
            try
            {
                Check(ct);
                var picker = new GraphicsCapturePicker();
                WinRT.Interop.InitializeWithWindow.Initialize(picker, owner());
                var item = await picker.PickSingleItemAsync().AsTask(ct);
                Check(ct); completion.TrySetResult(item);
            }
            catch (OperationCanceledException) { completion.TrySetCanceled(ct); }
            catch (Exception error) { completion.TrySetException(error); }
        })) throw Unavailable();
        return await completion.Task.WaitAsync(ct);
    }

    private async Task<LocalWindowCapture> FrameAsync(GraphicsCaptureItem item, string source, bool fallback, CancellationToken ct)
    {
        Check(ct);
        var size = item.Size;
        ValidateSize(size.Width, size.Height);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var token = timeout.Token;
        using var device = CreateDevice();
        using var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 1, size);
        using var session = pool.CreateCaptureSession(item);
        // Keep the OS capture border. Do not request borderless capture permissions or suppress system indicators.
        var completion = new TaskCompletionSource<Direct3D11CaptureFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new object(); var accepting = true;
        void Arrived(Direct3D11CaptureFramePool sender, object args)
        {
            lock (gate)
            {
                if (!accepting) return;
                try
                {
                    var frame = sender.TryGetNextFrame();
                    if (frame is null) return;
                    if (!completion.TrySetResult(frame)) frame.Dispose();
                }
                catch (Exception error) { completion.TrySetException(error); }
            }
        }
        void Closed(GraphicsCaptureItem sender, object args) => completion.TrySetException(Unavailable());
        pool.FrameArrived += Arrived; item.Closed += Closed;
        Direct3D11CaptureFrame? acquired = null;
        try
        {
            Check(token); session.StartCapture();
            acquired = await completion.Task.WaitAsync(token);
            Check(token);
            var content = acquired.ContentSize; ValidateSize(content.Width, content.Height);
            // A resized source may no longer fit the initial pool. Do not return clipped/mislabelled pixels.
            if (content.Width > size.Width || content.Height > size.Height) throw Unavailable();
            using var bitmap = await SoftwareBitmap.CreateCopyFromSurfaceAsync(acquired.Surface).AsTask(token);
            using var stream = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream).AsTask(token);
            encoder.SetSoftwareBitmap(bitmap);
            encoder.BitmapTransform.Bounds = new BitmapBounds { Width = (uint)content.Width, Height = (uint)content.Height };
            await encoder.FlushAsync().AsTask(token); Check(token);
            if (stream.Size > LocalWindowTools.MaximumCaptureBytes) throw new LocalToolException(DesktopResources.Get("LocalCaptureTooLarge"));
            stream.Seek(0);
            using var reader = new DataReader(stream.GetInputStreamAt(0));
            var bytes = new byte[(int)stream.Size];
            await reader.LoadAsync((uint)bytes.Length).AsTask(token); reader.ReadBytes(bytes); Check(token);
            return new(source, item.DisplayName, content.Width, content.Height, bytes, fallback);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && timeout.IsCancellationRequested)
        { throw Unavailable(); }
        finally
        {
            lock (gate) accepting = false;
            pool.FrameArrived -= Arrived; item.Closed -= Closed;
            // If cancellation won the race before the awaiting scope acquired a frame, release that frame here.
            acquired?.Dispose();
            if (completion.Task.IsCompletedSuccessfully && !ReferenceEquals(acquired, completion.Task.Result)) completion.Task.Result.Dispose();
            else _ = completion.Task.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        }
    }

    private static void ValidateSize(int width, int height)
    {
        if (width < 1 || height < 1 || (long)width * height > LocalWindowTools.MaximumCapturePixels)
            throw new LocalToolException(DesktopResources.Get("LocalCaptureTooLarge"));
    }

    internal static IDirect3DDevice CreateDevice()
    {
        nint device = 0, context = 0, dxgi = 0, inspectable = 0;
        try
        {
            var hr = D3D11CreateDevice(0, 1 /* hardware */, 0, 0x20 /* BGRA */, 0, 0, 7, out device, out _, out context);
            if (hr < 0)
            {
                if (context != 0) { Marshal.Release(context); context = 0; }
                if (device != 0) { Marshal.Release(device); device = 0; }
                hr = D3D11CreateDevice(0, 5 /* WARP */, 0, 0x20, 0, 0, 7, out device, out _, out context);
            }
            Marshal.ThrowExceptionForHR(hr);
            var iid = new Guid("54EC77FA-1377-44E6-8C32-88FD5F44C84C");
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(device, in iid, out dxgi));
            Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi, out inspectable));
            return WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
        }
        finally
        {
            if (inspectable != 0) Marshal.Release(inspectable);
            if (dxgi != 0) Marshal.Release(dxgi);
            if (context != 0) Marshal.Release(context);
            if (device != 0) Marshal.Release(device);
        }
    }

    private static GraphicsCaptureItem CreateItem(nint target, bool monitor)
    {
        const string name = "Windows.Graphics.Capture.GraphicsCaptureItem";
        nint className = 0, factory = 0, result = 0;
        try
        {
            Marshal.ThrowExceptionForHR(WindowsCreateString(name, name.Length, out className));
            var iid = typeof(ICaptureItemInterop).GUID;
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(className, in iid, out factory));
            var interop = (ICaptureItemInterop)Marshal.GetObjectForIUnknown(factory);
            try
            {
                var itemId = new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760");
                Marshal.ThrowExceptionForHR(monitor ? interop.CreateForMonitor(target, in itemId, out result) : interop.CreateForWindow(target, in itemId, out result));
                return WinRT.MarshalInterface<GraphicsCaptureItem>.FromAbi(result);
            }
            finally { Marshal.ReleaseComObject(interop); }
        }
        finally
        {
            if (result != 0) Marshal.Release(result);
            if (factory != 0) Marshal.Release(factory);
            if (className != 0) WindowsDeleteString(className);
        }
    }

    private static LocalToolException Unavailable() => new(DesktopResources.Get("LocalCaptureUnavailable"));
    [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICaptureItemInterop
    {
        [PreserveSig] int CreateForWindow(nint window, in Guid iid, out nint item);
        [PreserveSig] int CreateForMonitor(nint monitor, in Guid iid, out nint item);
    }
    [DllImport("d3d11.dll", ExactSpelling = true)] private static extern int D3D11CreateDevice(nint adapter, int driverType, nint software, uint flags, nint levels, uint levelCount, uint sdkVersion, out nint device, out int featureLevel, out nint context);
    [DllImport("d3d11.dll", ExactSpelling = true)] private static extern int CreateDirect3D11DeviceFromDXGIDevice(nint dxgi, out nint device);
    [DllImport("combase.dll", ExactSpelling = true, CharSet = CharSet.Unicode)] private static extern int WindowsCreateString(string text, int length, out nint value);
    [DllImport("combase.dll", ExactSpelling = true)] private static extern int WindowsDeleteString(nint value);
    [DllImport("combase.dll", ExactSpelling = true)] private static extern int RoGetActivationFactory(nint name, in Guid iid, out nint factory);
}
