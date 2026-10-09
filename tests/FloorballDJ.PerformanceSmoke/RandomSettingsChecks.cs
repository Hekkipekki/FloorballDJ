using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NAudio.Wave;
using FloorballDJ.Models;
using FloorballDJ.Services;
using FloorballDJ.Views;

internal static class RandomSettingsChecks
{
    internal static async Task RunAsync(string root, string? output = null)
    {
        var samples = new List<object>();
        foreach (var (files, groups) in new[] { (553, 1), (553, 12), (2000, 24) })
        {
            var project = Fixture(root, files, groups);
            for (var trial = 0; trial < 3; trial++)
            {
                var before = GC.GetAllocatedBytesForCurrentThread();
                var started = Stopwatch.GetTimestamp();
                var window = new RandomPlayerSettingsWindow(project);
                var construction = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                Check(!window.ViewData.IsReady && !window.TryApplyDraft(), "Save stays disabled during preparation");
                Check(await window.Initialization.WaitAsync(TimeSpan.FromSeconds(10)), "Window preparation succeeds");
                var ready = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                Check(window.Work.FileProbes == files && window.Work.JingleEditors == files, "One fresh probe per path and only one group realized");
                Check(window.ViewData.Setups.SelectMany(setup => setup.Profiles).Count(profile => profile.HasRealizedDecks) == 1, "Other groups remain unbuilt even with summary bindings");
                var content = (FrameworkElement)window.Content;
                started = Stopwatch.GetTimestamp();
                content.Measure(new Size(1440, 900)); content.Arrange(new Rect(0, 0, 1440, 900)); content.UpdateLayout();
                var layout = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                await Idle();
                var deck = window.ViewData.SelectedProfile!.Decks[0];
                var previous = window.Work.Overviews;
                started = Stopwatch.GetTimestamp();
                foreach (var item in deck.Jingles) item.IsIncluded = !item.IsIncluded;
                await Idle();
                var bulk = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                Check(window.Work.Overviews - previous == 1, "Bulk notifications coalesce into exactly one overview");
                samples.Add(new { files, groups, trial, constructionMs = construction, readyMs = ready, preparationCallerBytes = allocated,
                    fixedLayoutMs = layout, bulkEditMs = bulk, bulkOverviews = window.Work.Overviews - previous,
                    fileProbes = window.Work.FileProbes, jingleEditors = window.Work.JingleEditors });
                if (output is not null && files == 553 && groups == 12 && trial == 1)
                {
                    Directory.CreateDirectory(output);
                    var bitmap = new RenderTargetBitmap(1440, 900, 96, 96, PixelFormats.Pbgra32); bitmap.Render(content);
                    var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
                    using var imageFile = File.Create(Path.Combine(output, "random-settings.png")); png.Save(imageFile);
                }
                window.Close();
                await Idle();
            }
        }
        await CheckEditing(root);
        await CheckVisibleSelection(root, "en");
        await CheckVisibleSelection(root, "sv");
        await RandomSettingsSearchChecks.RunAsync(root, output);
        await RandomSettingsLayoutChecks.RunAsync(root, output);
        await IntrinsicCardChecks.RunAsync(output);
        await CheckWorkersAndLifetime(root);
        await CheckDiagnostics(root);
        if (output is not null)
        {
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output, "random-settings-checks.json"), JsonSerializer.Serialize(new {
                schemaVersion = 1, version = "p4.13-random-settings-v1", checkedUtc = DateTimeOffset.UtcNow, passed = true, samples,
                boundedWorker = true, ownedInputs = true, staleAndCloseGuards = true, bulkOverviewCoalesced = true,
                lazySaveParity = true, searchSortAndDraftIsolation = true, mainAndPreviewWhileFileChecksBlocked = true,
                shownDeckSelectionAndTemplateLanguage = true,
                retainedDeckViewCurrentSourceAndFocus = true,
                finiteOverviewAndBoundedWrappedCards = true,
                limitation = "Synthetic desktop fixtures, 3 observations/case, manually measured/arranged 1440x900 content. Not actual ProBook/profile, compositor-present or click-to-render qualification. Caller bytes include preparation continuations, not total process allocation."
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        Console.WriteLine("PASS: random-settings bounded owned background preparation, fresh path deduplication, lazy group/summary/save parity, draft isolation, search/sort/bulk edits, fresh additions, stale/close/failure guards and main/preview playback during blocked file checks.");
    }

    private static async Task CheckEditing(string root)
    {
        var project = Fixture(root, 85, 6);
        var first = project.Decks[0].Jingles[0];
        first.Title = "Åmål – Mål";
        var duplicate = project.Decks[1].Jingles[1]; duplicate.FilePath = first.FilePath;
        var missing = project.Decks[1].Jingles[2]; File.Delete(missing.FilePath);
        var text = project.Decks[1].Jingles[3]; text.FilePath = ""; text.IsTextBlock = true;
        ProjectService.EnsureLayout(project);
        var original = JsonSerializer.Serialize(project.Settings);
        var expected = project.Settings.RandomPoolSetups.Select(setup => setup.Profiles.Select(profile => LegacyProjection(project, profile)).ToArray()).ToArray();
        var window = new RandomPlayerSettingsWindow(project);
        Check(await window.Initialization, "Editing fixture ready");
        for (var s = 0; s < window.ViewData.Setups.Count; s++)
            for (var p = 0; p < window.ViewData.Setups[s].Profiles.Count; p++)
            {
                var draft = window.ViewData.Setups[s].Profiles[p];
                Check(JsonSerializer.Serialize(ToModel(draft)) == JsonSerializer.Serialize(expected[s][p]), "Lazy saving retains exact original available-ID order and every profile setting");
            }
        Check(window.Work.JingleEditors == 83 && window.Work.FileProbes == 83, "Duplicate path checked once; missing/text omitted; duplicate jingle identity retained");
        var selected = window.ViewData.SelectedProfile!;
        Call(window, "ApplySearch", "amal mal"); await Idle();
        Check(selected.Decks.SelectMany(deck => deck.Jingles).Single(item => item.JingleId == first.Id).IsVisible, "Accent-insensitive all-token search remains equivalent");
        Check(selected.Decks.SelectMany(deck => deck.Jingles).Count(item => item.IsVisible) == 1, "Search matching remains selective");
        Check(((TabControl)window.FindName("DeckTabs")).SelectedItem == selected.Decks[0], "Best search hit selects its original deck");
        Call(window, "ApplySearch", ""); await Idle();
        Check(selected.Decks.SelectMany(deck => deck.Jingles).All(item => item.IsVisible), "Empty search restores all items");
        var deck = selected.Decks[0]; deck.IncludeWholeDeck = false;
        foreach (var item in deck.Jingles) item.IsIncluded = false;
        Call(window, "SelectVisible_Click", null, new RoutedEventArgs()); await Idle();
        Check(deck.Jingles.All(item => item.IsIncluded) && deck.SelectedCount == deck.Jingles.Count, "Select visible retains individual choices");
        deck.IncludeWholeDeck = true;
        Check(deck.Jingles.All(item => item.DisplayIsIncluded && !item.CanToggleIndividually), "Whole-deck locking/display preserved");
        deck.IncludeWholeDeck = false;
        Check(deck.Jingles.All(item => item.IsIncluded && item.CanToggleIndividually), "Individual choices survive whole-deck toggle");
        Call(window, "SortButton_Click", new Button { Tag = "desc" }, new RoutedEventArgs());
        Check(deck.Jingles.Select(item => item.Title).SequenceEqual(deck.Jingles.Select(item => item.Title).OrderByDescending(title => title, StringComparer.CurrentCultureIgnoreCase)), "Descending sort preserved");
        Call(window, "SortButton_Click", new Button { Tag = "deck" }, new RoutedEventArgs());
        Check(deck.Jingles.Select(item => item.OriginalOrder).SequenceEqual(Enumerable.Range(0, deck.Jingles.Count)), "Deck-order restoration preserved");
        selected.Name = "Draft edit"; selected.Shortcut = null;
        selected.FollowUpJingleIds = [first.Id]; selected.FollowUpFadeInSeconds = .9; selected.FollowUpFadeOutSeconds = 1.2;
        selected.DeckVariationEnabled = false; selected.MaxConsecutiveFromSameDeck = 7;
        await Idle();
        Check(JsonSerializer.Serialize(project.Settings) == original, "Editing never commits before Save");
        window.Close();
        Check(JsonSerializer.Serialize(project.Settings) == original, "Cancel leaves original normalized settings unchanged");
        window = new RandomPlayerSettingsWindow(project);
        Check(await window.Initialization, "Reopening gets fresh availability");
        var untouched = window.ViewData.Setups[1].Profiles[0];
        Check(!untouched.HasRealizedDecks, "Unvisited setup remains unbuilt");
        Check(window.TryApplyDraft(), "Valid draft commits");
        Check(!untouched.HasRealizedDecks, "Save does not materialize unvisited groups");
        Check(JsonSerializer.Serialize(project.Settings.RandomPoolSetups.Select(setup => setup.Profiles.ToArray()).ToArray()) == JsonSerializer.Serialize(expected), "Saved models exactly match former projection for all setups");
        Check(ReferenceEquals(project.Settings.RandomPoolProfiles, project.Settings.RandomPoolSetups[0].Profiles), "Legacy active group alias retained");
        window.Close();

        project = Fixture(root, 85, 1);
        window = new RandomPlayerSettingsWindow(project); Check(await window.Initialization, "Mutation fixture ready");
        var oldGroup = window.ViewData.SelectedProfile!;
        File.Delete(project.Decks[0].Jingles[0].FilePath);
        Call(window, "AddProfile_Click", null, new RoutedEventArgs());
        await Until(() => window.ViewData.Profiles.Count == 2);
        Check(oldGroup.Decks.Sum(d => d.Jingles.Count) == 85 && window.ViewData.SelectedProfile!.Decks.Sum(d => d.Jingles.Count) == 84, "Add validates fresh files while retaining existing draft availability");
        Call(window, "DuplicateProfile_Click", null, new RoutedEventArgs()); await Until(() => window.ViewData.Profiles.Count == 3);
        Check(window.ViewData.SelectedProfile!.Name.Contains("kopia") && window.ViewData.SelectedProfile.Id != oldGroup.Id, "Group duplication retains existing naming/identity behavior");
        Call(window, "DuplicateSetup_Click", null, new RoutedEventArgs()); await Until(() => window.ViewData.Setups.Count == 2);
        Check(window.ViewData.Profiles.Count == 3, "Setup duplication keeps every group");
        Call(window, "AddSetup_Click", null, new RoutedEventArgs()); await Until(() => window.ViewData.Setups.Count == 3);
        Check(window.ViewData.Profiles.Count == 1 && window.ViewData.SelectedProfile!.SelectedSoundCount == 0, "New setup retains default empty group");
        Call(window, "RemoveProfile_Click", null, new RoutedEventArgs()); await Until(() => window.ViewData.Profiles.Count == 1 && window.ViewData.IsReady);
        Check(window.ViewData.SelectedProfile!.Name == "Slumpgrupp 1", "Removing last group recreates default");
        window.Close();
    }

    private static async Task CheckVisibleSelection(string root, string locale)
    {
        var originalLanguage = LanguageService.CurrentLanguage;
        LanguageService.SetLanguage(locale);
        var project = Fixture(root, 85, 6);
        var unchanged = JsonSerializer.Serialize(project.Settings);
        var window = new RandomPlayerSettingsWindow(project) { ShowActivated = false, ShowInTaskbar = false };
        try
        {
            window.Show(); Check(await window.Initialization, "Visible async preparation completes"); await Idle();
            var tabs = (TabControl)window.FindName("DeckTabs");
            var songs = VisualChildren(tabs).OfType<FloorballDJ.Controls.VirtualizingSongItemsControl>().Single();
            var panel = songs.Panel;
            void CurrentView()
            {
                var deck = (RandomPlayerDeckEditor)tabs.SelectedContent!;
                Check(ReferenceEquals(songs, VisualChildren(tabs).OfType<FloorballDJ.Controls.VirtualizingSongItemsControl>().Single()) &&
                    ReferenceEquals(panel, songs.Panel), "Group/setup/deck changes retain one actual song control and panel");
                Check(ReferenceEquals(songs.ItemsSource, deck.VisibleJingles) &&
                    VisualChildren(songs).OfType<CheckBox>().All(card => card.DataContext is RandomPlayerJingleEditor item && deck.Jingles.Contains(item)),
                    "Retained cards and source always belong to the current deck");
            }
            Check(tabs.SelectedIndex == 0 && ReferenceEquals(tabs.SelectedContent, window.ViewData.SelectedProfile!.Decks[0]), "Opening a loaded empty tab control selects and displays the first populated deck");
            Check(VisualChildren(tabs).OfType<CheckBox>().Any(check => check.Content as string == LanguageService.Translate("Använd hela decket")), "Deck template uses the selected language");
            Check(VisualChildren(tabs).OfType<TextBlock>().Any(text => text.Text == LanguageService.Translate("Nya spelbara jinglar som senare läggs till i decket inkluderas automatiskt.")), "Late-created deck help uses the selected language");
            tabs.SelectedIndex = 1; await Idle();
            CurrentView();
            Check(tabs.SelectedIndex == 1, "Later generation preserves an existing deck choice");
            window.ViewData.SelectedProfile = window.ViewData.Profiles[1]; await Idle();
            CurrentView();
            Check(tabs.SelectedIndex >= 0 && ReferenceEquals(tabs.SelectedContent, window.ViewData.SelectedProfile!.Decks[tabs.SelectedIndex]), $"Changing a shown group displays a deck from the new group: index {tabs.SelectedIndex}, items {tabs.Items.Count}, profile {window.ViewData.SelectedProfile?.Name}, content {(tabs.SelectedContent as RandomPlayerDeckEditor)?.Name}");
            window.ViewData.SelectedSetup = window.ViewData.Setups[1]; await Idle();
            CurrentView();
            Check(tabs.SelectedIndex >= 0 && ReferenceEquals(tabs.SelectedContent, window.ViewData.SelectedProfile!.Decks[tabs.SelectedIndex]), "Changing a shown setup displays a deck from the new setup");
            var setup = window.ViewData.SelectedSetup;
            window.ViewData.SelectedSetup = null; await Idle();
            Check(tabs.SelectedContent is null && songs.Items.Count == 0 && !songs.IsVisible,
                "Empty selection hides retained content and clears former items");
            window.ViewData.SelectedSetup = setup; await Idle(); CurrentView();
            for (var i = 0; i < 20; i++)
            {
                window.ViewData.SelectedProfile = window.ViewData.Profiles[i % 2];
                tabs.SelectedIndex = i % 3;
            }
            await Idle(); CurrentView();
            window.ShowActivated = true; window.Activate();
            var captured = VisualChildren(songs).OfType<CheckBox>().First(card => card.IsEnabled);
            Check(Mouse.Capture(captured), "Current card accepts real WPF mouse capture");
            window.ViewData.SelectedProfile = window.ViewData.Profiles.First(group => !ReferenceEquals(group, window.ViewData.SelectedProfile));
            await Idle(); CurrentView();
            Check(!ReferenceEquals(Mouse.Captured, captured) && !VisualChildren(songs).Contains(captured),
                "Captured card is released and discarded instead of rebound to another group");
            tabs.SelectedIndex = 1; await Idle(); CurrentView();
            Check(songs.FocusItem(0), "Retained view accepts focus after rapid source changes");
            var formerlyFocused = songs.FocusedItem;
            // Schedule a same-source restore, then replace its group before it runs.
            songs.RestoreFocusLater(formerlyFocused!);
            window.ViewData.SelectedProfile = window.ViewData.Profiles.First(group => !ReferenceEquals(group, window.ViewData.SelectedProfile));
            await Idle(); CurrentView();
            Check(!ReferenceEquals(songs.FocusedItem, formerlyFocused) &&
                (Keyboard.FocusedElement is not CheckBox focused || focused.DataContext is not RandomPlayerJingleEditor item ||
                    ((RandomPlayerDeckEditor)tabs.SelectedContent!).Jingles.Contains(item)), "Old-group pending focus never restores a stale editor");
            // Native TabControl remains the reference for the complete deck view.
            var reference = new TabControl { ItemsSource = tabs.ItemsSource, SelectedItem = tabs.SelectedItem,
                ContentTemplate = tabs.ContentTemplate, ItemTemplate = tabs.ItemTemplate, Template = tabs.Template,
                FontFamily = tabs.FontFamily, FontSize = tabs.FontSize, FontWeight = tabs.FontWeight,
                FontStyle = tabs.FontStyle, Foreground = tabs.Foreground };
            reference.Width = tabs.ActualWidth; reference.Height = tabs.ActualHeight;
            var comparison = new Window { Content = reference, SizeToContent = SizeToContent.WidthAndHeight,
                ShowActivated = false, ShowInTaskbar = false, Owner = window };
            try
            {
                comparison.Show(); await Idle(); reference.UpdateLayout();
                Check(reference.RenderSize == tabs.RenderSize &&
                    VisualChildren(reference).OfType<CheckBox>().Select(card => card.RenderSize).SequenceEqual(VisualChildren(tabs).OfType<CheckBox>().Select(card => card.RenderSize)),
                    $"Retained deck view has the native tab/content geometry: {tabs.RenderSize}/{reference.RenderSize}; panel actual {songs.Panel!.ViewportHeight}/{songs.Panel.CardHeight}/{songs.Panel.Columns}/{songs.Panel.VerticalOffset}/{songs.Containers}; native {VisualChildren(reference).OfType<FloorballDJ.Controls.SongWrapPanel>().Single().ViewportHeight}/{VisualChildren(reference).OfType<FloorballDJ.Controls.SongWrapPanel>().Single().CardHeight}/{VisualChildren(reference).OfType<FloorballDJ.Controls.SongWrapPanel>().Single().Columns}; actual {string.Join(';', VisualChildren(tabs).OfType<CheckBox>().Select(card => card.RenderSize))}; native {string.Join(';', VisualChildren(reference).OfType<CheckBox>().Select(card => card.RenderSize))}");
            }
            finally { reference.ItemsSource = null; comparison.Close(); }
            var deckTemplate = tabs.ContentTemplate;
            var label = new FrameworkElementFactory(typeof(TextBlock)); label.SetValue(TextBlock.TextProperty, "Replacement deck template");
            tabs.ContentTemplate = new DataTemplate { VisualTree = label }; await Idle();
            Check(!VisualChildren(tabs).OfType<FloorballDJ.Controls.VirtualizingSongItemsControl>().Any() &&
                VisualChildren(tabs).OfType<TextBlock>().Any(text => text.Text == "Replacement deck template"),
                "A genuine content-template change replaces the former visual tree");
            tabs.ContentTemplate = deckTemplate; await Idle();
            songs = VisualChildren(tabs).OfType<FloorballDJ.Controls.VirtualizingSongItemsControl>().Single(); panel = songs.Panel;
            CurrentView();
            songs.RestoreFocusLater(songs.FocusedItem!); window.Close(); await Idle();
        }
        finally { window.Close(); await Idle(); LanguageService.SetLanguage(originalLanguage); }
        Check(JsonSerializer.Serialize(project.Settings) == unchanged, "Visible selection and Cancel never commit draft edits");
    }

    private static IEnumerable<DependencyObject> VisualChildren(DependencyObject root)
    {
        yield return root;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in VisualChildren(VisualTreeHelper.GetChild(root, index))) yield return child;
    }

    private static async Task CheckWorkersAndLifetime(string root)
    {
        var project = Fixture(root, 3, 1);
        var input = RandomSettingsLibraryInput.Capture(project);
        var ownerThread = Environment.CurrentManagedThreadId;
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0; var maximum = 0;
        var service = new RandomSettingsPreparation(path => {
            Check(Environment.CurrentManagedThreadId != ownerThread, "File checks stay off UI");
            maximum = Math.Max(maximum, Interlocked.Increment(ref active)); entered.TrySetResult();
            Check(release.Wait(TimeSpan.FromSeconds(10)), "Blocked file check released"); Interlocked.Decrement(ref active); return File.Exists(path);
        });
        var first = service.PrepareAsync(input, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var canceled = new CancellationTokenSource();
        var queued = service.PrepareAsync(input, canceled.Token); canceled.Cancel();
        try { await queued; throw new InvalidOperationException("Queued work must cancel"); } catch (OperationCanceledException) { }
        release.Set(); await first; Check(maximum == 1, "One admitted active worker; waiting work cancellable");

        foreach (var scenario in new[] { "close", "path", "settings", "profile" })
        {
            project = Fixture(root, 3, 1);
            using var unblock = new ManualResetEventSlim();
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var current = true;
            service = new RandomSettingsPreparation(path => { started.TrySetResult(); Check(unblock.Wait(TimeSpan.FromSeconds(10)), "Window file check released"); return File.Exists(path); });
            var window = new RandomPlayerSettingsWindow(project, () => current, service);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Check(!window.ViewData.IsReady, "Blocked preparation stays safely disabled");
            await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.Input);
            if (scenario == "close")
            {
                var media = Path.Combine(root, "random-blocked-silent.wav");
                using (var writer = new WaveFileWriter(media, new WaveFormat(48000, 16, 2))) writer.Write(new byte[48000 * 4 * 3], 0, 48000 * 4 * 3);
                using var engine = new AudioEngine(); engine.Configure(null, null, -60, -12, 0, .03);
                var primary = new Jingle { FilePath = media, DurationSeconds = 3, FadeInOverrideSeconds = 0, AllowMultipleClicks = true };
                var preview = new Jingle { FilePath = media, DurationSeconds = 3, FadeInOverrideSeconds = 0 };
                engine.Play(primary); engine.SetSecondaryOutput(true); engine.Play(preview); engine.SetSecondaryOutput(false);
                await Until(() => engine.GetCurrentPosition() is { TotalSeconds: > 0 } && engine.GetSecondarySnapshot().Position.TotalSeconds > 0);
                window.Close();
            }
            else if (scenario == "path") project.Decks[0].Jingles[0].FilePath = "changed.wav";
            else if (scenario == "settings") project.Settings.RandomPoolSetups[0].Profiles[0].Name = "changed";
            else current = false;
            unblock.Set();
            Check(!await window.Initialization && !window.ViewData.IsReady && !window.TryApplyDraft(), "Closed/stale result cannot bind or save");
            window.Close();
        }
        var failed = new RandomPlayerSettingsWindow(Fixture(root, 3, 1), null, new RandomSettingsPreparation(_ => throw new IOException("private path")));
        Check(!await failed.Initialization && !failed.ViewData.IsReady && !failed.TryApplyDraft(), "Preparation failure leaves Cancel usable and prevents partial Save"); failed.Close();
        project = Fixture(root, 3, 1);
        var staleSave = new RandomPlayerSettingsWindow(project); Check(await staleSave.Initialization, "Stale-save fixture ready");
        project.Decks[0].Jingles[0].StartSeconds++;
        Check(!staleSave.TryApplyDraft() && !staleSave.ViewData.IsReady, "A source edit after readiness cannot commit an obsolete draft"); staleSave.Close();
    }

    private static async Task CheckDiagnostics(string root)
    {
        var directory = Path.Combine(root, "random-settings-diagnostics");
        var session = PerformanceDiagnostics.Start(directory, Dispatcher.CurrentDispatcher);
        var project = Fixture(root, 3, 1); project.Decks[0].Jingles[0].Title = "Private random fixture title";
        RandomPlayerSettingsWindow window;
        using (PerformanceDiagnostics.BeginCommand("randomSettingsFixture")) window = new RandomPlayerSettingsWindow(project);
        try { Check(await window.Initialization, "Diagnostic window ready"); }
        finally { window.Close(); await session.DisposeAsync(); }
        var logged = File.ReadAllText(Path.Combine(directory, "events.jsonl"));
        Check(logged.Contains("RandomSettingsReady") && logged.Contains("RandomSettingsFileChecks") && logged.Contains("RandomSettingsJingleEditors"), "Opening stages and work counts recorded");
        var events = File.ReadLines(Path.Combine(directory, "events.jsonl")).Select(line => JsonSerializer.Deserialize<JsonElement>(line)).Where(item => item.GetProperty("type").GetString() == "event").ToArray();
        var opened = events.Single(item => item.GetProperty("stage").GetString() == "RandomSettingsOpenRequested");
        Check(opened.GetProperty("commandId").GetInt64() > 0 && events.Where(item => item.GetProperty("stage").GetString() is "RandomSettingsFileProbe" or "RandomSettingsJingleEditors").All(item =>
            item.GetProperty("commandId").GetInt64() == opened.GetProperty("commandId").GetInt64() && item.GetProperty("operationId").GetInt64() == opened.GetProperty("operationId").GetInt64()), "Worker/group stages retain opening correlation after the synchronous input scope ends");
        Check(!logged.Contains(root) && !logged.Contains(project.Decks[0].Jingles[0].Title) && session.DroppedEvents == 0, "Dialog diagnostics preserve privacy and complete capture");
    }

    private static RandomPoolProfile LegacyProjection(FloorballProject project, RandomPoolProfile source) => new() {
        Id = source.Id, Name = source.Name, Shortcut = ShortcutService.Normalize(source.Shortcut),
        DeckIds = project.Decks.Where(deck => source.DeckIds.Contains(deck.Id) && deck.Jingles.Any(item => item.HasAudio && File.Exists(item.FilePath))).Select(deck => deck.Id).Distinct().ToList(),
        JingleIds = project.Decks.SelectMany(deck => deck.Jingles).Where(item => item.HasAudio && File.Exists(item.FilePath) && source.JingleIds.Contains(item.Id)).Select(item => item.Id).Distinct().ToList(),
        FollowUpJingleIds = source.FollowUpJingleIds.Distinct().ToList(), FollowUpFadeInSeconds = source.FollowUpFadeInSeconds, FollowUpFadeOutSeconds = source.FollowUpFadeOutSeconds,
        DeckVariationEnabled = source.DeckVariationEnabled, MaxConsecutiveFromSameDeck = source.MaxConsecutiveFromSameDeck
    };
    private static RandomPoolProfile ToModel(RandomPlayerProfileEditor profile) => (RandomPoolProfile)typeof(RandomPlayerSettingsWindow).GetMethod("ToModel", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [profile])!;
    private static void Call(RandomPlayerSettingsWindow window, string method, params object?[] arguments) => typeof(RandomPlayerSettingsWindow).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, arguments);
    private static async Task Idle() => await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    private static async Task Until(Func<bool> done) { var watch = Stopwatch.StartNew(); while (!done() && watch.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(10); Check(done(), "Async edit/playback completed within ten seconds"); }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    internal static FloorballProject Fixture(string root, int fileCount, int groups)
    {
        var folder = Path.Combine(root, "random-fixture"); Directory.CreateDirectory(folder);
        var project = ProjectService.CreateDefault(); project.Decks.Clear();
        for (var index = 0; index < fileCount; index++)
        {
            if (index % 40 == 0) project.Decks.Add(new Deck { Name = $"Deck {index / 40}", Rows = 8, Columns = 5 });
            var path = Path.Combine(folder, $"file-{index}.wav"); File.WriteAllText(path, "fixture");
            project.Decks[^1].Jingles.Add(new Jingle { Position = index % 40, FilePath = path, Title = $"Låt {index}", DurationSeconds = 120, StartSeconds = 5, EndSeconds = 90 });
        }
        var ids = project.Decks.SelectMany(deck => deck.Jingles).Where((_, index) => index % 3 == 0).Select(item => item.Id).ToList();
        project.Settings.RandomPoolSetups = Enumerable.Range(0, Math.Min(3, groups)).Select(setup => new RandomPoolSetup {
            Name = $"Setup {setup}", Profiles = Enumerable.Range(0, groups).Where(index => index % Math.Min(3, groups) == setup).Select(index => new RandomPoolProfile {
                Name = $"Group {index}", Shortcut = $"Ctrl+F{index % 12 + 1}", DeckIds = [project.Decks[0].Id], JingleIds = ids.ToList(),
                FollowUpJingleIds = ids.Take(2).ToList(), FollowUpFadeInSeconds = .3, FollowUpFadeOutSeconds = .7, DeckVariationEnabled = true, MaxConsecutiveFromSameDeck = 3
            }).ToList()
        }).ToList();
        project.Settings.ActiveRandomPoolSetupId = project.Settings.RandomPoolSetups[0].Id;
        project.Settings.RandomPoolProfiles = project.Settings.RandomPoolSetups[0].Profiles;
        ProjectService.EnsureLayout(project);
        return project;
    }
}
