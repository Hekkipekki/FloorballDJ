using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using FloorballDJ.Models;

namespace FloorballDJ.Services;

public sealed class JingleMergeService
{
    private const int SampleRate = 48000;

    public Task MergeAsync(Jingle first, Jingle second, double transitionSeconds, string outputPath,
        CancellationToken cancellationToken = default)
    {
        using var firstReader = new AudioFileReader(first.FilePath);
        using var secondReader = new AudioFileReader(second.FilePath);
        var firstEnd = EffectiveEnd(first, firstReader.TotalTime.TotalSeconds);
        var firstStart = Math.Clamp(first.StartSeconds, 0, firstEnd);
        var crossfadeStart = Math.Max(firstStart, firstEnd - Math.Max(0, transitionSeconds));
        var secondEnd = EffectiveEnd(second, secondReader.TotalTime.TotalSeconds);
        var secondStart = Math.Clamp(second.StartSeconds, 0, secondEnd);
        return MergeAsync(first, second, firstStart, crossfadeStart, secondStart,
            second.EndSeconds.HasValue ? secondEnd : null, transitionSeconds, false, outputPath, cancellationToken);
    }

    public Task MergeAsync(Jingle first, Jingle second, double firstStartSeconds,
        double firstCrossfadeStartSeconds, double secondStartSeconds, double? secondEndSeconds,
        double transitionSeconds, bool preserveFirstDuringTransition, string outputPath,
        CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var firstReader = new AudioFileReader(first.FilePath);
        using var secondReader = new AudioFileReader(second.FilePath);

        var firstEffectiveEnd = EffectiveEnd(first, firstReader.TotalTime.TotalSeconds);
        var firstStart = Math.Clamp(firstStartSeconds, 0, firstEffectiveEnd);
        var crossfadeStart = Math.Clamp(firstCrossfadeStartSeconds, firstStart, firstEffectiveEnd);
        var requestedFadeIn = Math.Max(0, transitionSeconds);
        var firstEnd = preserveFirstDuringTransition
            ? firstEffectiveEnd
            : Math.Min(firstEffectiveEnd, crossfadeStart + requestedFadeIn);

        var secondEffectiveEnd = Math.Clamp(
            secondEndSeconds ?? EffectiveEnd(second, secondReader.TotalTime.TotalSeconds),
            0, secondReader.TotalTime.TotalSeconds);
        var secondStart = Math.Clamp(secondStartSeconds, 0, secondEffectiveEnd);
        var firstDuration = Math.Max(0.001, firstEnd - firstStart);
        var secondDuration = Math.Max(0.001, secondEffectiveEnd - secondStart);
        var overlap = Math.Clamp(firstEnd - crossfadeStart, 0, secondDuration);

        var firstSource = Prepare(firstReader, first, firstStart, firstDuration);
        var secondSource = Prepare(secondReader, second, secondStart, secondDuration);
        var merged = new CrossfadeSequenceSampleProvider(firstSource, secondSource, firstDuration,
            overlap, requestedFadeIn, !preserveFirstDuringTransition);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        WaveFileWriter.CreateWaveFile16(outputPath, merged);
        cancellationToken.ThrowIfCancellationRequested();
    }, cancellationToken);

    private static double EffectiveEnd(Jingle jingle, double totalSeconds) =>
        Math.Clamp(jingle.EndSeconds ?? totalSeconds, 0, totalSeconds);

    private static ISampleProvider Prepare(AudioFileReader reader, Jingle jingle, double startSeconds, double duration)
    {
        ISampleProvider source = reader;
        if (source.WaveFormat.Channels == 1) source = new MonoToStereoSampleProvider(source);
        else if (source.WaveFormat.Channels != 2)
            throw new NotSupportedException("Kombinering stöder mono- och stereofiler.");
        if (source.WaveFormat.SampleRate != SampleRate) source = new WdlResamplingSampleProvider(source, SampleRate);
        var segment = new OffsetSampleProvider(source)
        {
            SkipOver = TimeSpan.FromSeconds(Math.Max(0, startSeconds)),
            Take = TimeSpan.FromSeconds(duration)
        };
        var gainDb = jingle.GainDb + (jingle.NormalizationEnabled ? jingle.NormalizationGainDb : 0);
        return new VolumeSampleProvider(segment) { Volume = (float)Math.Pow(10, gainDb / 20) };
    }

    private sealed class CrossfadeSequenceSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _first;
        private readonly ISampleProvider _second;
        private readonly int _channels;
        private readonly long _preSamples;
        private readonly long _overlapSamples;
        private readonly long _fadeInSamples;
        private readonly bool _fadeOutFirst;
        private readonly float[] _firstBuffer = new float[8192];
        private readonly float[] _secondBuffer = new float[8192];
        private long _preRead;
        private long _overlapRead;
        private long _secondRead;
        private bool _firstFinished;

        public CrossfadeSequenceSampleProvider(ISampleProvider first, ISampleProvider second,
            double firstDuration, double overlap, double secondFadeInDuration, bool fadeOutFirst)
        {
            if (!first.WaveFormat.Equals(second.WaveFormat)) throw new ArgumentException("Ljudformaten kunde inte anpassas.");
            _first = first; _second = second; WaveFormat = first.WaveFormat; _channels = WaveFormat.Channels;
            var firstSamples = Frames(firstDuration) * _channels;
            _overlapSamples = Frames(overlap) * _channels;
            _fadeInSamples = Frames(secondFadeInDuration) * _channels;
            _fadeOutFirst = fadeOutFirst;
            _preSamples = Math.Max(0, firstSamples - _overlapSamples);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            var written = 0;
            while (written < count)
            {
                if (_preRead < _preSamples)
                {
                    var wanted = Align(Math.Min(count - written, (int)Math.Min(int.MaxValue, _preSamples - _preRead)));
                    if (wanted <= 0) break;
                    var read = _first.Read(buffer, offset + written, wanted);
                    _preRead += read; written += read;
                    if (read < wanted) _firstFinished = true;
                    if (read == 0) _preRead = _preSamples;
                    continue;
                }

                if (_overlapRead < _overlapSamples)
                {
                    var wanted = Align(Math.Min(Math.Min(count - written, _firstBuffer.Length),
                        (int)Math.Min(int.MaxValue, _overlapSamples - _overlapRead)));
                    if (wanted <= 0) break;
                    var firstRead = _firstFinished ? 0 : _first.Read(_firstBuffer, 0, wanted);
                    var secondRead = _second.Read(_secondBuffer, 0, wanted);
                    var available = Math.Max(firstRead, secondRead);
                    for (var index = 0; index < available; index++)
                    {
                        var overlapProgress = (float)(_overlapRead + index) / Math.Max(1, _overlapSamples - 1);
                        var fadeInProgress = _fadeInSamples <= 0
                            ? 1
                            : Math.Clamp((float)(_secondRead + index) / Math.Max(1, _fadeInSamples - 1), 0, 1);
                        var a = index < firstRead ? _firstBuffer[index] : 0;
                        var b = index < secondRead ? _secondBuffer[index] : 0;
                        var firstGain = _fadeOutFirst ? 1 - overlapProgress : 1;
                        buffer[offset + written + index] = a * firstGain + b * fadeInProgress;
                    }
                    _overlapRead += wanted;
                    _secondRead += secondRead;
                    written += available;
                    if (available < wanted) _overlapRead = _overlapSamples;
                    continue;
                }

                var tailOffset = offset + written;
                var tail = _second.Read(buffer, tailOffset, count - written);
                if (_secondRead < _fadeInSamples)
                {
                    for (var index = 0; index < tail; index++)
                    {
                        var gain = Math.Clamp((float)(_secondRead + index) / Math.Max(1, _fadeInSamples - 1), 0, 1);
                        buffer[tailOffset + index] *= gain;
                    }
                }
                _secondRead += tail;
                written += tail;
                if (tail == 0) break;
            }
            return written;
        }

        private int Align(int samples) => samples - samples % _channels;
        private static long Frames(double seconds) => (long)Math.Round(seconds * SampleRate);
    }
}
