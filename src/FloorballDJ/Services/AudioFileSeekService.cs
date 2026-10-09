using System.Buffers;
using NAudio.Wave;

namespace FloorballDJ.Services;

/// <summary>
/// Seeks decoded audio without relying on Media Foundation's broken direct FLAC seek.
/// Some 24-bit FLAC files report the requested position but return silence afterwards.
/// Decoding and discarding up to the requested frame is deterministic and sample-accurate.
/// </summary>
public static class AudioFileSeekService
{
    private const int BufferSize = 32768;

    public static bool RequiresSequentialSeek(string? path)
        => string.Equals(Path.GetExtension(path), ".flac", StringComparison.OrdinalIgnoreCase);

    public static void Seek(AudioFileReader reader, string? path, TimeSpan requestedPosition,
        CancellationToken cancellationToken = default)
    {
        var target = TimeSpan.FromTicks(Math.Clamp(requestedPosition.Ticks, 0, reader.TotalTime.Ticks));
        if (!RequiresSequentialSeek(path))
        {
            reader.CurrentTime = target;
            return;
        }

        // Position zero is safe for Media Foundation. Seeking to any other FLAC timestamp can
        // leave the decoder at the right clock position while it outputs only zero samples.
        reader.Position = 0;
        var channels = Math.Max(1, reader.WaveFormat.Channels);
        var targetFrames = (long)Math.Round(target.TotalSeconds * reader.WaveFormat.SampleRate);
        var remainingSamples = targetFrames * channels;
        if (remainingSamples <= 0) return;

        var buffer = ArrayPool<float>.Shared.Rent(BufferSize - BufferSize % channels);
        try
        {
            while (remainingSamples > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var wanted = (int)Math.Min(buffer.Length, remainingSamples);
                wanted -= wanted % channels;
                if (wanted <= 0) break;
                var read = reader.Read(buffer, 0, wanted);
                if (read <= 0) break;
                remainingSamples -= read;
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(buffer);
        }
    }
}

/// <summary>
/// Defers seeking until the consumer's audio thread calls Read. Media Foundation FLAC readers
/// must not be primed with Read on one thread and then consumed on another COM apartment.
/// </summary>
public sealed class AudioFileSeekSampleProvider : ISampleProvider
{
    private readonly AudioFileReader _reader;
    private readonly string? _path;
    private readonly object _gate = new();
    private TimeSpan? _pendingPosition;
    private readonly PerformanceOperation _performance;
    private bool _returnedDecodedFrames;

    public AudioFileSeekSampleProvider(AudioFileReader reader, string? path) : this(reader, path, default) { }

    internal AudioFileSeekSampleProvider(AudioFileReader reader, string? path, PerformanceOperation performance)
    {
        _reader = reader;
        _path = path;
        _performance = performance;
    }

    public WaveFormat WaveFormat => _reader.WaveFormat;

    public void Seek(TimeSpan position)
    {
        _performance.Mark("SeekRequested", position.TotalSeconds);
        lock (_gate) _pendingPosition = position;
    }

    public int Read(float[] buffer, int offset, int count)
    {
        lock (_gate)
        {
            if (_pendingPosition is TimeSpan position)
            {
                using (_performance.Measure("SeekApplied")) AudioFileSeekService.Seek(_reader, _path, position);
                _pendingPosition = null;
            }
            if (!_performance.Enabled) return _reader.Read(buffer, offset, count);
            var started = _performance.Timestamp;
            var read = _reader.Read(buffer, offset, count);
            _performance.Duration("DecodeRead", started, read);
            if (read > 0 && !_returnedDecodedFrames)
            {
                _returnedDecodedFrames = true;
                _performance.Mark("FirstDecodedFrames", read);
            }
            return read;
        }
    }
}
