using System.Collections;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using FloorballDJ.Models;
using FloorballDJ.Services;
using FloorballDJ.ViewModels;
using FloorballDJ.Views;
using NAudio.Wave;

internal static class PresentationFixChecks
{
    internal static async Task RunAsync(string root, string? output)
    {
        if (output is not null) Directory.CreateDirectory(output);
        var language = LanguageService.CurrentLanguage;
        try
        {
            foreach (var locale in new[] { "sv", "en" })
            {
                LanguageService.SetLanguage(locale);
                var project = RandomSettingsChecks.Fixture(root, 132, 3);
                var window = new RandomPlayerSettingsWindow(project) { ShowInTaskbar = false };
                try
                {
                    window.Show(); Check(await window.Initialization, "Visual fixture prepared"); await Idle();
                    window.WindowState = WindowState.Normal; window.Width = 1460; window.Height = 860; await Idle();
                    var tabs = (TabControl)window.FindName("DeckTabs");
                    Check(tabs.Background is SolidColorBrush background && background.Color == Color.FromRgb(14, 23, 38), "Tab body has explicit dark background independent of native theme");
                    var deck = window.ViewData.SelectedProfile!.Decks[0];
                    foreach (var selected in new[] { false, true })
                    {
                        deck.IncludeWholeDeck = selected; foreach (var song in deck.Jingles) song.IsIncluded = selected;
                        await Idle();
                        var songs = Children(tabs).OfType<FloorballDJ.Controls.VirtualizingSongItemsControl>().Single();
                        Check(Children(songs).OfType<CheckBox>().Any(), "Visible song cards were realized");
                        foreach (var card in Children(songs).OfType<CheckBox>())
                        {
                            var border = (Border)card.Template.FindName("Card", card);
                            Check(border.Background is SolidColorBrush brush && brush.Color.A == 255 && brush.Color.R < 40,
                                "Selected and unselected cards have opaque dark surfaces");
                            Check(Children(card).OfType<TextBlock>().Any(text => text.Text == ((RandomPlayerJingleEditor)card.DataContext).Title &&
                                text.Foreground is SolidColorBrush foreground && foreground.Color.R > 220), "Song title stays readable even when whole-deck cards are disabled");
                        }
                        if (output is not null) SaveImage(tabs, Path.Combine(output, $"random-{locale}-{(selected ? "selected" : "unselected")}.png"));
                    }
                }
                finally { window.Close(); await Idle(); }
            }
            await CheckAutoplay(root, output);
            await CheckLimiter(root);
            await CheckMainButton(root, output);
        }
        finally { LanguageService.SetLanguage(language); }
        if (output is not null) File.WriteAllText(Path.Combine(output, "presentation-fix-checks.json"), JsonSerializer.Serialize(new {
            version = "rc-workflow-fixes-v1", passed = true, darkSelectedAndUnselected = true, autoplayDropdownAllDecks = true,
            sessionDarkChecked = true, manualAutoplayExitWarning = true, directShortcutSwitchesView = true,
            livePrimaryLimiterOnly = true, previewAndNormalizationRetained = true, sessionOnly = true, newProfileHeadroomDefaultOff = true,
            limitation = "WPF rendered fixtures and deterministic DSP/silent WASAPI checks; not loud playback or physical venue qualification."
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("PASS: dark selected/unselected song cards, accessible autoplay dropdown, live main-only limiter toggle, preview/normalization retention and new-profile headroom default.");
    }

    private static async Task CheckAutoplay(string root, string? output)
    {
        using var audio = new AudioEngine(); var projects = new ProjectService();
        using var vm = new MainViewModel(projects, new ProfilePreferencesService(projects.AppDataDirectory, projects.DefaultProjectPath), audio);
        vm.Settings.DeckCount = 15;
        while (vm.Decks.Count < 15) vm.Decks.Add(new Deck { Name = "Deck " + vm.Decks.Count, Jingles = [new Jingle { FilePath = "fixture.wav", Title = "Song " + vm.Decks.Count }] });
        var view = new AutoplayView { DataContext = vm }; var window = new Window { Content = view, Width = 1040, Height = 700, ShowInTaskbar = false };
        try
        {
            window.Show(); await view.RefreshAvailableAsync(); await Idle();
            var combo = (ComboBox)view.FindName("DeckFilterCombo"); var list = (ListBox)view.FindName("AvailableList");
            Check(combo.Items.Count == 16 && combo.SelectedIndex == 0, "All plus every one of fifteen decks is directly selectable");
            for (var i = 1; i < combo.Items.Count; i++)
            {
                combo.SelectedIndex = i; await Idle();
                Check(list.Items.Cast<Jingle>().SequenceEqual(vm.Decks[i - 1].Jingles.Where(j => j.HasAudio).OrderBy(j => j.Title)), "Dropdown filters the same complete deck source");
            }
            combo.SelectedIndex = 0; await Idle(); combo.IsDropDownOpen = true; await Idle();
            Check(combo.IsDropDownOpen && combo.ActualWidth > 160, "Dropdown opens at laptop width");
            if (output is not null)
            {
                SaveImage(view, Path.Combine(output, "autoplay-dropdown.png"));
                var popup = (System.Windows.Controls.Primitives.Popup)combo.Template.FindName("PART_Popup", combo);
                SaveImage((FrameworkElement)popup.Child, Path.Combine(output, "autoplay-deck-menu.png"));
            }
        }
        finally { window.Close(); await Idle(); }
    }

    private sealed class ConstantSource : ISampleProvider
    {
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        public int Read(float[] buffer, int offset, int count) { Array.Fill(buffer, 1.5f, offset, count); return count; }
    }
    private static async Task CheckLimiter(string root)
    {
        var effects = new DjEffectsSampleProvider(new ConstantSource(), new Jingle(), -1); var buffer = new float[128];
        effects.Read(buffer, 0, buffer.Length); Check(buffer.All(value => value <= 1), "Enabled limiter limits overrange samples");
        effects.SetLimiterEnabled(false); effects.Read(buffer, 0, buffer.Length); Check(buffer.All(value => value > 1.4f), "Live bypass takes effect on the same provider");
        effects.SetLimiterEnabled(true); effects.Read(buffer, 0, buffer.Length); Check(buffer.All(value => value <= 1), "Live protection returns without rebuilding provider");
        Check(!new AppSettings().AutoMixHeadroomEnabled, "New profiles default to no automatic overlay attenuation");
        var path = Path.Combine(root, "limiter-silent.wav");
        using (var writer = new WaveFileWriter(path, new WaveFormat(48000, 16, 2))) writer.Write(new byte[48000 * 4 * 4], 0, 48000 * 4 * 4);
        using var audio = new AudioEngine(); audio.Configure(null, null, -60, -12, 0, .03, autoMixHeadroomEnabled: false);
        audio.Play(new Jingle { FilePath = path, DurationSeconds = 4, NormalizationEnabled = true, NormalizationGainDb = -10 });
        audio.SetSecondaryOutput(true); audio.Play(new Jingle { FilePath = path, DurationSeconds = 4 }); audio.SetSecondaryOutput(false);
        await Task.Delay(80);
        object[] Voices() => ((IEnumerable)typeof(AudioEngine).GetField("_voices", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(audio)!).Cast<object>().ToArray();
        bool Secondary(object voice) => (bool)voice.GetType().GetProperty("UsesSecondaryDevice")!.GetValue(voice)!;
        DjEffectsSampleProvider Effects(object voice) => (DjEffectsSampleProvider)voice.GetType().GetProperty("Effects")!.GetValue(voice)!;
        var primary = Voices().Single(v => !Secondary(v)); var preview = Voices().Single(Secondary);
        var gain = primary.GetType().GetProperty("TargetVolume")!.GetValue(primary);
        var effectPath = Path.Combine(root, "effect-silent.wav"); File.Copy(path, effectPath, overwrite: true);
        audio.Play(new Jingle { FilePath = effectPath, DurationSeconds = 4, PlayMode = JinglePlayMode.Mix });
        Check(Voices().Count(v => !Secondary(v)) == 2 && Equals(gain, primary.GetType().GetProperty("TargetVolume")!.GetValue(primary)),
            "Mix effect overlays the playing song without lowering its target gain");
        audio.SetPrimaryLimiterBypassed(true);
        Check(!Effects(primary).LimiterEnabled && Effects(preview).LimiterEnabled && Equals(gain, primary.GetType().GetProperty("TargetVolume")!.GetValue(primary)),
            "Primary bypass is live, leaves preview protected and keeps normalization/gain");
        audio.Configure(null, null, -60, -12, 0, .03, autoMixHeadroomEnabled: false);
        Check(!Effects(primary).LimiterEnabled && Effects(preview).LimiterEnabled, "Ordinary reconfiguration retains session-only primary override");
        audio.SetPrimaryLimiterBypassed(false); Check(Effects(primary).LimiterEnabled && Effects(preview).LimiterEnabled, "Button restores profile protection");
        var projects = new ProjectService(); using var vm = new MainViewModel(projects, new ProfilePreferencesService(projects.AppDataDirectory, projects.DefaultProjectPath), audio);
        var settings = JsonSerializer.Serialize(vm.Settings); vm.PrimaryLimiterBypassed = true;
        Check(JsonSerializer.Serialize(vm.Settings) == settings, "Session toggle never alters serialized settings");
        vm.PrimaryLimiterBypassed = false; audio.StopAll();
        using var freshAudio = new AudioEngine();
        using var freshVm = new MainViewModel(projects, new ProfilePreferencesService(projects.AppDataDirectory, projects.DefaultProjectPath), freshAudio);
        Check(!freshVm.PrimaryLimiterBypassed, "A fresh app session starts without the temporary bypass");
        Check(JsonSerializer.Deserialize<AppSettings>("{\"AutoMixHeadroomEnabled\":true}")!.AutoMixHeadroomEnabled,
            "An existing explicit headroom preference remains supported");
    }
    private static async Task CheckMainButton(string root, string? output)
    {
        var previousDirectory = Environment.GetEnvironmentVariable("FLOORBALLDJ_DATA_DIR");
        var previousMain = Application.Current.MainWindow;
        var directory = Path.Combine(root, "main-limiter-ui"); Directory.CreateDirectory(directory);
        Environment.SetEnvironmentVariable("FLOORBALLDJ_DATA_DIR", directory);
        MainWindow? main = null; var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var projects = new ProjectService(); await projects.SaveAsync(RandomSettingsChecks.Fixture(directory, 12, 2), projects.DefaultProjectPath);
            main = new MainWindow(new LicenseService(), shutdownApplicationOnClose: false) { ShowInTaskbar = false };
            main.Closed += (_, _) => closed.TrySetResult(); Application.Current.MainWindow = main; main.Show();
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (!main.StartupReady && DateTime.UtcNow < deadline) await Task.Delay(20);
            Check(main.StartupReady, "Real main window completed startup"); await Idle();
            main.WindowState = WindowState.Normal; main.Width = 1366; main.Height = 768; await Idle();
            var vm = (MainViewModel)main.DataContext;
            var toggle = (System.Windows.Controls.Primitives.ToggleButton)main.FindName("PrimaryLimiterToggle");
            var session = (System.Windows.Controls.Primitives.ToggleButton)main.FindName("SessionToggle");
            var sessionChrome = (Border)session.Template.FindName("ToggleChrome", session);
            Check(session.IsChecked == true && sessionChrome.Background is SolidColorBrush surface && surface.Color.R < 50 &&
                session.Foreground is SolidColorBrush ink && ink.Color.G > ink.Color.R, "Checked Session keeps a dark surface and green indicator/text");
            var settings = JsonSerializer.Serialize(vm.Settings);
            foreach (var locale in new[] { "sv", "en" })
            {
                LanguageService.SetLanguage(locale);
                foreach (var bypass in new[] { false, true })
                {
                    toggle.IsChecked = bypass; await Task.Delay(80); await Idle();
                    Check(vm.PrimaryLimiterBypassed == bypass && (string)toggle.Content == LanguageService.Translate(bypass ? "LIMITER AV" : "LIMITER"),
                        "Real main button updates the session flag and localized label");
                    Check(JsonSerializer.Serialize(vm.Settings) == settings, "Main button preserves profile settings");
                    var chrome = (Border)toggle.Template.FindName("LimiterChrome", toggle);
                    Check(chrome.Background is SolidColorBrush color && (bypass ? color.Color.R > 240 && color.Color.B < 120 : color.Color.R < 50),
                        "Native theme cannot hide the amber bypass state");
                    Check(toggle.ActualHeight >= 24 && toggle.ActualWidth > 70 && toggle.IsVisible, "Button is usable at laptop window size");
                    if (output is not null) SaveImage((FrameworkElement)((FrameworkElement)toggle.Parent).Parent,
                        Path.Combine(output, $"main-volume-{locale}-{(bypass ? "bypass" : "protected")}.png"));
                }
            }
            toggle.IsChecked = false;
            var autoplay = (AutoplayView)main.FindName("EmbeddedAutoplay");
            var queued = vm.Decks[0].Jingles[0]; vm.ReplaceQueue([queued]);
            vm.SetAutoplayMode(true); autoplay.Visibility = Visibility.Visible;
            var prompts = 0; main.ConfirmAutoplayExit = () => { prompts++; return false; };
            var selectedDeck = vm.SelectedDeck;
            var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = Mouse.PreviewMouseWheelEvent };
            typeof(MainWindow).GetMethod("DeckTabs_PreviewMouseWheel", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, [main, wheel]);
            Check(prompts == 1 && wheel.Handled && vm.AutoplayModeActive && vm.SelectedDeck == selectedDeck && vm.PlaybackQueue.Count == 1,
                "Declining an actual deck-wheel exit retains view, selected deck and queue");
            main.ConfirmAutoplayExit = () => { prompts++; return true; };
            Check(main.TryLeaveAutoplay() && !vm.AutoplayModeActive && autoplay.Visibility == Visibility.Collapsed && vm.PlaybackQueue.Count == 1,
                "Confirming leaves Autoplay but retains queued songs");
            var path = Path.Combine(directory, "shortcut-silent.wav");
            using (var writer = new WaveFileWriter(path, new WaveFormat(48000, 16, 2))) writer.Write(new byte[48000 * 4 * 4], 0, 48000 * 4 * 4);
            var direct = vm.Decks[1].Jingles[0]; direct.FilePath = path; direct.Shortcut = "F12"; direct.ShortcutSwitchesDeck = false;
            vm.NotifyJingleChanged(); vm.SetAutoplayMode(true); autoplay.Visibility = Visibility.Visible;
            main.ConfirmAutoplayExit = () => throw new InvalidOperationException("Live shortcut must not be delayed by an exit prompt");
            Keyboard.ClearFocus();
            using (var source = new HwndSource(new HwndSourceParameters("rc-view-shortcut") { Width = 1, Height = 1, WindowStyle = 0 }))
                await Program.DispatchKeyAsync(main, source, Key.F12);
            Check(!vm.AutoplayModeActive && autoplay.Visibility == Visibility.Collapsed && vm.SelectedDeck == vm.Decks[1] && vm.PlaybackQueue.Count == 1,
                "Direct shortcut exits to its deck even without ShortcutSwitchesDeck; queue survives");
            vm.Audio.StopAll();
            main.Close(); await closed.Task.WaitAsync(TimeSpan.FromSeconds(20));
        }
        finally
        {
            if (main is { IsVisible: true }) { main.Close(); await closed.Task.WaitAsync(TimeSpan.FromSeconds(20)); }
            Application.Current.MainWindow = previousMain;
            Environment.SetEnvironmentVariable("FLOORBALLDJ_DATA_DIR", previousDirectory);
        }
    }
    private static IEnumerable<DependencyObject> Children(DependencyObject root)
    {
        yield return root; for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) foreach (var child in Children(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static async Task Idle() => await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    private static void SaveImage(FrameworkElement element, string path)
    {
        var image = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen()) context.DrawRectangle(new VisualBrush(element), null, new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        image.Render(drawing); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using var stream = File.Create(path); encoder.Save(stream);
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
