using FloorballDJ.Models;
using NAudio.Dsp;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace FloorballDJ.Services;

public sealed record MusicAnalysis(
    double Bpm,
    double BeatConfidence,
    string Key,
    string Scale,
    string CamelotCode,
    double KeyConfidence,
    double Energy,
    double SuggestedTransitionSeconds,
    double SuggestedFadeSeconds,
    long FileSize,
    long FileWriteUtcTicks,
    double SourceStartSeconds,
    double SourceEndSeconds);

public sealed record MusicCompatibility(
    int Score,
    int RhythmScore,
    int HarmonicScore,
    int EnergyScore,
    string Summary);

/// <summary>
/// Lightweight, deterministic music analysis for offline mix assistance. The service never writes to the source file.
/// Results are deliberately advisory: noisy effects and speech do not always have a meaningful key or tempo.
/// </summary>
public sealed class MusicAnalysisService
{
    private const int AnalysisSampleRate = 11025;
    private const int EnergyFrameSize = 1024;
    private const int EnergyHopSize = 256;
    private const int KeyFrameSize = 4096;
    private const int KeyHopSize = 4096;
    private const double MaximumAnalysisSeconds = 600;

    private static readonly double[] MajorProfile = [6.35, 2.23, 3.48, 2.33, 4.38, 4.09, 2.52, 5.19, 2.39, 3.66, 2.29, 2.88];
    private static readonly double[] MinorProfile = [6.33, 2.68, 3.52, 5.38, 2.60, 3.53, 2.54, 4.75, 3.98, 2.69, 3.34, 3.17];
    private static readonly string[] KeyNames = ["C", "C♯", "D", "D♯", "E", "F", "F♯", "G", "G♯", "A", "A♯", "B"];
    private static readonly string[] MajorCamelot = ["8B", "3B", "10B", "5B", "12B", "7B", "2B", "9B", "4B", "11B", "6B", "1B"];
    private static readonly string[] MinorCamelot = ["5A", "12A", "7A", "2A", "9A", "4A", "11A", "6A", "1A", "8A", "3A", "10A"];

    public Task<MusicAnalysis> AnalyzeAsync(string path, double startSeconds, double endSeconds,
        CancellationToken cancellationToken = default) => Task.Run(
        () => Analyze(path, startSeconds, endSeconds, cancellationToken), cancellationToken);

    public static MusicCompatibility Compare(MusicAnalysis first, MusicAnalysis second)
    {
        var rhythm = RhythmCompatibility(first.Bpm, second.Bpm);
        var harmonic = HarmonicCompatibility(first.CamelotCode, second.CamelotCode,
            first.KeyConfidence, second.KeyConfidence);
        var energy = (int)Math.Round(Math.Clamp(100 - Math.Abs(first.Energy - second.Energy) * 1.25, 0, 100));
        var confidence = Math.Clamp((first.BeatConfidence + second.BeatConfidence +
                                     first.KeyConfidence + second.KeyConfidence) / 4, 0, 1);
        var score = (int)Math.Round(Math.Clamp(rhythm * .45 + harmonic * .35 + energy * .15 + confidence * 100 * .05, 0, 100));
        var summary = score switch
        {
            >= 85 => "Mycket bra musikalisk match",
            >= 70 => "Bra match – liten finjustering kan behövas",
            >= 55 => "Användbar match – förhandslyssna övergången",
            _ => "Svag match – prova annan startpunkt eller låt"
        };
        return new MusicCompatibility(score, rhythm, harmonic, energy, summary);
    }

    public static MusicAnalysis FromJingle(Jingle jingle) => new(
        jingle.DetectedBpm ?? 0,
        jingle.BeatConfidence ?? 0,
        jingle.DetectedKey ?? "Okänd",
        jingle.DetectedScale ?? "",
        jingle.CamelotCode ?? "–",
        jingle.KeyConfidence ?? 0,
        jingle.MusicalEnergy ?? 0,
        jingle.SuggestedTransitionSeconds ?? jingle.StartSeconds,
        jingle.SuggestedTransitionFadeSeconds ?? 1.5,
        jingle.MusicAnalysisFileSize,
        jingle.MusicAnalysisFileWriteUtcTicks,
        jingle.MusicAnalysisStartSeconds,
        jingle.MusicAnalysisEndSeconds);

