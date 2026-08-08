using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace HistoryVulcan.Shell;

internal static class FloatingWindowTheme
{
    private const uint DwmWindowAttributeBorderColor = 34;

    public static void ApplyNativeBorder(Window window, Brush borderBrush)
    {
        if (borderBrush is not SolidColorBrush solid)
            return;

        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
            return;

        var color = solid.Color;
        var colorRef = (uint)(color.R | (color.G << 8) | (color.B << 16));
        _ = DwmSetWindowAttribute(
            handle,
            DwmWindowAttributeBorderColor,
            ref colorRef,
            sizeof(uint));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr windowHandle,
        uint attribute,
        ref uint attributeValue,
        uint attributeSize);
}
