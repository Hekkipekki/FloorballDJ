using System.Windows;
using System.Windows.Media;

namespace FloorballDJ.Controls;

public sealed class StereoLevelMeter : FrameworkElement
{
    private const int SegmentCount = 18;
    private static readonly Brush OffBrush = new SolidColorBrush(Color.FromRgb(29, 45, 63));
    private static readonly Brush GreenBrush = new SolidColorBrush(Color.FromRgb(54, 224, 180));
    private static readonly Brush YellowBrush = new SolidColorBrush(Color.FromRgb(255, 193, 90));
    private static readonly Brush RedBrush = new SolidColorBrush(Color.FromRgb(255, 100, 117));
    private static readonly Brush LabelBrush = new SolidColorBrush(Color.FromRgb(145, 162, 186));
    private static readonly Typeface LabelTypeface = new("Cascadia Mono");

    public static readonly DependencyProperty LeftDbProperty = DependencyProperty.Register(
        nameof(LeftDb), typeof(float), typeof(StereoLevelMeter),
        new FrameworkPropertyMetadata(-60f, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty RightDbProperty = DependencyProperty.Register(
        nameof(RightDb), typeof(float), typeof(StereoLevelMeter),
        new FrameworkPropertyMetadata(-60f, FrameworkPropertyMetadataOptions.AffectsRender));

    public float LeftDb
    {
        get => (float)GetValue(LeftDbProperty);
        set => SetValue(LeftDbProperty, value);
    }

    public float RightDb
    {
        get => (float)GetValue(RightDbProperty);
        set => SetValue(RightDbProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(Math.Min(availableSize.Width, 176), Math.Min(availableSize.Height, 44));

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        DrawChannel(dc, "L", LeftDb, 4);
        DrawChannel(dc, "R", RightDb, Math.Max(22, RenderSize.Height / 2 + 2));
    }

    private void DrawChannel(DrawingContext dc, string label, float db, double y)
    {
        const double labelWidth = 14;
        const double gap = 2;
        var meterWidth = Math.Max(1, RenderSize.Width - labelWidth);
        var segmentWidth = Math.Max(2, (meterWidth - gap * (SegmentCount - 1)) / SegmentCount);
        var height = Math.Max(6, Math.Min(10, RenderSize.Height / 2 - 6));
        var activeSegments = (int)Math.Ceiling(Math.Clamp((db + 60) / 60, 0, 1) * SegmentCount);

        var formattedLabel = new FormattedText(label, System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, LabelTypeface, 9, LabelBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(formattedLabel, new Point(0, y - 1));

        for (var index = 0; index < SegmentCount; index++)
        {
            var x = labelWidth + index * (segmentWidth + gap);
            var brush = index >= activeSegments ? OffBrush :
                index >= SegmentCount - 2 ? RedBrush :
                index >= SegmentCount - 5 ? YellowBrush : GreenBrush;
            dc.DrawRoundedRectangle(brush, null, new Rect(x, y, segmentWidth, height), 1, 1);
        }
    }
}
