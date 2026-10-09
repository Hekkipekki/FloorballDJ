using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using FloorballDJ.Controls;
using FloorballDJ.Services;
using FloorballDJ.Views;

// Opt-in analysis: every alternative is confined to these synthetic windows.
// A timing win does not promote a candidate into the shared production service.
internal static class WindowPlacementAnalysis
{
    private sealed record Monitor(IntPtr Handle, Box Work, Box Bounds);
    private sealed record Box(double X, double Y, double Width, double Height)
    {
        internal static Box From(Rect rect) => new(rect.X, rect.Y, rect.Width, rect.Height);
        internal static Box From(NativeRect rect) => new(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
        internal bool Near(Box other) => Math.Abs(X - other.X) <= 1 && Math.Abs(Y - other.Y) <= 1 && Math.Abs(Width - other.Width) <= 1 && Math.Abs(Height - other.Height) <= 1;
    }
    private sealed record Frame(string State, int Monitor, double Dpi, Box OuterPixels, Box RestoreDip, Box NativeRestore,
        double MinWidth, double MinHeight);
    private sealed record Observation(string Strategy, int Monitor, string OwnerState, bool Modal, int Trial,
        double ConstructorMs, double SetupMs, double? SynchronousShowMs, double ReadyIdleMs, long CallerBytes,
        Frame Maximized, Frame Restored, Frame Remaximized, bool OwnerDisabledDuringModal, bool OwnerEnabledAfterClose,
        bool FocusInside, bool OwnerForegroundAfterClose, int SizeChanges, object[] PlacementEvents);
    private static readonly string[] Strategies = ["baseline", "early-max-retain", "early-work-retain", "early-work-native"];

