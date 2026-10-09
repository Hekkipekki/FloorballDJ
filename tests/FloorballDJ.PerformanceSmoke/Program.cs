using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using FloorballDJ;
using FloorballDJ.Models;
using FloorballDJ.Services;
using FloorballDJ.ViewModels;
using FloorballDJ.Views;
using NAudio.Wave;

internal static class Program
{
    [STAThread]
    private static int Main(string[] arguments)
    {
        var root = Path.Combine(Path.GetTempPath(), "FloorballDJ-performance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("FLOORBALLDJ_DATA_DIR", root);
        var app = new DiagnosticTestApplication();
#pragma warning disable WPF0001 // Match the production App.xaml theme in this pinned-runtime fixture.
        app.ThemeMode = ThemeMode.Dark;
#pragma warning restore WPF0001
        app.Resources["FlexibleDouble"] = new FloorballDJ.Converters.FlexibleDoubleConverter();
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/PresentationFramework.Fluent;component/Themes/Fluent.xaml", UriKind.Relative)
        });
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/FloorballDJ;component/Themes/Theme.xaml", UriKind.Relative)
        });
        var exitCode = 0;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        _ = Dispatcher.CurrentDispatcher.InvokeAsync(async () =>
        {
            try
            {
                Check(app.Windows.Count == 0, "Diagnostic startup must not launch production profile/license windows");
                if (arguments.Contains("--shortcut-benchmark")) ShortcutChecks.Benchmark(Argument(arguments, "--output") ?? Path.Combine(root, "shortcut-benchmark"));
                else if (arguments.Contains("--random-validation-checks")) await RandomPoolAvailabilityChecks.RunAsync(root, Argument(arguments, "--output"));
                else if (arguments.Contains("--presentation-checks")) await PresentationFixChecks.RunAsync(root, Argument(arguments, "--output"));
                else if (arguments.Contains("--rc-workflow-checks")) await RcWorkflowChecks.RunAsync(root, Argument(arguments, "--output"));
                else if (arguments.Contains("--keep-alive-checks")) await KeepAliveChecks.RunAsync(root, Argument(arguments, "--output"));
                else if (arguments.Contains("--local-font-checks")) await LocalTypographyChecks.RunAsync(root);
                else if (arguments.Contains("--waveform-checks")) await WaveformChecks.RunAsync(root, Argument(arguments, "--output"));
                else if (arguments.Contains("--output-reuse-checks")) await OutputReuseChecks.RunAsync(root, Argument(arguments, "--output"));
                else if (arguments.Contains("--save-checks")) await SaveChecks.RunAsync(root, Argument(arguments, "--output"));
                else if (arguments.Contains("--metadata-checks")) await MetadataChecks.RunAsync(root, Argument(arguments, "--output"));
                else if (arguments.Contains("--autoplay-checks")) await AutoplayChecks.RunAsync(root, Argument(arguments, "--output"));
                else if (arguments.Contains("--ui-work-checks")) await UiWorkChecks.RunAsync(root, Argument(arguments, "--output"));
                else if (arguments.Contains("--random-settings-checks")) await RandomSettingsChecks.RunAsync(root, Argument(arguments, "--output"));
                else if (arguments.Contains("--main-view-analysis")) await MainViewSettingsAnalysis.RunAsync(root, Argument(arguments, "--output"), int.TryParse(Argument(arguments, "--main-view-trials"), out var mainViewTrials) ? mainViewTrials : 6, arguments.Contains("--main-view-interactions"));
                else if (arguments.Contains("--main-startup-checks")) await MainViewSettingsAnalysis.CheckStartupAsync(root);
                else if (arguments.Contains("--fit-checks")) await WindowFitChecks.RunAsync(Argument(arguments, "--output"));
                else if (arguments.Contains("--placement-analysis")) await WindowPlacementAnalysis.RunAsync(root, Argument(arguments, "--output"), int.TryParse(Argument(arguments, "--placement-trials"), out var placementTrials) ? placementTrials : 6);
                else if (arguments.Contains("--show-analysis")) await RandomSettingsShowAnalysis.RunAsync(root, Argument(arguments, "--output"), Argument(arguments, "--show-case"), int.TryParse(Argument(arguments, "--show-trials"), out var showTrials) ? showTrials : 6);
                else if (arguments.Contains("--layout-analysis")) await RandomSettingsLayoutAnalysis.RunAsync(root, Argument(arguments, "--output"), Argument(arguments, "--layout-case"), int.TryParse(Argument(arguments, "--layout-trials"), out var layoutTrials) ? layoutTrials : 6);
                else if (arguments.Contains("--layout-selection-probe")) await RandomSettingsLayoutAnalysis.SelectionProbeAsync(root, Argument(arguments, "--output"));
                else if (arguments.Contains("--benchmark")) await BenchmarkAsync(arguments, root);
                else await CheckAsync(root);
            }
            catch (Exception exception) { Console.Error.WriteLine(exception); exitCode = 1; }
            finally
            {
                WaveformScheduler.Shared.Dispose();
                PerformanceDiagnostics.Stop();
                Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
            }
        });
        Dispatcher.Run();
        return exitCode;
    }

    private sealed class DiagnosticTestApplication : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            // Application's dispatcher can schedule startup even without App.Run.
            // Keep the app resources but never launch license/profile windows or
            // their timers/autosaves alongside this isolated diagnostic fixture.
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
        }
    }

    private static async Task CheckAsync(string root)
    {
        CheckDisabledAllocations();
        ShortcutChecks.Run();
        await RandomPoolAvailabilityChecks.RunAsync(root, null);
        await WaveformChecks.RunAsync(root);
        await OutputReuseChecks.RunAsync(root);
        await SaveChecks.RunAsync(root);
        await MetadataChecks.RunAsync(root);
        await AutoplayChecks.RunAsync(root);
        await UiWorkChecks.RunAsync(root);
        await PresentationFixChecks.RunAsync(root, null);
        await RcWorkflowChecks.RunAsync(root, null);
        await KeepAliveChecks.RunAsync(root, null);
        await LocalTypographyChecks.RunAsync(root);
        await RandomSettingsChecks.RunAsync(root);
        await WindowFitChecks.RunAsync(null);
        await MainViewSettingsAnalysis.CheckStartupAsync(root);
        var buffer = new PerformanceEventBuffer(2);
        Check(buffer.TryWrite(default) && buffer.TryWrite(default) && !buffer.TryWrite(default), "Bounded producer must drop instead of wait");
        Check(buffer.Dropped == 1 && buffer.TryRead(out _) && buffer.TryWrite(default), "Drop count and reuse");
        buffer.Complete();
        Check(!buffer.TryWrite(default), "Completed queue rejects writes");

        var capture = Path.Combine(root, "protocol");
        var session = PerformanceDiagnostics.Start(capture, Dispatcher.CurrentDispatcher);
        session.Record(0, 0, "ZeroDuration", durationTicks: 0);
        using (PerformanceDiagnostics.BeginCommand("outer"))
        {
            PerformanceDiagnostics.BeginOperation("OuterOperation").Mark("OuterBefore");
            using (PerformanceDiagnostics.BeginCommand("inner"))
            {
                await Task.Yield();
                PerformanceDiagnostics.BeginOperation("InnerOperation").Mark("InnerAfterAwait");
            }
            PerformanceDiagnostics.BeginOperation("OuterOperation").Mark("OuterAfter");
        }
        PerformanceDiagnostics.BeginOperation("UncorrelatedOperation").Mark("AfterScope");
        await CheckConcurrentCommands();
        CheckSampleParity();
        await CheckShortcutPrecedence(root);
        var project = ProjectService.CreateDefault();
        project.Name = "Private profile content must not be logged";
        await new ProjectService().SaveAsync(project, Path.Combine(root, "private-profile.floorballdj.json"));
        // Observe the actual samples instead of assuming the worker/probe ran
        // within a fixed 1.1 seconds on a busy or slower machine.
        await WaitForRuntimeProbes(capture);
        await session.DisposeAsync();
        Check(session.Failure is null && session.DroppedEvents == 0, "Capture drains without losing protocol events");
        var events = ReadEvents(capture);
        var outer = events.Single(item => Stage(item) == "OuterBefore").GetProperty("commandId").GetInt64();
        var inner = events.Single(item => Stage(item) == "InnerAfterAwait").GetProperty("commandId").GetInt64();
        Check(outer > 0 && inner > 0 && outer != inner, "Nested command IDs differ and survive await");
        Check(events.Single(item => Stage(item) == "OuterAfter").GetProperty("commandId").GetInt64() == outer, "Outer context restored");
        Check(events.Single(item => Stage(item) == "AfterScope").GetProperty("commandId").GetInt64() == 0, "No leaked input context");
        Check(events.Where(item => Stage(item) == "ParallelMarker").Select(item => item.GetProperty("commandId").GetInt64()).Distinct().Count() == 4,
            "Concurrent input scopes remain separate");
        Check(events.Count(item => Stage(item) == "FirstBufferReturned") == 1 && events.Count(item => Stage(item) == "FirstSignalReturned") == 1,
            "Audio first-buffer/signal markers occur once");
        Check(events.Any(item => Stage(item) == "SaveSnapshot") && events.Any(item => Stage(item) == "SaveCompleted"), "Snapshot and save timing");
        Check(events.Single(item => Stage(item) == "ZeroDuration").GetProperty("durationMs").GetDouble() == 0,
            "Zero-duration measurements must not be confused with point markers");
        Check(events.Where(item => Stage(item) == "JingleIdentity").Select(item => item.GetProperty("detail").GetString())
                .SequenceEqual(events.Where(item => Stage(item) == "ExpectedShortcutChoice").Select(item => item.GetProperty("detail").GetString())),
            "Selected-deck duplicate wins; otherwise the first other deck wins");
        Check(events.Any(item => Stage(item) == "DispatcherQueueDelay") && events.Any(item => Stage(item) == "CpuCoreEquivalents"), "Runtime and dispatcher probes");
        var logged = File.ReadAllText(Path.Combine(capture, "events.jsonl"));
        Check(!logged.Contains(root) && !logged.Contains(project.Name), "No media/profile paths or contents in events");
        await CheckRandomHotkeyPriority(root);

        // Failure to open the diagnostics directory must not escape into playback/application code.
        var invalid = Path.Combine(root, "not-a-directory");
        File.WriteAllText(invalid, "occupied");
        var failed = PerformanceDiagnostics.Start(invalid);
        await Task.Delay(100);
        await failed.DisposeAsync();
        Check(failed.Failure is not null && !PerformanceDiagnostics.Enabled, "Writer failure deactivates capture safely");

        var capped = PerformanceDiagnostics.Start(Path.Combine(root, "capped"), maximumBytes: 4096);
        var cappedOperation = PerformanceDiagnostics.BeginOperation("CapTest");
        for (var index = 0; index < 200; index++) cappedOperation.Mark("CapMarker");
        await Task.Delay(350);
        await capped.DisposeAsync();
        Check(capped.StopReason == "EventByteLimit" && new FileInfo(Path.Combine(capped.DirectoryPath, "events.jsonl")).Length <= 4096,
            "Capture byte budget respected");
        CheckDisabledAllocations();
        Console.WriteLine("PASS: disabled allocation-free hooks, bounded queue/drop accounting, async/nested/concurrent command correlation, sample parity, shortcut precedence, privacy, runtime probes, writer failure and capture cap.");
    }

    private static async Task WaitForRuntimeProbes(string capture)
    {
        var deadline = Stopwatch.StartNew();
        var path = Path.Combine(capture, "events.jsonl");
        while (deadline.Elapsed < TimeSpan.FromSeconds(5))
        {
            if (File.Exists(path))
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                var logged = await reader.ReadToEndAsync();
                if (logged.Contains("\"stage\":\"DispatcherQueueDelay\"") && logged.Contains("\"stage\":\"CpuCoreEquivalents\"")) return;
            }
            await Task.Delay(50);
        }
        throw new TimeoutException("Runtime/dispatcher probes did not arrive in the diagnostic capture.");
    }

    private static async Task CheckConcurrentCommands()
    {
        await Task.WhenAll(Enumerable.Range(0, 4).Select(index => Task.Run(async () =>
        {
            using var command = PerformanceDiagnostics.BeginCommand("parallel");
            await Task.Delay(index + 1);
            PerformanceDiagnostics.BeginOperation("ParallelOperation").Mark("ParallelMarker", index);
        })));
    }

    private static void CheckDisabledAllocations()
    {
        Check(!PerformanceDiagnostics.Enabled, "Diagnostics default off");
        DisabledHooks();
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 10000; index++) DisabledHooks();
        Check(GC.GetAllocatedBytesForCurrentThread() - before == 0, "Disabled hooks must allocate no per-call objects");
    }

    private static void DisabledHooks()
    {
        using var command = PerformanceDiagnostics.BeginCommand("keyboard");
        var operation = PerformanceDiagnostics.BeginOperation("PlaybackRequested");
        using var duration = operation.Measure("DisabledDuration");
        operation.Mark("DisabledMarker");
        PerformanceDiagnostics.RouteResolved("jingle");
    }

    private static void CheckSampleParity()
    {
        float[] samples = [0, 0, .1f, -.2f, .5f, -.6f, .8f, -.9f];
        var raw = new ArraySource(samples);
        using var command = PerformanceDiagnostics.BeginCommand("sampleParity");
        var instrumented = new PerformanceSampleProvider(new ArraySource(samples), PerformanceDiagnostics.BeginOperation("PlaybackRequested"));
        var expected = Enumerable.Repeat(42f, 20).ToArray();
        var actual = (float[])expected.Clone();
        foreach (var count in new[] { 2, 4, 4, 4 })
        {
            var rawCount = raw.Read(expected, 3, count);
            var instrumentedCount = instrumented.Read(actual, 3, count);
            Check(rawCount == instrumentedCount && actual.SequenceEqual(expected), "Instrumentation preserves samples, offsets, lengths and EOF");
        }
    }

    private static async Task CheckShortcutPrecedence(string root)
    {
        var window = new MainWindow();
        var vm = (MainViewModel)window.DataContext;
        using var source = CreateHiddenSource();
        try
        {
            var first = vm.Decks[0].Jingles[0];
            var selected = vm.Decks[1].Jingles[0];
            foreach (var jingle in new[] { first, selected })
            {
                jingle.Shortcut = "F6";
                jingle.FilePath = Path.Combine(root, "missing-private-track.wav");
            }
            var session = PerformanceDiagnostics.BeginOperation("ShortcutCharacterization");
            vm.SelectedDeck = vm.Decks[1];
            await DispatchKeyAsync(window, source, Key.F6);
            session.Mark("ExpectedShortcutChoice", detail: selected.Id.ToString("N"));
            Check(vm.Status.StartsWith(LanguageService.Translate("Kunde inte spela "), StringComparison.Ordinal), "Missing-file playback remains safely handled");
            vm.SelectedDeck = vm.Decks[2];
            await DispatchKeyAsync(window, source, Key.F6);
            session.Mark("ExpectedShortcutChoice", detail: first.Id.ToString("N"));
        }
        finally { CloseWithoutSave(window); }
    }

    private static async Task CheckRandomHotkeyPriority(string root)
    {
        var path = Path.Combine(root, "random-priority-silent.wav");
        using (var writer = new WaveFileWriter(path, new WaveFormat(48000, 16, 2)))
            writer.Write(new byte[48000 * 4 * 3], 0, 48000 * 4 * 3);
        var capture = Path.Combine(root, "random-priority");
        var session = PerformanceDiagnostics.Start(capture, Dispatcher.CurrentDispatcher);
        var window = new MainWindow();
        var vm = (MainViewModel)window.DataContext;
        using var source = CreateHiddenSource();
        try
        {
            var direct = vm.Decks[0].Jingles[0];
            var category = vm.Decks[1].Jingles[0];
            var pooled = vm.Decks[2].Jingles[0];
            foreach (var jingle in new[] { direct, category, pooled })
            {
                jingle.FilePath = path;
                jingle.FadeInOverrideSeconds = 0;
                jingle.FadeOutOverrideSeconds = .03;
            }
            direct.Shortcut = "F6";
            category.CategoryShortcut = "F6";
            var profile = new RandomPoolProfile { Shortcut = "F6", JingleIds = [pooled.Id] };
            vm.Settings.RandomPoolSetups.Clear();
            vm.Settings.RandomPoolProfiles = [profile];
            vm.Audio.Configure(null, null, -60, -12, 0, .03);
            vm.SelectedDeck = vm.Decks[0];
            async Task StartExpected(Jingle expected)
            {
                vm.Audio.StopAll(notify: false);
                await DispatchKeyAsync(window, source, Key.F6);
                PerformanceDiagnostics.BeginOperation("RoutingExpectation").Mark("ExpectedRouteJingle", detail: expected.Id.ToString("N"));
                var started = Stopwatch.GetTimestamp();
                while (vm.Audio.GetCurrentPosition() is not { TotalSeconds: > 0 } && Stopwatch.GetElapsedTime(started).TotalSeconds < 5)
                    await Task.Delay(5);
                Check(vm.Audio.GetCurrentPosition() is { TotalSeconds: > 0 }, "Hotkey-priority fixture must return audio");
                await Task.Delay(70);
            }
            await StartExpected(pooled);
            await DispatchKeyAsync(window, source, Key.F6);
            await Task.Delay(150);
            profile.JingleIds.Clear();
            await StartExpected(category);
            category.CategoryShortcut = null;
            await StartExpected(direct);
            profile.JingleIds.Add(pooled.Id);
            var setup = new RandomPoolSetup { Profiles = [new RandomPoolProfile { Shortcut = "F6", JingleIds = [category.Id] }] };
            vm.Settings.RandomPoolSetups.Add(setup);
            vm.Settings.ActiveRandomPoolSetupId = setup.Id;
            await StartExpected(category);
        }
        finally
        {
            CloseWithoutSave(window);
            await session.DisposeAsync();
        }
        var events = ReadEvents(capture);
        var actual = events.Where(item => Stage(item) == "JingleIdentity").Select(item => item.GetProperty("detail").GetString()).ToArray();
        var expectedIds = events.Where(item => Stage(item) == "ExpectedRouteJingle").Select(item => item.GetProperty("detail").GetString()).ToArray();
        Check(actual.Length == 5 && actual[0] == expectedIds[0] && actual[1] == expectedIds[0] && actual.Skip(2).SequenceEqual(expectedIds.Skip(1)),
            "Random group precedes category/direct; empty group falls through; setup changes are immediate");
        Check(events.Count(item => Stage(item) == "PlaybackAction" && item.GetProperty("detail").GetString() == "FadingOut") == 1,
            "Repeated random shortcut preserves fade-out behavior");
        Check(events.Any(item => Stage(item) == "RandomPoolFileValidation") && events.Any(item => Stage(item) == "RandomPoolMembership"),
            "Random membership/file-validation timings are captured separately");
        Check(session.DroppedEvents == 0 && session.Failure is null, "Priority capture must be complete");
        Console.WriteLine("PASS: actual random/category/direct shortcut priority, empty-pool fallback, repeat fade-out and active-setup changes (silent WASAPI).");
    }

    private static async Task BenchmarkAsync(string[] arguments, string root)
    {
        var iterations = int.Parse(Argument(arguments, "--iterations") ?? "100");
        var spacing = int.Parse(Argument(arguments, "--spacing-ms") ?? "250");
        if (iterations is < 1 or > 10000 || spacing is < 100 or > 10000) throw new ArgumentException("Use 1–10000 iterations and 100–10000 ms spacing.");
        var route = Argument(arguments, "--route") ?? "keyboard";
        if (route is not ("keyboard" or "engine")) throw new ArgumentException("Route must be keyboard or engine.");
        var output = Argument(arguments, "--output") ?? Path.Combine(root, "benchmark");
        var suppliedFile = Argument(arguments, "--file");
        var path = suppliedFile ?? Path.Combine(root, "silent-reference.wav");
        if (suppliedFile is null)
        {
            using var writer = new WaveFileWriter(path, new WaveFormat(48000, 16, 2));
            var silence = new byte[48000 * 4 * 3];
            writer.Write(silence, 0, silence.Length);
        }
        if (!File.Exists(path)) throw new FileNotFoundException("Benchmark media is missing.");
        var diagnostics = !arguments.Contains("--without-diagnostics");
        var session = diagnostics ? PerformanceDiagnostics.Start(output, Dispatcher.CurrentDispatcher) : null;
        var window = new MainWindow();
        var vm = (MainViewModel)window.DataContext;
        using var source = CreateHiddenSource();
        var jingle = vm.Decks[0].Jingles[0];
        jingle.FilePath = path;
        jingle.Shortcut = "F6";
        jingle.FadeInOverrideSeconds = 0;
        jingle.StartSeconds = double.Parse(Argument(arguments, "--start-seconds") ?? "0", System.Globalization.CultureInfo.InvariantCulture);
        if (!double.IsFinite(jingle.StartSeconds) || jingle.StartSeconds < 0) throw new ArgumentException("Start must be finite and nonnegative.");
        vm.Audio.Configure(null, null, -60, -12, 0, .03);
        if (suppliedFile is not null) Console.WriteLine("The supplied file will play repeatedly at -60 dB master gain. This is not muted.");
        var times = new List<double>();
        var modifierWaits = 0;
        try
        {
            for (var index = 0; index < iterations; index++)
            {
                // The real handler reads global modifiers, including keys used in another app.
                // Wait outside the measured interval so ordinary desktop use cannot invalidate F6.
                while (route == "keyboard" && Keyboard.Modifiers != ModifierKeys.None)
                {
                    modifierWaits++;
                    await Task.Delay(50);
                }
                vm.Audio.StopAll(notify: false);
                var started = Stopwatch.GetTimestamp();
                if (route == "keyboard")
                {
                    if (!TryDispatchKey(window, source, Key.F6))
                    {
                        // A modifier may have changed between the guard and the real handler.
                        modifierWaits++;
                        if (modifierWaits > 10000) throw new InvalidOperationException("Repeated input contention; release modifier keys and retry.");
                        await Task.Delay(50);
                        index--;
                        continue;
                    }
                }
                else
                {
                    using var command = PerformanceDiagnostics.BeginCommand("benchmarkEngine");
                    PerformanceDiagnostics.RouteResolved("directEngine");
                    vm.Audio.Play(jingle);
                }
                times.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                var deadline = Stopwatch.GetTimestamp();
                while (vm.Audio.GetCurrentPosition() is not { TotalSeconds: > 0 } && Stopwatch.GetElapsedTime(deadline).TotalSeconds < 5)
                    await Task.Delay(5);
                if (vm.Audio.GetCurrentPosition() is not { TotalSeconds: > 0 })
                    throw new InvalidOperationException("No decoded frames within five seconds. Check source start position and the audio endpoint.");
                await Task.Delay(spacing);
            }
        }
        finally
        {
            CloseWithoutSave(window);
            if (session is not null) await session.DisposeAsync();
        }
        if (session?.Failure is not null) throw new IOException($"Capture failed: {session.Failure}");
        if (session is not null)
        {
            var events = ReadEvents(output);
            var starts = events.Where(item => Stage(item) == "PlaybackAction" && item.GetProperty("detail").GetString() == "Started").Count();
            var firstBuffers = events.Count(item => Stage(item) == "FirstBufferReturned");
            Check(starts == iterations && firstBuffers == iterations && session.DroppedEvents == 0 && session.StopReason == "CaptureClosed",
                "Every benchmark trigger must start and return a first buffer without trace loss");
        }
        Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(Path.Combine(output, "benchmark.json"), JsonSerializer.Serialize(new
        {
            schemaVersion = 1, route, iterations, spacingMs = spacing, diagnostics, modifierWaits,
            diagnosticApplicationStartup = "resources-only-v1",
            audioOutputs = AudioOutputPool.Requested ? "reuse" : "legacy",
            fixture = suppliedFile is null ? "silent 48kHz stereo 16-bit WAV" : "supplied media",
            sourceExtension = Path.GetExtension(path), sourceStartSeconds = jingle.StartSeconds,
            masterGainDb = -60, userFadeOverrideSeconds = 0, minimumRampSeconds = .03,
            synchronousDispatchMs = times,
            limitation = "Synthetic managed handler/direct engine; excludes physical input queue and speaker onset. Hidden window excludes visible cart rendering; bound controls may still prepare waveforms."
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PASS: {iterations} {route} triggers. Synchronous dispatch mean {times.Average():0.00} ms; diagnostics {(diagnostics ? "on" : "off")}.");
        Console.WriteLine(Path.GetFullPath(output));
    }

    private static HwndSource CreateHiddenSource()
        => new(new HwndSourceParameters("FloorballDJ performance input") { Width = 1, Height = 1, WindowStyle = 0 });

    private static void CloseWithoutSave(MainWindow window)
    {
        // Tests own an isolated in-memory profile; exercise normal timer/engine cleanup without an autosave.
        typeof(MainWindow).GetField("_closeCommitted", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true);
        window.Close();
    }

    internal static async Task DispatchKeyAsync(MainWindow window, HwndSource source, Key key)
    {
        // The real handler reads desktop modifier state. Keep characterization
        // deterministic during ordinary typing, but still fail a missing handler.
        Keyboard.ClearFocus();
        for (var attempt = 0; attempt < 200; attempt++)
        {
            if (Keyboard.Modifiers == ModifierKeys.None && TryDispatchKey(window, source, key)) return;
            await Task.Delay(50);
        }
        throw new InvalidOperationException("Configured shortcut was not handled within ten seconds; desktop modifier contention or handler failure.");
    }

    private static bool TryDispatchKey(MainWindow window, HwndSource source, Key key)
    {
        var handler = typeof(MainWindow).GetMethod("MainWindow_PreviewKeyDown", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException("MainWindow_PreviewKeyDown");
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        handler.Invoke(window, [window, args]);
        return args.Handled;
    }

    private static string? Argument(string[] arguments, string name)
    {
        var index = Array.IndexOf(arguments, name);
        return index >= 0 && index + 1 < arguments.Length ? arguments[index + 1] : null;
    }
    private static List<JsonElement> ReadEvents(string capture)
        => File.ReadLines(Path.Combine(capture, "events.jsonl")).Select(line =>
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.Clone();
        }).Where(item => item.GetProperty("type").GetString() == "event").ToList();
    private static string? Stage(JsonElement item) => item.GetProperty("stage").GetString();
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class ArraySource(float[] samples) : ISampleProvider
    {
        private int _position;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        public int Read(float[] buffer, int offset, int count)
        {
            var read = Math.Min(count, samples.Length - _position);
            Array.Copy(samples, _position, buffer, offset, read);
            _position += read;
            return read;
        }
    }
}
