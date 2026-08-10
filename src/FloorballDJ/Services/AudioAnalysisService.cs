using NAudio.Dsp;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace FloorballDJ.Services;

public sealed record LoudnessAnalysis(
    double IntegratedLufs,
    double TruePeakDbtp,
    double LoudnessRangeLu,
    double MaxMomentaryLufs,
    DateTimeOffset AnalyzedAt,
    long FileSize,
    long FileWriteUtcTicks)
{
    public double SuggestedGain(double targetLufs, double peakCeilingDbtp)
        => Math.Clamp(Math.Min(targetLufs - IntegratedLufs, peakCeilingDbtp - TruePeakDbtp), -24, 12);
}

public sealed class AudioAnalysisService
{
    public Task<LoudnessAnalysis> AnalyzeAsync(string path, double startSeconds = 0, double? endSeconds = null,
        CancellationToken cancellationToken = default)
        => Task.Run(() => Analyze(path, startSeconds, endSeconds, cancellationToken), cancellationToken);

    private static LoudnessAnalysis Analyze(string path, double startSeconds, double? endSeconds, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Ljudfilen kunde inte hittas.", path);
        var file = new FileInfo(path);
        var blockEnergies = new List<double>();
        var shortTermLoudness = new List<double>();
        double maxMomentary = double.NegativeInfinity;

        using (var reader = new AudioFileReader(path))
        {
            var start = TimeSpan.FromSeconds(Math.Clamp(startSeconds, 0, reader.TotalTime.TotalSeconds));
            var end = TimeSpan.FromSeconds(Math.Clamp(endSeconds ?? reader.TotalTime.TotalSeconds, start.TotalSeconds, reader.TotalTime.TotalSeconds));
            reader.CurrentTime = start;
            var channels = reader.WaveFormat.Channels;
            var sampleRate = reader.WaveFormat.SampleRate;
            var highShelf = Enumerable.Range(0, channels).Select(_ => BiQuadFilter.HighShelf(sampleRate, 1681.974f, .707f, 4f)).ToArray();
            var highPass = Enumerable.Range(0, channels).Select(_ => BiQuadFilter.HighPassFilter(sampleRate, 38.135f, .5f)).ToArray();
            var segmentFrames = Math.Max(1, sampleRate / 10);
            var recentSegments = new Queue<(double Energy, int Frames)>();
            var recentShortSegments = new Queue<(double Energy, int Frames)>();
            var buffer = new float[16384 - 16384 % Math.Max(1, channels)];
            double segmentEnergy = 0;
            var framesInSegment = 0;
            var remainingFrames = (long)Math.Ceiling((end - start).TotalSeconds * sampleRate);

            while (remainingFrames > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var wanted = (int)Math.Min(buffer.Length, remainingFrames * channels);
                wanted -= wanted % channels;
                if (wanted <= 0) break;
                var read = reader.Read(buffer, 0, wanted);
                if (read <= 0) break;
                for (var offset = 0; offset + channels <= read; offset += channels)
                {
                    double frameEnergy = 0;
                    for (var channel = 0; channel < channels; channel++)
                    {
                        var filtered = highPass[channel].Transform(highShelf[channel].Transform(buffer[offset + channel]));
                        var weight = channels >= 6 && channel == 3 ? 0d : channel >= 4 ? 1.41d : 1d;
                        frameEnergy += weight * filtered * filtered;
                    }
                    segmentEnergy += frameEnergy;
                    framesInSegment++;
                    remainingFrames--;
                    if (framesInSegment >= segmentFrames)
                    {
                        AddSegment(segmentEnergy, framesInSegment, recentSegments, 4, blockEnergies, ref maxMomentary);
                        AddShortSegment(segmentEnergy, framesInSegment, recentShortSegments, shortTermLoudness);
                        segmentEnergy = 0;
                        framesInSegment = 0;
                    }
                    if (remainingFrames <= 0) break;
                }
            }
            if (framesInSegment > 0)
            {
                AddSegment(segmentEnergy, framesInSegment, recentSegments, 4, blockEnergies, ref maxMomentary);
                AddShortSegment(segmentEnergy, framesInSegment, recentShortSegments, shortTermLoudness);
            }
        }

        var integrated = CalculateIntegratedLoudness(blockEnergies);
        var range = CalculateLoudnessRange(shortTermLoudness, integrated);
        var truePeak = AnalyzeTruePeak(path, startSeconds, endSeconds, cancellationToken);
        return new LoudnessAnalysis(integrated, truePeak, range,
            double.IsFinite(maxMomentary) ? maxMomentary : -70,
            DateTimeOffset.Now, file.Length, file.LastWriteTimeUtc.Ticks);
    }

