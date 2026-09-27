using NAudio.Wave;

namespace FloorballDJ.Services;

/// <summary>
/// Applies gain ramps inside the audio callback. This avoids audible zippering and
/// output-buffer stalls caused by changing VolumeSampleProvider from the UI thread.
/// </summary>
internal sealed class SmoothGainSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly object _gate = new();
    private float _currentGain;
    private float _targetGain;
    private float _startGain;
    private long _totalFrames;
    private long _framesRemaining;
    private TaskCompletionSource _transition = CompletedTransition();

    private static TaskCompletionSource CompletedTransition()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        completion.SetResult();
        return completion;
    }

    public Task TransitionCompleted { get { lock (_gate) return _transition.Task; } }

    public SmoothGainSampleProvider(ISampleProvider source, float initialGain = 1)
    {
        _source = source;
        _currentGain = _targetGain = Math.Clamp(initialGain, 0, 4);
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    public void SetTarget(float targetGain, double transitionSeconds)
    {
        lock (_gate)
        {
            _transition.TrySetResult();
            _transition = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _targetGain = Math.Clamp(targetGain, 0, 4);
            _framesRemaining = (long)Math.Round(Math.Max(0, transitionSeconds) * WaveFormat.SampleRate);
            _totalFrames = _framesRemaining;
            _startGain = _currentGain;
            if (_framesRemaining <= 0)
            {
                _currentGain = _targetGain;
                _transition.TrySetResult();
                return;
            }

        }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        var read = _source.Read(buffer, offset, count);
        if (read <= 0) return read;

        var channels = Math.Max(1, WaveFormat.Channels);
        lock (_gate)
        {
            var end = offset + read;
            for (var frameStart = offset; frameStart < end; frameStart += channels)
            {
                var frameEnd = Math.Min(end, frameStart + channels);
                for (var sample = frameStart; sample < frameEnd; sample++)
                    buffer[sample] *= _currentGain;

                if (_framesRemaining <= 0) continue;
                // Calculate from the original gain, avoiding accumulated float error
                // during long fades. Smoothstep reaches both endpoints gently.
                var progress = 1.0 - (double)--_framesRemaining / _totalFrames;
                var blend = progress * progress * (3 - 2 * progress);
                _currentGain = (float)(_startGain + (_targetGain - _startGain) * blend);
                if (_framesRemaining == 0)
                {
                    _currentGain = _targetGain;
                    _transition.TrySetResult();
                }
            }
        }
        return read;
    }
}
