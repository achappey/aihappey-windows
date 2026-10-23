using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace AIHappey.Desktop.UiTests;

/// <summary>Charts use a native composition surface that RenderTargetBitmap does not capture.</summary>
internal static class ChartScreenCapture
{
    internal static (int Width, int Height, byte[] Pixels) Capture(Window window, FrameworkElement element)
    {
        var root = (FrameworkElement)window.Content;
        var bounds = element.TransformToVisual(root).TransformBounds(new(0, 0, element.ActualWidth, element.ActualHeight));
        var scale = root.XamlRoot.RasterizationScale;
        var origin = new Point { X = (int)(bounds.X * scale), Y = (int)(bounds.Y * scale) };
        if (!ClientToScreen(WinRT.Interop.WindowNative.GetWindowHandle(window), ref origin)) throw new InvalidOperationException("Cannot locate chart window.");
        var width = Math.Max(1, (int)(bounds.Width * scale)); var height = Math.Max(1, (int)(bounds.Height * scale));
        var info = new BitmapInfo { Size = (uint)Marshal.SizeOf<BitmapInfo>(), Width = width, Height = -height, Planes = 1, BitCount = 32 };
        var screen = GetDC(IntPtr.Zero); var target = CreateCompatibleDC(screen);
        var bitmap = CreateDIBSection(screen, ref info, 0, out var pixels, IntPtr.Zero, 0);
        if (screen == IntPtr.Zero || target == IntPtr.Zero || bitmap == IntPtr.Zero) throw new InvalidOperationException("Cannot capture native chart surface.");
        var previous = SelectObject(target, bitmap);
        try
        {
            if (!BitBlt(target, 0, 0, width, height, screen, origin.X, origin.Y, 0x00CC0020)) throw new InvalidOperationException("Cannot read native chart pixels.");
            var bytes = new byte[checked(width * height * 4)]; Marshal.Copy(pixels, bytes, 0, bytes.Length);
            return (width, height, bytes);
        }
        finally { SelectObject(target, previous); DeleteObject(bitmap); DeleteDC(target); ReleaseDC(IntPtr.Zero, screen); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo
    {
        public uint Size; public int Width, Height; public ushort Planes, BitCount;
        public uint Compression, SizeImage; public int XPelsPerMeter, YPelsPerMeter; public uint ClrUsed, ClrImportant;
    }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ClientToScreen(IntPtr window, ref Point point);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool BitBlt(IntPtr target, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, uint operation);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(IntPtr value);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteDC(IntPtr dc);
}
