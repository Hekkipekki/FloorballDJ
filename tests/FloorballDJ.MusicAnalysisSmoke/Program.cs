using FloorballDJ.Services;
using FloorballDJ.Models;
using NAudio.Wave;
using System.Reflection;

var folder = Path.Combine(Path.GetTempPath(), "FloorballDJ", "MusicAnalysisSmoke", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(folder);
try
{
    var firstPath = Path.Combine(folder, "first.wav");
    var secondPath = Path.Combine(folder, "second.wav");
    CreatePulseTrack(firstPath, 120, 20, 220);
    CreatePulseTrack(secondPath, 122, 20, 220);
    var service = new MusicAnalysisService();
    var first = await service.AnalyzeAsync(firstPath, 0, 20);
    var second = await service.AnalyzeAsync(secondPath, 0, 20);
    var match = MusicAnalysisService.Compare(first, second);
    if (first.Bpm is < 105 or > 135) throw new InvalidOperationException($"Oväntad BPM: {first.Bpm:0.0}");
    if (first.SuggestedTransitionSeconds is <= 0 or >= 20) throw new InvalidOperationException("Övergångspunkten ligger utanför ljudet.");
    if (match.Score < 65) throw new InvalidOperationException($"Liknande testspår fick för låg matchning: {match.Score}");
    var mixedPath = Path.Combine(folder, "mixed.wav");
    var firstJingle = new Jingle { Title = "First", FilePath = firstPath, DurationSeconds = 20 };
    var secondJingle = new Jingle { Title = "Second", FilePath = secondPath, DurationSeconds = 20 };
    await new JingleMergeService().MergeManyAsync(
        [new JingleMergeService.Segment(firstJingle, 0, 8), new JingleMergeService.Segment(secondJingle, 0, 8)],
        [new JingleMergeService.Transition(6, 2, 2, JingleMergeService.TransitionMode.Crossfade)], mixedPath);
    var readPeaks = typeof(FloorballDJ.Controls.WaveformControl).GetMethod("ReadPeaks",
        BindingFlags.Static | BindingFlags.NonPublic) ?? throw new MissingMethodException("ReadPeaks");
    var peaks = (Array?)readPeaks.Invoke(null, [mixedPath]);
    if (peaks is null || peaks.Length < 2)
        throw new InvalidOperationException("Den skapade mixen gav ingen waveform.");
    Console.WriteLine($"PASS BPM={first.Bpm:0.0}, key={first.Key} {first.Scale}/{first.CamelotCode}, energy={first.Energy:0}, match={match.Score}%");
}
finally
{
    try { Directory.Delete(folder, true); } catch { }
}

static void CreatePulseTrack(string path, double bpm, double seconds, double rootFrequency)
{
    const int sampleRate = 44100;
    using var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1));
    var beatLength = 60 / bpm;
    for (var index = 0; index < sampleRate * seconds; index++)
    {
        var time = index / (double)sampleRate;
        var beatPhase = time % beatLength;
        var pulse = Math.Exp(-beatPhase * 24);
        var chord = Math.Sin(2 * Math.PI * rootFrequency * time) * .24 +
                    Math.Sin(2 * Math.PI * rootFrequency * 1.1892 * time) * .16 +
                    Math.Sin(2 * Math.PI * rootFrequency * 1.4983 * time) * .14;
        var kick = Math.Sin(2 * Math.PI * (72 - beatPhase * 35) * time) * pulse * .65;
        writer.WriteSample((float)Math.Clamp(chord + kick, -.95, .95));
    }
}
