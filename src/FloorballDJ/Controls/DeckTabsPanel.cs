using System.Windows;
using System.Windows.Controls;

namespace FloorballDJ.Controls;

/// <summary>Stable-order tab rows, wrapping by available width and a user-defined count.</summary>
public sealed class DeckTabsPanel : Panel
{
    public static readonly DependencyProperty TabWidthProperty = DependencyProperty.RegisterAttached("TabWidth", typeof(double), typeof(DeckTabsPanel), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsParentMeasure));
    public static readonly DependencyProperty TabHeightProperty = DependencyProperty.RegisterAttached("TabHeight", typeof(double), typeof(DeckTabsPanel), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsParentMeasure));
    public static readonly DependencyProperty StartsNewRowProperty = DependencyProperty.RegisterAttached("StartsNewRow", typeof(bool), typeof(DeckTabsPanel), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsParentMeasure));
    public static double GetTabWidth(DependencyObject child) => (double)child.GetValue(TabWidthProperty);
    public static void SetTabWidth(DependencyObject child, double value) => child.SetValue(TabWidthProperty, value);
    public static double GetTabHeight(DependencyObject child) => (double)child.GetValue(TabHeightProperty);
    public static void SetTabHeight(DependencyObject child, double value) => child.SetValue(TabHeightProperty, value);
    public static bool GetStartsNewRow(DependencyObject child) => (bool)child.GetValue(StartsNewRowProperty);
    public static void SetStartsNewRow(DependencyObject child, bool value) => child.SetValue(StartsNewRowProperty, value);
    private double WidthFor(UIElement child) => GetTabWidth(child) > 0 ? GetTabWidth(child) : ItemWidth;
    private double HeightFor(UIElement child) => GetTabHeight(child) > 0 ? GetTabHeight(child) : ItemHeight;
    public static readonly DependencyProperty ItemWidthProperty = DependencyProperty.Register(nameof(ItemWidth), typeof(double), typeof(DeckTabsPanel), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty ItemHeightProperty = DependencyProperty.Register(nameof(ItemHeight), typeof(double), typeof(DeckTabsPanel), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty MaxColumnsProperty = DependencyProperty.Register(nameof(MaxColumns), typeof(int), typeof(DeckTabsPanel), new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public double ItemWidth { get => (double)GetValue(ItemWidthProperty); set => SetValue(ItemWidthProperty, value); }
    public double ItemHeight { get => (double)GetValue(ItemHeightProperty); set => SetValue(ItemHeightProperty, value); }
    public int MaxColumns { get => (int)GetValue(MaxColumnsProperty); set => SetValue(MaxColumnsProperty, value); }

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (UIElement child in InternalChildren)
            child.Measure(new Size(WidthFor(child) > 0 ? Math.Max(64, WidthFor(child)) : double.PositiveInfinity,
                double.PositiveInfinity));
        return Layout(availableSize.Width, false);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Layout(finalSize.Width, true);
        return finalSize;
    }

    private Size Layout(double width, bool arrange)
    {
        double x = 0, y = 0, rowHeight = 0, usedWidth = 0;
        var count = 0;
        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility == Visibility.Collapsed) continue;
            var w = Math.Min(width, WidthFor(child) > 0 ? Math.Max(64, WidthFor(child)) : child.DesiredSize.Width);
            var h = Math.Max(child.DesiredSize.Height, HeightFor(child) > 0 ? Math.Max(36, HeightFor(child)) : 0);
            if (count > 0 && (GetStartsNewRow(child) || (MaxColumns > 0 && count >= MaxColumns) || x + w > width))
            {
                y += rowHeight;
                x = rowHeight = 0;
                count = 0;
            }
            if (arrange) child.Arrange(new Rect(x, y, w, h));
            x += w;
            usedWidth = Math.Max(usedWidth, x);
            rowHeight = Math.Max(rowHeight, h);
            count++;
        }
        return new Size(usedWidth, y + rowHeight);
    }
}
