using System.Windows;
using System.Windows.Media;
using NAudio.Wave;

namespace FloorballDJ.Controls;

public sealed class WaveformControl : FrameworkElement
{
    private readonly record struct WavePeak(float Minimum, float Maximum);
    private const int MaxCachedWaveforms = 128;
    private static readonly object PeakCacheGate = new();
    private static readonly Dictionary<string, Task<WavePeak[]>> PeakCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Queue<string> PeakCacheOrder = new();
    private static readonly Brush BackgroundBrush = new LinearGradientBrush(
        Color.FromRgb(10, 19, 31), Color.FromRgb(13, 27, 42), 0);
    private static readonly Pen GridPen = new(new SolidColorBrush(Color.FromArgb(35, 145, 162, 186)), 1);
    private static readonly Pen IdlePen = new(new SolidColorBrush(Color.FromRgb(54, 76, 101)), 1.25);
    private static readonly Pen ActivePen = new(new SolidColorBrush(Color.FromRgb(54, 224, 180)), 1.6);
    private static readonly Brush IdleWaveBrush = new SolidColorBrush(Color.FromRgb(54, 76, 101));
    private static readonly Brush ActiveWaveBrush = new SolidColorBrush(Color.FromRgb(54, 224, 180));
    private static readonly Pen MarkerPen = new(new SolidColorBrush(Color.FromRgb(239, 247, 252)), 1.5);
    private static readonly Pen PlayheadPen = new(new SolidColorBrush(Color.FromRgb(245, 250, 255)), 2.2);
    private static readonly Brush ShadeBrush = new SolidColorBrush(Color.FromArgb(155, 5, 9, 16));

    static WaveformControl()
    {
        foreach (var freezable in new Freezable[] { BackgroundBrush, GridPen, IdlePen, ActivePen, IdleWaveBrush, ActiveWaveBrush, MarkerPen, PlayheadPen, ShadeBrush })
            if (freezable.CanFreeze) freezable.Freeze();
    }

    private WavePeak[] _peaks = [];
    private double _startFraction;
    private double _endFraction = 1;
    private int _loadVersion;
    private CancellationTokenSource? _loadCancellation;
    private StreamGeometry? _cachedGeometry;
    private WavePeak[]? _cachedGeometryPeaks;
    private double _cachedWidth;
    private double _cachedHeight;
    private double _cachedViewStart = double.NaN;
    private double _cachedViewEnd = double.NaN;

