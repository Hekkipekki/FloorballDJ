using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using FloorballDJ.Services;
using FloorballDJ.Views;

internal static class WindowFitChecks
{
    private sealed record Display(IntPtr Handle, Rect Work);
    private sealed record Spec(string Name, double Width, double Height, double MinWidth, double MinHeight);
    internal static async Task RunAsync(string? output)
    {
        var geometryCases = CheckGeometry();
        var observations = new List<object>(); var displays = Displays();
        foreach (var display in displays)
        {
            var owner = new Window { Title = "Fit checks owner", Width = 800, Height = 600, ShowInTaskbar = false, Content = new Button { Content = "Synthetic owner" } };
            try
            {
                owner.Show(); var handle = new WindowInteropHelper(owner).Handle;
                Check(SetWindowPos(handle, IntPtr.Zero, (int)display.Work.X + 20, (int)display.Work.Y + 20, 800, 600, 0x0014), "Place test owner"); await Idle();
                foreach (var state in new[] { WindowState.Normal, WindowState.Maximized })
                {
                    owner.WindowState = state; await Idle();
                    Check(MonitorFromWindow(handle, 2) == display.Handle, "Owner stays on target monitor");
                    foreach (var spec in new[] {
                        new Spec("ordinary", 1000, 700, 700, 400), new Spec("oversized", 4000, 3000, 2500, 2000),
                        new Spec("minimum-larger-than-request", 300, 200, 1000, 800), new Spec("fractional", 701.2, 500.4, 601.1, 350.1) })
                        foreach (var modal in new[] { false, true })
                        {
                            var input = new TextBox { Text = "Synthetic editable content", Margin = new Thickness(20) };
                            var child = new Window { Owner = owner, Width = spec.Width, Height = spec.Height, MinWidth = spec.MinWidth, MinHeight = spec.MinHeight,
                                WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false, Content = input };
                            WindowPlacementService.FitToOwnerMonitor(child);
                            observations.Add(await Inspect(child, owner, display, spec.Name, modal, input, () => child.Close()));
                        }
                    var prompt = new TextPromptWindow("Synthetic prompt", "Fixture", "No profile or personal content", "initial") { Owner = owner, ShowInTaskbar = false };
                    var box = (TextBox)prompt.FindName("ValueBox");
                    observations.Add(await Inspect(prompt, owner, display, "real-prompt-save", true, box, () => {
                        box.Text = " saved value ";
                        typeof(TextPromptWindow).GetMethod("Save_Click", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(prompt, [null, new RoutedEventArgs()]);
                    }));
                    Check(prompt.Value == "saved value", "Actual prompt retains value/trim/save behavior");
                    var shortcut = new ShortcutCaptureWindow("Ctrl+F1") { Owner = owner, ShowInTaskbar = false };
                    observations.Add(await Inspect(shortcut, owner, display, "real-shortcut-autoheight-cancel", true, shortcut, () => {
                        shortcut.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(shortcut)!, Environment.TickCount, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                    }));
                    Check(shortcut.SelectedShortcut == "Ctrl+F1", "Actual shortcut Cancel preserves existing shortcut");
                }
            }
            finally { owner.Close(); await Idle(); }
        }
        if (output is not null)
        {
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output, "window-fit-checks.json"), JsonSerializer.Serialize(new {
                version = "p4.7-fit-checks-v1", passed = true, checkedUtc = DateTimeOffset.UtcNow, geometryCases, observations,
                limitation = "Native rectangles on currently available monitors; normal/maximized synthetic owners and modal/modeless editable dialogs. Real prompt Save and auto-height shortcut Cancel. WPF focus and native modal enablement are checked; OS foreground recovery is an observation, not a passed assertion. No OS display settings, personal profile or audio are changed. Physical mixed-DPI, actual main-view and long-session qualification remain open."
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        Console.WriteLine($"PASS: {observations.Count} native Fit checks; work-area/margin/centering, ordinary/oversized/minimum/fractional sizes, owner/modal/focus and real prompt/autoheight shortcut behavior.");
    }

    private static int CheckGeometry()
    {
        var count = 0;
        void Case(Rect work, double scaleX, double scaleY, Size requested, Size minimum)
        {
            var fit = WindowPlacementService.CalculateFit(work, requested, minimum,
                new Matrix(1 / scaleX, 0, 0, 1 / scaleY, 321, -987), new Matrix(scaleX, 0, 0, scaleY, -44, 66));
            var rect = fit.Pixels;
            Check(rect.Width >= 1 && rect.Height >= 1 && work.Contains(rect), "Positive planned native rectangle remains in the work area");
            Check(Math.Abs(rect.X + rect.Width / 2 - (work.X + work.Width / 2)) <= .5 &&
                Math.Abs(rect.Y + rect.Height / 2 - (work.Y + work.Height / 2)) <= .5, "Pixel centering works with signed/offset origins");
            Check(fit.MinimumDip.Width * scaleX <= rect.Width + 1e-6 && fit.MinimumDip.Height * scaleY <= rect.Height + 1e-6,
                "DIP minimums cannot contradict the planned pixel size");
            Check(fit.MinimumDip.Width <= minimum.Width + 1e-6 && fit.MinimumDip.Height <= minimum.Height + 1e-6,
                "Fitting never raises the caller's minimum");
            Check(Math.Abs(fit.SizeDip.Width * scaleX - rect.Width) <= 1e-6 && Math.Abs(fit.SizeDip.Height * scaleY - rect.Height) <= 1e-6,
                "Logical size converts back to the exact rounded native size, independently of coordinate translations");
            if (work.Width >= 25) Check(rect.Left >= work.Left + 12 && rect.Right <= work.Right - 12, "Physical horizontal margins are scale-independent");
            if (work.Height >= 25) Check(rect.Top >= work.Top + 12 && rect.Bottom <= work.Bottom - 12, "Physical vertical margins are scale-independent");
            if (requested.Width >= Math.Max(320, minimum.Width) && requested.Height >= Math.Max(220, minimum.Height) &&
                Math.Ceiling(requested.Width * scaleX) <= work.Width - 24 && Math.Ceiling(requested.Height * scaleY) <= work.Height - 24)
                Check(rect.Width == Math.Ceiling(requested.Width * scaleX) && rect.Height == Math.Ceiling(requested.Height * scaleY) && fit.MinimumDip == minimum,
                    "An ordinary fitting request keeps its native size and minimums");
            count++;
        }
        foreach (var area in new[] { new Rect(0, 0, 1920, 1032), new Rect(-1920, 0, 1920, 1032), new Rect(1600, -900, 1366, 728),
            new Rect(-1344, 48, 1280, 672), new Rect(0, 0, 640, 400), new Rect(0, 0, 320, 220), new Rect(0, 0, 23, 21) })
            foreach (var scaleX in new[] { 1d, 1.25, 1.5, 1.75, 2, 3 })
                foreach (var scaleY in new[] { 1d, 1.25, 1.5, 1.75, 2, 3 })
                {
                    Case(area, scaleX, scaleY, new Size(1000, 700), new Size(700, 400));
                    Case(area, scaleX, scaleY, new Size(4000, 3000), new Size(2500, 2000));
                    Case(area, scaleX, scaleY, new Size(300, 200), new Size(1000, 800));
                    Case(area, scaleX, scaleY, new Size(701.2, 500.4), new Size(601.1, 350.1));
                }
        var random = new Random(42);
        for (var i = 0; i < 20000; i++) Case(new Rect(random.Next(-5000, 5001), random.Next(-3000, 3001), random.Next(25, 4001), random.Next(25, 2401)),
            1 + random.Next(193) / 96d, 1 + random.Next(193) / 96d,
            new Size(random.NextDouble() * 6000, random.NextDouble() * 4000), new Size(random.NextDouble() * 5000, random.NextDouble() * 3000));
        Console.WriteLine($"PASS: {count} scaled Fit geometry cases; physical envelope, minimum/size consistency, signed origins, fractional pixels and ordinary-size parity.");
        return count;
    }

    private static async Task<object> Inspect(Window child, Window owner, Display display, string name, bool modal, UIElement focus, Action close)
    {
        object? result = null; Exception? error = null;
        async Task Capture()
        {
            await Render(child); await Idle();
            var handle = new WindowInteropHelper(child).Handle; Check(GetWindowRect(handle, out var bounds), "Read final native rectangle");
            var actual = new Rect(bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top); var work = display.Work;
            Check(child.WindowState == WindowState.Normal && MonitorFromWindow(handle, 2) == display.Handle, "Fit preserves normal state and owner monitor");
            Check(actual.Left >= work.Left + 11 && actual.Top >= work.Top + 11 && actual.Right <= work.Right - 11 && actual.Bottom <= work.Bottom - 11,
                $"Final Fit rectangle respects physical margins: {name}, actual {actual}, work {work}");
            Check(Math.Abs(actual.Left + actual.Width / 2 - (work.Left + work.Width / 2)) <= 1 &&
                Math.Abs(actual.Top + actual.Height / 2 - (work.Top + work.Height / 2)) <= 1, "Actual rectangle stays centered after minimum-size constraints");
            var dpi = VisualTreeHelper.GetDpi(child);
            Check(child.MinWidth * dpi.DpiScaleX <= actual.Width + 1 && child.MinHeight * dpi.DpiScaleY <= actual.Height + 1, "Minimum sizes agree with the fitted native rectangle");
            if (name == "ordinary" && dpi.DpiScaleX == 1 && dpi.DpiScaleY == 1) Check(actual.Width == 1000 && actual.Height == 700 && child.MinWidth == 700 && child.MinHeight == 400, "Ordinary size/minimums are unchanged");
            if (modal) Check(!IsWindowEnabled(new WindowInteropHelper(owner).Handle), "Modal owner is natively disabled");
            focus.Focus(); Check(child.IsKeyboardFocusWithin && ReferenceEquals(Keyboard.FocusedElement, focus), "Actual child accepts WPF keyboard focus");
            result = new { name, modal, ownerState = owner.WindowState.ToString(), dpiX = dpi.PixelsPerInchX, dpiY = dpi.PixelsPerInchY,
                work = Rectangle(work), actual = Rectangle(actual), child.MinWidth, child.MinHeight, sizeToContent = child.SizeToContent.ToString() };
            close();
        }
        try
        {
            if (modal)
            {
                _ = child.Dispatcher.BeginInvoke(new Action(async () => { try { await Capture(); } catch (Exception exception) { error = exception; child.Close(); } }), DispatcherPriority.ContextIdle);
                var saved = child.ShowDialog();
                if (error is not null) throw error;
                Check(saved == (name == "real-prompt-save"), "Modal Save/Cancel result preserved");
            }
            else { child.Show(); await Capture(); }
            await Idle(); Check(result is not null && IsWindowEnabled(new WindowInteropHelper(owner).Handle), "Capture completed and owner is enabled after close");
            return new { geometry = result, ownerForegroundAfterClose = GetForegroundWindow() == new WindowInteropHelper(owner).Handle };
        }
        finally { child.Close(); await Idle(); }
    }
    private static object Rectangle(Rect value) => new { value.X, value.Y, value.Width, value.Height };
    private static List<Display> Displays()
    {
        var result = new List<Display>();
        MonitorCallback callback = (IntPtr handle, IntPtr hdc, ref NativeRect rect, IntPtr data) => {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() }; Check(GetMonitorInfo(handle, ref info), "Read work area");
            result.Add(new Display(handle, new Rect(info.Work.Left, info.Work.Top, info.Work.Right - info.Work.Left, info.Work.Bottom - info.Work.Top))); return true;
        };
        Check(EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero), "Enumerate displays"); return result;
    }
    private static async Task Render(Window window)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); EventHandler handler = (_, _) => done.TrySetResult(); CompositionTarget.Rendering += handler;
        try { window.InvalidateVisual(); await done.Task.WaitAsync(TimeSpan.FromSeconds(20)); } finally { CompositionTarget.Rendering -= handler; }
    }
    private static async Task Idle() => await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public NativeRect Bounds, Work; public uint Flags; }
    private delegate bool MonitorCallback(IntPtr handle, IntPtr hdc, ref NativeRect rect, IntPtr data);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorCallback callback, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(IntPtr handle, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr handle, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr handle, out NativeRect rect);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(IntPtr handle, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowEnabled(IntPtr handle);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
}
