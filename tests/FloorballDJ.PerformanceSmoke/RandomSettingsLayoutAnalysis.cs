using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FloorballDJ.Models;
using FloorballDJ.Controls;
using FloorballDJ.Services;
using FloorballDJ.Views;

// An opt-in experiment, deliberately excluded from the normal behavior suite.
// It opens real HWNDs and mirrors App's language handlers without App startup.
internal static class RandomSettingsLayoutAnalysis
{
    private static Metrics? _current;
    private sealed record Case(string Name, int Files, int PerDeck, string Language, bool RemoveSongTemplate = false,
        bool SinglePool = false, bool FiniteOverview = false, bool RemoveOverview = false);
    private sealed class Metrics
    {
        public bool Hooks;
        public int LoadedCalls, TreeCalls;
        public double LoadedMs, TreeMs;
        public List<object> Operations = [];
    }

    internal static async Task SelectionProbeAsync(string root, string? output)
    {
        var observations = new List<object>();
        foreach (var prepareBeforeShow in new[] { false, true })
        {
            var project = RandomSettingsChecks.Fixture(root, 553, 12);
            var window = new RandomPlayerSettingsWindow(project) { ShowActivated = false, ShowInTaskbar = false };
            if (prepareBeforeShow) Check(await window.Initialization, "Before-show preparation");
            window.Show(); Check(await window.Initialization, "Visible preparation");
            await NextRendering(window); await Idle();
            var tabs = (TabControl)window.FindName("DeckTabs");
            observations.Add(new { prepareBeforeShow, tabs.SelectedIndex, tabCount = tabs.Items.Count, tree = CountTree(window) });
            window.Close(); await Idle();
        }
        var json = JsonSerializer.Serialize(observations, new JsonSerializerOptions { WriteIndented = true });
        if (output is not null) { Directory.CreateDirectory(output); File.WriteAllText(Path.Combine(output, "selection-probe.json"), json); }
        Console.WriteLine(json);
    }

