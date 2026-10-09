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
        var operation = PerformanceDiagnostics.BeginOperation("WindowPlacementConfigured");
        window.SourceInitialized += (_, _) =>
        {
            using var placementDuration = operation.Measure("WindowPlacementSourceInitialized");
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
                if (!maximize)
                {
                    var width = double.IsNaN(window.Width) ? Math.Max(window.ActualWidth, window.MinWidth) : window.Width;
                    var height = double.IsNaN(window.Height) ? Math.Max(window.ActualHeight, window.MinHeight) : window.Height;
                    var toDevice = source?.CompositionTarget?.TransformToDevice ?? System.Windows.Media.Matrix.Identity;
                    var fit = CalculateFit(new Rect(work.Left, work.Top, work.Right - work.Left, work.Bottom - work.Top),
                        new Size(width, height), new Size(window.MinWidth, window.MinHeight), fromDevice, toDevice);
                    window.MinWidth = fit.MinimumDip.Width;
                    window.MinHeight = fit.MinimumDip.Height;
                    window.Width = fit.SizeDip.Width;
                    window.Height = fit.SizeDip.Height;
                    using (operation.Measure("WindowPlacementSetWindowPos"))
                        SetWindowPos(windowHandle, IntPtr.Zero, (int)fit.Pixels.X, (int)fit.Pixels.Y,
                            (int)fit.Pixels.Width, (int)fit.Pixels.Height, SwpNoActivate | SwpNoZOrder);
                    return;
                }

                var topLeft = fromDevice.Transform(new Point(work.Left, work.Top));
                var bottomRight = fromDevice.Transform(new Point(work.Right, work.Bottom));
                var availableWidth = Math.Max(320, bottomRight.X - topLeft.X);
                var availableHeight = Math.Max(220, bottomRight.Y - topLeft.Y);
                window.MinWidth = Math.Min(window.MinWidth, availableWidth);
                window.MinHeight = Math.Min(window.MinHeight, availableHeight);
                using (operation.Measure("WindowPlacementSetWindowPos"))
                    SetWindowPos(windowHandle, IntPtr.Zero, work.Left, work.Top,
                        work.Right - work.Left, work.Bottom - work.Top, SwpNoActivate | SwpNoZOrder);
            }

            if (maximize)
                using (operation.Measure("WindowPlacementMaximize")) window.WindowState = WindowState.Maximized;
        };
    }

    internal readonly record struct FitBounds(Rect Pixels, Size MinimumDip, Size SizeDip);

    // One physical envelope governs both the native rectangle and WPF minimums.
    // Work-area origins stay in pixels; size vectors exclude coordinate offsets.
    internal static FitBounds CalculateFit(Rect workPixels, Size requestedDip, Size minimumDip,
        System.Windows.Media.Matrix fromDevice, System.Windows.Media.Matrix toDevice)
    {
        var workWidth = Math.Max(1, (int)workPixels.Width);
        var workHeight = Math.Max(1, (int)workPixels.Height);
        var marginX = Math.Min(WindowMarginPixels, (workWidth - 1) / 2);
        var marginY = Math.Min(WindowMarginPixels, (workHeight - 1) / 2);
        var capacityWidth = workWidth - marginX * 2;
        var capacityHeight = workHeight - marginY * 2;
        var capacityDip = fromDevice.Transform(new Vector(capacityWidth, capacityHeight));
        var minimum = new Size(Math.Min(minimumDip.Width, capacityDip.X), Math.Min(minimumDip.Height, capacityDip.Y));
        var desired = new Vector(
            Math.Clamp(requestedDip.Width, Math.Max(minimum.Width, Math.Min(320, capacityDip.X)), capacityDip.X),
            Math.Clamp(requestedDip.Height, Math.Max(minimum.Height, Math.Min(220, capacityDip.Y)), capacityDip.Y));
        var desiredPixels = toDevice.Transform(desired);
        var pixelWidth = Math.Clamp((int)Math.Ceiling(desiredPixels.X), 1, capacityWidth);
        var pixelHeight = Math.Clamp((int)Math.Ceiling(desiredPixels.Y), 1, capacityHeight);
        var sizeDip = fromDevice.Transform(new Vector(pixelWidth, pixelHeight));
        return new FitBounds(new Rect(workPixels.X + (workWidth - pixelWidth) / 2,
            workPixels.Y + (workHeight - pixelHeight) / 2, pixelWidth, pixelHeight), minimum, new Size(sizeDip.X, sizeDip.Y));
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
