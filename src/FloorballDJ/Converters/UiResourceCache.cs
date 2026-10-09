using System.Globalization;
using System.Runtime.CompilerServices;
using System.Collections;
using System.Windows.Data;
using System.Windows.Media;
using FloorballDJ.Infrastructure;

namespace FloorballDJ.Converters;

internal static class UiResourceCache
{
    private readonly record struct BrushKey(string Text, string Culture);
    private static readonly BoundedCache<BrushKey, Brush> Solid = new(256);
    private static readonly BoundedCache<BrushKey, Brush> Gradient = new(256);
    internal static int SolidCount => Solid.Count;
    internal static int GradientCount => Gradient.Count;

    internal static Brush GetSolid(string text) => Solid.GetOrAdd(new(text, CultureInfo.CurrentCulture.Name), static key =>
    {
        try { return Freeze((Brush?)new BrushConverter().ConvertFromString(key.Text) ?? Brushes.Transparent); }
        catch { return Brushes.Transparent; }
    });

    internal static Brush GetGradient(string text) => Gradient.GetOrAdd(new(text, CultureInfo.CurrentCulture.Name), static key =>
    {
        try
        {
            var parsed = new BrushConverter().ConvertFromString(key.Text);
            var color = parsed is SolidColorBrush solid ? solid.Color : Color.FromRgb(24, 35, 56);
            return Freeze(new LinearGradientBrush(
            [
                new GradientStop(Mix(color, Colors.White, .16), 0),
                new GradientStop(color, .46),
                new GradientStop(Mix(color, Colors.Black, .20), .72),
                new GradientStop(Mix(color, Colors.White, .07), 1)
            ], new System.Windows.Point(0, 0), new System.Windows.Point(1, 1)));
        }
        catch { return Freeze(new SolidColorBrush(Color.FromRgb(24, 35, 56))); }
    });

    private static Brush Freeze(Brush brush)
    {
        if (brush.CanFreeze && !brush.IsFrozen) brush.Freeze();
        return brush;
    }
    private static Color Mix(Color source, Color target, double amount) => Color.FromArgb(source.A,
        (byte)(source.R + (target.R - source.R) * amount),
        (byte)(source.G + (target.G - source.G) * amount),
        (byte)(source.B + (target.B - source.B) * amount));
}

/// <summary>Per-converter views, weakly owned by their source. A hit refreshes
/// membership because positions/layout may have been edited in place.</summary>
internal sealed class StableCollectionViews
{
    private readonly ConditionalWeakTable<IList, SourceViews> _sources = new();
    private sealed class SourceViews
    {
        internal readonly LinkedList<(object? Owner, int First, int Last, ListCollectionView View)> Views = new();
    }
    internal long CreatedCount { get; private set; }
    internal ListCollectionView Get(IList source, object? owner, int first, int last, Predicate<object> filter)
    {
        var retained = _sources.GetOrCreateValue(source).Views;
        for (var node = retained.First; node is not null; node = node.Next)
        {
            if (!ReferenceEquals(node.Value.Owner, owner) || node.Value.First != first || node.Value.Last != last) continue;
            retained.Remove(node);
            retained.AddFirst(node);
            node.Value.View.Refresh();
            return node.Value.View;
        }
        var view = new ListCollectionView(source) { Filter = filter };
        retained.AddFirst((owner, first, last, view));
        if (retained.Count > 4) retained.RemoveLast();
        CreatedCount++;
        return view;
    }
    internal int RetainedCount(IList source) => _sources.TryGetValue(source, out var views) ? views.Views.Count : 0;
}
