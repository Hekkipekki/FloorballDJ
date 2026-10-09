using System.Collections;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FloorballDJ.Converters;
using FloorballDJ.Models;
using FloorballDJ.Services;
using FloorballDJ.ViewModels;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

internal static class UiWorkChecks
{
    internal static async Task RunAsync(string root, string? output = null)
    {
        var directory = Path.Combine(root, "ui-work");
        Directory.CreateDirectory(directory);
        var notifications = CheckNotifications(directory);
        var resources = CheckResources();
        var views = CheckViews();
        var idle = CheckIdle();
        var audio = new List<object>();
        foreach (var reuse in new[] { false, true }) audio.Add(await CheckVolumes(directory, reuse));
        if (output is not null)
        {
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output, "ui-work-checks.json"), JsonSerializer.Serialize(new
            {
                schemaVersion = 1, optimization = "p1-ui-work-v1", checkedUtc = DateTimeOffset.UtcNow,
                diagnosticApplicationStartup = "resources-only-v1", passed = true,
                notifications, resources, views, idle, audio,
                measurement = "Controlled notifications, allocations, work counts and quiet real WASAPI target-level equivalence. Not laptop CPU/song onset, audible DSP or broad hardware qualification."
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        Console.WriteLine("PASS: changed-only static bindings with coherent time/meter updates, bounded equivalent brush/font resources, live stable weak-source page/deck views, allocation-free idle snapshots and dual-path cached-volume equivalence/invalidation.");
    }

    private static object CheckNotifications(string root)
    {
        using var vm = CreateVm(root);
        var changes = new List<string?>();
        vm.PropertyChanged += (_, args) =>
        {
            changes.Add(args.PropertyName);
            if (args.PropertyName == nameof(vm.NowPlaying))
            {
                var current = vm.NowPlaying;
                Check(vm.RemainingText == (current.Duration <= TimeSpan.Zero ? "--:--.-" : MainViewModel.Format(current.Duration - current.Position)), "Snapshot observers see coherent remaining time");
                Check(vm.PositionFraction == (current.Duration.TotalSeconds <= 0 ? 0 : Math.Clamp(current.Position.TotalSeconds / current.Duration.TotalSeconds, 0, 1)), "Snapshot observers see coherent seek fraction");
            }
            if (args.PropertyName == nameof(vm.PreviewPlaying))
                Check(vm.PreviewTimeText == (vm.PreviewPlaying.Duration <= TimeSpan.Zero ? "--:-- / --:--" : $"{MainViewModel.Format(vm.PreviewPlaying.Position)} / {MainViewModel.Format(vm.PreviewPlaying.Duration)}"), "Preview observers see coherent time");
        };
        var id = Guid.NewGuid();
        var start = new PlaybackSnapshot(id, "Fixture title", "fixture.wav", TimeSpan.Zero, TimeSpan.FromSeconds(60), -25, -30, true, false);
        SetSnapshot(vm, nameof(vm.NowPlaying), start);
        SetSnapshot(vm, nameof(vm.PreviewPlaying), start);
        var oldConverter = new CountActiveConverter();
        var newConverter = new CountActiveConverter();
        var oldButton = BindActive(vm, id, "NowPlaying.JingleId", oldConverter);
        var newButton = BindActive(vm, id, nameof(vm.NowPlayingJingleId), newConverter);
        FlushBindings();
        Check(oldButton.IsChecked == true && newButton.IsChecked == true, "Both active-cart bindings initially agree");
        changes.Clear(); oldConverter.Count = newConverter.Count = 0;
        for (var index = 1; index <= 1000; index++)
        {
            var snapshot = start with { Position = TimeSpan.FromMilliseconds(index * 50), PeakLeftDb = -20 + index % 10 };
            SetSnapshot(vm, nameof(vm.NowPlaying), snapshot);
            SetSnapshot(vm, nameof(vm.PreviewPlaying), snapshot);
            FlushBindings();
        }
        foreach (var property in new[] { nameof(vm.NowPlayingTitle), nameof(vm.NowPlayingLabel), nameof(vm.NowPlayingJingleId), nameof(vm.NowPlayingFilePath), nameof(vm.NowPlayingIsPaused), nameof(vm.PreviewTitle), nameof(vm.HasPreview) })
            Check(!changes.Contains(property), property + " must not change for time/meter-only frames");
        Check(changes.Count(item => item == nameof(vm.NowPlaying)) == 1000 && changes.Count(item => item == nameof(vm.PositionFraction)) == 1000,
            "All 1,000 snapshot and seek-position updates remain available");
        Check(oldConverter.Count == 1000 && newConverter.Count == 0, "Real WPF cart binding avoids 1,000 redundant identity conversions");
        var remainingChanges = changes.Count(item => item == nameof(vm.RemainingText));
        var previewTimeChanges = changes.Count(item => item == nameof(vm.PreviewTimeText));
        changes.Clear();
        var frame = vm.NowPlaying;
        SetSnapshot(vm, nameof(vm.NowPlaying), frame with { PeakRightDb = -11 });
        Check(changes.SequenceEqual([nameof(vm.NowPlaying)]), "Meter-only frame does not update static/time properties");
        changes.Clear();
        SetSnapshot(vm, nameof(vm.NowPlaying), frame with { JingleId = Guid.NewGuid(), Title = "new", FilePath = "new.wav", IsPaused = true, IsFadingOut = true });
        FlushBindings();
        Check(newButton.IsChecked == false && changes.Contains(nameof(vm.NowPlayingJingleId)) && changes.Contains(nameof(vm.NowPlayingTitle)) && changes.Contains(nameof(vm.NowPlayingLabel)) && changes.Contains(nameof(vm.NowPlayingIsPaused)), "Changed identity/title/pause/fade still update immediately");
        var language = LanguageService.CurrentLanguage;
        try
        {
            LanguageService.SetLanguage(language == "en" ? "sv" : "en");
            changes.Clear();
            SetSnapshot(vm, nameof(vm.NowPlaying), vm.NowPlaying);
            Check(changes.Contains(nameof(vm.NowPlayingTitle)) && changes.Contains(nameof(vm.NowPlayingLabel)) && changes.Contains(nameof(vm.Status)), "Language changes update static text even for an equal snapshot");
        }
        finally { LanguageService.SetLanguage(language); }
        SetSnapshot(vm, nameof(vm.NowPlaying), new PlaybackSnapshot(null, "ready", "", TimeSpan.Zero, TimeSpan.Zero, -60, -60, false, false));
        SetSnapshot(vm, nameof(vm.PreviewPlaying), new PlaybackSnapshot(null, "preview", "", TimeSpan.Zero, TimeSpan.Zero, -60, -60, false, false));
        Check(vm.RemainingText == "--:--.-" && vm.PositionFraction == 0 && vm.PreviewTimeText == "--:-- / --:--" && !vm.HasPreview, "Empty state clears time and preview availability");
        return new { frames = 1000, originalCartBindingConversions = 1000, newCartBindingConversions = 0, staticTitleNotifications = 0, remainingChanges, previewTimeChanges, pollingIntervalMs = 50 };
    }

    private static object CheckResources()
    {
        var solid = new HexBrushConverter(); var gradient = new ButtonGradientConverter();
        foreach (var text in new[] { "#182338", "#80FF0000", "Red", "#abc", "sc#0.5,0.3,0.2,0.1", "bad brush", "" })
        {
            var actualSolid = (Brush)solid.Convert(text, typeof(Brush), null!, CultureInfo.CurrentCulture);
            var actualGradient = (Brush)gradient.Convert(text, typeof(Brush), null!, CultureInfo.CurrentCulture);
            Brush reference;
            try { reference = (Brush?)new BrushConverter().ConvertFromString(text) ?? Brushes.Transparent; } catch { reference = Brushes.Transparent; }
            Check(Pixels(reference).SequenceEqual(Pixels(actualSolid)), "Solid brush render is unchanged: " + text);
            Check(Pixels(OriginalGradient(text)).SequenceEqual(Pixels(actualGradient)), "Gradient render is unchanged: " + text);
            Check(actualSolid.IsFrozen && actualGradient.IsFrozen, "Shared brushes are immutable");
            Check(ReferenceEquals(actualSolid, solid.Convert(text, typeof(Brush), null!, CultureInfo.CurrentCulture)), "Solid hit reuses the object");
            Check(ReferenceEquals(actualGradient, gradient.Convert(text, typeof(Brush), null!, CultureInfo.CurrentCulture)), "Gradient hit reuses the object");
        }
        // Frozen brushes remain usable by the existing WPF animation machinery.
        var animated = new Border { Background = (Brush)gradient.Convert("#182338", typeof(Brush), null!, CultureInfo.CurrentCulture) };
        animated.BeginAnimation(UIElement.OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(1, .5, TimeSpan.FromMilliseconds(10)));
        var oldBytes = Allocated(() => { for (var index = 0; index < 1000; index++) OriginalGradient("#182338"); });
        var newBytes = Allocated(() => { for (var index = 0; index < 1000; index++) gradient.Convert("#182338", typeof(Brush), null!, CultureInfo.CurrentCulture); });
        Check(newBytes < oldBytes / 10, "Repeated gradient allocation must fall substantially");
        foreach (var value in new[] { "", "Arial", "bundled:Inter", "BUNDLED:Inter", "custom:FixtureFont" })
        {
            var font = FontService.Resolve(value);
            Check(ReferenceEquals(font, FontService.Resolve(value)), "Resolved font hit is stable");
            Check(value.StartsWith("custom:") ? font.Source == "./#FixtureFont" : value.StartsWith("bundled:", StringComparison.OrdinalIgnoreCase) ? font.Source == "./#Inter" : font.Source == (value.Length == 0 ? "Segoe UI Variable Display" : value), "Font source semantics are unchanged");
        }
        for (var index = 0; index < 400; index++)
        {
            var text = "#" + index.ToString("X6");
            solid.Convert(text, typeof(Brush), null!, CultureInfo.CurrentCulture);
            gradient.Convert(text, typeof(Brush), null!, CultureInfo.CurrentCulture);
            FontService.Resolve("Fixture font " + index);
        }
        Check(UiResourceCache.SolidCount <= 256 && UiResourceCache.GradientCount <= 256 && FontService.ResolvedCount <= 128, "Resource caches remain bounded");
        return new { gradientConversions = 1000, originalAllocatedBytes = oldBytes, cachedAllocatedBytes = newBytes, brushEntriesPerKind = 256, fontEntries = 128, exactRenderedPixelParity = true };
    }

    private static object CheckViews()
    {
        var project = ProjectService.CreateDefault();
        var source = project.Decks;
        var take = new TakeCountConverter();
        var first = (ListCollectionView)take.Convert([source, 2], typeof(object), null!, CultureInfo.CurrentCulture);
        Check(first.Cast<Deck>().SequenceEqual(source.Take(2)), "Deck prefix view matches RC1");
        source.Move(source.Count - 1, 0);
        var moved = (ListCollectionView)take.Convert([source, 2], typeof(object), null!, CultureInfo.CurrentCulture);
        Check(ReferenceEquals(first, moved) && moved.Cast<Deck>().SequenceEqual(source.Take(2)), "Live deck order updates without a new view");
        source.Insert(0, new Deck { Name = "added" });
        take.Convert([source, 2], typeof(object), null!, CultureInfo.CurrentCulture);
        Check(first.Cast<Deck>().SequenceEqual(source.Take(2)), "Add/remove prefix membership stays current");
        source.RemoveAt(0);
        take.Convert([source, 2], typeof(object), null!, CultureInfo.CurrentCulture);
        Check(first.Cast<Deck>().SequenceEqual(source.Take(2)), "Removed prefix member is not retained");
        var deck = source[0]; deck.PageCount = 6;
        var slots = new TakeSlotsConverter();
        var page = (ListCollectionView)slots.Convert([deck.Jingles, deck, 0], typeof(object), null!, CultureInfo.CurrentCulture);
        for (var index = 0; index < 1000; index++)
            Check(ReferenceEquals(page, slots.Convert([deck.Jingles, deck, 0], typeof(object), null!, CultureInfo.CurrentCulture)), "Repeated page view is stable");
        Check(slots.Views.CreatedCount == 1, "1,000 repeated requests create only one page view");
        deck.Jingles[0].Position = 1000;
        slots.Convert([deck.Jingles, deck, 0], typeof(object), null!, CultureInfo.CurrentCulture);
        Check(!page.Cast<Jingle>().Contains(deck.Jingles[0]), "In-place position changes invalidate membership on conversion");
        deck.Jingles[0].Position = 0;
        deck.SetPageLayout(0, 1, 3);
        var resized = (ListCollectionView)slots.Convert([deck.Jingles, deck, 0], typeof(object), null!, CultureInfo.CurrentCulture);
        Check(resized.Cast<Jingle>().SequenceEqual(deck.Jingles.Where(jingle => jingle.Position < 3)), "Page layout changes produce the current range");
        for (var index = 0; index < 6; index++) slots.Convert([deck.Jingles, deck, index], typeof(object), null!, CultureInfo.CurrentCulture);
        Check(slots.Views.RetainedCount(deck.Jingles) == 4, "No more than four views per source are retained");
        var replacement = new ObservableCollection<Jingle>(deck.Jingles);
        Check(!ReferenceEquals(resized, slots.Convert([replacement, deck, 0], typeof(object), null!, CultureInfo.CurrentCulture)), "Replacement collections have independent views");
        var weak = MakeWeakSource(slots);
        for (var attempt = 0; attempt < 3 && weak.IsAlive; attempt++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        Check(!weak.IsAlive, "Converter cache cannot retain an abandoned profile source");
        return new { repeatRequests = 1000, originalViewsCreated = 1000, newViewsCreated = 1, retainedViewsPerSource = 4, abandonedSourceCollected = true };
    }

    private static object CheckIdle()
    {
        using var audio = new AudioEngine(false);
        var calls = 0;
        PlaybackSnapshot? previous = null;
        audio.SnapshotChanged += (_, snapshot) => { calls++; previous = snapshot; };
        for (var index = 0; index < 50; index++) { audio.PublishSnapshot(); audio.GetSecondarySnapshot(); }
        var primary = previous; var secondary = audio.GetSecondarySnapshot(); var work = audio.VolumeWork;
        calls = 0;
        var allocated = Allocated(() => { calls = 0; for (var index = 0; index < 1000; index++) { audio.PublishSnapshot(); audio.GetSecondarySnapshot(); } });
        Check(calls == 1000 && ReferenceEquals(primary, previous) && ReferenceEquals(secondary, audio.GetSecondarySnapshot()), "Idle snapshot reuse preserves all requested publications");
        Check(audio.VolumeWork == work && allocated <= 2048, "Idle polling performs no repeated group work and negligible managed allocation");
        return new { ticks = 1000, primaryPublications = calls, allocatedBytes = allocated, volumeRebuilds = audio.VolumeWork.Rebuilds - work.Rebuilds };
    }

    private static async Task<object> CheckVolumes(string root, bool reuse)
    {
        using var audio = new AudioEngine(reuse);
        audio.Configure(null, null, -12, -9, 0, 0);
        var paths = Enumerable.Range(0, 5).Select(index => Wave(root, $"volume-{reuse}-{index}.wav")).ToArray();
        var a = new Jingle { FilePath = paths[0], PlayMode = JinglePlayMode.Solo, AllowMultipleClicks = true, GainDb = -2 };
        var b = new Jingle { FilePath = paths[1], PlayMode = JinglePlayMode.Duck, GainDb = 1, NormalizationEnabled = true, NormalizationGainDb = 2 };
        audio.Play(a); audio.Play(a); audio.Play(b, playbackGainOffsetDb: -3);
        await Task.Delay(120);
        audio.PublishSnapshot();
        CheckAgainstOriginal(audio, -12, -9, true, 0);
        var before = audio.VolumeWork;
        var references = Voices(audio);
        for (var index = 0; index < 1000; index++)
        {
            foreach (var voice in references) _ = OriginalVolume(voice, references, -12, -9, true, 0);
            audio.PublishSnapshot();
        }
        var stable = audio.VolumeWork;
        Check(stable == before, "1,000 stable ticks must not rebuild groups or recalculate targets");
        audio.Configure(null, null, -12, -9, 0, 0);
        audio.RefreshVolumes();
        Check(audio.VolumeWork == stable, "Identical configuration and explicit refresh do not rebuild unchanged volume state");
        a.GainDb = 4; b.NormalizationGainDb = -7;
        audio.PublishSnapshot(); CheckAgainstOriginal(audio, -12, -9, true, 0);
        Check(audio.VolumeWork.Rebuilds == before.Rebuilds + 1, "Live gain/normalization changes are picked up by the next poll");
        a.AllowMultipleClicks = false; b.PlayMode = JinglePlayMode.Solo; b.Id = Guid.NewGuid();
        audio.PublishSnapshot(); CheckAgainstOriginal(audio, -12, -9, true, 0);
        var mix = new Jingle { FilePath = paths[2], PlayMode = JinglePlayMode.Mix, GainDb = -4 };
        audio.Play(mix, playbackGainOffsetDb: 2); await Task.Delay(100);
        audio.PublishSnapshot(); CheckAgainstOriginal(audio, -12, -9, true, 0);
        audio.SetSecondaryOutput(true);
        var preview = new Jingle { FilePath = paths[3], PlayMode = JinglePlayMode.Duck, GainDb = 2 };
        audio.Play(preview, playbackGainOffsetDb: -5); await Task.Delay(100);
        audio.SetSecondaryMonitorVolumeDb(-8);
        audio.PublishSnapshot(); CheckAgainstOriginal(audio, -12, -9, true, -8);
        audio.Configure(null, null, -18, -4, 0, 0, autoMixHeadroomEnabled: false);
        CheckAgainstOriginal(audio, -18, -4, false, -8);
        var transitioning = new Jingle { FilePath = paths[4], PlayMode = JinglePlayMode.Mix };
        audio.Play(transitioning, fadeInSecondsOverride: .25);
        var fadingVoice = Voices(audio).Single(voice => ReferenceEquals(Property(voice, "Jingle"), transitioning));
        var heldVolume = ((VolumeSampleProvider)Property(fadingVoice, "Volume")!).Volume;
        transitioning.GainDb = -6;
        audio.PublishSnapshot();
        Check(((VolumeSampleProvider)Property(fadingVoice, "Volume")!).Volume == heldVolume, "Static-volume updates remain deferred during an existing fade");
        await Task.Delay(300);
        audio.PublishSnapshot(); CheckAgainstOriginal(audio, -18, -4, false, -8);
        var fade = audio.FadeOutPrimaryOutputAsync(.2, allowSpaceResume: true);
        audio.PublishSnapshot();
        Check(audio.TryResumeSpaceFade(), "Space reversal restores active volume membership");
        await fade;
        audio.PublishSnapshot(); CheckAgainstOriginal(audio, -18, -4, false, -8);
        audio.StopSecondaryOutput();
        audio.PublishSnapshot();
        await audio.StopAllDeClickedAsync();
        audio.PublishSnapshot();
        Check(audio.GetSecondarySnapshot().JingleId is null, "Stop removes preview membership");
        var stopped = audio.VolumeWork;
        audio.PublishSnapshot(); audio.PublishSnapshot();
        Check(audio.VolumeWork == stopped, "Stable empty state does not rebuild");
        return new { outputs = reuse ? "reuse" : "legacy", stableTicks = 1000, originalTargetCalculations = references.Length * 1000, newTargetCalculations = stable.Targets - before.Targets, newGroupRebuilds = stable.Rebuilds - before.Rebuilds, exactTargetFloatParity = true };
    }

    private static void CheckAgainstOriginal(AudioEngine audio, double master, double duck, bool headroom, double monitor)
    {
        var voices = Voices(audio);
        foreach (var voice in voices.Where(Active))
        {
            if ((bool)Property(voice, "IsVolumeTransitioning")!) continue;
            var actual = ((VolumeSampleProvider)Property(voice, "Volume")!).Volume;
            var expected = OriginalVolume(voice, voices, master, duck, headroom, monitor);
            Check(BitConverter.SingleToInt32Bits(actual) == BitConverter.SingleToInt32Bits(expected), "Cached target must match the original volume expression exactly");
        }
    }
    private static float OriginalVolume(object voice, object[] voices, double master, double duck, bool headroom, double monitor)
    {
        var jingle = (Jingle)Property(voice, "Jingle")!;
        var secondary = (bool)Property(voice, "UsesSecondaryDevice")!;
        var gain = jingle.GainDb + (jingle.NormalizationEnabled ? jingle.NormalizationGainDb : 0) + master +
            (double)Property(voice, "PolyphonyHeadroomDb")! + (double)Property(voice, "PlaybackGainOffsetDb")! + (secondary ? monitor : 0);
        var active = voices.Where(candidate => Active(candidate) && (bool)Property(candidate, "UsesSecondaryDevice")! == secondary).ToArray();
        if (active.Any(candidate => candidate != voice && ((Jingle)Property(candidate, "Jingle")!).PlayMode == JinglePlayMode.Duck) && jingle.PlayMode != JinglePlayMode.Duck) gain += duck;
        var containsMix = active.Any(candidate => ((Jingle)Property(candidate, "Jingle")!).PlayMode == JinglePlayMode.Mix);
        var layers = active.Count(candidate => !((Jingle)Property(candidate, "Jingle")!).AllowMultipleClicks) +
            active.Where(candidate => ((Jingle)Property(candidate, "Jingle")!).AllowMultipleClicks).Select(candidate => ((Jingle)Property(candidate, "Jingle")!).Id).Distinct().Count();
        if (headroom && layers > 1 && !containsMix) gain -= 3.0103 * Math.Log(layers, 2);
        return Math.Clamp((float)Math.Pow(10, gain / 20), 0, 4);
    }
    private static bool Active(object voice) => !(bool)Property(voice, "IsDisposed")! && !(bool)Property(voice, "StopRequested")! && !(bool)Property(voice, "NaturalEndRequested")!;
    private static object? Property(object target, string name) => target.GetType().GetProperty(name)!.GetValue(target);
    private static object[] Voices(AudioEngine audio) => ((IEnumerable)typeof(AudioEngine).GetField("_voices", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(audio)!).Cast<object>().ToArray();
    private static MainViewModel CreateVm(string root)
    {
        var projects = new ProjectService();
        var vm = new MainViewModel(projects, new ProfilePreferencesService(root, projects.DefaultProjectPath), new AudioEngine());
        var timer = (DispatcherTimer)typeof(MainViewModel).GetField("_timer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm)!;
        Check(timer.Interval.TotalMilliseconds == 50, "Production polling interval stays 50 ms");
        timer.Stop(); // Controlled snapshot injection must not race the real idle timer.
        return vm;
    }
    private static void SetSnapshot(MainViewModel vm, string property, PlaybackSnapshot snapshot) => typeof(MainViewModel).GetProperty(property)!.SetValue(vm, snapshot);
    private static void FlushBindings() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
    private sealed class CountActiveConverter : IMultiValueConverter
    {
        internal int Count;
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) { Count++; return new ActiveJingleConverter().Convert(values, targetType, parameter, culture); }
        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }
    private static ToggleButton BindActive(MainViewModel vm, Guid id, string path, IMultiValueConverter converter)
    {
        var button = new ToggleButton();
        var binding = new MultiBinding { Converter = converter, Mode = BindingMode.OneWay };
        binding.Bindings.Add(new Binding { Source = id });
        binding.Bindings.Add(new Binding(path) { Source = vm });
        button.SetBinding(ToggleButton.IsCheckedProperty, binding);
        return button;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference MakeWeakSource(TakeSlotsConverter converter)
    {
        var source = new ObservableCollection<Jingle>(); var owner = new Deck { Jingles = source };
        converter.Convert([source, owner, 0], typeof(object), null!, CultureInfo.CurrentCulture);
        return new WeakReference(source);
    }
    private static byte[] Pixels(Brush brush)
    {
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen()) drawing.DrawRectangle(brush, null, new Rect(0, 0, 40, 40));
        var bitmap = new RenderTargetBitmap(40, 40, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
        var pixels = new byte[40 * 40 * 4]; bitmap.CopyPixels(pixels, 40 * 4, 0); return pixels;
    }
    private static Brush OriginalGradient(string value)
    {
        try
        {
            var parsed = new BrushConverter().ConvertFromString(value);
            var color = parsed is SolidColorBrush solid ? solid.Color : Color.FromRgb(24, 35, 56);
            Color Mix(Color target, double amount) => Color.FromArgb(color.A, (byte)(color.R + (target.R - color.R) * amount), (byte)(color.G + (target.G - color.G) * amount), (byte)(color.B + (target.B - color.B) * amount));
            return new LinearGradientBrush([new(Mix(Colors.White, .16), 0), new(color, .46), new(Mix(Colors.Black, .20), .72), new(Mix(Colors.White, .07), 1)], new Point(0, 0), new Point(1, 1));
        }
        catch { return new SolidColorBrush(Color.FromRgb(24, 35, 56)); }
    }
    private static long Allocated(Action action)
    {
        action(); var before = GC.GetAllocatedBytesForCurrentThread(); action(); return GC.GetAllocatedBytesForCurrentThread() - before;
    }
    private static string Wave(string root, string name)
    {
        var path = Path.Combine(root, name); using var writer = new WaveFileWriter(path, new WaveFormat(48000, 16, 2));
        writer.Write(new byte[48000 * 4 * 10], 0, 48000 * 4 * 10); return path;
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