    private static void AddSegment(double energy, int frames, Queue<(double Energy, int Frames)> recent, int count,
        List<double> blocks, ref double maxMomentary)
    {
        recent.Enqueue((energy, frames));
        while (recent.Count > count) recent.Dequeue();
        if (recent.Count < count) return;
        var mean = recent.Sum(item => item.Energy) / Math.Max(1, recent.Sum(item => item.Frames));
        blocks.Add(mean);
        maxMomentary = Math.Max(maxMomentary, EnergyToLufs(mean));
    }

    private static void AddShortSegment(double energy, int frames, Queue<(double Energy, int Frames)> recent, List<double> values)
    {
        recent.Enqueue((energy, frames));
        while (recent.Count > 30) recent.Dequeue();
        if (recent.Count < 30) return;
        var mean = recent.Sum(item => item.Energy) / Math.Max(1, recent.Sum(item => item.Frames));
        values.Add(EnergyToLufs(mean));
    }

    private static double CalculateIntegratedLoudness(IEnumerable<double> energies)
    {
        var absolute = energies.Where(energy => EnergyToLufs(energy) >= -70).ToArray();
        if (absolute.Length == 0) return -70;
        var ungated = EnergyToLufs(absolute.Average());
        var relativeGate = Math.Max(-70, ungated - 10);
        var gated = absolute.Where(energy => EnergyToLufs(energy) >= relativeGate).ToArray();
        return gated.Length == 0 ? ungated : EnergyToLufs(gated.Average());
    }

    private static double CalculateLoudnessRange(List<double> values, double integrated)
    {
        var gated = values.Where(value => value >= -70 && value >= integrated - 20).OrderBy(value => value).ToArray();
        if (gated.Length < 2) return 0;
        return Math.Max(0, Percentile(gated, .95) - Percentile(gated, .10));
    }

    private static double Percentile(double[] sorted, double percentile)
    {
        var position = (sorted.Length - 1) * percentile;
        var lower = (int)Math.Floor(position);
        var upper = Math.Min(sorted.Length - 1, lower + 1);
        return sorted[lower] + (sorted[upper] - sorted[lower]) * (position - lower);
    }

    private static double AnalyzeTruePeak(string path, double startSeconds, double? endSeconds, CancellationToken cancellationToken)
    {
        using var reader = new AudioFileReader(path);
        var start = Math.Clamp(startSeconds, 0, reader.TotalTime.TotalSeconds);
        var end = Math.Clamp(endSeconds ?? reader.TotalTime.TotalSeconds, start, reader.TotalTime.TotalSeconds);
        reader.CurrentTime = TimeSpan.FromSeconds(start);
        var oversampled = new WdlResamplingSampleProvider(reader, reader.WaveFormat.SampleRate * 4);
        var remainingSamples = (long)Math.Ceiling((end - start) * oversampled.WaveFormat.SampleRate * oversampled.WaveFormat.Channels);
        var buffer = new float[16384];
        float peak = 0;
        while (remainingSamples > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = oversampled.Read(buffer, 0, (int)Math.Min(buffer.Length, remainingSamples));
            if (read <= 0) break;
            for (var index = 0; index < read; index++) peak = Math.Max(peak, Math.Abs(buffer[index]));
            remainingSamples -= read;
        }
        return peak <= 0.0000001f ? -120 : 20 * Math.Log10(peak);
    }

    private static double EnergyToLufs(double energy) => energy <= 1e-12 ? -120 : -0.691 + 10 * Math.Log10(energy);
}
