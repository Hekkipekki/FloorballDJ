using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FloorballDJ;
using FloorballDJ.Controls;
using FloorballDJ.Models;
using FloorballDJ.Services;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        var root = Path.Combine(Path.GetTempPath(), "FloorballDJ-rc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("FLOORBALLDJ_DATA_DIR", root);
        var app = new App();
        app.InitializeComponent();
        try
        {
            CheckRamps();
            CheckTabs();
            CheckBackup(root);
            // Deliberately silent WAV: exercises WASAPI without emitting test audio.
            CheckPlayback(root).GetAwaiter().GetResult();
            Console.WriteLine("PASS: sample-complete fades, post-fader meter, wrapped tabs, persisted tab settings, backup layout, live fade snapshots, cancellation, pause and manual-stop events.");
            app.Shutdown();
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
    }

    private static void CheckRamps()
    {
        var reverse = new SmoothGainSampleProvider(new ConstantSource());
        reverse.SetTarget(0, 1);
        var half = new float[48000];
        reverse.Read(half, 0, half.Length);
        reverse.SetTarget(1, .08);
        var recovering = new float[8000];
        reverse.Read(recovering, 0, recovering.Length);
        Check(Math.Abs(recovering[0] - half[^1]) < .001 && recovering[^1] == 1, "Reverse fade continuously from current gain");
        foreach (var seconds in new[] { .03, .75, 2.5, 30.0 })
        {
            var gain = new SmoothGainSampleProvider(new ConstantSource());
            gain.SetTarget(0, seconds);
            Check(!gain.TransitionCompleted.IsCompleted, "Fade must wait for audio frames");
            var meter = new MeteringSampleProvider(gain, 480);
            var peaks = new List<float>();
            meter.StreamVolume += (_, e) => peaks.Add(e.MaxSampleValues[0]);
            var frames = (int)Math.Round(seconds * 48000);
            var samples = new float[frames * 2];
            meter.Read(samples, 0, samples.Length);
            Check(gain.TransitionCompleted.IsCompleted, "Exact frame completion");
            for (var i = 2; i < samples.Length; i += 2)
                Check(samples[i] <= samples[i - 2] && samples[i] >= 0 && samples[i] == samples[i + 1], "Smooth monotonic stereo fade");
            Check(samples[^1] < .00001f, "No abrupt residual at end");
            Check(peaks.Count > 1 && peaks[^1] < peaks[0], "Meter follows actual fade");
            gain.Read(samples, 0, 2);
            Check(samples[0] == 0 && samples[1] == 0, "Silent endpoint");
            gain.SetTarget(1, .5);
            gain.Read(samples, 0, Math.Min(samples.Length, 1000));
            gain.SetTarget(0, 0);
            Check(gain.TransitionCompleted.IsCompleted, "Immediate interrupted fade");
        }
    }

    private static void CheckTabs()
    {
        var panel = new DeckTabsPanel { ItemWidth = 120, ItemHeight = 50, MaxColumns = 3 };
        for (var i = 0; i < 8; i++) panel.Children.Add(new Border { Child = new TextBlock { Text = "Deck " + i } });
        panel.Measure(new Size(600, 1000));
        panel.Arrange(new Rect(0, 0, 600, panel.DesiredSize.Height));
        Check(panel.DesiredSize.Height == 150, "Maximum tabs per row");
        Check(VisualTreeHelper.GetOffset(panel.Children[3]).Y == 50, "Stable second row");
        panel.Measure(new Size(250, 1000));
        panel.Arrange(new Rect(0, 0, 250, panel.DesiredSize.Height));
        Check(panel.DesiredSize.Height == 200, "Narrow screen wrapping");
        var settings = new AppSettings { DeckTabWidth = 120, DeckTabHeight = 50, DeckTabsPerRow = 3 };
        var restored = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
        Check(restored.DeckTabWidth == 120 && restored.DeckTabHeight == 50 && restored.DeckTabsPerRow == 3, "Persist tab settings");
        var deck = new Deck { TabWidth = 200, TabHeight = 70, TabStartsNewRow = true };
        var restoredDeck = JsonSerializer.Deserialize<Deck>(JsonSerializer.Serialize(deck))!;
        Check(restoredDeck.TabWidth == 200 && restoredDeck.TabHeight == 70 && restoredDeck.TabStartsNewRow, "Persist individual tab settings");
        Check(JsonSerializer.Deserialize<Deck>("{}")!.TabWidth == 0, "Legacy tabs use defaults");
        DeckTabsPanel.SetTabWidth(panel.Children[1], 200);
        DeckTabsPanel.SetTabHeight(panel.Children[1], 70);
        DeckTabsPanel.SetStartsNewRow(panel.Children[1], true);
        panel.Measure(new Size(600, 1000));
        panel.Arrange(new Rect(0, 0, 600, panel.DesiredSize.Height));
        Check(panel.Children[1].RenderSize == new Size(200, 70) && VisualTreeHelper.GetOffset(panel.Children[1]).Y == 50, "Independent size and manual row break");
    }

    private static void CheckBackup(string root)
    {
        var window = new FloorballDJ.Views.BackupProgressWindow("Återställer flyttbackup", "Återställer komplett flyttbackup", "Backupens filer, profil och inställningar installeras lokalt på den här datorn.");
        window.Report(new PortableBackupProgress("Kopierar backupfiler…", 66, 333, 505, @"Media\Långa filnamn\Ett mycket långt filnamn med artist och låttitel som behöver radbrytas.flac"));
        var content = (FrameworkElement)window.Content;
        content.Measure(new Size(449, 280)); content.Arrange(new Rect(0, 0, 449, 280)); content.UpdateLayout();
        var scroll = (ScrollViewer)content;
        Check(scroll.ExtentHeight > scroll.ViewportHeight, "Small dialog allows scrolling instead of clipping");
        var detail = (TextBlock)window.FindName("DetailText");
        Check(detail.ActualHeight >= detail.DesiredSize.Height && detail.TextWrapping == TextWrapping.Wrap, "Full detail text layout");
        scroll.ScrollToEnd(); content.UpdateLayout();
        var image = new RenderTargetBitmap(449, 280, 96, 96, PixelFormats.Pbgra32);
        image.Render(content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var file = File.Create(Path.Combine(root, "backup-small.png")); encoder.Save(file);
        Console.WriteLine(Path.Combine(root, "backup-small.png"));
    }

    private static async Task CheckPlayback(string root)
    {
        var path = Path.Combine(root, "silence.wav");
        using (var writer = new WaveFileWriter(path, new WaveFormat(48000, 16, 2)))
            writer.Write(new byte[48000 * 4 * 8], 0, 48000 * 4 * 8);
        using var engine = new AudioEngine();
        engine.Configure(null, null, -60, -12, .03, .3);
        PlaybackSnapshot? snapshot = null;
        var completions = 0;
        engine.SnapshotChanged += (_, value) => snapshot = value;
        engine.PlaybackCompleted += (_, _) => completions++;
        var jingle = new Jingle { Title = "Silent fade test", FilePath = path };
        engine.Play(jingle);
        await Task.Delay(150).ConfigureAwait(false);
        var reversible = engine.FadeOutAllAsync(1, allowSpaceResume: true);
        await Task.Delay(120).ConfigureAwait(false);
        var beforeResume = engine.GetCurrentPosition();
        Check(engine.TryResumeSpaceFade(), "Space cancels an active space fade");
        await reversible.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await Task.Delay(1100).ConfigureAwait(false);
        engine.PublishSnapshot();
        Check(snapshot?.JingleId == jingle.Id && !snapshot.IsFadingOut && engine.GetCurrentPosition() > beforeResume && completions == 0,
            "Resumed voice survives old fade deadline without seeking or advancing queue");
        Check(!engine.TryResumeSpaceFade(), "No pending fade after resume");
        var fade = engine.FadeOutAllAsync(.4);
        Check(!engine.TryResumeSpaceFade(), "Non-space stop cannot be reversed by Space");
        await Task.Delay(100).ConfigureAwait(false);
        engine.PublishSnapshot();
        Check(snapshot?.JingleId == jingle.Id && snapshot.IsFadingOut && snapshot.FilePath == path, "Keep title and waveform during fade");
        await fade.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        engine.PublishSnapshot();
        Check(snapshot?.JingleId == null && completions == 0, "Manual fade becomes idle only at completion, without follow-up event");
        engine.Play(jingle);
        await Task.Delay(100).ConfigureAwait(false);
        var pause = engine.PauseOrResumeAsync();
        engine.PublishSnapshot();
        Check(snapshot?.IsFadingOut == true, "Pause fade visible");
        await pause.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await engine.FadeOutAllAsync(.4).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        engine.Play(jingle);
        var cancelled = engine.FadeOutAllAsync(3);
        engine.StopAll();
        await cancelled.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class ConstantSource : ISampleProvider
    {
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        public int Read(float[] buffer, int offset, int count) { Array.Fill(buffer, 1f, offset, count); return count; }
    }
}
