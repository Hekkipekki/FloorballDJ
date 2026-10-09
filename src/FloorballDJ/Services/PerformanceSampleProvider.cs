using NAudio.Wave;

namespace FloorballDJ.Services;

/// <summary>Diagnostic boundary before the output consumer, not an audio-device clock.</summary>
internal sealed class PerformanceSampleProvider(ISampleProvider source, PerformanceOperation operation) : ISampleProvider
{
    private bool _returnedFrames;
    private bool _returnedSignal;
    public WaveFormat WaveFormat => source.WaveFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        // A capped/failed/closed capture also removes per-buffer diagnostic work.
        if (!operation.Enabled) return source.Read(buffer, offset, count);
        var started = operation.Timestamp;
        int read;
        try { read = source.Read(buffer, offset, count); }
        catch
        {
            operation.Mark("AudioReadFailed");
            throw;
        }
        operation.Duration("AudioProviderRead", started, read);
        if (read > 0 && !_returnedFrames)
        {
            _returnedFrames = true;
            operation.Mark("FirstBufferReturned", read);
        }
        if (!_returnedSignal)
            for (var index = offset; index < offset + read; index++)
                if (Math.Abs(buffer[index]) > .0001f)
                {
                    _returnedSignal = true;
                    operation.Mark("FirstSignalReturned");
                    break;
                }
        return read;
    }
}