    public static readonly DependencyProperty FilePathProperty = DependencyProperty.Register(
        nameof(FilePath), typeof(string), typeof(WaveformControl),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender, OnFilePathChanged));

    public static readonly DependencyProperty PositionFractionProperty = DependencyProperty.Register(
        nameof(PositionFraction), typeof(double), typeof(WaveformControl),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ViewStartFractionProperty = DependencyProperty.Register(
        nameof(ViewStartFraction), typeof(double), typeof(WaveformControl),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ViewEndFractionProperty = DependencyProperty.Register(
        nameof(ViewEndFraction), typeof(double), typeof(WaveformControl),
        new FrameworkPropertyMetadata(1d, FrameworkPropertyMetadataOptions.AffectsRender));

    public string FilePath
    {
        get => (string)GetValue(FilePathProperty);
        set => SetValue(FilePathProperty, value);
    }

    public double PositionFraction
    {
        get => (double)GetValue(PositionFractionProperty);
        set => SetValue(PositionFractionProperty, value);
    }

    public double ViewStartFraction
    {
        get => (double)GetValue(ViewStartFractionProperty);
        set => SetValue(ViewStartFractionProperty, value);
    }

    public double ViewEndFraction
    {
        get => (double)GetValue(ViewEndFractionProperty);
        set => SetValue(ViewEndFractionProperty, value);
    }

    public double StartFraction
    {
        get => _startFraction;
        set { _startFraction = Math.Clamp(value, 0, 1); InvalidateVisual(); }
    }

    public double EndFraction
    {
        get => _endFraction;
        set { _endFraction = Math.Clamp(value, 0, 1); InvalidateVisual(); }
    }

    public async Task LoadAsync(string path)
    {
        var version = ++_loadVersion;
        var cancellation = new CancellationTokenSource();
        var previousCancellation = Interlocked.Exchange(ref _loadCancellation, cancellation);
        previousCancellation?.Cancel();
        previousCancellation?.Dispose();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            _peaks = [];
            ClearGeometryCache();
            InvalidateVisual();
            return;
        }

        string? cacheKey = null;
        Task<WavePeak[]>? loadTask = null;
        try
        {
            var info = new FileInfo(path);
            cacheKey = $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
            lock (PeakCacheGate)
            {
                if (!PeakCache.TryGetValue(cacheKey, out loadTask))
                {
                    loadTask = Task.Run(() => ReadPeaks(path));
                    PeakCache[cacheKey] = loadTask;
                    PeakCacheOrder.Enqueue(cacheKey);
                    while (PeakCache.Count > MaxCachedWaveforms && PeakCacheOrder.TryDequeue(out var oldest))
                        PeakCache.Remove(oldest);
                }
            }
            var peaks = await loadTask.WaitAsync(cancellation.Token);
            if (version != _loadVersion) return;
            _peaks = peaks;
            ClearGeometryCache();
        }
        catch (OperationCanceledException) { return; }
        catch
        {
            if (cacheKey is not null && loadTask is not null)
                lock (PeakCacheGate)
                    if (PeakCache.TryGetValue(cacheKey, out var cached) && ReferenceEquals(cached, loadTask))
                        PeakCache.Remove(cacheKey);
            if (version == _loadVersion) _peaks = [];
        }
        ClearGeometryCache();
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var bounds = new Rect(RenderSize);
        dc.DrawRectangle(BackgroundBrush, null, bounds);
        if (RenderSize.Width <= 1 || RenderSize.Height <= 1) return;

        for (var i = 1; i < 4; i++)
        {
            var x = RenderSize.Width * i / 4;
            dc.DrawLine(GridPen, new Point(x, 5), new Point(x, RenderSize.Height - 5));
        }

        var mid = RenderSize.Height / 2;
        var viewStart = ViewStartFraction;
        var viewEnd = Math.Max(viewStart + .000001, ViewEndFraction);
        var viewSpan = viewEnd - viewStart;
        var playedX = RenderSize.Width * (Math.Clamp(PositionFraction, viewStart, viewEnd) - viewStart) / viewSpan;
        if (_peaks.Length > 1)
        {
            var geometry = GetWaveGeometry(viewStart, viewEnd);
            dc.DrawGeometry(IdleWaveBrush, null, geometry);
            if (playedX > 0)
            {
                dc.PushClip(new RectangleGeometry(new Rect(0, 0, Math.Min(RenderSize.Width, playedX), RenderSize.Height)));
                dc.DrawGeometry(ActiveWaveBrush, null, geometry);
                dc.Pop();
            }
        }
        else
        {
            dc.DrawLine(IdlePen, new Point(0, mid), new Point(RenderSize.Width, mid));
        }

        if (PositionFraction >= viewStart && PositionFraction <= viewEnd)
            dc.DrawLine(PlayheadPen, new Point(playedX, 2), new Point(playedX, RenderSize.Height - 2));

        if (StartFraction > 0 || EndFraction < 1)
        {
            var startX = (StartFraction - viewStart) * RenderSize.Width / viewSpan;
            var endX = (EndFraction - viewStart) * RenderSize.Width / viewSpan;
            if (startX > 0) dc.DrawRectangle(ShadeBrush, null, new Rect(0, 0, Math.Min(startX, RenderSize.Width), RenderSize.Height));
            if (endX < RenderSize.Width) dc.DrawRectangle(ShadeBrush, null, new Rect(Math.Max(0, endX), 0, RenderSize.Width - Math.Max(0, endX), RenderSize.Height));
            if (startX >= 0 && startX <= RenderSize.Width) dc.DrawLine(MarkerPen, new Point(startX, 0), new Point(startX, RenderSize.Height));
            if (endX >= 0 && endX <= RenderSize.Width) dc.DrawLine(MarkerPen, new Point(endX, 0), new Point(endX, RenderSize.Height));
        }
    }

    private StreamGeometry GetWaveGeometry(double viewStart, double viewEnd)
    {
        var width = RenderSize.Width;
        var height = RenderSize.Height;
        if (_cachedGeometry is not null && ReferenceEquals(_cachedGeometryPeaks, _peaks) &&
            Math.Abs(_cachedWidth - width) < .01 && Math.Abs(_cachedHeight - height) < .01 &&
            Math.Abs(_cachedViewStart - viewStart) < 0.000000001 && Math.Abs(_cachedViewEnd - viewEnd) < 0.000000001)
            return _cachedGeometry;

        // Skapa en sammanhängande yta i stället för fristående vertikala linjer.
        // Interpoleringen gör att en rullande tiasekundersvy flyttar sig mjukt mellan
        // cacheproverna utan att staplar blinkar när deras heltalsindex ändras.
        var pointCount = Math.Max(2, (int)Math.Ceiling(width / 1.5) + 1);
        var top = new Point[pointCount];
        var bottom = new Point[pointCount];
        var amplitude = Math.Max(1, height / 2 - 5);
        var middle = height / 2;
        for (var index = 0; index < pointCount; index++)
        {
            var screenFraction = index / (double)(pointCount - 1);
            var fraction = viewStart + (viewEnd - viewStart) * screenFraction;
            var peak = InterpolatePeak(fraction);
            var x = width * screenFraction;
            top[index] = new Point(x, middle - peak.Maximum * amplitude);
            bottom[index] = new Point(x, middle - peak.Minimum * amplitude);
        }

        var geometry = new StreamGeometry { FillRule = FillRule.Nonzero };
        using (var context = geometry.Open())
        {
            context.BeginFigure(top[0], true, true);
            for (var index = 1; index < top.Length; index++) context.LineTo(top[index], true, false);
            for (var index = bottom.Length - 1; index >= 0; index--) context.LineTo(bottom[index], true, false);
        }
        if (geometry.CanFreeze) geometry.Freeze();
        _cachedGeometry = geometry;
        _cachedGeometryPeaks = _peaks;
        _cachedWidth = width;
        _cachedHeight = height;
        _cachedViewStart = viewStart;
        _cachedViewEnd = viewEnd;
        return geometry;
    }

    private WavePeak InterpolatePeak(double fraction)
    {
        if (_peaks.Length == 0 || fraction < 0 || fraction > 1) return new WavePeak(0, 0);
        var position = fraction * (_peaks.Length - 1);
        var first = Math.Clamp((int)Math.Floor(position), 0, _peaks.Length - 1);
        var second = Math.Min(_peaks.Length - 1, first + 1);
        var amount = (float)(position - first);
        return new WavePeak(
            _peaks[first].Minimum + (_peaks[second].Minimum - _peaks[first].Minimum) * amount,
            _peaks[first].Maximum + (_peaks[second].Maximum - _peaks[first].Maximum) * amount);
    }

    private void ClearGeometryCache()
    {
        _cachedGeometry = null;
        _cachedGeometryPeaks = null;
        _cachedViewStart = _cachedViewEnd = double.NaN;
    }

    private static async void OnFilePathChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is WaveformControl control)
            await control.LoadAsync(args.NewValue as string ?? "");
    }

    private static WavePeak[] ReadPeaks(string path)
    {
        using var reader = new AudioFileReader(path);
        var count = Math.Clamp((int)Math.Ceiling(reader.TotalTime.TotalSeconds * 80), 6000, 240000);
        var totalSamples = Math.Max(1L, reader.Length / 4);
        var samplesPerPeak = Math.Max(1L, totalSamples / count);
        var result = new List<WavePeak>(count);
        var buffer = new float[Math.Min(16384, (int)Math.Min(int.MaxValue, samplesPerPeak))];
        long accumulated = 0;
        float minimum = 0, maximum = 0;
        int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                minimum = Math.Min(minimum, buffer[i]);
                maximum = Math.Max(maximum, buffer[i]);
                accumulated++;
                if (accumulated < samplesPerPeak) continue;
                result.Add(new WavePeak(minimum, maximum));
                accumulated = 0;
                minimum = maximum = 0;
            }
        }
        if (accumulated > 0) result.Add(new WavePeak(minimum, maximum));
        return [.. result];
    }
}
