using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using FloorballDJ.Models;
using FloorballDJ.Services;
using NAudio.Wave;

internal static class JinglePlaybackChecks
{
    internal static async Task RunAsync(string root)
    {
        var path = Path.Combine(root, "shared-jingle.wav");
        using (var writer = new WaveFileWriter(path, new WaveFormat(48000, 16, 2)))
            writer.Write(new byte[48000 * 2 * 2 * 8], 0, 48000 * 2 * 2 * 8);

        foreach (var reuse in new[] { false, true })
        foreach (var secondary in new[] { false, true })
        {
            using var engine = new AudioEngine(reuse);
            engine.Configure(null, null, -60, -12, .2, .4);
            engine.SetSecondaryOutput(secondary);
            PlaybackSnapshot Snapshot()
            {
                PlaybackSnapshot? snapshot = null;
                void Capture(object? sender, PlaybackSnapshot value) => snapshot = value;
                engine.SnapshotChanged += Capture;
                try { engine.PublishSnapshot(); }
                finally { engine.SnapshotChanged -= Capture; }
                return secondary ? engine.GetSecondarySnapshot() : snapshot!;
            }

            var first = new Jingle { FilePath = path, StartSeconds = .5, EndSeconds = 6 };
            var second = new Jingle { FilePath = path, StartSeconds = 2, EndSeconds = 7 };
            Check(engine.Play(first) == PlaybackAction.Started, "First shared-file button starts");
            await WaitUntil(() => Snapshot().Position.TotalSeconds > 0);
            Check(engine.Play(second) == PlaybackAction.Started, "A different button sharing the file starts a crossfade");
            var outgoing = Voices(engine).Single(voice => Jingle(voice).Id == first.Id);
            var incoming = Voices(engine).Single(voice => Jingle(voice).Id == second.Id);
            Check((bool)Property(outgoing, "StopRequested")! && !(bool)Property(incoming, "StopRequested")!,
                "Only the outgoing button fades out during the overlap");
            Check(Snapshot().JingleId == second.Id && Snapshot().Duration.TotalSeconds == 5,
                "The incoming button becomes current with its own clip boundaries");
            await WaitUntil(() => Voices(engine).Length == 1 && Snapshot().Position.TotalSeconds > .2);
            Check(Jingle(Voices(engine).Single()).Id == second.Id && !Snapshot().IsFadingOut,
                "The incoming clip keeps playing after the outgoing fade finishes");
            Check(engine.Play(second) == PlaybackAction.FadingOut, "Pressing the same button still fades it out");
            await WaitUntil(() => Voices(engine).Length == 0);

            foreach (var mode in new[] { JinglePlayMode.Mix, JinglePlayMode.Duck })
            {
                first.PlayMode = second.PlayMode = mode;
                engine.Play(first);
                Check(engine.Play(second) == PlaybackAction.Started && Voices(engine).Length == 2,
                    "Different shared-file buttons can mix or duck together");
                Check(engine.Play(second) == PlaybackAction.FadingOut && !(bool)Property(Voices(engine)
                        .Single(voice => Jingle(voice).Id == first.Id), "StopRequested")!,
                    "Toggling one shared-file button leaves the other playing");
                engine.StopAll();
            }

            first.PlayMode = second.PlayMode = JinglePlayMode.Solo;
            first.AllowMultipleClicks = second.AllowMultipleClicks = true;
            engine.Play(first);
            Check(engine.Play(second) == PlaybackAction.Started && (bool)Property(Voices(engine)
                    .Single(voice => Jingle(voice).Id == first.Id), "StopRequested")!,
                "Multiple-click mode does not prevent crossfading to a different shared-file button");
            engine.StopAll();

            first.PlayMode = second.PlayMode = JinglePlayMode.Mix;
            for (var index = 0; index < 8; index++)
                Check(engine.Play(first) == PlaybackAction.Started, "Repeated clicks start independent voices");
            Check(engine.Play(first) == PlaybackAction.PolyphonyLimitReached, "The per-button eight-voice limit remains");
            Check(engine.Play(second) == PlaybackAction.Started, "Another shared-file button has its own voice limit");
            Check((double)Property(Voices(engine).Single(voice => Jingle(voice).Id == second.Id), "PolyphonyHeadroomDb")! == 0,
                "A button's first voice does not inherit another button's polyphony attenuation");
            engine.StopAll();

            var completed = new List<Guid>();
            engine.PlaybackCompleted += (_, jingle) => completed.Add(jingle.Id);
            first.StartSeconds = 0;
            first.EndSeconds = .4;
            engine.Play(second);
            engine.Play(first);
            await WaitUntil(() => { engine.PublishSnapshot(); return completed.Contains(first.Id); });
            Check(completed.Count(id => id == first.Id) == 1 && Voices(engine).Any(voice => Jingle(voice).Id == second.Id),
                "A shared-file button completes independently while the other is still playing");
        }
        Console.WriteLine("PASS: shared-file button crossfades, independent trims, repeat-click fades, mix/duck, polyphony and completion on both output paths and routes.");
    }

    private static object[] Voices(AudioEngine engine) => ((IEnumerable)typeof(AudioEngine)
        .GetField("_voices", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(engine)!).Cast<object>().ToArray();
    private static object? Property(object voice, string name) => voice.GetType().GetProperty(name)!.GetValue(voice);
    private static Jingle Jingle(object voice) => (Jingle)Property(voice, "Jingle")!;
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task WaitUntil(Func<bool> condition)
    {
        var timeout = Stopwatch.StartNew();
        while (!condition())
        {
            if (timeout.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("Jingle playback did not reach the expected state");
            await Task.Delay(10);
        }
    }
}
