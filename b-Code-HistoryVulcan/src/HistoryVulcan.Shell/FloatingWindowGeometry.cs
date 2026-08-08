using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace HistoryVulcan.Shell;

internal readonly record struct FloatingPlacement(int Left, int Top, int Width, int Height);

internal readonly record struct FloatingDragContext(
    string PageId,
    Size EmbeddedSize,
    Point AnchorOffset,
    bool ContinueWithDrag);

internal static class FloatingWindowGeometry
{
    private const uint MonitorDefaultToNearest = 2;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoZOrder = 0x0004;
    private const int MonitorDpiTypeEffective = 0;

    public static FloatingPlacement CalculatePlacement(
        Point pointerPixels,
        Rect workAreaPixels,
        Size desiredSizeDip,
        Point anchorOffsetDip,
        double dpiX,
        double dpiY)
    {
        var scaleX = Math.Max(dpiX, 1) / 96d;
        var scaleY = Math.Max(dpiY, 1) / 96d;
        var workWidth = Math.Max(1, (int)Math.Round(workAreaPixels.Width));
        var workHeight = Math.Max(1, (int)Math.Round(workAreaPixels.Height));
        var width = Math.Clamp((int)Math.Round(desiredSizeDip.Width * scaleX), 1, workWidth);
        var height = Math.Clamp((int)Math.Round(desiredSizeDip.Height * scaleY), 1, workHeight);
        var anchorX = Math.Clamp((int)Math.Round(anchorOffsetDip.X * scaleX), 0, width);
        var anchorY = Math.Clamp((int)Math.Round(anchorOffsetDip.Y * scaleY), 0, height);
        var left = Math.Clamp(
            (int)Math.Round(pointerPixels.X) - anchorX,
            (int)Math.Round(workAreaPixels.Left),
            (int)Math.Round(workAreaPixels.Right) - width);
        var top = Math.Clamp(
            (int)Math.Round(pointerPixels.Y) - anchorY,
            (int)Math.Round(workAreaPixels.Top),
            (int)Math.Round(workAreaPixels.Bottom) - height);
        return new FloatingPlacement(left, top, width, height);
    }

    public static Point GetCursorPosition()
    {
        if (!NativeMethods.GetCursorPos(out var point))
            return default;
        return new Point(point.X, point.Y);
    }

    public static FloatingPlacement PlaceWindow(
        Window window,
        Point pointerPixels,
        Size desiredSizeDip,
        Point anchorOffsetDip)
    {
        var monitor = NativeMethods.MonitorFromPoint(
            new NativePoint
            {
                X = (int)Math.Round(pointerPixels.X),
                Y = (int)Math.Round(pointerPixels.Y),
            },
            MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !NativeMethods.GetMonitorInfo(monitor, ref info))
            throw new InvalidOperationException("无法读取鼠标所在显示器工作区");

        var dpiX = 96u;
        var dpiY = 96u;
        var dpiResult = NativeMethods.GetDpiForMonitor(
            monitor,
            MonitorDpiTypeEffective,
            out dpiX,
            out dpiY);
        if (dpiResult != 0 || dpiX == 0 || dpiY == 0)
        {
            dpiX = 96;
            dpiY = 96;
        }
        var workArea = new Rect(
            info.WorkArea.Left,
            info.WorkArea.Top,
            info.WorkArea.Right - info.WorkArea.Left,
            info.WorkArea.Bottom - info.WorkArea.Top);
        var placement = CalculatePlacement(
            pointerPixels,
            workArea,
            desiredSizeDip,
            anchorOffsetDip,
            dpiX,
            dpiY);

        window.SizeToContent = SizeToContent.Manual;
        window.WindowState = WindowState.Normal;
        window.Width = placement.Width * 96d / dpiX;
        window.Height = placement.Height * 96d / dpiY;
        var handle = new WindowInteropHelper(window).EnsureHandle();
        if (!NativeMethods.SetWindowPos(
                handle,
                IntPtr.Zero,
                placement.Left,
                placement.Top,
                placement.Width,
                placement.Height,
                SwpNoActivate | SwpNoZOrder))
        {
            throw new InvalidOperationException("无法把窗口定位到鼠标所在显示器");
        }

        return placement;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect WorkArea;
        public uint Flags;
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetCursorPos(out NativePoint point);

        [DllImport("user32.dll")]
        internal static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

        [DllImport("shcore.dll")]
        internal static extern int GetDpiForMonitor(
            IntPtr monitor,
            int dpiType,
            out uint dpiX,
            out uint dpiY);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetWindowPos(
            IntPtr window,
            IntPtr insertAfter,
            int x,
            int y,
            int width,
            int height,
            uint flags);
    }
}
