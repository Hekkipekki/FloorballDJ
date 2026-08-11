using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using FloorballDJ.Models;

namespace FloorballDJ.Services;

public sealed class JingleMergeService
{
    private const int SampleRate = 48000;

    public enum TransitionMode
    {
        Crossfade,
        SequentialFade,
        MixSound
    }

    public sealed record Segment(Jingle Jingle, double StartSeconds, double? EndSeconds,
        double VolumeAdjustmentDb = 0);

    public sealed record Transition(double StartSecondsInPrevious, double FadeOutSeconds,
        double FadeInSeconds, TransitionMode Mode);

    public Task MergeManyAsync(IReadOnlyList<Segment> segments, IReadOnlyList<Transition> transitions,
        string outputPath, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        if (segments.Count < 2) throw new ArgumentException("Välj minst två ljud.", nameof(segments));
        if (transitions.Count != segments.Count - 1)
            throw new ArgumentException("Varje skarv mellan ljuden måste ha en övergång.", nameof(transitions));

        var readers = new List<AudioFileReader>(segments.Count);
        try
        {
            foreach (var segment in segments)
            {
                cancellationToken.ThrowIfCancellationRequested();
                readers.Add(new AudioFileReader(segment.Jingle.FilePath));
            }

            var starts = new double[segments.Count];
            var ends = new double[segments.Count];
            var durations = new double[segments.Count];
            var timelineStarts = new double[segments.Count];
            for (var index = 0; index < segments.Count; index++)
            {
                var total = readers[index].TotalTime.TotalSeconds;
                var configuredEnd = segments[index].EndSeconds ?? EffectiveEnd(segments[index].Jingle, total);
                var naturalEnd = Math.Clamp(configuredEnd, 0, total);
                starts[index] = Math.Clamp(segments[index].StartSeconds, 0, naturalEnd);
                ends[index] = naturalEnd;

                if (index < transitions.Count && transitions[index].Mode != TransitionMode.MixSound)
                {
                    var transitionStart = Math.Clamp(transitions[index].StartSecondsInPrevious, starts[index], naturalEnd);
                    ends[index] = Math.Min(naturalEnd, transitionStart + Math.Max(0, transitions[index].FadeOutSeconds));
                }

                durations[index] = Math.Max(0.001, ends[index] - starts[index]);
            }

            for (var index = 0; index < transitions.Count; index++)
            {
                var transition = transitions[index];
                var markerOffset = Math.Clamp(transition.StartSecondsInPrevious - starts[index], 0, durations[index]);
                timelineStarts[index + 1] = timelineStarts[index] + markerOffset;
                if (transition.Mode == TransitionMode.SequentialFade)
                    timelineStarts[index + 1] += Math.Max(0, transition.FadeOutSeconds);
            }

            var inputs = new List<ISampleProvider>(segments.Count);
            for (var index = 0; index < segments.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = Prepare(readers[index], segments[index].Jingle, starts[index], durations[index],
                    segments[index].VolumeAdjustmentDb);
                var fadeIn = index == 0 ? 0 : Math.Max(0, transitions[index - 1].FadeInSeconds);
                var fadeOutStart = -1d;
                var fadeOut = 0d;
                if (index < transitions.Count && transitions[index].Mode != TransitionMode.MixSound)
                {
                    fadeOutStart = Math.Clamp(transitions[index].StartSecondsInPrevious - starts[index], 0, durations[index]);
                    fadeOut = Math.Max(0, transitions[index].FadeOutSeconds);
                }
                source = new SegmentEnvelopeSampleProvider(source, fadeIn, fadeOutStart, fadeOut);
                if (timelineStarts[index] > 0)
                    source = new OffsetSampleProvider(source) { DelayBy = TimeSpan.FromSeconds(timelineStarts[index]) };
                inputs.Add(source);
            }

            var merged = new MixingSampleProvider(inputs);

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
            WaveFileWriter.CreateWaveFile16(outputPath, merged);
            cancellationToken.ThrowIfCancellationRequested();
        }
        finally
        {
            foreach (var reader in readers) reader.Dispose();
        }
    }, cancellationToken);

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

    private static ISampleProvider Prepare(AudioFileReader reader, Jingle jingle, double startSeconds, double duration,
        double volumeAdjustmentDb = 0)
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
        var gainDb = jingle.GainDb + (jingle.NormalizationEnabled ? jingle.NormalizationGainDb : 0) + volumeAdjustmentDb;
        return new VolumeSampleProvider(segment) { Volume = (float)Math.Pow(10, gainDb / 20) };
    }

    private sealed class SegmentEnvelopeSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly long _fadeInSamples;
        private readonly long _fadeOutStartSamples;
        private readonly long _fadeOutSamples;
        private long _position;

        public SegmentEnvelopeSampleProvider(ISampleProvider source, double fadeInSeconds,
            double fadeOutStartSeconds, double fadeOutSeconds)
        {
            _source = source;
            WaveFormat = source.WaveFormat;
            _fadeInSamples = ToSamples(fadeInSeconds);
            _fadeOutStartSamples = fadeOutStartSeconds < 0 ? -1 : ToSamples(fadeOutStartSeconds);
            _fadeOutSamples = ToSamples(fadeOutSeconds);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            var read = _source.Read(buffer, offset, count);
            for (var index = 0; index < read; index++)
            {
                var samplePosition = _position + index;
                var gain = _fadeInSamples <= 0 ? 1f : Math.Clamp((float)samplePosition / _fadeInSamples, 0, 1);
                if (_fadeOutStartSamples >= 0 && samplePosition >= _fadeOutStartSamples)
                {
                    var fadeOutGain = _fadeOutSamples <= 0
                        ? 0
                        : Math.Clamp(1f - (float)(samplePosition - _fadeOutStartSamples) / _fadeOutSamples, 0, 1);
                    gain *= fadeOutGain;
                }
                buffer[offset + index] *= gain;
            }
            _position += read;
            return read;
        }

        private long ToSamples(double seconds) => (long)Math.Round(Math.Max(0, seconds) * WaveFormat.SampleRate * WaveFormat.Channels);
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
