using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FloorballDJ.Controls;
using FloorballDJ.Models;
using FloorballDJ.Services;
using FloorballDJ.Views;
using NAudio.Wave;

internal static class RandomSettingsLayoutChecks
{
    internal static async Task RunAsync(string root, string? output)
    {
        var observations = new List<object>();
        var language = LanguageService.CurrentLanguage;
        try
        {
            foreach (var locale in new[] { "en", "sv" })
            {
                LanguageService.SetLanguage(locale);
                var project = RandomSettingsChecks.Fixture(root, 2000, 6);
                var all = project.Decks.SelectMany(deck => deck.Jingles).Where(jingle => jingle.HasAudio).ToArray();
                all[0].Title = new string('W', 180); // Exercise trimming on the intrinsically measured production card.
                var id = project.Decks[0].Id;
                project.Decks.Clear();
                var large = new Deck { Id = id, Name = new string('W', 180), Rows = 50, Columns = 12, PageCount = 4 };
                for (var i = 0; i < all.Length; i++) { all[i].Position = i; large.Jingles.Add(all[i]); }
                project.Decks.Add(large);
                foreach (var group in project.Settings.RandomPoolSetups.SelectMany(setup => setup.Profiles)) group.JingleIds = [];
                ProjectService.EnsureLayout(project);
                var unchanged = JsonSerializer.Serialize(project.Settings);
                var window = new RandomPlayerSettingsWindow(project) { ShowInTaskbar = false };
                try
                {
                    window.Show();
                    Check(await window.Initialization.WaitAsync(TimeSpan.FromSeconds(10)), "A 100-percent single-deck overview reaches idle");
                    await Idle();
                    window.WindowState = WindowState.Normal; window.Width = 1460; window.Height = 860; await Idle();
                    var tabs = (TabControl)window.FindName("DeckTabs");
                    var songs = Children(window).OfType<VirtualizingSongItemsControl>().Single();
                    var scroll = Children(songs).OfType<ScrollViewer>().Single();
                    var panel = songs.Panel!;
                    var deck = window.ViewData.SelectedProfile!.Decks[0];
                    Check(deck.Jingles.Count == 2000, "Draft retains every song");
                    Bound(songs);
                    CheckReferenceWrap(songs);
                    var pool = (ListBox)window.FindName("PoolItemsList");
                    var indicators = Children(pool).OfType<FrameworkElement>().Where(item => item.Name == "PART_Indicator").ToArray();
                    Check(indicators.Length == 1 && indicators[0].ActualWidth <= pool.ActualWidth, "100-percent indicator has finite width");
                    var overviewWidth = indicators[0].ActualWidth;
                    var measures = 0;
                    SizeChangedEventHandler changing = (_, _) => measures++;
                    indicators[0].SizeChanged += changing;
                    await Task.Delay(120); await Idle();
                    indicators[0].SizeChanged -= changing;
                    Check(measures == 0 && indicators[0].ActualWidth == overviewWidth && indicators[0].IsMeasureValid && indicators[0].IsArrangeValid,
                        "Settled overview does not continually grow or request layout");
                    Check(Children(pool).OfType<TextBlock>().Any(text => text.ToolTip as string == large.Name), "Full long deck name remains available as a tooltip");

                    deck.IncludeWholeDeck = false;
                    foreach (var item in deck.Jingles) item.IsIncluded = false;
                    await Idle();
                    Check(window.ViewData.PoolItems.Count == 0 && Children(pool).OfType<ProgressBar>().Count() == 0, "Empty pool removes indicators and reaches stable idle");
                    deck.Jingles[^1].IsIncluded = true;
                    await Idle();
                    var work = panel.Work;
                    scroll.ScrollToBottom(); await Idle(); Bound(songs);
                    var last = Children(songs).OfType<CheckBox>().Single(check => ReferenceEquals(check.DataContext, deck.Jingles[^1]));
                    Check(last.IsChecked == true && last.IsEnabled, "Offscreen individual state binds when the final song is realized");
                    Check(panel.Work.NewContainers == work.NewContainers && panel.Work.Recycled > work.Recycled, "Distant scrolling reuses the existing bounded container pool");
                    CheckReferenceWrap(songs);
                    deck.IncludeWholeDeck = true; await Idle();
                    Check(Children(songs).OfType<CheckBox>().All(check => check.IsChecked == true && !check.IsEnabled), "Whole-deck display/locking applies to recycled cards");
                    deck.IncludeWholeDeck = false; await Idle();
                    Check(deck.Jingles.Count(item => item.IsIncluded) == 1 && last.IsChecked == true && last.IsEnabled, "Whole-deck toggle retains all individual states");
                    scroll.ScrollToTop(); await Idle();
                    window.Activate();
                    Check(songs.FocusItem(0), "First card accepts real WPF keyboard focus");
                    for (var i = 0; i < 95; i++) PressKey(songs, Key.Tab);
                    Check(ReferenceEquals(Focused(), deck.Jingles[95]), "Tab crosses unrealized rows in complete source order");
                    PressKey(songs, Key.Down);
                    Check(ReferenceEquals(Focused(), deck.Jingles[95 + panel.Columns]), "Down navigates one wrapped row");
                    PressKey(songs, Key.End);
                    Check(ReferenceEquals(Focused(), deck.Jingles[^1]), "End realizes and focuses the last song");
                    PressKey(songs, Key.Tab);
                    Check(!songs.IsKeyboardFocusWithin, "Tab leaves the final card without trapping focus");
                    Check(songs.FocusItem(0), "Restore first-card focus");
                    typeof(VirtualizingSongItemsControl).GetMethod("LeaveList", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(songs, [true]);
                    Check(!songs.IsKeyboardFocusWithin, "Reverse navigation leaves the first card");
                    songs.Focus(); // OnGotKeyboardFocus redirects to the card, so owner.Focus() may return false.
                    Check(ReferenceEquals(Focused(), deck.Jingles[0]), "Entering the list starts at its first source item");
                    PressKey(songs, Key.Right); PressKey(songs, Key.Left);
                    Check(ReferenceEquals(Focused(), deck.Jingles[0]), "Horizontal keys preserve wrapped source order");
                    PressKey(songs, Key.PageDown); Bound(songs);
                    Check(deck.Jingles.IndexOf(Focused()!) > 0, "PageDown reaches another viewport");
                    PressKey(songs, Key.Home);
                    var first = (CheckBox)Keyboard.FocusedElement;
                    var included = deck.Jingles[0].IsIncluded;
                    PressKey(songs, Key.Space); KeyUp(first, Key.Space);
                    Check(deck.Jingles[0].IsIncluded != included, "Space still toggles the bound checkbox");
                    scroll.ScrollToBottom(); await Idle();
                    Check(songs.FocusItem(songs.Items.Count - 1), "No-op search fixture focuses an offscreen song");
                    var unchangedWork = panel.Work; var unchangedOffset = panel.VerticalOffset;
                    var unchangedCard = Keyboard.FocusedElement;
                    Call(window, "ApplySearch", ""); await Idle(); Bound(songs);
                    Check(panel.Work.Resets == unchangedWork.Resets && panel.Work.NewContainers == unchangedWork.NewContainers &&
                        panel.VerticalOffset == unchangedOffset && ReferenceEquals(Keyboard.FocusedElement, unchangedCard),
                        "Repeated empty search preserves the actual focused card, scroll and container pool");
                    var focused = Focused();
                    Call(window, "SortButton_Click", new Button { Tag = "desc" }, new RoutedEventArgs()); await Idle();
                    Check(ReferenceEquals(Focused(), focused), "Sorting retains focused song identity");
                    Check(deck.Jingles.SequenceEqual(deck.Jingles.OrderByDescending(item => item.Title, StringComparer.CurrentCultureIgnoreCase).ThenBy(item => item.OriginalOrder)), "Culture-aware stable descending order retained");
                    Call(window, "SortButton_Click", new Button { Tag = "deck" }, new RoutedEventArgs()); await Idle();
                    Check(deck.Jingles.Select(item => item.OriginalOrder).SequenceEqual(Enumerable.Range(0, 2000)), "Original deck order restored exactly");
                    ((TextBox)window.FindName("SearchBox")).Focus();
                    Call(window, "ApplySearch", "lat 1999"); await Idle(); Bound(songs);
                    Check(songs.Items.Count == 1 && Children(songs).OfType<CheckBox>().Count() == 1 &&
                        ReferenceEquals(Children(songs).OfType<CheckBox>().Single().DataContext, deck.Jingles[^1]), "Search realizes its offscreen match while retaining full draft");
                    Call(window, "ApplySearch", "no matching song"); await Idle();
                    Check(songs.Items.Count == 0 && Children(songs).OfType<CheckBox>().Count() == 0 && panel.ExtentHeight == 0, "Empty search releases cards and resets scrolling");
                    Call(window, "ApplySearch", ""); await Idle(); Bound(songs);
                    foreach (var width in new[] { 1040d, 1200d, 1600d })
                    {
                        window.Width = width; await Idle(); scroll.ScrollToTop(); await Idle();
                        CheckReferenceWrap(songs); Bound(songs);
                    }
                    songs.FontSize = 22; await Idle(); CheckReferenceWrap(songs); Bound(songs);
                    songs.FontSize = window.FontSize; await Idle();
                    var originalFamily = songs.FontFamily;
                    songs.FontFamily = new FontFamily("Cascadia Mono"); songs.FontWeight = FontWeights.Bold; songs.FontStyle = FontStyles.Italic;
                    await Idle(); CheckReferenceWrap(songs); Bound(songs);
                    songs.FontFamily = originalFamily; songs.FontWeight = window.FontWeight; songs.FontStyle = window.FontStyle; await Idle();
                    if (locale == "en") await CheckPlayback(root, window, songs, scroll);
                    Check(JsonSerializer.Serialize(project.Settings) == unchanged, "Layout, focus, search and draft edits do not change saved settings");
                    Check(window.TryApplyDraft(), "Draft can be saved after recycling/filter/sort");
                    Check(project.Settings.RandomPoolProfiles[0].JingleIds.Contains(deck.Jingles[^1].JingleId), "Save retains an individual choice that started offscreen");
                    observations.Add(new { language = locale, completeSongCount = deck.Jingles.Count, panel.RealizedCount,
                        panel.Columns, panel.CardHeight, overviewWidth, realDpi = VisualTreeHelper.GetDpi(window).PixelsPerInchX });
                }
                finally { window.Close(); await Idle(); }
                var cancelled = new RandomPlayerSettingsWindow(project);
                Check(await cancelled.Initialization, "Cancel fixture initializes");
                var beforeCancel = JsonSerializer.Serialize(project.Settings);
                cancelled.ViewData.SelectedProfile!.Decks[0].Jingles[^1].IsIncluded = false;
                cancelled.Close();
                Check(JsonSerializer.Serialize(project.Settings) == beforeCancel, "Cancel retains the saved offscreen choice");
            }
        }
        finally { LanguageService.SetLanguage(language); }
        if (output is not null)
        {
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output, "random-settings-layout-checks.json"), JsonSerializer.Serialize(new {
                version = "p4.5-layout-checks-v1", passed = true, checkedUtc = DateTimeOffset.UtcNow, observations,
                checks = "bounded recycling, 100/0-percent overview, long-name tooltip, en/sv, source order, reference WrapPanel geometry/card pixels, three sizes, changed font, keyboard navigation/focus/Space, search including empty/offscreen hit, whole-deck state, Save/Cancel, main+preview progress during repeated scrolling",
                limitation = "Shown synthetic desktop fixtures at current monitor DPI, WPF routed key events with actual keyboard focus, not OS hotkey/onset or physical alternate-DPI/monitor qualification. Playback positions demonstrate ongoing output, not an acoustic/glitch-free assertion."
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        Console.WriteLine("PASS: shown random settings bounded wrapped recycling, reference layout/pixels, focus/navigation, search/sort/offscreen choices, stable overview and main/preview progress.");
    }

    private static void CheckReferenceWrap(VirtualizingSongItemsControl songs)
    {
        var panel = songs.Panel!;
        var reference = new ItemsControl { ItemsSource = songs.ItemsSource, ItemTemplate = songs.ItemTemplate,
            FontFamily = songs.FontFamily, FontSize = songs.FontSize, FontWeight = songs.FontWeight, Foreground = songs.Foreground,
            FontStyle = songs.FontStyle, FontStretch = songs.FontStretch, UseLayoutRounding = songs.UseLayoutRounding,
            SnapsToDevicePixels = songs.SnapsToDevicePixels };
        TextOptions.SetTextFormattingMode(reference, TextOptions.GetTextFormattingMode(songs));
        TextOptions.SetTextRenderingMode(reference, TextOptions.GetTextRenderingMode(songs));
        reference.Template = new ControlTemplate(typeof(ItemsControl)) { VisualTree = new FrameworkElementFactory(typeof(ItemsPresenter)) };
        reference.ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(WrapPanel)));
        reference.Measure(new Size(panel.ViewportWidth, double.PositiveInfinity));
        reference.Arrange(new Rect(0, 0, panel.ViewportWidth, reference.DesiredSize.Height)); reference.UpdateLayout();
        var wrap = Children(reference).OfType<WrapPanel>().Single();
        var original = Children(reference).OfType<CheckBox>().ToArray();
        foreach (var card in Children(songs).OfType<CheckBox>())
        {
            var index = songs.Items.IndexOf(card.DataContext);
            var actual = card.TransformToAncestor(panel).Transform(new Point());
            var expected = original[index].TransformToAncestor(wrap).Transform(new Point());
            expected.Y -= panel.VerticalOffset;
            Check(Math.Abs(actual.X - expected.X) < .01 && Math.Abs(actual.Y - expected.Y) < .01 && card.RenderSize == original[index].RenderSize,
                $"Wrapped positions/sizes match original WrapPanel at index {index}: {actual}/{expected}, {card.RenderSize}/{original[index].RenderSize}");
            // The shown window can be underneath the user's mouse; the detached
            // reference has no mouse. Compare equivalent non-hover states.
            if (!card.IsMouseOver) Check(Pixels(card).SequenceEqual(Pixels(original[index])), "Original card template renders identical pixels for song " + index);
        }
        reference.ItemsSource = null;
    }
    private static byte[] Pixels(FrameworkElement element)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element); var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0); return pixels;
    }
    private static void Bound(VirtualizingSongItemsControl songs)
    {
        var panel = songs.Panel!;
        var bound = ((int)Math.Ceiling(panel.ViewportHeight / panel.CardHeight) + 3) * panel.Columns + 1;
        Check(panel.RealizedCount <= Math.Min(songs.Items.Count, bound), "Realized cards stay proportional to the viewport, including a focused card");
        var containers = songs.Containers;
        Check(containers.Active == panel.RealizedCount && containers.Active + containers.Cached <= containers.Budget &&
            containers.Budget <= Math.Min(songs.Items.Count, bound), "Active and cached containers together stay within one current viewport budget");
        var checks = Children(songs).OfType<CheckBox>().ToArray();
        Check(checks.Length == panel.RealizedCount && checks.Select(check => check.DataContext).Distinct().Count() == checks.Length,
            "Every realized container displays exactly one distinct song card");
        var first = Math.Max(0, (int)Math.Floor(panel.VerticalOffset / panel.CardHeight) * panel.Columns);
        var end = Math.Min(songs.Items.Count, (int)Math.Ceiling((panel.VerticalOffset + panel.ViewportHeight) / panel.CardHeight) * panel.Columns);
        for (var i = first; i < end; i++) Check(checks.Any(check => ReferenceEquals(check.DataContext, songs.Items[i])), "Every row intersecting the viewport has its real song controls");
    }
    private static async Task CheckPlayback(string root, Window window, VirtualizingSongItemsControl songs, ScrollViewer scroll)
    {
        var path = Path.Combine(root, "layout-output-silent.wav");
        using (var writer = new WaveFileWriter(path, new WaveFormat(48000, 16, 2))) writer.Write(new byte[48000 * 4 * 12], 0, 48000 * 4 * 12);
        using var engine = new AudioEngine(); engine.Configure(null, null, -60, -12, 0, .03);
        engine.Play(new Jingle { FilePath = path, DurationSeconds = 12, FadeInOverrideSeconds = 0 });
        engine.SetSecondaryOutput(true); engine.Play(new Jingle { FilePath = path, DurationSeconds = 12, FadeInOverrideSeconds = 0 }); engine.SetSecondaryOutput(false);
        await Task.Delay(120);
        var main = engine.GetCurrentPosition()!.Value.TotalSeconds; var preview = engine.GetSecondarySnapshot().Position.TotalSeconds;
        for (var i = 0; i < 30; i++) { if (i % 2 == 0) scroll.ScrollToBottom(); else scroll.ScrollToTop(); await Idle(); Bound(songs); }
        await Task.Delay(120);
        Check(engine.GetCurrentPosition()!.Value.TotalSeconds > main && engine.GetSecondarySnapshot().Position.TotalSeconds > preview,
            "Main and preview outputs progress across repeated distant scroll/layout changes");
    }
    private static RandomPlayerJingleEditor? Focused() => (Keyboard.FocusedElement as FrameworkElement)?.DataContext as RandomPlayerJingleEditor;
    private static void PressKey(VirtualizingSongItemsControl songs, Key key)
    {
        var target = Keyboard.FocusedElement as UIElement ?? songs;
        var source = PresentationSource.FromVisual(songs)!;
        var preview = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        target.RaiseEvent(preview);
        if (!preview.Handled) target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key) { RoutedEvent = Keyboard.KeyDownEvent });
    }
    private static void KeyUp(UIElement target, Key key) => target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(target)!, Environment.TickCount, key) { RoutedEvent = Keyboard.KeyUpEvent });
    private static IEnumerable<DependencyObject> Children(DependencyObject root)
    {
        yield return root;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) foreach (var child in Children(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static void Call(Window window, string method, params object?[] args) => window.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, args);
    private static async Task Idle() => await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
