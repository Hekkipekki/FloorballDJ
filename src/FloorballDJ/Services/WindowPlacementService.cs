using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace FloorballDJ.Services;

public static class WindowPlacementService
{
    private const uint MonitorDefaultToNearest = 2;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoZOrder = 0x0004;

    public static void MaximizeOnOwnerMonitor(Window window)
    {
        window.SourceInitialized += (_, _) =>
        {
            var windowHandle = new WindowInteropHelper(window).Handle;
            var owner = window.Owner ?? Application.Current?.MainWindow;
            var ownerHandle = owner is null ? IntPtr.Zero : new WindowInteropHelper(owner).Handle;
            var monitor = MonitorFromWindow(ownerHandle != IntPtr.Zero ? ownerHandle : windowHandle, MonitorDefaultToNearest);
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info))
            {
                var work = info.WorkArea;
                SetWindowPos(windowHandle, IntPtr.Zero, work.Left, work.Top,
                    work.Right - work.Left, work.Bottom - work.Top, SwpNoActivate | SwpNoZOrder);
            }

            window.WindowState = WindowState.Maximized;
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect WorkArea;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr windowHandle, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr windowHandle, IntPtr insertAfter, int x, int y,
        int width, int height, uint flags);
}
