using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows.Controls;
using FloorballDJ.Models;
using FloorballDJ.Services;
using FloorballDJ.Views;
using NAudio.Wave;

internal static class KeepAliveChecks
{
    private sealed class FakeOutput : IWavePlayer
    {
        public float Volume { get; set; }
        public WaveFormat OutputWaveFormat { get; } = new(48000, 16, 2);
        public PlaybackState PlaybackState { get; private set; } = PlaybackState.Playing;
        public bool Disposed { get; private set; }
        public event EventHandler<StoppedEventArgs>? PlaybackStopped { add { } remove { } }
        public void Init(IWaveProvider source) { }
        public void Play() => PlaybackState = PlaybackState.Playing;
        public void Pause() => PlaybackState = PlaybackState.Paused;
        public void Stop() => PlaybackState = PlaybackState.Stopped;
        public void Dispose() { Disposed = true; PlaybackState = PlaybackState.Stopped; }
    }
    internal static async Task RunAsync(string root, string? output)
    {
        var silence = new SilenceProvider(new WaveFormat(48000, 16, 2)); var samples = Enumerable.Repeat((byte)99, 4096).ToArray();
        Check(silence.Read(samples, 0, samples.Length) == samples.Length && samples.All(sample => sample == 0), "Silence contains only digital zeroes");
        var created = new List<(string? Id, FakeOutput Output)>();
        var controller = new PrimaryOutputKeepAlive(id => { var fake = new FakeOutput(); created.Add((id, fake)); return fake; });
        controller.Configure(false, "unused"); await controller.Settled; Check(created.Count == 0, "Disabled mode never opens an output");
        controller.Configure(true, "primary"); await controller.Settled;
        Check(controller.Active && created.Count == 1 && created[0].Id == "primary", "Exactly one chosen primary stream opens");
        controller.Configure(true, "primary"); await controller.Settled; Check(created.Count == 1, "Unchanged settings reuse the silence stream");
        controller.Configure(true, "next"); await controller.Settled;
        Check(created.Count == 2 && created[0].Output.Disposed && !created[1].Output.Disposed, "Endpoint change releases the old stream before replacement");
        controller.Invalidate(); await controller.Settled; Check(created.Count == 3 && created[1].Output.Disposed, "Device/power invalidation recreates one stream");
        controller.Configure(false, "next"); await controller.Settled; Check(!controller.Active && created.All(item => item.Output.Disposed), "Turning off releases all streams");
        controller.Dispose(); await controller.Settled;

        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        FakeOutput? late = null;
        var blocked = new PrimaryOutputKeepAlive(_ => { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException(); return late = new FakeOutput(); });
        blocked.Configure(true, null); Check(await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(3))), "Delayed device open entered");
        blocked.Configure(false, null); blocked.Dispose(); release.Set(); await blocked.Settled.WaitAsync(TimeSpan.FromSeconds(3));
        Check(late is { Disposed: true } && !blocked.Active, "Disable/close reject and release a late device result without blocking the caller");
        var attempts = 0;
        var failure = new PrimaryOutputKeepAlive(_ => { if (++attempts == 1) throw new IOException("unavailable fixture"); return new FakeOutput(); });
        failure.Configure(true, null); await failure.Settled; await Task.Delay(50);
        Check(!failure.Active && attempts == 1, "Failure cannot spin or escape into playback");
        failure.Invalidate(); await failure.Settled; Check(failure.Active && attempts == 2, "A later device event retries successfully");
        failure.Dispose(); await failure.Settled;

        var projects = new ProjectService(); var project = ProjectService.CreateDefault();
        project.Settings.AutoplayLastPlaylistPath = Path.Combine(root, "registered.fdjplaylist.json");
        var settings = new SettingsWindow(project, [new OutputDevice("", "Default")], new ProfilePreferencesService(projects.AppDataDirectory, projects.DefaultProjectPath));
        try
        {
            settings.ShowInTaskbar = false; settings.Show();
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Check(!settings.ViewData.Draft.KeepPrimaryOutputActive, "Setting defaults off for new and older profiles");
            var checkbox = (CheckBox)settings.FindName("KeepPrimaryOutputActiveCheck"); checkbox.IsChecked = true;
            Check(settings.ViewData.Draft.KeepPrimaryOutputActive, "Real setting binds to its isolated draft");
            typeof(SettingsWindow).GetMethod("Copy", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [settings.ViewData.Draft, project.Settings]);
            Check(project.Settings.KeepPrimaryOutputActive && project.Settings.AutoplayLastPlaylistPath.EndsWith("registered.fdjplaylist.json"),
                "Settings copy retains keep-alive and the newly registered playlist path");
        }
        finally { settings.Close(); }
        var saved = Path.Combine(root, "keep-alive-profile.json"); await projects.SaveAsync(project, saved);
        Check((await projects.LoadAsync(saved)).Settings.KeepPrimaryOutputActive, "The optional setting survives profile save/load");

        using var audio = new AudioEngine(false); var completed = 0; audio.PlaybackCompleted += (_, _) => completed++;
        audio.Configure(null, null, -60, -12, 0, .05, keepPrimaryOutputActive: true); await audio.KeepAlive.Settled.WaitAsync(TimeSpan.FromSeconds(5));
        Check(audio.KeepAlive.Active && audio.GetCurrentPosition() is null && completed == 0, "Real WASAPI silence is active but creates no song or queue-completion event");
        var path = Path.Combine(root, "keep-alive-play.wav");
        using (var writer = new WaveFileWriter(path, new WaveFormat(44100, 16, 2))) writer.Write(new byte[44100 * 4 * 3], 0, 44100 * 4 * 3);
        audio.Play(new Jingle { FilePath = path }); audio.SetSecondaryOutput(true); audio.Play(new Jingle { FilePath = path }); audio.SetSecondaryOutput(false);
        await Task.Delay(120); audio.StopAll();
        Check(audio.KeepAlive.Active && completed == 0, "Primary/preview playback and Stop All preserve silence without advancing a queue");
        audio.KeepAlive.Invalidate(); await audio.KeepAlive.Settled.WaitAsync(TimeSpan.FromSeconds(5)); Check(audio.KeepAlive.Active, "Native output can be recreated");
        audio.Configure(null, null, -60, -12, 0, .05); await audio.KeepAlive.Settled;
        Check(!audio.KeepAlive.Active, "Native stream closes when its setting is disabled");
        if (output is not null)
        {
            Directory.CreateDirectory(output); File.WriteAllText(Path.Combine(output, "keep-alive-checks.json"), JsonSerializer.Serialize(new {
                passed = true, digitalSilence = true, defaultOff = true, singleStream = true, nonblockingStaleClose = true,
                failureRetry = true, nativePrimaryPreviewStop = true, profilePersistence = true,
                limitation = "Device changes/retry races are deterministic fixtures; native WASAPI uses available desktop endpoints. Arena noise and physical hotplug/suspend remain unqualified."
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        Console.WriteLine("PASS: opt-in digital silence, one primary stream, unchanged/endpoint/stale-close/failure-retry lifecycle, settings/playlist-path persistence and real silent WASAPI primary+preview/Stop All.");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
