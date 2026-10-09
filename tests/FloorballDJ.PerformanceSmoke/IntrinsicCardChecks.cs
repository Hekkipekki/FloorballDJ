using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using FloorballDJ.Controls;

internal static class IntrinsicCardChecks
{
    private sealed class Item : INotifyPropertyChanged
    {
        private string _title = "Short title";
        public string Title { get => _title; set { _title = value; PropertyChanged?.Invoke(this, new(nameof(Title))); } }
        public event PropertyChangedEventHandler? PropertyChanged;
    }
    private sealed class Probe : Decorator
    {
        public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(Probe),
            new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsMeasure, (owner, args) => ((TextBlock)((Probe)owner).Child).Text = (string)args.NewValue));
        public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
        public Probe() { Child = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis }; }
        internal int Measures;
        protected override Size MeasureOverride(Size constraint) { Measures++; return base.MeasureOverride(constraint); }
    }
    internal static async Task RunAsync(string? output)
    {
        var data = new ObservableCollection<Item>(Enumerable.Range(0, 80).Select(_ => new Item()));
        var factory = new FrameworkElementFactory(typeof(Probe)); factory.SetValue(FrameworkElement.WidthProperty, 260d);
        factory.SetBinding(Probe.TextProperty, new Binding(nameof(Item.Title)));
        var songs = new VirtualizingSongItemsControl { ItemsSource = data, ItemTemplate = new DataTemplate { VisualTree = factory },
            ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(SongWrapPanel))),
            Template = new ControlTemplate(typeof(ItemsControl)) { VisualTree = new FrameworkElementFactory(typeof(ItemsPresenter)) } };
        var window = new Window { Width = 1040, Height = 700, ShowInTaskbar = false, Content = songs };
        try
        {
            window.Show(); await Idle();
            var panel = songs.Panel!;
            var first = Children(songs).OfType<Probe>().First(); var count = first.Measures;
            for (var pass = 0; pass < 12; pass++) { panel.InvalidateMeasure(); songs.UpdateLayout(); }
            Check(first.Measures == count, "Stable panel layout reuses the actual text measurement without switching constraints");
            var stableMeasurements = first.Measures - count;
            var height = panel.CardHeight; songs.FontSize = 32; await Idle();
            Check(panel.CardHeight > height && first.Measures > count, "Inherited font change invalidates and recomputes the intrinsic size");
            Check(Children(songs).OfType<Probe>().All(probe => probe.ActualHeight >= panel.CardHeight - .01), "New font height is applied to every realized row");
            count = first.Measures; data[0].Title = "An updated title with a different text measurement"; await Idle();
            Check(first.Measures > count && first.Text == data[0].Title, "Live bound content invalidates cached WPF measurement");
            var alternate = new FrameworkElementFactory(typeof(Probe)); alternate.SetValue(FrameworkElement.WidthProperty, 300d);
            alternate.SetValue(FrameworkElement.HeightProperty, 70d); alternate.SetBinding(Probe.TextProperty, new Binding(nameof(Item.Title)));
            songs.ItemTemplate = new DataTemplate { VisualTree = alternate }; await Idle();
            Check(Math.Abs(panel.CardHeight - 70) < .01 && panel.Columns == (int)Math.Floor(panel.ViewportWidth / 300), "Replacement template changes intrinsic height/width and wrapping");
            panel.SetVerticalOffset(panel.ExtentHeight); await Idle();
            Check(Children(songs).OfType<Probe>().All(probe => probe.ActualHeight == 70 && probe.Text.Length > 0), "Recycled/rebound sample cards are measured and display current content");
            var created = songs.CreatedContainers; var reused = songs.ReusedContainers;
            var discarded = AssignTransientSource(songs); await Idle();
            songs.ItemsSource = new ObservableCollection<Item>(Enumerable.Range(0, 5).Select(_ => new Item { Title = "Current source" })); await Idle();
            Check(songs.ReusedContainers > reused && songs.CreatedContainers == created,
                "Source resets reuse real container/template instances rather than allocating replacements");
            Check(Children(songs).OfType<Probe>().All(probe => probe.Text == "Current source"), "Pooled templates rebind every current title");
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); await Idle();
            Check(!discarded.IsAlive, "Cached/rebound containers do not retain discarded source data");
            songs.ItemTemplate = null;
            songs.ItemsSource = new ObservableCollection<string>(["Plain replacement"]); await Idle();
            Check(!Children(songs).OfType<Probe>().Any() && Children(songs).OfType<TextBlock>().Any(text => text.Text == "Plain replacement"),
                "Removing the template cannot reuse an obsolete explicit-template tree");
            songs.ItemsSource = new ObservableCollection<string>(); await Idle();
            Check(songs.Containers.Active == 0 && songs.Containers.Cached == 0 && songs.Containers.Budget == 0,
                "A visible empty list releases both generated and cached containers");
            if (output is not null)
            {
                Directory.CreateDirectory(output);
                File.WriteAllText(Path.Combine(output, "intrinsic-card-checks.json"), JsonSerializer.Serialize(new {
                    version = "p4.10-intrinsic-checks-v1", passed = true, stablePanelPasses = 12,
                    stableTextMeasurements = stableMeasurements, inheritedFontInvalidation = true, boundContentInvalidation = true,
                    templateInvalidation = true, recycledContentCurrent = true,
                    sourceResetReuse = true, discardedDataReleased = true, emptyPoolReleased = true, removedTemplateInvalidation = true,
                    limitation = "Shown WPF text probe with fixed card width; current monitor DPI. The production layout/pixel/focus suite covers real song templates separately. Physical mixed-DPI remains unqualified."
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
            Console.WriteLine("PASS: intrinsic card measurement reuse, inherited font/live content/template invalidation and recycled sample binding.");
        }
        finally { window.Close(); await Idle(); }
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference AssignTransientSource(VirtualizingSongItemsControl songs)
    {
        var source = new ObservableCollection<Item>(Enumerable.Range(0, 80).Select(_ => new Item { Title = "Transient source" }));
        songs.ItemsSource = source; songs.UpdateLayout();
        return new WeakReference(source[0]);
    }
    private static IEnumerable<DependencyObject> Children(DependencyObject root)
    {
        yield return root; for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) foreach (var child in Children(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static async Task Idle() => await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
