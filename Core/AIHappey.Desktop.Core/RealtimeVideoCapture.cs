using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Storage.Streams;
using Windows.Foundation.Collections;

namespace AIHappey.Desktop.Core;

/// <summary>Explicitly selected, session-owned visual input. Never stores frames on disk.</summary>
internal sealed class RealtimeVideoCapture : IAsyncDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private MediaCapture? camera;
    private MediaFrameReader? reader;
    private GraphicsCaptureItem? item;
    private IDirect3DDevice? device;
    private Direct3D11CaptureFramePool? pool;
    private GraphicsCaptureSession? capture;
    private Task? pump;
    private int disposed;
    public async Task<bool> StartAsync(bool screen, nint owner, RealtimePreferences settings, Func<byte[], CancellationToken, Task> frame, Action ended, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token);
        var token = linked.Token;
        if (screen)
        {
            if (!GraphicsCaptureSession.IsSupported()) throw new NotSupportedException("Windows screen capture is unavailable.");
            var picker = new GraphicsCapturePicker(); WinRT.Interop.InitializeWithWindow.Initialize(picker, owner);
            item = await picker.PickSingleItemAsync().AsTask(token);
            token.ThrowIfCancellationRequested();
            if (item is null) return false;
            item.Closed += ItemClosed;
            device = NativeWindowsCapture.CreateDevice();
            pool = Direct3D11CaptureFramePool.CreateFreeThreaded(device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, item.Size);
            capture = pool.CreateCaptureSession(item);
            // Keep the OS capture border; never request borderless or programmatic source selection.
            capture.StartCapture();
        }
        else
        {
            camera = new MediaCapture();
            await camera.InitializeAsync(new MediaCaptureInitializationSettings { StreamingCaptureMode = StreamingCaptureMode.Video,
                MemoryPreference = MediaCaptureMemoryPreference.Cpu, SharingMode = MediaCaptureSharingMode.SharedReadOnly }).AsTask(token);
            var source = camera.FrameSources.Values.FirstOrDefault(s => s.Info.SourceKind == MediaFrameSourceKind.Color)
                ?? throw new InvalidOperationException("No camera is available.");
            reader = await camera.CreateFrameReaderAsync(source, "BGRA8").AsTask(token);
            if (await reader.StartAsync().AsTask(token) != MediaFrameReaderStartStatus.Success) throw new InvalidOperationException("The camera could not be started.");
        }
        pump = PumpAsync(settings.Clone(), frame, ended);
        return true;
    }
    private void ItemClosed(GraphicsCaptureItem sender, object args) => lifetime.Cancel();
    private async Task PumpAsync(RealtimePreferences settings, Func<byte[], CancellationToken, Task> send, Action ended)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1 / settings.CameraFrameRate));
            while (await timer.WaitForNextTickAsync(lifetime.Token))
            {
                SoftwareBitmap? bitmap = null;
                if (reader is not null)
                {
                    using var frame = reader.TryAcquireLatestFrame();
                    if (frame?.VideoMediaFrame?.SoftwareBitmap is { } source) bitmap = SoftwareBitmap.Copy(source);
                }
                else if (pool is not null)
                {
                    using var frame = pool.TryGetNextFrame();
                    if (frame is not null)
                    {
                        if (frame.ContentSize.Width != item!.Size.Width || frame.ContentSize.Height != item.Size.Height)
                        { pool.Recreate(device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, item.Size); continue; }
                        bitmap = await SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface).AsTask(lifetime.Token);
                    }
                }
                using (bitmap)
                {
                    if (bitmap is null) continue;
                    var jpeg = await EncodeAsync(bitmap, settings.JpegQuality, lifetime.Token);
                    lifetime.Token.ThrowIfCancellationRequested();
                    await send(jpeg, lifetime.Token);
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch { /* UI receives a safe capture-ended notice, never SDK/device exception details. */ }
        finally { ended(); }
    }
    private static async Task<byte[]> EncodeAsync(SoftwareBitmap bitmap, double quality, CancellationToken ct)
    {
        if ((long)bitmap.PixelWidth * bitmap.PixelHeight > 40_000_000) throw new InvalidDataException("Capture too large.");
        using var output = new InMemoryRandomAccessStream();
        var properties = new BitmapPropertySet { ["ImageQuality"] = new BitmapTypedValue((float)quality, Windows.Foundation.PropertyType.Single) };
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, output, properties).AsTask(ct);
        using var converted = SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);
        encoder.SetSoftwareBitmap(converted);
        var scale = Math.Min(1, 1280d / Math.Max(bitmap.PixelWidth, bitmap.PixelHeight));
        encoder.BitmapTransform.ScaledWidth = (uint)Math.Max(1, bitmap.PixelWidth * scale);
        encoder.BitmapTransform.ScaledHeight = (uint)Math.Max(1, bitmap.PixelHeight * scale);
        await encoder.FlushAsync().AsTask(ct);
        if (output.Size > 2 * 1024 * 1024) throw new InvalidDataException("Capture too large.");
        output.Seek(0); using var input = new DataReader(output.GetInputStreamAt(0));
        var bytes = new byte[(int)output.Size]; await input.LoadAsync((uint)bytes.Length).AsTask(ct); input.ReadBytes(bytes); return bytes;
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        lifetime.Cancel();
        if (pump is not null) await pump;
        if (item is not null) item.Closed -= ItemClosed;
        if (reader is not null) { try { await reader.StopAsync(); } finally { reader.Dispose(); } }
        camera?.Dispose(); capture?.Dispose(); pool?.Dispose(); device?.Dispose();
    }
}