    internal static async Task RunAsync(string root, string? output, string? caseName = null, int trials = 6)
    {
        output ??= Path.Combine(root, "layout-analysis");
        Directory.CreateDirectory(output);
        // These are the same two class-handler registrations as App.OnStartup.
        EventManager.RegisterClassHandler(typeof(FrameworkElement), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) => {
                if (_current is not { Hooks: true } m || sender is not FrameworkElement element) return;
                var started = Stopwatch.GetTimestamp();
                LanguageService.TranslateElement(element);
                m.LoadedCalls++; m.LoadedMs += Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            }), true);
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) => {
                if (_current is not { Hooks: true } m || sender is not Window window) return;
                window.Dispatcher.BeginInvoke(() => {
                    var started = Stopwatch.GetTimestamp(); LanguageService.TranslateTree(window);
                    m.TreeCalls++; m.TreeMs += Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                }, DispatcherPriority.Loaded);
            }), true);
        var cases = new[] {
            new Case("553-40-en", 553, 40, "en"), new Case("553-40-sv", 553, 40, "sv"),
            new Case("553-40-no-hooks", 553, 40, "none"),
            new Case("553-40-single-pool-finite", 553, 40, "en", SinglePool: true, FiniteOverview: true),
            new Case("553-553-finite", 553, 553, "en", FiniteOverview: true),
            new Case("553-553-finite-no-song-template", 553, 553, "en", true, FiniteOverview: true),
            new Case("2000-40-en", 2000, 40, "en"),
            new Case("2000-2000-finite", 2000, 2000, "en", FiniteOverview: true),
            new Case("2000-2000-finite-no-song-template", 2000, 2000, "en", true, FiniteOverview: true)
        };
        var probes = new[] {
            new Case("553-40-single-pool", 553, 40, "en", SinglePool: true),
            new Case("553-553-en", 553, 553, "en"),
            new Case("553-553-no-hooks", 553, 553, "none"),
            new Case("553-553-no-overview", 553, 553, "en", RemoveOverview: true)
        };
        var samples = new List<object>(); var windows = new List<WeakReference>();
        if (caseName is not null) cases = cases.Concat(probes).Where(item => item.Name == caseName).ToArray();
        Check(cases.Length > 0 && trials is > 0 and <= 20, "Valid analysis case and trial count");
        var cold = await Observe(root, output, cases[0], -1, windows);
        var random = new Random(42);
        for (var trial = 0; trial < trials; trial++)
            foreach (var item in cases.OrderBy(_ => random.Next()))
            {
                Console.WriteLine($"Opening {item.Name} trial {trial + 1}/{trials}");
                samples.Add(await Observe(root, output, item, trial, windows));
                Console.WriteLine($"Observed {item.Name} trial {trial + 1}/{trials}");
            }
        _current = null;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var alive = windows.Count(item => item.IsAlive);
        File.WriteAllText(Path.Combine(output, "layout-analysis.json"), JsonSerializer.Serialize(new {
            schemaVersion = 1, version = "p4.3-layout-analysis-v1", checkedUtc = DateTimeOffset.UtcNow,
            runtime = Environment.Version.ToString(), processorCount = Environment.ProcessorCount,
            coldProcessObservation = cold, samples,
            closedWindowCount = windows.Count, survivingClosedWindowWeakReferences = alive,
            trialsPerCase = trials,
            limitation = "Synthetic desktop files; interleaved observations/case, real shown maximized HWNDs, production resources and mirrored language handlers, no production main-window polling/audio. WPF ContentRendered/Rendering/idle are proxies, not physical presentation or input-to-sound. Warm timings include instrumented dispatcher/hooks; allocations are UI-thread only. Stable overview and viewport song recycling are production behavior; historical FiniteOverview flags now repeat that setting. RemoveSongTemplate remains an experimental ablation. No-hooks is a mixed-language counterfactual, not an equivalent production mode. Forced GC is a retention spot check, not long-session qualification."
        }, new JsonSerializerOptions { WriteIndented = true }));
        LanguageService.SetLanguage("en");
        Console.WriteLine($"PASS: {windows.Count} shown openings, controls/language/order invariants and phase observations. Closed windows still reachable after forced GC: {alive}/{windows.Count}.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<object> Observe(string root, string output, Case item, int trial, List<WeakReference> windows)
    {
        var project = Fixture(root, item);
        LanguageService.SetLanguage(item.Language == "sv" ? "sv" : "en");
        var metrics = new Metrics { Hooks = item.Language != "none" }; _current = metrics;
        var dispatcher = Dispatcher.CurrentDispatcher;
        var inProgress = new Dictionary<DispatcherOperation, long>();
        DispatcherHookEventHandler started = (_, e) => inProgress[e.Operation] = Stopwatch.GetTimestamp();
        DispatcherHookEventHandler completed = (_, e) => {
            if (!inProgress.Remove(e.Operation, out var began)) return;
            var ms = Stopwatch.GetElapsedTime(began).TotalMilliseconds;
            if (ms >= 1) metrics.Operations.Add(new { priority = e.Operation.Priority.ToString(), durationMs = ms });
        };
        dispatcher.Hooks.OperationStarted += started; dispatcher.Hooks.OperationCompleted += completed;
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var gc = Enumerable.Range(0, 3).Select(GC.CollectionCount).ToArray();
        var begin = Stopwatch.GetTimestamp();
        var window = new RandomPlayerSettingsWindow(project) { ShowActivated = false, ShowInTaskbar = false };
        windows.Add(new WeakReference(window));
        var construct = Stopwatch.GetElapsedTime(begin).TotalMilliseconds;
        double? firstContent = null; bool? firstContentReady = null;
        window.ContentRendered += (_, _) => {
            firstContent ??= Stopwatch.GetElapsedTime(begin).TotalMilliseconds;
            firstContentReady ??= window.ViewData.IsReady;
        };
        var tabs = (TabControl)window.FindName("DeckTabs");
        var poolList = (ListBox)window.FindName("PoolItemsList");
        if (item.RemoveOverview) poolList.Visibility = Visibility.Collapsed;
        if (item.FiniteOverview)
        {
            ScrollViewer.SetHorizontalScrollBarVisibility(poolList, ScrollBarVisibility.Disabled);
            poolList.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        }
        if (item.RemoveSongTemplate)
        {
            // Retain data, editors, tab headers, and the rest of the window; omit
            // only the selected-deck content template to isolate its UI cost.
            var label = new FrameworkElementFactory(typeof(TextBlock));
            label.SetValue(TextBlock.TextProperty, "Analysis: selected-deck controls omitted");
            tabs.ContentTemplate = new DataTemplate { VisualTree = label };
        }
        var showBegin = Stopwatch.GetTimestamp(); window.Show();
        var synchronousShow = Stopwatch.GetElapsedTime(showBegin).TotalMilliseconds;
        try
        {
            try { Check(await window.Initialization.WaitAsync(TimeSpan.FromSeconds(20)), "Shown window prepared"); }
            catch (TimeoutException)
            {
                File.WriteAllText(Path.Combine(output, "timeout-" + item.Name + ".json"), JsonSerializer.Serialize(new {
                    item.Name, window.ViewData.IsReady, task = window.Initialization.Status.ToString(),
                    window.Work.FileProbes, window.Work.JingleEditors, tabs.SelectedIndex, tree = CountTree(window), metrics.Operations,
                    metrics.LoadedCalls, metrics.TreeCalls,
                    width = window.ActualWidth, height = window.ActualHeight,
                    invalid = Descendants(window).OfType<FrameworkElement>().Where(element => !element.IsMeasureValid || !element.IsArrangeValid).Select(element => new {
                        type = element.GetType().Name, element.Name, text = (element as TextBlock)?.Text,
                        element.IsMeasureValid, element.IsArrangeValid, width = element.ActualWidth, height = element.ActualHeight,
                        desiredWidth = element.DesiredSize.Width, desiredHeight = element.DesiredSize.Height
                    }).Take(40).ToArray()
                }, new JsonSerializerOptions { WriteIndented = true }));
                throw;
            }
            var ready = Stopwatch.GetElapsedTime(begin).TotalMilliseconds;
            await NextRendering(window);
            var populatedRender = Stopwatch.GetElapsedTime(begin).TotalMilliseconds;
            await Idle();
            var stable = Stopwatch.GetElapsedTime(begin).TotalMilliseconds;
            var callerBytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
            var collections = Enumerable.Range(0, 3).Select(index => GC.CollectionCount(index) - gc[index]).ToArray();
            var openOperations = metrics.Operations.ToArray(); metrics.Operations.Clear();
            var translation = new { metrics.LoadedCalls, metrics.LoadedMs, metrics.TreeCalls, metrics.TreeMs };
            var openingTree = CountTree(window);
            Check(window.ViewData.SelectedProfile is not null, $"Shown selection survives binding: setup {window.ViewData.SelectedSetup?.Name}, profiles {window.ViewData.Profiles.Count}, tab items {tabs.Items.Count}");
            var selected = window.ViewData.SelectedProfile!;
            var deck = selected.Decks[0];
            var expected = Math.Min(item.PerDeck, item.Files);
            Check(window.Work.FileProbes == item.Files && window.Work.JingleEditors == item.Files, "Fresh path checks, one group's editors");
            if (!item.RemoveSongTemplate) CheckViewport(window, deck, expected);
            Check(item.Language != "en" || window.Title == "Random song player", "Production English title translated");
            Check(item.Language != "sv" || window.Title == "Slumpmässig låtspelare", "Swedish title retained");
            Check(item.RemoveSongTemplate || Descendants(tabs).OfType<CheckBox>().Any(check => check.Content as string == deck.WholeDeckLabel), "Late template language matches its deck model");
            var phaseSamples = new List<object>();
            if (!item.RemoveSongTemplate)
            {
                if (tabs.Items.Count > 1)
                {
                    phaseSamples.Add(await Phase("deck-switch", () => tabs.SelectedIndex = 1, window, metrics));
                    phaseSamples.Add(await Phase("deck-return", () => tabs.SelectedIndex = 0, window, metrics));
                }
                var nextGroup = window.ViewData.Profiles[1];
                phaseSamples.Add(await Phase("group-switch", () => window.ViewData.SelectedProfile = nextGroup, window, metrics));
                Check(ReferenceEquals(tabs.SelectedContent, nextGroup.Decks[0]), "New group displays its own deck");
                CheckViewport(window, nextGroup.Decks[0], expected);
                phaseSamples.Add(await Phase("group-return", () => window.ViewData.SelectedProfile = selected, window, metrics));
                Check(ReferenceEquals(tabs.SelectedContent, selected.Decks[0]), "Returning group displays its own deck");
                CheckViewport(window, selected.Decks[0], expected);
                var search = (TextBox)window.FindName("SearchBox");
                phaseSamples.Add(await Phase("search-single", () => search.Text = "lat " + (expected - 1), window, metrics));
                Check(deck.Jingles.Count(jingle => jingle.IsVisible) == 1, "Search's displayed deck has exactly one match");
                CheckViewport(window, deck, 1);
                phaseSamples.Add(await Phase("search-clear", () => search.Clear(), window, metrics));
                var songList = Descendants(window).OfType<VirtualizingSongItemsControl>().Single();
                var scroll = Descendants(songList).OfType<ScrollViewer>().Single();
                phaseSamples.Add(await Phase("scroll-bottom", () => scroll.ScrollToBottom(), window, metrics));
                CheckViewport(window, deck, expected);
                Check(Descendants(songList).OfType<CheckBox>().Any(check => ReferenceEquals(check.DataContext, deck.Jingles[^1])), "Bottom scroll realizes the final song, never truncates the pool");
                scroll.ScrollToTop(); await Idle();
                if (trial == 0 && item.Name is "553-40-en" or "553-553-finite") SaveImage(window, Path.Combine(output, item.Name + ".png"));
            }
            return new {
                item.Name, item.Files, item.PerDeck, item.Language, item.RemoveSongTemplate, item.FiniteOverview, item.SinglePool, item.RemoveOverview, trial,
                constructionMs = construct, synchronousShowMs = synchronousShow, firstContentRenderedMs = firstContent,
                firstContentWasReady = firstContentReady, initializationContextIdleMs = ready,
                nextRenderingAfterInitializationMs = populatedRender, openingStableIdleMs = stable,
                callerBytes, collections, translation, openingTree, openOperations, phaseSamples,
                overviewGeometry = new { listWidth = poolList.ActualWidth,
                    maximumIndicatorWidth = Descendants(poolList).OfType<FrameworkElement>().Where(element => element.Name == "PART_Indicator").Select(element => element.ActualWidth).DefaultIfEmpty().Max() },
                widthDip = window.ActualWidth, heightDip = window.ActualHeight, dpi = VisualTreeHelper.GetDpi(window).PixelsPerInchX
            };
        }
        finally
        {
            window.Close(); await Idle(); _current = null;
            dispatcher.Hooks.OperationStarted -= started; dispatcher.Hooks.OperationCompleted -= completed;
        }
    }

    private static void CheckViewport(Window window, RandomPlayerDeckEditor deck, int expected)
    {
        var songs = Descendants(window).OfType<VirtualizingSongItemsControl>().Single();
        var panel = Descendants(songs).OfType<SongWrapPanel>().Single();
        var checks = Descendants(songs).OfType<CheckBox>().ToArray();
        var bound = ((int)Math.Ceiling(panel.ViewportHeight / panel.CardHeight) + 3) * panel.Columns + 1;
        Check(songs.Items.Count == expected && checks.Length > 0 && checks.Length <= Math.Min(expected, bound),
            $"Complete filtered view with bounded populated controls: items {songs.Items.Count}/{expected}, controls {checks.Length}, bound {bound}");
        Check(checks.All(check => check.DataContext is RandomPlayerJingleEditor jingle && deck.Jingles.Contains(jingle) && jingle.IsVisible), "Realized controls belong to the current deck and search");
        Check(checks.Length == panel.RealizedCount, "Every realized container has a song template");
        var first = Math.Max(0, (int)Math.Floor(panel.VerticalOffset / panel.CardHeight) * panel.Columns);
        var end = Math.Min(songs.Items.Count, (int)Math.Ceiling((panel.VerticalOffset + panel.ViewportHeight) / panel.CardHeight) * panel.Columns);
        for (var i = first; i < end; i++) Check(checks.Any(check => ReferenceEquals(check.DataContext, songs.Items[i])), "All viewport songs are present");
    }

    private static async Task<object> Phase(string name, Action work, Window window, Metrics metrics)
    {
        metrics.Operations.Clear();
        var allocated = GC.GetAllocatedBytesForCurrentThread(); var begin = Stopwatch.GetTimestamp();
        work(); var synchronous = Stopwatch.GetElapsedTime(begin).TotalMilliseconds;
        await NextRendering(window); await Idle();
        var elapsed = Stopwatch.GetElapsedTime(begin).TotalMilliseconds;
        return new { name, synchronousMs = synchronous, renderingAndIdleMs = elapsed,
            callerBytes = GC.GetAllocatedBytesForCurrentThread() - allocated, tree = CountTree(window), operations = metrics.Operations.ToArray() };
    }

    private static async Task NextRendering(Window window)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler handler = (_, _) => completion.TrySetResult();
        CompositionTarget.Rendering += handler;
        try { window.InvalidateVisual(); await completion.Task.WaitAsync(TimeSpan.FromSeconds(20)); }
        finally { CompositionTarget.Rendering -= handler; }
    }

    private sealed record TreeCount(int VisualNodes, int SongChecks, int SongChecksInViewport, int TextBlocks, int LocallyEnumeratedBindings);
    private static TreeCount CountTree(Window window)
    {
        var nodes = Descendants(window).ToArray();
        var songs = nodes.OfType<CheckBox>().Where(check => check.DataContext is RandomPlayerJingleEditor).ToArray();
        var visible = songs.Count(check => {
            var scroll = Ancestor<ScrollViewer>(check);
            if (scroll is null || !check.IsVisible) return false;
            return check.TransformToAncestor(scroll).TransformBounds(new Rect(check.RenderSize))
                .IntersectsWith(new Rect(0, 0, scroll.ActualWidth, scroll.ActualHeight));
        });
        var bound = 0;
        foreach (var node in nodes)
        {
            var values = node.GetLocalValueEnumerator();
            while (values.MoveNext()) if (System.Windows.Data.BindingOperations.IsDataBound(node, values.Current.Property)) bound++;
        }
        return new(nodes.Length, songs.Length, visible, nodes.OfType<TextBlock>().Count(), bound);
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, index))) yield return child;
    }
    private static T? Ancestor<T>(DependencyObject child) where T : DependencyObject
    {
        while ((child = VisualTreeHelper.GetParent(child)) is not null) if (child is T result) return result;
        return null;
    }
    private static async Task Idle() => await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static FloorballProject Fixture(string root, Case item)
    {
        var project = RandomSettingsChecks.Fixture(root, item.Files, item.Files > 1000 ? 24 : 12);
        if (item.SinglePool)
            foreach (var profile in project.Settings.RandomPoolSetups.SelectMany(setup => setup.Profiles)) profile.JingleIds = [];
        if (item.PerDeck == 40) return project;
        var all = project.Decks.SelectMany(deck => deck.Jingles).Where(jingle => jingle.HasAudio).ToArray();
        var original = project.Decks[0].Id; project.Decks.Clear();
        var large = new Deck { Id = original, Name = "Large deck", Rows = 50, Columns = 12, PageCount = (int)Math.Ceiling(all.Length / 600.0) };
        for (var index = 0; index < all.Length; index++) { all[index].Position = index; large.Jingles.Add(all[index]); }
        project.Decks.Add(large); ProjectService.EnsureLayout(project);
        return project;
    }
    private static void SaveImage(Window window, string path)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }
}