    public static void ApplyToJingle(Jingle jingle, MusicAnalysis result)
    {
        jingle.DetectedBpm = result.Bpm;
        jingle.BeatConfidence = result.BeatConfidence;
        jingle.DetectedKey = result.Key;
        jingle.DetectedScale = result.Scale;
        jingle.CamelotCode = result.CamelotCode;
        jingle.KeyConfidence = result.KeyConfidence;
        jingle.MusicalEnergy = result.Energy;
        jingle.SuggestedTransitionSeconds = result.SuggestedTransitionSeconds;
        jingle.SuggestedTransitionFadeSeconds = result.SuggestedFadeSeconds;
        jingle.MusicAnalysisFileSize = result.FileSize;
        jingle.MusicAnalysisFileWriteUtcTicks = result.FileWriteUtcTicks;
        jingle.MusicAnalysisStartSeconds = result.SourceStartSeconds;
        jingle.MusicAnalysisEndSeconds = result.SourceEndSeconds;
    }

    private static MusicAnalysis Analyze(string path, double requestedStart, double requestedEnd,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Ljudfilen kunde inte hittas.", path);
        var file = new FileInfo(path);
        using var reader = new AudioFileReader(path);
        var sourceStart = Math.Clamp(requestedStart, 0, reader.TotalTime.TotalSeconds);
        var sourceEnd = Math.Clamp(requestedEnd, sourceStart, reader.TotalTime.TotalSeconds);
        if (sourceEnd - sourceStart < .5)
            throw new InvalidOperationException("Ljuddelen är för kort för musikalisk analys.");

        // Long programme mixes are analysed from their final ten minutes. That keeps memory bounded and
        // still gives the transition assistant information about the section where the next clip will enter.
        var readStart = Math.Max(sourceStart, sourceEnd - MaximumAnalysisSeconds);
        AudioFileSeekService.Seek(reader, path, TimeSpan.FromSeconds(readStart), cancellationToken);
        ISampleProvider mono = reader.WaveFormat.Channels switch
        {
            1 => reader,
            2 => new StereoToMonoSampleProvider(reader) { LeftVolume = .5f, RightVolume = .5f },
            _ => new MultiChannelToMonoSampleProvider(reader)
        };
        if (mono.WaveFormat.SampleRate != AnalysisSampleRate)
            mono = new WdlResamplingSampleProvider(mono, AnalysisSampleRate);

        var wanted = (int)Math.Ceiling((sourceEnd - readStart) * AnalysisSampleRate);
        var samples = new float[wanted];
        var offset = 0;
        while (offset < samples.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = mono.Read(samples, offset, Math.Min(16384, samples.Length - offset));
            if (read <= 0) break;
            offset += read;
        }
        if (offset < samples.Length) Array.Resize(ref samples, offset);
        if (samples.Length < EnergyFrameSize) throw new InvalidOperationException("Ljudfilen innehåller för lite analyserbart ljud.");

        var onset = BuildOnsetEnvelope(samples, cancellationToken, out var rmsDb);
        var (bpm, beatConfidence, beatLag, beatPhase) = DetectTempo(onset);
        var (keyIndex, major, keyConfidence) = DetectKey(samples, cancellationToken);
        var energy = CalculateEnergy(rmsDb, onset);
        var suggestion = SuggestTransition(readStart, sourceStart, sourceEnd, bpm, beatLag, beatPhase);
        var fade = bpm > 1 ? Math.Clamp(4 * 60 / bpm, .5, 4) : Math.Clamp((sourceEnd - sourceStart) * .12, .5, 2.5);
        var key = keyIndex >= 0 ? KeyNames[keyIndex] : "Okänd";
        var scale = keyIndex < 0 ? "" : major ? "major" : "minor";
        var camelot = keyIndex < 0 ? "–" : major ? MajorCamelot[keyIndex] : MinorCamelot[keyIndex];
        return new MusicAnalysis(Math.Round(bpm, 1), beatConfidence, key, scale, camelot,
            keyConfidence, Math.Round(energy), suggestion, fade,
            file.Length, file.LastWriteTimeUtc.Ticks, sourceStart, sourceEnd);
    }

