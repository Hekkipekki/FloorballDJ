using FloorballDJ.Models;
using NAudio.Dsp;
using NAudio.Wave;

namespace FloorballDJ.Services;

public sealed class DjEffectsSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly BiQuadFilter[] _low;
    private readonly BiQuadFilter[] _mid;
    private readonly BiQuadFilter[] _high;
    private readonly double[] _envelopes;
    private readonly bool _compressorEnabled;
    private readonly double _thresholdDb;
    private readonly double _ratio;
    private readonly double _attackCoefficient;
    private readonly double _releaseCoefficient;
    private readonly float _ceiling;
    private readonly bool _limiterEnabled;
    private float _maximumReductionDb;

    public DjEffectsSampleProvider(ISampleProvider source, Jingle settings, double limiterCeilingDbtp, bool limiterEnabled = true)
    {
        _source = source;
        WaveFormat = source.WaveFormat;
        var channels = Math.Max(1, WaveFormat.Channels);
        var sampleRate = WaveFormat.SampleRate;
        _low = Enumerable.Range(0, channels).Select(_ => BiQuadFilter.LowShelf(sampleRate, 120, .707f, (float)settings.EqLowDb)).ToArray();
        _mid = Enumerable.Range(0, channels).Select(_ => BiQuadFilter.PeakingEQ(sampleRate, 1000, .9f, (float)settings.EqMidDb)).ToArray();
        _high = Enumerable.Range(0, channels).Select(_ => BiQuadFilter.HighShelf(sampleRate, 8000, .707f, (float)settings.EqHighDb)).ToArray();
        _envelopes = new double[channels];
        _compressorEnabled = settings.CompressorEnabled;
        _thresholdDb = Math.Clamp(settings.CompressorThresholdDb, -48, 0);
        _ratio = Math.Clamp(settings.CompressorRatio, 1, 20);
        _attackCoefficient = TimeCoefficient(settings.CompressorAttackMs, sampleRate);
        _releaseCoefficient = TimeCoefficient(settings.CompressorReleaseMs, sampleRate);
        _ceiling = (float)Math.Pow(10, Math.Clamp(limiterCeilingDbtp, -12, 0) / 20);
        _limiterEnabled = limiterEnabled;
    }

    public WaveFormat WaveFormat { get; }
    public float MaximumReductionDb => _maximumReductionDb;

    public int Read(float[] buffer, int offset, int count)
    {
        var read = _source.Read(buffer, offset, count);
        var channels = WaveFormat.Channels;
        for (var index = 0; index < read; index++)
        {
            var channel = index % channels;
            var sample = _high[channel].Transform(_mid[channel].Transform(_low[channel].Transform(buffer[offset + index])));
            if (_compressorEnabled)
            {
                var level = Math.Abs(sample);
                var coefficient = level > _envelopes[channel] ? _attackCoefficient : _releaseCoefficient;
                _envelopes[channel] = coefficient * _envelopes[channel] + (1 - coefficient) * level;
                var envelopeDb = _envelopes[channel] <= 1e-9 ? -120 : 20 * Math.Log10(_envelopes[channel]);
                if (envelopeDb > _thresholdDb)
                {
                    var reductionDb = (envelopeDb - _thresholdDb) * (1 - 1 / _ratio);
                    _maximumReductionDb = Math.Max(_maximumReductionDb * .9995f, (float)reductionDb);
                    sample *= (float)Math.Pow(10, -reductionDb / 20);
                }
            }
            buffer[offset + index] = _limiterEnabled ? SoftLimit(sample, _ceiling) : sample;
        }
        return read;
    }

    private static float SoftLimit(float sample, float ceiling)
    {
        var absolute = Math.Abs(sample);
        if (absolute <= ceiling) return sample;
        var excess = (absolute - ceiling) / Math.Max(.0001f, 1 - ceiling);
        var limited = ceiling + (1 - ceiling) * MathF.Tanh(excess);
        return MathF.CopySign(Math.Min(1, limited), sample);
    }

    private static double TimeCoefficient(double milliseconds, int sampleRate)
        => Math.Exp(-1 / (Math.Max(.1, milliseconds) * .001 * sampleRate));
}