    internal static async Task RunAsync(string root, string? output, int trials)
    {
        Check(trials is > 0 and <= 20, "Valid placement trial count");
        output ??= Path.Combine(root, "placement-analysis"); Directory.CreateDirectory(output);
        var monitors = Monitors(); Check(monitors.Count > 0, "At least one available monitor");
        var previousLanguage = LanguageService.CurrentLanguage; LanguageService.SetLanguage("en");
        // Match the application's translation handlers without App.OnStartup.
        EventManager.RegisterClassHandler(typeof(FrameworkElement), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) => { if (sender is FrameworkElement element) LanguageService.TranslateElement(element); }), true);
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) => { if (sender is Window window) window.Dispatcher.BeginInvoke(() => LanguageService.TranslateTree(window), DispatcherPriority.Loaded); }), true);
        var references = new List<WeakReference>(); var results = new List<Observation>(); var fitted = new List<object>();
        try
        {
            for (var index = 0; index < monitors.Count; index++)
            {
                var monitor = monitors[index];
                var owner = new Window { Title = "Placement analysis owner", Width = 800, Height = 600,
                    ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual,
                    Content = new Button { Content = "Synthetic owner", Margin = new Thickness(30) } };
                try
                {
                    owner.Show(); MoveOwner(owner, monitor); await Rendering(owner); await Idle();
                    var ownerHandle = new WindowInteropHelper(owner).Handle;
                    Check(MonitorFromWindow(ownerHandle, 2) == monitor.Handle, "Owner moved to available target display");
                    foreach (var state in new[] { WindowState.Normal, WindowState.Maximized })
                    {
                        owner.WindowState = state; await Rendering(owner); await Idle();
                        // Separate first-process observation from the warm sequence.
                        if (index == 0 && state == WindowState.Normal)
                            results.Add(await Observe(root, output, owner, monitors, "baseline", -1, false, references));
                        var random = new Random(42);
                        for (var trial = 0; trial < trials; trial++)
                            foreach (var strategy in Strategies.OrderBy(_ => random.Next()))
                            {
                                Console.WriteLine($"Placement monitor {index}, owner {state}, {strategy} {trial + 1}/{trials}");
                                results.Add(await Observe(root, output, owner, monitors, strategy, trial, false, references));
                            }
                        foreach (var strategy in Strategies)
                            results.Add(await Observe(root, output, owner, monitors, strategy, 0, true, references));
                        fitted.Add(await Fit(owner, monitors, references, false));
                        fitted.Add(await Fit(owner, monitors, references, true));
                    }
                }
                finally { owner.Close(); await Idle(); }
            }
            await Idle(); GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); await Idle();
            var parity = results.Where(item => item.Strategy != "baseline").Select(item => {
                var baseline = results.First(other => other.Strategy == "baseline" && other.Trial >= 0 &&
                    other.Monitor == item.Monitor && other.OwnerState == item.OwnerState && other.Modal == item.Modal);
                return new { item.Strategy, item.Monitor, item.OwnerState, item.Modal, item.Trial,
                    sameMaximized = Equivalent(item.Maximized, baseline.Maximized), sameRestored = Equivalent(item.Restored, baseline.Restored),
                    sameRemaximized = Equivalent(item.Remaximized, baseline.Remaximized),
                    sameMaximizedOuterPixels = item.Maximized.OuterPixels.Near(baseline.Maximized.OuterPixels),
                    sameRestoreDip = item.Maximized.RestoreDip.Near(baseline.Maximized.RestoreDip) };
            }).ToArray();
            File.WriteAllText(Path.Combine(output, "placement-analysis.json"), JsonSerializer.Serialize(new {
                schemaVersion = 1, version = "p4.6-placement-analysis-v1", checkedUtc = DateTimeOffset.UtcNow,
                runtime = Environment.Version.ToString(), processorCount = Environment.ProcessorCount, trialsPerCase = trials,
                monitors = monitors.Select((item, index) => new { index, item.Work, item.Bounds }), observations = results, parity, fitted,
                closedChildren = references.Count, survivingClosedChildren = references.Count(item => item.IsAlive),
                limitation = "Synthetic 553-file/12-group shown editors with real owner windows on currently available displays; no production main-view/audio. Trial -1 is the first editor after showing the synthetic owner, not cold application/WPF startup. Activation and mirrored translation enabled equally. Alternatives retain production placement or seed owner-work-area DIP bounds before native creation; none is enabled in the app. Show and constructor/setup are separated; ready/render/idle and UI-thread bytes are proxies, not physical presentation/peak memory. Modal ShowDialog is measured to readiness, not return-to-close as a Show duration. Restore and remaximize checks are part of behavior, outside opening measurements. Fit withinWork records the existing service outcome including failures. No OS display settings are changed; unavailable mixed-DPI/smaller-work-area/hardware cases remain unqualified. A geometry match on these monitors is not a universal guarantee. Forced GC is only a retention spot check."
            }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"PASS: {references.Count} synthetic child windows; owned/modal, restore/remaximize, current-display and fit checks captured. Candidate parity is recorded, not assumed.");
        }
        finally { LanguageService.SetLanguage(previousLanguage); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<Observation> Observe(string root, string output, Window owner, List<Monitor> monitors,
        string strategy, int trial, bool modal, List<WeakReference> references)
    {
        owner.Activate(); ((Button)owner.Content).Focus(); await Idle();
        var ownerHandle = new WindowInteropHelper(owner).Handle;
        var target = monitors.FindIndex(item => item.Handle == MonitorFromWindow(ownerHandle, 2));
        var ownerState = owner.WindowState.ToString();
        var project = RandomSettingsChecks.Fixture(root, 553, 12); var settings = JsonSerializer.Serialize(project.Settings);
        var capture = Path.Combine(output, "events", $"{target}-{ownerState}-{strategy}-{trial}-{modal}");
        var session = PerformanceDiagnostics.Start(capture, owner.Dispatcher);
        var bytes = GC.GetAllocatedBytesForCurrentThread(); var begin = Stopwatch.GetTimestamp();
        RandomPlayerSettingsWindow window;
        using (PerformanceDiagnostics.BeginCommand("placement-analysis"))
            window = new RandomPlayerSettingsWindow(project, null, RandomSettingsPreparation.Shared, strategy != "early-work-native") { Owner = owner, ShowInTaskbar = false };
        references.Add(new WeakReference(window));
        var constructorMs = Elapsed(begin); var setup = Stopwatch.GetTimestamp();
        if (strategy is "early-work-retain" or "early-work-native") SeedWork(window, owner, monitors[target]);
        if (strategy != "baseline") window.WindowState = WindowState.Maximized;
        var setupMs = Elapsed(setup); var sizeChanges = 0; window.SizeChanged += (_, _) => sizeChanges++;
        double? showMs = null; double readyMs = 0; long callerBytes = 0;
        Frame? maximized = null, restored = null, remaximized = null;
        var disabled = false; var focusInside = false; Exception? callbackError = null;
        async Task Inspect()
        {
            Check(await window.Initialization.WaitAsync(TimeSpan.FromSeconds(20)), "Owned editor prepares");
            await Rendering(window); await Idle();
            readyMs = Elapsed(begin); callerBytes = GC.GetAllocatedBytesForCurrentThread() - bytes;
            maximized = Capture(window, monitors);
            Check(maximized.State == "Maximized" && ReferenceEquals(window.Owner, owner) && GetWindow(new WindowInteropHelper(window).Handle, 4) == ownerHandle,
                "Native and WPF owner association retained");
            disabled = modal && !IsWindowEnabled(ownerHandle);
            if (modal) Check(disabled, "ShowDialog disables the native owner");
            var songs = Children(window).OfType<VirtualizingSongItemsControl>().Single();
            Check(songs.Items.Count == 40 && songs.Panel!.RealizedCount > 0 && songs.Panel.RealizedCount <= 40,
                "Owned editor displays complete first-deck data with real bounded cards");
            var search = (TextBox)window.FindName("SearchBox"); search.Focus();
            focusInside = window.IsKeyboardFocusWithin && ReferenceEquals(Keyboard.FocusedElement, search);
            Check(focusInside, "Child search accepts actual keyboard focus");
            window.WindowState = WindowState.Normal; await Rendering(window); await Idle(); restored = Capture(window, monitors);
            window.WindowState = WindowState.Maximized; await Rendering(window); await Idle(); remaximized = Capture(window, monitors);
            Check(Equivalent(maximized, remaximized), "Remaximizing preserves the candidate's own initial geometry/restore state");
            Check(JsonSerializer.Serialize(project.Settings) == settings, "Analysis/Cancel preserves source settings");
        }
        try
        {
            if (modal)
            {
                _ = window.Dispatcher.BeginInvoke(new Action(async () => {
                    try { await Inspect(); }
                    catch (Exception error) { callbackError = error; }
                    finally { window.Close(); }
                }), DispatcherPriority.ContextIdle);
                Check(window.ShowDialog() == false, "Closing modal editor follows Cancel");
                if (callbackError is not null) throw callbackError;
                Check(maximized is not null && restored is not null && remaximized is not null, "Modal observation completed before close");
            }
            else { var stamp = Stopwatch.GetTimestamp(); window.Show(); showMs = Elapsed(stamp); await Inspect(); window.Close(); }
            await Idle();
            var enabled = IsWindowEnabled(ownerHandle); Check(enabled, "Owner remains enabled after child closes");
            var ownerForeground = GetForegroundWindow() == ownerHandle;
            await session.DisposeAsync();
            Check(session.Failure is null && session.DroppedEvents == 0, "Complete placement capture");
            var events = File.ReadLines(Path.Combine(capture, "events.jsonl")).Select(line => JsonSerializer.Deserialize<JsonElement>(line))
                .Where(item => item.TryGetProperty("stage", out var stage) && stage.GetString()!.StartsWith("WindowPlacement", StringComparison.Ordinal)).Cast<object>().ToArray();
            return new Observation(strategy, target, ownerState, modal, trial, constructorMs, setupMs, showMs, readyMs, callerBytes,
                maximized!, restored!, remaximized!, disabled, enabled, focusInside, ownerForeground, sizeChanges, events);
        }
        finally { window.Close(); await Idle(); await session.DisposeAsync(); }
    }

    private static async Task<object> Fit(Window owner, List<Monitor> monitors, List<WeakReference> references, bool oversized)
    {
        var window = new Window { Owner = owner, Width = oversized ? 4000 : 1000, Height = oversized ? 3000 : 700,
            MinWidth = oversized ? 2500 : 700, MinHeight = oversized ? 2000 : 400, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false, Content = new Button { Content = "Fit fixture" } };
        WindowPlacementService.FitToOwnerMonitor(window); references.Add(new WeakReference(window));
        try
        {
            window.Show(); await Rendering(window); await Idle();
            var frame = Capture(window, monitors); var target = monitors.FindIndex(item => item.Handle == MonitorFromWindow(new WindowInteropHelper(owner).Handle, 2));
            var work = monitors[target].Work;
            Check(frame.State == "Normal" && frame.Monitor == target, "Existing Fit preserves normal state and owner's monitor");
            // Record the baseline's actual fit. In particular, MinWidth/MinHeight
            // may force a larger native rectangle than SetWindowPos requested.
            var withinWork = frame.OuterPixels.X >= work.X && frame.OuterPixels.Y >= work.Y &&
                frame.OuterPixels.X + frame.OuterPixels.Width <= work.X + work.Width + 1 && frame.OuterPixels.Y + frame.OuterPixels.Height <= work.Y + work.Height + 1;
            return new { ownerState = owner.WindowState.ToString(), oversized, withinWork, work, frame };
        }
        finally { window.Close(); await Idle(); }
    }
    private static bool Equivalent(Frame first, Frame second) => first.State == second.State && first.Monitor == second.Monitor &&
        first.Dpi == second.Dpi && first.MinWidth == second.MinWidth && first.MinHeight == second.MinHeight &&
        first.OuterPixels.Near(second.OuterPixels) && first.RestoreDip.Near(second.RestoreDip) && first.NativeRestore.Near(second.NativeRestore);
    private static void SeedWork(Window window, Window owner, Monitor monitor)
    {
        var from = PresentationSource.FromVisual(owner)!.CompositionTarget!.TransformFromDevice;
        var topLeft = from.Transform(new Point(monitor.Work.X, monitor.Work.Y));
        var bottomRight = from.Transform(new Point(monitor.Work.X + monitor.Work.Width, monitor.Work.Y + monitor.Work.Height));
        var width = Math.Max(320, bottomRight.X - topLeft.X); var height = Math.Max(220, bottomRight.Y - topLeft.Y);
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = topLeft.X; window.Top = topLeft.Y; window.Width = width; window.Height = height;
        window.MinWidth = Math.Min(window.MinWidth, width); window.MinHeight = Math.Min(window.MinHeight, height);
    }
    private static void MoveOwner(Window owner, Monitor monitor)
    {
        var work = monitor.Work; var width = Math.Min(800, (int)work.Width - 40); var height = Math.Min(600, (int)work.Height - 40);
        Check(SetWindowPos(new WindowInteropHelper(owner).Handle, IntPtr.Zero, (int)work.X + 20, (int)work.Y + 20, width, height, 0x0014), "Move synthetic owner");
    }
    private static Frame Capture(Window window, List<Monitor> monitors)
    {
        var handle = new WindowInteropHelper(window).Handle; Check(GetWindowRect(handle, out var rect), "Read outer bounds");
        var placement = new Placement { Length = Marshal.SizeOf<Placement>() }; Check(GetWindowPlacement(handle, ref placement), "Read native restore bounds");
        return new Frame(window.WindowState.ToString(), monitors.FindIndex(item => item.Handle == MonitorFromWindow(handle, 2)), VisualTreeHelper.GetDpi(window).PixelsPerInchX,
            Box.From(rect), Box.From(window.RestoreBounds), Box.From(placement.Normal), window.MinWidth, window.MinHeight);
    }
    private static List<Monitor> Monitors()
    {
        var result = new List<Monitor>();
        MonitorCallback callback = (IntPtr handle, IntPtr hdc, ref NativeRect rect, IntPtr data) => {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() }; Check(GetMonitorInfo(handle, ref info), "Read available work area");
            result.Add(new Monitor(handle, Box.From(info.Work), Box.From(info.Bounds))); return true;
        };
        Check(EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero), "Enumerate available monitors"); return result;
    }
    private static async Task Rendering(Window window)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler handler = (_, _) => done.TrySetResult(); CompositionTarget.Rendering += handler;
        try { window.InvalidateVisual(); await done.Task.WaitAsync(TimeSpan.FromSeconds(20)); }
        finally { CompositionTarget.Rendering -= handler; }
    }
    private static IEnumerable<DependencyObject> Children(DependencyObject root)
    {
        yield return root;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) foreach (var child in Children(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static async Task Idle() => await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    private static double Elapsed(long stamp) => Stopwatch.GetElapsedTime(stamp).TotalMilliseconds;
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public NativeRect Bounds, Work; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct Placement { public int Length, Flags, Show; public NativePoint Minimum, Maximum; public NativeRect Normal; }
    private delegate bool MonitorCallback(IntPtr monitor, IntPtr hdc, ref NativeRect rect, IntPtr data);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorCallback callback, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(IntPtr handle, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr handle, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr handle, out NativeRect rect);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowPlacement(IntPtr handle, ref Placement placement);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(IntPtr handle, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowEnabled(IntPtr handle);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr handle, uint command);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
}
