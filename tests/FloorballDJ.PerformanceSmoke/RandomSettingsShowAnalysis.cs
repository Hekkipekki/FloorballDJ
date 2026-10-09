using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using FloorballDJ.Controls;
using FloorballDJ.Services;
using FloorballDJ.Views;

// Opt-in analysis only. Counterfactuals identify work, not replacement UX.
internal static class RandomSettingsShowAnalysis
{
    private sealed record Case(string Name, bool Shell = false, bool Placement = true,
        bool EnsureHandle = false, bool PrepareFirst = false, bool Icon = true, bool Fluent = true);
    private sealed class Translation
    {
        internal int LoadedCalls, TreeCalls;
        internal double LoadedMs, TreeMs;
    }
    private static Translation? _translation;

    internal static async Task RunAsync(string root, string? output, string? caseName, int trials)
    {
        output ??= Path.Combine(root, "show-analysis"); Directory.CreateDirectory(output);
        var previousLanguage = LanguageService.CurrentLanguage; LanguageService.SetLanguage("en");
        EventManager.RegisterClassHandler(typeof(FrameworkElement), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) => {
                if (_translation is not { } metrics || sender is not FrameworkElement element) return;
                var begin = Stopwatch.GetTimestamp(); LanguageService.TranslateElement(element);
                metrics.LoadedCalls++; metrics.LoadedMs += Stopwatch.GetElapsedTime(begin).TotalMilliseconds;
            }), true);
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) => {
                if (sender is not Window window) return;
                window.Dispatcher.BeginInvoke(() => {
                    if (_translation is not { } metrics) return;
                    var begin = Stopwatch.GetTimestamp(); LanguageService.TranslateTree(window);
                    metrics.TreeCalls++; metrics.TreeMs += Stopwatch.GetElapsedTime(begin).TotalMilliseconds;
                }, DispatcherPriority.Loaded);
            }), true);
        var cases = new[] {
            new Case("normal"), new Case("ensure-handle", EnsureHandle: true),
            new Case("prepare-before-show", PrepareFirst: true),
            new Case("native-max-no-placement", Placement: false), new Case("no-icon", Icon: false),
            new Case("shell", Shell: true), new Case("shell-no-placement", Shell: true, Placement: false),
            new Case("shell-no-fluent", Shell: true, Fluent: false)
        };
        if (caseName is not null) cases = cases.Where(item => item.Name == caseName).ToArray();
        Check(cases.Length > 0 && trials is > 0 and <= 20, "Valid show case/trial count");
        var windows = new List<WeakReference>(); var observations = new List<object>();
        try
        {
            var cold = await Observe(root, output, cases[0], -1, windows);
            var random = new Random(42);
            for (var trial = 0; trial < trials; trial++)
                foreach (var item in cases.OrderBy(_ => random.Next()))
                {
                    Console.WriteLine($"Show analysis {item.Name} {trial + 1}/{trials}");
                    observations.Add(await Observe(root, output, item, trial, windows));
                }
            _translation = null;
            await Idle(); GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); await Idle();
            File.WriteAllText(Path.Combine(output, "show-analysis.json"), JsonSerializer.Serialize(new {
                schemaVersion = 1, version = "p4.4-show-analysis-v1", checkedUtc = DateTimeOffset.UtcNow,
                runtime = Environment.Version.ToString(), processorCount = Environment.ProcessorCount,
                trialsPerCase = trials, coldProcessObservation = cold, observations,
                closedWindowCount = windows.Count, survivingClosedWindows = windows.Count(item => item.IsAlive),
                limitation = "Shown activating synthetic 553-file/12-group desktop fixture (40 songs in selected deck), production resources and mirrored language handlers; diagnostics enabled equally across variants. No owner, production main-view polling/audio. SourceInitialized observer runs after placement (which can raise Loaded reentrantly); placement spans are nested, not additive. EnsureHandle/PrepareFirst shift work earlier; compare total opening, not Show alone. No-placement, no-icon, shell and no-Fluent variants alter behavior/appearance and are experimental only. Icon removal occurs after constructor; shell-no-Fluent still retains application-level merged resources. UI-thread allocation and WPF events/rendering/idle are proxies, not process peak memory, physical presentation or ProBook/onset qualification. Forced GC is a retention spot check."
            }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"PASS: {windows.Count} shown windows, source/placement/resource/search measurements and populated-view/state invariants.");
        }
        finally { _translation = null; LanguageService.SetLanguage(previousLanguage); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<object> Observe(string root, string output, Case item, int trial, List<WeakReference> windows)
    {
        var project = item.Shell ? null : RandomSettingsChecks.Fixture(root, 553, 12);
        var settingsBefore = project is null ? null : JsonSerializer.Serialize(project.Settings);
        var capture = Path.Combine(output, "events", item.Name + "-" + trial);
        var session = PerformanceDiagnostics.Start(capture, Dispatcher.CurrentDispatcher);
        var translation = new Translation(); _translation = translation;
        var bytes = GC.GetAllocatedBytesForCurrentThread(); var begin = Stopwatch.GetTimestamp();
        Window window;
        using (PerformanceDiagnostics.BeginCommand("show-analysis"))
        {
            if (item.Shell)
            {
                window = new Window { Title = "Analysis shell", Width = 1460, Height = 860, MinWidth = 1040, MinHeight = 680,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    Content = new TextBlock { Text = "Analysis shell: editor omitted", Margin = new Thickness(22) } };
                if (item.Placement) WindowPlacementService.MaximizeOnOwnerMonitor(window);
            }
            else window = new RandomPlayerSettingsWindow(project!, null, RandomSettingsPreparation.Shared, item.Placement);
        }
        // EnsureHandle runs placement before Show. WPF requires activation when
        // showing an already maximized window; keep activation equal in all cases.
        window.ShowActivated = true; window.ShowInTaskbar = false;
        if (!item.Placement) window.WindowState = WindowState.Maximized;
        if (!item.Icon) window.Icon = null;
#pragma warning disable WPF0001
        if (!item.Fluent) window.ThemeMode = ThemeMode.None;
#pragma warning restore WPF0001
        windows.Add(new WeakReference(window));
        var constructorMs = Elapsed(begin);
        double? sourceMs = null, loadedMs = null, contentMs = null;
        bool? firstContentReady = null;
        var sizes = new List<object>(); var windowsMessages = new Dictionary<int, int>();
        HwndSource? source = null;
        HwndSourceHook hook = (IntPtr handle, int message, IntPtr wParam, IntPtr lParam, ref bool handled) => {
            if (message is 0x0005 or 0x0046 or 0x0047)
                windowsMessages[message] = windowsMessages.GetValueOrDefault(message) + 1;
            return IntPtr.Zero;
        };
        window.SourceInitialized += (_, _) => {
            sourceMs ??= Elapsed(begin);
            source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle); source?.AddHook(hook);
        };
        window.Loaded += (_, _) => loadedMs ??= Elapsed(begin);
        window.ContentRendered += (_, _) => { contentMs ??= Elapsed(begin); firstContentReady ??= window is RandomPlayerSettingsWindow editor && editor.ViewData.IsReady; };
        window.SizeChanged += (_, args) => sizes.Add(new { elapsedMs = Elapsed(begin), width = args.NewSize.Width, height = args.NewSize.Height });
        double ensureMs = 0, prepareMs = 0;
        var operations = new List<object>(); var inProgress = new Dictionary<DispatcherOperation, long>();
        DispatcherHookEventHandler start = (_, args) => inProgress[args.Operation] = Stopwatch.GetTimestamp();
        DispatcherHookEventHandler finish = (_, args) => {
            if (!inProgress.Remove(args.Operation, out var timestamp)) return;
            var durationMs = Elapsed(timestamp);
            if (durationMs >= 1) operations.Add(new { priority = args.Operation.Priority.ToString(), durationMs });
        };
        window.Dispatcher.Hooks.OperationStarted += start; window.Dispatcher.Hooks.OperationCompleted += finish;
        try
        {
            if (item.PrepareFirst && window is RandomPlayerSettingsWindow prepared)
            {
                var stamp = Stopwatch.GetTimestamp(); Check(await prepared.Initialization.WaitAsync(TimeSpan.FromSeconds(20)), "Preparation before Show"); prepareMs = Elapsed(stamp);
            }
            if (item.EnsureHandle) { var stamp = Stopwatch.GetTimestamp(); new WindowInteropHelper(window).EnsureHandle(); ensureMs = Elapsed(stamp); }
            var showBegin = Stopwatch.GetTimestamp(); window.Show(); var showMs = Elapsed(showBegin);
            if (window is RandomPlayerSettingsWindow editor) Check(await editor.Initialization.WaitAsync(TimeSpan.FromSeconds(20)), "Prepared shown editor");
            await NextRendering(window); await Idle();
            var stableMs = Elapsed(begin); var callerBytes = GC.GetAllocatedBytesForCurrentThread() - bytes;
            var openingTranslation = new { translation.LoadedCalls, translation.LoadedMs, translation.TreeCalls, translation.TreeMs };
            var openingOperations = operations.ToArray();
            operations.Clear();
            var phases = new List<object>();
            if (window is RandomPlayerSettingsWindow actual)
            {
                var tabs = (TabControl)actual.FindName("DeckTabs");
                Check(ReferenceEquals(tabs.SelectedContent, actual.ViewData.SelectedProfile!.Decks[0]), "Shown first deck populated");
                var songs = Children(actual).OfType<VirtualizingSongItemsControl>().Single();
                CheckCards(songs, 40);
                var search = (TextBox)actual.FindName("SearchBox");
                phases.Add(await Phase("search-single", () => search.Text = "lat 39", window, songs)); CheckCards(songs, 1);
                phases.Add(await Phase("search-equivalent", () => search.Text = " LAT 39 ", window, songs)); CheckCards(songs, 1);
                phases.Add(await Phase("search-clear", search.Clear, window, songs)); CheckCards(songs, 40);
                phases.Add(await Phase("search-empty-unchanged", () => Call(actual, "ApplySearch", ""), window, songs)); CheckCards(songs, 40);
                var selected = actual.ViewData.SelectedProfile;
                phases.Add(await Phase("group-switch", () => actual.ViewData.SelectedProfile = actual.ViewData.Profiles[1], window, songs));
                var currentSongs = Children(actual).OfType<VirtualizingSongItemsControl>().Single(); CheckCards(currentSongs, 40);
                phases.Add(await Phase("group-return", () => actual.ViewData.SelectedProfile = selected, window, currentSongs));
                CheckCards(Children(actual).OfType<VirtualizingSongItemsControl>().Single(), 40);
                Check(JsonSerializer.Serialize(project!.Settings) == settingsBefore, "Analysis and Cancel preserve original settings");
            }
            source?.RemoveHook(hook);
            var geometry = new { window.ActualWidth, window.ActualHeight, state = window.WindowState.ToString(), dpi = VisualTreeHelper.GetDpi(window).PixelsPerInchX };
            window.Close(); await Idle(); await session.DisposeAsync();
            var events = File.ReadLines(Path.Combine(capture, "events.jsonl")).Select(line => JsonSerializer.Deserialize<JsonElement>(line))
                .Where(record => record.TryGetProperty("type", out var type) && type.GetString() == "event" &&
                    record.GetProperty("stage").GetString()!.StartsWith("WindowPlacement", StringComparison.Ordinal)).ToArray();
            Check(session.Failure is null && session.DroppedEvents == 0, "Complete placement diagnostics");
            return new { item.Name, item.Shell, item.Placement, item.EnsureHandle, item.PrepareFirst, item.Icon, item.Fluent, trial,
                constructorMs, ensureMs, prepareMs, synchronousShowMs = showMs, sourceInitializedMs = sourceMs, loadedMs,
                firstContentRenderedMs = contentMs, firstContentReady, openingStableIdleMs = stableMs, callerBytes,
                translation = openingTranslation, geometry, sizes, postSourceWindowMessages = windowsMessages, placementEvents = events, openingOperations, laterOperations = operations, phases };
        }
        finally
        {
            source?.RemoveHook(hook); window.Close(); await Idle(); await session.DisposeAsync();
            window.Dispatcher.Hooks.OperationStarted -= start; window.Dispatcher.Hooks.OperationCompleted -= finish;
            _translation = null;
        }
    }

    private static async Task<object> Phase(string name, Action action, Window window, VirtualizingSongItemsControl before)
    {
        var work = before.Panel!.Work; var bytes = GC.GetAllocatedBytesForCurrentThread(); var begin = Stopwatch.GetTimestamp();
        action(); var synchronousMs = Elapsed(begin);
        await NextRendering(window); await Idle();
        var songs = Children(window).OfType<VirtualizingSongItemsControl>().Single(); var panel = songs.Panel!;
        return new { name, synchronousMs, renderingAndIdleMs = Elapsed(begin), callerBytes = GC.GetAllocatedBytesForCurrentThread() - bytes,
            sameControl = ReferenceEquals(before, songs), items = songs.Items.Count, panel.RealizedCount,
            work = new { panel.Work.Resets, panel.Work.Recycled, panel.Work.NewContainers },
            resetDelta = ReferenceEquals(before, songs) ? panel.Work.Resets - work.Resets : (int?)null,
            newContainerDelta = ReferenceEquals(before, songs) ? panel.Work.NewContainers - work.NewContainers : (int?)null };
    }
    private static void CheckCards(VirtualizingSongItemsControl songs, int count)
    {
        var panel = songs.Panel!; var checks = Children(songs).OfType<CheckBox>().ToArray();
        var bound = ((int)Math.Ceiling(panel.ViewportHeight / panel.CardHeight) + 3) * panel.Columns + 1;
        Check(songs.Items.Count == count && checks.Length == panel.RealizedCount && checks.Length > 0 && checks.Length <= Math.Min(count, bound), "Complete filtered data with bounded nonempty card templates");
        var first = (int)Math.Floor(panel.VerticalOffset / panel.CardHeight) * panel.Columns;
        var end = Math.Min(count, (int)Math.Ceiling((panel.VerticalOffset + panel.ViewportHeight) / panel.CardHeight) * panel.Columns);
        for (var i = first; i < end; i++) Check(checks.Any(check => ReferenceEquals(check.DataContext, songs.Items[i])), "Every viewport item is present");
    }
    private static async Task NextRendering(Window window)
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
    private static void Call(Window window, string name, params object?[] args) => window.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, args);
    private static double Elapsed(long stamp) => Stopwatch.GetElapsedTime(stamp).TotalMilliseconds;
    private static async Task Idle() => await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