    private static double[] BuildOnsetEnvelope(float[] samples, CancellationToken cancellationToken, out double rmsDb)
    {
        var frames = Math.Max(1, 1 + (samples.Length - EnergyFrameSize) / EnergyHopSize);
        var logEnergy = new double[frames];
        double totalSquares = 0;
        for (var sample = 0; sample < samples.Length; sample++) totalSquares += samples[sample] * samples[sample];
        rmsDb = 20 * Math.Log10(Math.Max(1e-9, Math.Sqrt(totalSquares / Math.Max(1, samples.Length))));
        for (var frame = 0; frame < frames; frame++)
        {
            if ((frame & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            var start = frame * EnergyHopSize;
            double sum = 0;
            for (var index = 0; index < EnergyFrameSize && start + index < samples.Length; index++)
                sum += samples[start + index] * samples[start + index];
            logEnergy[frame] = Math.Log10(1e-10 + sum / EnergyFrameSize);
        }

        var onset = new double[frames];
        for (var index = 1; index < frames; index++) onset[index] = Math.Max(0, logEnergy[index] - logEnergy[index - 1]);
        if (onset.Length > 4)
        {
            var smoothed = new double[onset.Length];
            for (var index = 1; index < onset.Length - 1; index++)
                smoothed[index] = onset[index - 1] * .2 + onset[index] * .6 + onset[index + 1] * .2;
            onset = smoothed;
        }
        var mean = onset.Average();
        for (var index = 0; index < onset.Length; index++) onset[index] = Math.Max(0, onset[index] - mean * .65);
        return onset;
    }

    private static (double Bpm, double Confidence, int Lag, int Phase) DetectTempo(double[] onset)
    {
        var hopSeconds = EnergyHopSize / (double)AnalysisSampleRate;
        var minLag = Math.Max(2, (int)Math.Floor(60 / (200 * hopSeconds)));
        var maxLag = Math.Min(onset.Length / 3, (int)Math.Ceiling(60 / (60 * hopSeconds)));
        if (maxLag <= minLag) return (0, 0, 0, 0);
        var energy = onset.Sum(value => value * value);
        if (energy < 1e-10) return (0, 0, 0, 0);

        var bestLag = minLag;
        var bestScore = double.NegativeInfinity;
        for (var lag = minLag; lag <= maxLag; lag++)
        {
            double numerator = 0, left = 0, right = 0;
            for (var index = lag; index < onset.Length; index++)
            {
                numerator += onset[index] * onset[index - lag];
                left += onset[index] * onset[index];
                right += onset[index - lag] * onset[index - lag];
            }
            var correlation = numerator / Math.Sqrt(Math.Max(1e-12, left * right));
            var bpm = 60 / (lag * hopSeconds);
            var preference = bpm is >= 78 and <= 165 ? 1 : .94;
            var score = correlation * preference;
            if (score > bestScore) { bestScore = score; bestLag = lag; }
        }

        var phaseScores = new double[bestLag];
        for (var index = 0; index < onset.Length; index++) phaseScores[index % bestLag] += onset[index];
        var phase = Array.IndexOf(phaseScores, phaseScores.Max());
        var bpmResult = 60 / (bestLag * hopSeconds);
        return (bpmResult, Math.Clamp((bestScore - .03) / .42, 0, 1), bestLag, phase);
    }

    private static (int KeyIndex, bool Major, double Confidence) DetectKey(float[] samples,
        CancellationToken cancellationToken)
    {
        if (samples.Length < KeyFrameSize) return (-1, true, 0);
        var chroma = new double[12];
        var exponent = (int)Math.Log2(KeyFrameSize);
        var fft = new Complex[KeyFrameSize];
        var frameCount = 0;
        for (var start = 0; start + KeyFrameSize <= samples.Length; start += KeyHopSize)
        {
            if ((frameCount++ & 31) == 0) cancellationToken.ThrowIfCancellationRequested();
            for (var index = 0; index < KeyFrameSize; index++)
            {
                var window = .5 - .5 * Math.Cos(2 * Math.PI * index / (KeyFrameSize - 1));
                fft[index].X = (float)(samples[start + index] * window);
                fft[index].Y = 0;
            }
            FastFourierTransform.FFT(true, exponent, fft);
            var maxBin = Math.Min(KeyFrameSize / 2, (int)(5000d * KeyFrameSize / AnalysisSampleRate));
            var minBin = Math.Max(1, (int)(55d * KeyFrameSize / AnalysisSampleRate));
            for (var bin = minBin; bin <= maxBin; bin++)
            {
                var frequency = bin * AnalysisSampleRate / (double)KeyFrameSize;
                var midi = 69 + 12 * Math.Log2(frequency / 440d);
                var pitchClass = ((int)Math.Round(midi) % 12 + 12) % 12;
                var magnitude = Math.Sqrt(fft[bin].X * fft[bin].X + fft[bin].Y * fft[bin].Y);
                chroma[pitchClass] += Math.Sqrt(magnitude);
            }
        }

        if (chroma.Sum() < 1e-8) return (-1, true, 0);
        var candidates = new List<(double Score, int Key, bool Major)>(24);
        for (var key = 0; key < 12; key++)
        {
            candidates.Add((ProfileCorrelation(chroma, MajorProfile, key), key, true));
            candidates.Add((ProfileCorrelation(chroma, MinorProfile, key), key, false));
        }
        var ordered = candidates.OrderByDescending(item => item.Score).ToArray();
        var confidence = Math.Clamp((ordered[0].Score - ordered[1].Score + .02) * 2.5, 0, 1);
        return confidence < .04 ? (-1, true, confidence) : (ordered[0].Key, ordered[0].Major, confidence);
    }

    private static double ProfileCorrelation(double[] chroma, double[] profile, int tonic)
    {
        var shifted = new double[12];
        for (var index = 0; index < 12; index++) shifted[index] = profile[(index - tonic + 12) % 12];
        var chromaMean = chroma.Average();
        var profileMean = shifted.Average();
        double numerator = 0, left = 0, right = 0;
        for (var index = 0; index < 12; index++)
        {
            var a = chroma[index] - chromaMean;
            var b = shifted[index] - profileMean;
            numerator += a * b; left += a * a; right += b * b;
        }
        return numerator / Math.Sqrt(Math.Max(1e-12, left * right));
    }

    private static double CalculateEnergy(double rmsDb, double[] onset)
    {
        var level = Math.Clamp((rmsDb + 32) / 24 * 100, 0, 100);
        var active = onset.Length == 0 ? 0 : onset.Count(value => value > onset.Average() * 1.8) / (double)onset.Length * 100;
        return Math.Clamp(level * .82 + Math.Min(100, active * 5) * .18, 0, 100);
    }

    private static double SuggestTransition(double readStart, double sourceStart, double sourceEnd,
        double bpm, int lag, int phase)
    {
        var duration = sourceEnd - sourceStart;
        if (bpm <= 1 || lag <= 0)
            return Math.Round(Math.Clamp(sourceEnd - Math.Min(1.5, duration * .25), sourceStart, sourceEnd), 3);
        var beatsBeforeEnd = duration >= 30 ? 16 : duration >= 12 ? 8 : 4;
        var target = sourceEnd - beatsBeforeEnd * 60 / bpm;
        var phaseSeconds = readStart + phase * EnergyHopSize / (double)AnalysisSampleRate;
        var beatSeconds = lag * EnergyHopSize / (double)AnalysisSampleRate;
        var beatIndex = Math.Round((target - phaseSeconds) / beatSeconds);
        var aligned = phaseSeconds + beatIndex * beatSeconds;
        var earliest = sourceStart + Math.Min(1, duration * .15);
        return Math.Round(Math.Clamp(aligned, earliest, Math.Max(earliest, sourceEnd - .2)), 3);
    }

    private static int RhythmCompatibility(double first, double second)
    {
        if (first <= 1 || second <= 1) return 55;
        var ratios = new[] { second / first, second / (first * 2), second * 2 / first };
        var difference = ratios.Min(ratio => Math.Abs(1 - ratio));
        return (int)Math.Round(Math.Clamp(100 - difference * 450, 0, 100));
    }

    private static int HarmonicCompatibility(string first, string second, double firstConfidence, double secondConfidence)
    {
        if (!TryParseCamelot(first, out var firstNumber, out var firstLetter) ||
            !TryParseCamelot(second, out var secondNumber, out var secondLetter)) return 55;
        var confidence = Math.Min(firstConfidence, secondConfidence);
        var distance = Math.Abs(firstNumber - secondNumber);
        distance = Math.Min(distance, 12 - distance);
        var raw = firstNumber == secondNumber && firstLetter == secondLetter ? 100
            : firstNumber == secondNumber ? 92
            : firstLetter == secondLetter && distance == 1 ? 94
            : firstLetter == secondLetter && distance == 2 ? 72
            : 38;
        return (int)Math.Round(55 + (raw - 55) * Math.Clamp(confidence * 1.8, 0, 1));
    }

    private static bool TryParseCamelot(string value, out int number, out char letter)
    {
        number = 0; letter = '\0';
        if (string.IsNullOrWhiteSpace(value) || value.Length < 2 ||
            !int.TryParse(value[..^1], out number) || number is < 1 or > 12) return false;
        letter = char.ToUpperInvariant(value[^1]);
        return letter is 'A' or 'B';
    }

    private sealed class MultiChannelToMonoSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private float[] _sourceBuffer = [];

        public MultiChannelToMonoSampleProvider(ISampleProvider source)
        {
            _source = source;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            var channels = _source.WaveFormat.Channels;
            var wanted = count * channels;
            if (_sourceBuffer.Length < wanted) _sourceBuffer = new float[wanted];
            var read = _source.Read(_sourceBuffer, 0, wanted);
            var frames = read / channels;
            for (var frame = 0; frame < frames; frame++)
            {
                double sum = 0;
                for (var channel = 0; channel < channels; channel++) sum += _sourceBuffer[frame * channels + channel];
                buffer[offset + frame] = (float)(sum / channels);
            }
            return frames;
        }
    }
}
