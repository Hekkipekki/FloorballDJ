using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace FloorballDJ.Services;

public static class WindowPlacementService
{
    private const uint MonitorDefaultToNearest = 2;
    private const int WindowMarginPixels = 12;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoZOrder = 0x0004;

    public static void MaximizeOnOwnerMonitor(Window window)
        => Configure(window, maximize: true);

    /// <summary>Fits a normal dialog inside the owner's current monitor without hiding controls behind the taskbar.</summary>
    public static void FitToOwnerMonitor(Window window)
        => Configure(window, maximize: false);

    private static void Configure(Window window, bool maximize)
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
                var source = HwndSource.FromHwnd(windowHandle);
                var fromDevice = source?.CompositionTarget?.TransformFromDevice ?? System.Windows.Media.Matrix.Identity;
                var topLeft = fromDevice.Transform(new Point(work.Left, work.Top));
                var bottomRight = fromDevice.Transform(new Point(work.Right, work.Bottom));
                var availableWidth = Math.Max(320, bottomRight.X - topLeft.X);
                var availableHeight = Math.Max(220, bottomRight.Y - topLeft.Y);
                window.MinWidth = Math.Min(window.MinWidth, availableWidth);
                window.MinHeight = Math.Min(window.MinHeight, availableHeight);

                if (!maximize)
                {
                    var width = double.IsNaN(window.Width) ? Math.Max(window.ActualWidth, window.MinWidth) : window.Width;
                    var height = double.IsNaN(window.Height) ? Math.Max(window.ActualHeight, window.MinHeight) : window.Height;
                    window.Width = Math.Clamp(width, Math.Min(320, availableWidth), availableWidth);
                    window.Height = Math.Clamp(height, Math.Min(220, availableHeight), availableHeight);
                    var toDevice = source?.CompositionTarget?.TransformToDevice ?? System.Windows.Media.Matrix.Identity;
                    var sizePixels = toDevice.Transform(new Point(window.Width, window.Height));
                    var pixelWidth = Math.Min(work.Right - work.Left - WindowMarginPixels * 2, (int)Math.Ceiling(sizePixels.X));
                    var pixelHeight = Math.Min(work.Bottom - work.Top - WindowMarginPixels * 2, (int)Math.Ceiling(sizePixels.Y));
                    var left = work.Left + Math.Max(WindowMarginPixels, (work.Right - work.Left - pixelWidth) / 2);
                    var top = work.Top + Math.Max(WindowMarginPixels, (work.Bottom - work.Top - pixelHeight) / 2);
                    SetWindowPos(windowHandle, IntPtr.Zero, left, top, pixelWidth, pixelHeight, SwpNoActivate | SwpNoZOrder);
                    return;
                }

                SetWindowPos(windowHandle, IntPtr.Zero, work.Left, work.Top,
                    work.Right - work.Left, work.Bottom - work.Top, SwpNoActivate | SwpNoZOrder);
            }

            if (maximize) window.WindowState = WindowState.Maximized;
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
