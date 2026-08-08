using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace HistoryVulcan.Shell;

internal static class WindowForegroundActivator
{
    private const int ShowRestore = 9;

    public static void Activate(Window window)
    {
        window.Show();
        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;

        var handle = new WindowInteropHelper(window).EnsureHandle();
        var foreground = GetForegroundWindow();
        var currentThread = GetCurrentThreadId();
        var foregroundThread = foreground == IntPtr.Zero
            ? 0
            : GetWindowThreadProcessId(foreground, out _);
        var attached = foregroundThread != 0 &&
                       foregroundThread != currentThread &&
                       AttachThreadInput(currentThread, foregroundThread, true);

        try
        {
            _ = ShowWindowAsync(handle, ShowRestore);
            _ = BringWindowToTop(handle);
            _ = SetForegroundWindow(handle);
            _ = SetActiveWindow(handle);
            _ = SetFocus(handle);
            _ = window.Activate();
        }
        finally
        {
            if (attached)
                _ = AttachThreadInput(currentThread, foregroundThread, false);
        }

        if (window.IsActive || window.Topmost)
            return;

        // Keep the window at the top of the normal z-order without making it permanently topmost.
        window.Topmost = true;
        _ = window.Activate();
        window.Topmost = false;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr windowHandle, out uint processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint attachThread, uint attachToThread, bool attach);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindowAsync(IntPtr windowHandle, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(IntPtr windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr windowHandle);

    [DllImport("user32.dll")]
    private static extern IntPtr SetActiveWindow(IntPtr windowHandle);

    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr windowHandle);
}
