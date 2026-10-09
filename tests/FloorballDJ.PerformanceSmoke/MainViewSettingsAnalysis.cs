using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using FloorballDJ.Controls;
using FloorballDJ.Services;
using FloorballDJ.ViewModels;
using FloorballDJ.Views;
using NAudio.Wave;

internal static class MainViewSettingsAnalysis
{
    internal static async Task CheckStartupAsync(string root)
    {
        var previousDirectory = Environment.GetEnvironmentVariable("FLOORBALLDJ_DATA_DIR");
        var previousMain = Application.Current.MainWindow;
        var directory = Path.Combine(root, "main-startup-regression"); Directory.CreateDirectory(directory);
        Environment.SetEnvironmentVariable("FLOORBALLDJ_DATA_DIR", directory);
        MainWindow? main = null; var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var project = RandomSettingsChecks.Fixture(directory, 85, 6); var projects = new ProjectService();
            await projects.SaveAsync(project, projects.DefaultProjectPath); var bytes = File.ReadAllBytes(projects.DefaultProjectPath);
            var licensing = new LicenseService();
            main = new MainWindow(licensing, shutdownApplicationOnClose: false) { ShowInTaskbar = false };
            main.Closed += (_, _) => closed.TrySetResult(); Application.Current.MainWindow = main; main.Show();
            await Until(() => main.StartupReady); await Render(main); await Idle();
            var vm = (MainViewModel)main.DataContext;
            Check(vm.Decks.Select(deck => deck.Id).SequenceEqual(project.Decks.Select(deck => deck.Id)) &&
                File.ReadAllBytes(projects.DefaultProjectPath).SequenceEqual(bytes), "Startup binding preserves existing autosave and loads original deck identities");
            vm.Decks[0].Jingles[0].SessionPlayCount = 3;
            var toggle = (System.Windows.Controls.Primitives.ToggleButton)main.FindName("SessionToggle");
            toggle.IsChecked = false; await vm.FlushSavesAsync();
            Check(vm.Decks.SelectMany(deck => deck.Jingles).All(jingle => jingle.SessionPlayCount == 0) &&
                !(await projects.LoadAsync(projects.DefaultProjectPath)).Settings.TrackSession, "Real session disable retains count reset and persisted flag");
            toggle.IsChecked = true; await vm.FlushSavesAsync();
            Check((await projects.LoadAsync(projects.DefaultProjectPath)).Settings.TrackSession && licensing.Current.Kind.ToString() == "None", "Real enable persists; licensing stays untouched");
            main.Close(); await closed.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Console.WriteLine("PASS: actual main startup preserves autosave; real session-toggle reset/save and main save/dispose/close lifecycle.");
        }
        finally
        {
            if (main is { IsVisible: true }) { main.Close(); await closed.Task.WaitAsync(TimeSpan.FromSeconds(20)); }
            Application.Current.MainWindow = previousMain;
            Environment.SetEnvironmentVariable("FLOORBALLDJ_DATA_DIR", previousDirectory);
        }
    }

    private sealed class QueueSampler : IDisposable
    {
        private readonly Dispatcher _dispatcher;
        private readonly System.Threading.Timer _timer;
        private int _pending, _stopped;
        internal List<double> Delays { get; } = [];
        internal QueueSampler(Dispatcher dispatcher)
        {
            _dispatcher = dispatcher;
            _timer = new System.Threading.Timer(_ => {
                if (Volatile.Read(ref _stopped) != 0 || Interlocked.CompareExchange(ref _pending, 1, 0) != 0) return;
                var begin = Stopwatch.GetTimestamp();
                _dispatcher.BeginInvoke(() => {
                    if (Volatile.Read(ref _stopped) == 0) Delays.Add(Elapsed(begin));
                    Interlocked.Exchange(ref _pending, 0);
                }, DispatcherPriority.Normal);
            }, null, 0, 10);
        }
        public void Dispose() { Interlocked.Exchange(ref _stopped, 1); _timer.Dispose(); }
    }

    internal static async Task RunAsync(string root, string? output, int trials, bool interactions = false)
    {
        Check(trials is > 0 and <= 20, "Valid main-view trial count");
        output ??= Path.Combine(root, "main-view-analysis"); Directory.CreateDirectory(output);
        var language = LanguageService.CurrentLanguage; LanguageService.SetLanguage("en");
        EventManager.RegisterClassHandler(typeof(FrameworkElement), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) => { if (sender is FrameworkElement element) LanguageService.TranslateElement(element); }), true);
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) => { if (sender is Window window) window.Dispatcher.BeginInvoke(() => LanguageService.TranslateTree(window), DispatcherPriority.Loaded); }), true);
        EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent,
            new RoutedEventHandler((sender, _) => { if (sender is ContextMenu menu) LanguageService.TranslateTree(menu); }), true);
        EventManager.RegisterClassHandler(typeof(MenuItem), MenuItem.SubmenuOpenedEvent,
            new RoutedEventHandler((sender, _) => { if (sender is MenuItem item) LanguageService.TranslateTree(item); }), true);
        var project = RandomSettingsChecks.Fixture(root, 553, 12);
        var media = Path.Combine(root, "main-view-silent.wav");
        using (var writer = new WaveFileWriter(media, new WaveFormat(48000, 16, 2)))
        {
            var silence = new byte[48000 * 4];
            for (var second = 0; second < 90; second++) writer.Write(silence, 0, silence.Length);
        }
        foreach (var jingle in project.Decks[0].Jingles.Take(2))
        {
            jingle.FilePath = media; jingle.DurationSeconds = 90; jingle.StartSeconds = 0; jingle.EndSeconds = 90; jingle.FadeInOverrideSeconds = 0;
        }
        project.Settings.MasterVolumeDb = -60;
        using (var devices = new NAudio.CoreAudioApi.MMDeviceEnumerator())
        using (var endpoint = devices.GetDefaultAudioEndpoint(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.Role.Multimedia))
        {
            project.Settings.OutputDeviceId = endpoint.ID;
            project.Settings.SecondaryOutputDeviceId = endpoint.ID;
        }
        var projects = new ProjectService(); await projects.SaveAsync(project, projects.DefaultProjectPath);
        var savedBeforeStartup = File.ReadAllBytes(projects.DefaultProjectPath);
        MainWindow? main = null; var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var licensing = new LicenseService();
        var observations = new List<object>(); var references = new List<WeakReference>();
        try
        {
            var startupCapture = Path.Combine(output, "startup");
            await using (var startupSession = PerformanceDiagnostics.Start(startupCapture, Dispatcher.CurrentDispatcher))
            {
                // LicenseService is not evaluated or mutated. App.OnStartup's
                // production license gate is outside this isolated view fixture.
                main = new MainWindow(licensing, shutdownApplicationOnClose: false) { ShowInTaskbar = false };
                main.Closed += (_, _) => closed.TrySetResult();
                Application.Current.MainWindow = main; main.Show();
                await Until(() => main.StartupReady);
                Check(((MainViewModel)main.DataContext).Project.Decks.Select(deck => deck.Id).SequenceEqual(project.Decks.Select(deck => deck.Id)),
                    "Actual startup loads only the synthetic profile");
                await Render(main); await Idle();
                Check(File.ReadAllBytes(projects.DefaultProjectPath).SequenceEqual(savedBeforeStartup), "Initial binding never writes over the profile before/after load");
                Check(startupSession.Failure is null, "Startup capture writer healthy");
            }
            var vm = (MainViewModel)main.DataContext;
            vm.PreviewVolumeDb = -60;
            var first = await Observe(main, vm, output, false, -1, references, interactions);
            var random = new Random(42);
            for (var trial = 0; trial < trials; trial++)
                foreach (var active in new[] { false, true }.OrderBy(_ => random.Next()))
                {
                    Console.WriteLine($"Main-view modal analysis {(active ? "main+preview" : "idle")} {trial + 1}/{trials}");
                    observations.Add(await Observe(main, vm, output, active, trial, references, interactions));
                }
            vm.Audio.StopAll(); await Idle();
            var sessionToggle = (System.Windows.Controls.Primitives.ToggleButton)main.FindName("SessionToggle");
            Check(vm.Settings.TrackSession && vm.Decks.SelectMany(deck => deck.Jingles).Any(jingle => jingle.SessionPlayCount > 0), "Active trials exercise session tracking");
            sessionToggle.IsChecked = false; await vm.FlushSavesAsync();
            Check(!vm.Settings.TrackSession && vm.Decks.SelectMany(deck => deck.Jingles).All(jingle => jingle.SessionPlayCount == 0), "Genuine disable still resets counts and saves");
            Check(!(await projects.LoadAsync(projects.DefaultProjectPath)).Settings.TrackSession, "Disabled session setting reaches disk");
            sessionToggle.IsChecked = true; await vm.FlushSavesAsync();
            Check(vm.Settings.TrackSession && (await projects.LoadAsync(projects.DefaultProjectPath)).Settings.TrackSession, "Genuine enable still saves");
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); await Idle();
            var surviving = references.Count(item => item.IsAlive);
            var licenseUntouched = licensing.Current.Kind.ToString();
            Check(licenseUntouched == "None", "Fixture never evaluates, activates or alters licensing");
            main.Close(); await closed.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Check(File.Exists(projects.DefaultProjectPath), "Real main-window save/close lifecycle completes in isolated storage");
            File.WriteAllText(Path.Combine(output, "main-view-analysis.json"), JsonSerializer.Serialize(new {
                schemaVersion = 1, version = "p4.8-main-view-analysis-v1", checkedUtc = DateTimeOffset.UtcNow,
                runtime = Environment.Version.ToString(), processorCount = Environment.ProcessorCount, trialsPerCase = trials,
                interactionAnalysis = interactions ? "p4.11-template-phases-v1" : null,
                firstEditorAfterMainStartup = first, observations, closedEditors = references.Count, survivingClosedEditors = surviving,
                actualMainWindowClosed = !main.IsVisible, licenseEvaluation = licenseUntouched,
                startupProfilePreserved = true, realSessionToggleSaveAndReset = true,
                limitation = "Actual MainWindow/MainViewModel, loaded synthetic 553-jingle/12-group profile, production timers/resources/menu handler and modal ShowDialog; no personal profile. Two tracks use a silent 90-second 48kHz/16-bit stereo WAV; other library paths are synthetic availability fixtures, not codec benchmarks. Legacy per-voice backend; currently default endpoint explicitly selected for both synthetic routes, real main+preview readers. Main position uses the published primary-output snapshot, preview position the secondary reader snapshot; this is not acoustic/glitch-free or physical-onset proof. App.OnStartup language selection/license evaluation is excluded; license remains unevaluated None, no license storage/network is touched. A test-only constructor lets the real save/dispose/close lifecycle finish without shutting down the diagnostic dispatcher; public application shutdown behavior is unchanged. Mirrored language/menu handlers; menu invoked programmatically, not an OS click/hotkey. WPF/idle and caller-thread bytes include probe/UI work and are not physical presentation/peak memory. Queue sampling is bounded to one outstanding normal-priority callback. Modal lifetime includes optional scripted interactions, hold and close; it is not opening delay. Interaction ready/idle includes synchronous work; phase probes report generated containers and control identity, not retained visual-tree memory. Foreground outcomes/activation returns are observations, not forced or qualified OS-input behavior. First editor follows actual-main startup, not cold App.OnStartup. Desktop/current DPI only; actual profile/ProBook/mixed-DPI/long-session qualification remains open. Forced GC is only a retention spot check."
            }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"PASS: {references.Count} real-main-view modal openings{(interactions ? $" and {references.Count * 6} deck/group interactions" : "")}; actual profile init/menu/timers, Cancel isolation, populated views, main+preview progress and main save/close checked. OS foreground is recorded separately.");
        }
        finally
        {
            foreach (var editor in Application.Current.Windows.OfType<RandomPlayerSettingsWindow>().ToArray()) editor.Close();
            if (main is { IsVisible: true }) { main.Close(); await closed.Task.WaitAsync(TimeSpan.FromSeconds(20)); }
            LanguageService.SetLanguage(language);
        }
    }

    private static async Task<object> Observe(MainWindow main, MainViewModel vm, string output, bool active, int trial, List<WeakReference> references, bool interactions)
    {
        vm.Audio.StopAll(); await Idle();
        var capture = Path.Combine(output, "events", (active ? "active" : "idle") + "-" + trial);
        var session = PerformanceDiagnostics.Start(capture, main.Dispatcher);
        RandomPlayerSettingsWindow? dialog = null; Exception? observerError = null;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var activated = main.Activate();
        var ownerHandle = new WindowInteropHelper(main).Handle;
        var foregroundBefore = Foreground(ownerHandle, IntPtr.Zero);
        var settings = JsonSerializer.Serialize(vm.Settings);
        var diskBefore = File.ReadAllBytes(vm.CurrentProjectPath);
        var notifications = 0; var previewNotifications = 0;
        PropertyChangedEventHandler changed = (_, args) => {
            if (args.PropertyName == nameof(MainViewModel.NowPlaying)) notifications++;
            if (args.PropertyName == nameof(MainViewModel.PreviewPlaying)) previewNotifications++;
        };
        vm.PropertyChanged += changed;
        using var sampler = new QueueSampler(main.Dispatcher);
        var operations = new List<object>(); var starts = new Dictionary<DispatcherOperation, long>();
        DispatcherHookEventHandler operationStart = (_, args) => starts[args.Operation] = Stopwatch.GetTimestamp();
        DispatcherHookEventHandler operationFinish = (_, args) => {
            if (starts.Remove(args.Operation, out var timestamp) && Elapsed(timestamp) >= 1)
                operations.Add(new { priority = args.Operation.Priority.ToString(), durationMs = Elapsed(timestamp) });
        };
        long begin = 0, bytes = 0; double readyMs = 0; long openingBytes = 0;
        object? readyForeground = null, duringForeground = null, beforeCloseForeground = null;
        double? primaryBefore = null, previewBefore = null, primaryAfter = null, previewAfter = null;
        bool ownerDisabled = false, wpfFocus = false; int realized = 0, openingNotifications = 0, openingPreviewNotifications = 0;
        object[] openingOperations = []; double[] openingDelays = [];
        object? baselineDelays = null;
        var phaseResults = new List<object>();
        try
        {
            if (active)
            {
                vm.Play(vm.Project.Decks[0].Jingles[0]);
                vm.SetSecondaryOutput(true); vm.PlayPreview(vm.Project.Decks[0].Jingles[1]); vm.SetSecondaryOutput(false);
                await Until(() => vm.NowPlaying.Position.TotalSeconds > .1 && vm.Audio.GetSecondarySnapshot().Position.TotalSeconds > .1);
            }
            await Task.Delay(200); await Idle();
            baselineDelays = Stats(sampler.Delays); sampler.Delays.Clear(); notifications = 0; previewNotifications = 0;
            primaryBefore = active ? vm.NowPlaying.Position.TotalSeconds : null; previewBefore = vm.Audio.GetSecondarySnapshot().Position.TotalSeconds;
            main.Dispatcher.Hooks.OperationStarted += operationStart; main.Dispatcher.Hooks.OperationCompleted += operationFinish;
            _ = main.Dispatcher.BeginInvoke(new Action(async () => {
                try
                {
                    dialog = Application.Current.Windows.OfType<RandomPlayerSettingsWindow>().Single(); references.Add(new WeakReference(dialog));
                    var handle = new WindowInteropHelper(dialog).Handle;
                    duringForeground = Foreground(ownerHandle, handle);
                    ownerDisabled = !IsWindowEnabled(ownerHandle); Check(ownerDisabled, "Real modal menu disables the native main window");
                    Check(await dialog.Initialization.WaitAsync(TimeSpan.FromSeconds(20)), "Actual menu dialog prepares");
                    await Render(dialog); await Idle();
                    readyMs = Elapsed(begin); openingBytes = GC.GetAllocatedBytesForCurrentThread() - bytes;
                    AnalysisTraceMarkers.Log.Ready(trial, active);
                    readyForeground = Foreground(ownerHandle, handle);
                    openingDelays = sampler.Delays.ToArray(); openingOperations = operations.ToArray();
                    openingNotifications = notifications; openingPreviewNotifications = previewNotifications;
                    var songs = Children(dialog).OfType<VirtualizingSongItemsControl>().Single();
                    Check(songs.Items.Count == 40 && songs.Panel!.RealizedCount > 0 && songs.Panel.RealizedCount <= 40, "Real main-view route displays the complete bounded first deck");
                    realized = songs.Panel!.RealizedCount;
                    var search = (TextBox)dialog.FindName("SearchBox"); search.Focus();
                    wpfFocus = dialog.IsKeyboardFocusWithin; Check(wpfFocus, "Actual modal editor accepts WPF child focus");
                    if (interactions)
                    {
                        var tabs = (TabControl)dialog.FindName("DeckTabs");
                        var original = dialog.ViewData.SelectedProfile!; var originalDeck = original.Decks[0];
                        var other = dialog.ViewData.Profiles[1];
                        Check(!other.HasRealizedDecks, "First group probe begins with lazy data");
                        var openingControl = new WeakReference(songs);
                        phaseResults.Add(await Interaction("deck-switch", () => tabs.SelectedItem = original.Decks[1], () => original.Decks[1], dialog, active, trial, openingControl));
                        phaseResults.Add(await Interaction("deck-return", () => tabs.SelectedItem = originalDeck, () => originalDeck, dialog, active, trial, openingControl));
                        phaseResults.Add(await Interaction("group-first", () => dialog.ViewData.SelectedProfile = other, () => other.Decks[0], dialog, active, trial, openingControl));
                        phaseResults.Add(await Interaction("group-return", () => dialog.ViewData.SelectedProfile = original, () => originalDeck, dialog, active, trial, openingControl));
                        phaseResults.Add(await Interaction("group-warm", () => dialog.ViewData.SelectedProfile = other, () => other.Decks[0], dialog, active, trial, openingControl));
                        phaseResults.Add(await Interaction("group-return-warm", () => dialog.ViewData.SelectedProfile = original, () => originalDeck, dialog, active, trial, openingControl));
                    }
                    await Task.Delay(180); await Idle();
                    primaryAfter = active ? vm.NowPlaying.Position.TotalSeconds : null; previewAfter = vm.Audio.GetSecondarySnapshot().Position.TotalSeconds;
                    if (active) Check(primaryAfter > primaryBefore && previewAfter > previewBefore && notifications > 0 && previewNotifications > 0,
                        "Real main/preview readers and 50ms view-model publications progress during modal lifetime");
                    beforeCloseForeground = Foreground(ownerHandle, handle);
                }
                catch (Exception error) { observerError = error; }
                finally { dialog?.Close(); done.TrySetResult(); }
            }), DispatcherPriority.Normal);
            bytes = GC.GetAllocatedBytesForCurrentThread(); begin = Stopwatch.GetTimestamp();
            AnalysisTraceMarkers.Log.Start(trial, active);
            typeof(MainWindow).GetMethod("RandomPlayerSettings_Click", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, [null, new RoutedEventArgs()]);
            var menuReturnMs = Elapsed(begin);
            await done.Task.WaitAsync(TimeSpan.FromSeconds(25));
            if (observerError is not null) throw observerError;
            Check(dialog is not null && !dialog.IsVisible && IsWindowEnabled(ownerHandle), "Real menu returns from Cancel and main is enabled");
            var afterReturnForeground = Foreground(ownerHandle, IntPtr.Zero);
            await Task.Delay(80); await Idle();
            var afterIdleForeground = Foreground(ownerHandle, IntPtr.Zero);
            Check(JsonSerializer.Serialize(vm.Settings) == settings && File.ReadAllBytes(vm.CurrentProjectPath).SequenceEqual(diskBefore),
                "Cancel keeps live settings and isolated saved profile unchanged");
            main.Dispatcher.Hooks.OperationStarted -= operationStart; main.Dispatcher.Hooks.OperationCompleted -= operationFinish;
            sampler.Dispose();
            await session.DisposeAsync();
            Check(session.Failure is null && session.DroppedEvents == 0, "Complete main-view capture");
            var events = File.ReadLines(Path.Combine(capture, "events.jsonl")).Select(line => JsonSerializer.Deserialize<JsonElement>(line))
                .Where(record => record.TryGetProperty("stage", out var stage) && stage.GetString()!.StartsWith("RandomSettings", StringComparison.Ordinal)).ToArray();
            Check(events.Any(item => item.GetProperty("stage").GetString() == "RandomSettingsModalReturned"), "Production menu lifetime markers captured");
            return new { active, trial, openingReadyIdleMs = readyMs, openingCallerBytes = openingBytes, menuReturnMs,
                mainActivateReturned = activated, foregroundBefore, duringForeground, readyForeground, beforeCloseForeground, afterReturnForeground, afterIdleForeground,
                ownerDisabled, wpfFocus, realized, baselineQueue = baselineDelays, openingQueue = Stats(openingDelays),
                openingNotifications, openingPreviewNotifications, totalNotifications = notifications, totalPreviewNotifications = previewNotifications,
                primaryBefore, primaryAfter, previewBefore, previewAfter, openingOperations, phaseResults, events };
        }
        finally
        {
            main.Dispatcher.Hooks.OperationStarted -= operationStart; main.Dispatcher.Hooks.OperationCompleted -= operationFinish;
            vm.PropertyChanged -= changed; dialog?.Close(); vm.Audio.StopAll(); sampler.Dispose(); await session.DisposeAsync();
        }
    }

    private static async Task<object> Interaction(string phase, Action action, Func<RandomPlayerDeckEditor> expectedDeck,
        RandomPlayerSettingsWindow dialog, bool active, int trial, WeakReference openingControl)
    {
        var before = Children(dialog).OfType<VirtualizingSongItemsControl>().Single();
        var work = before.Panel!.Work; var dataWork = dialog.Work;
        var reused = before.ReusedContainers;
        var allocated = GC.GetAllocatedBytesForCurrentThread(); var begin = Stopwatch.GetTimestamp();
        AnalysisTraceMarkers.Log.PhaseStart(trial, active, phase);
        action(); var synchronousMs = Elapsed(begin);
        await Render(dialog); await Idle();
        var stableMs = Elapsed(begin); var bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
        AnalysisTraceMarkers.Log.PhaseReady(trial, active, phase);
        var after = Children(dialog).OfType<VirtualizingSongItemsControl>().Single(); var deck = expectedDeck();
        var cards = Children(after).OfType<CheckBox>().ToArray(); var panel = after.Panel!;
        Check(after.Containers.Active == panel.RealizedCount &&
            after.Containers.Active + after.Containers.Cached <= after.Containers.Budget, "Main-view active and cached cards share the viewport budget");
        Check(after.Items.Count == 40 && cards.Length == panel.RealizedCount && cards.Length > 0 && cards.Length <= 40 &&
            cards.All(card => card.DataContext is RandomPlayerJingleEditor item && deck.Jingles.Contains(item)), "Every interaction shows current bounded deck cards");
        var first = (int)Math.Floor(panel.VerticalOffset / panel.CardHeight) * panel.Columns;
        var end = Math.Min(40, (int)Math.Ceiling((panel.VerticalOffset + panel.ViewportHeight) / panel.CardHeight) * panel.Columns);
        for (var index = first; index < end; index++) Check(cards.Any(card => ReferenceEquals(card.DataContext, after.Items[index])), "Every current viewport item has a real card");
        var same = ReferenceEquals(before, after);
        return new { phase, synchronousMs, renderingAndIdleMs = stableMs, callerBytes = bytes, sameControl = same,
            openingControlReused = ReferenceEquals(openingControl.Target, after), newJingleEditors = dialog.Work.JingleEditors - dataWork.JingleEditors,
            fileProbes = dialog.Work.FileProbes - dataWork.FileProbes, panel.RealizedCount,
            newContainers = same ? panel.Work.NewContainers - work.NewContainers : panel.Work.NewContainers,
            reusedContainers = same ? after.ReusedContainers - reused : after.ReusedContainers,
            activeContainers = after.Containers.Active, cachedContainers = after.Containers.Cached, containerBudget = after.Containers.Budget,
            resets = same ? panel.Work.Resets - work.Resets : (int?)null };
    }

    private static object Stats(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        return new { count = sorted.Length, medianMs = sorted.Length == 0 ? (double?)null :
            sorted.Length % 2 == 0 ? (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2 : sorted[sorted.Length / 2],
            maxMs = sorted.Length == 0 ? (double?)null : sorted[^1] };
    }
    private static object Foreground(IntPtr owner, IntPtr child)
    {
        var foreground = GetForegroundWindow();
        return new { kind = foreground == IntPtr.Zero ? "none" : foreground == owner ? "main" : child != IntPtr.Zero && foreground == child ? "dialog" : "other" };
    }
    private static async Task Until(Func<bool> predicate)
    {
        var end = Stopwatch.StartNew();
        while (!predicate()) { if (end.Elapsed > TimeSpan.FromSeconds(25)) throw new TimeoutException("Main-view fixture readiness"); await Task.Delay(10); }
    }
    private static async Task Render(Window window)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); EventHandler handler = (_, _) => done.TrySetResult(); CompositionTarget.Rendering += handler;
        try { window.InvalidateVisual(); await done.Task.WaitAsync(TimeSpan.FromSeconds(20)); } finally { CompositionTarget.Rendering -= handler; }
    }
    private static IEnumerable<DependencyObject> Children(DependencyObject root)
    {
        yield return root;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) foreach (var child in Children(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static double Elapsed(long stamp) => Stopwatch.GetElapsedTime(stamp).TotalMilliseconds;
    private static async Task Idle() => await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowEnabled(IntPtr handle);
}
