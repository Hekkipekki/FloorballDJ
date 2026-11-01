using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;

namespace FloorballDJ.Views;

public static class QueueRowNumbers
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(QueueRowNumbers), new PropertyMetadata(false, EnabledChanged));
    public static void SetEnabled(DependencyObject target, bool value) => target.SetValue(EnabledProperty, value);
    public static bool GetEnabled(DependencyObject target) => (bool)target.GetValue(EnabledProperty);
    public static readonly DependencyProperty PositionProperty = DependencyProperty.RegisterAttached(
        "Position", typeof(int), typeof(QueueRowNumbers), new PropertyMetadata(0));
    public static int GetPosition(DependencyObject target) => (int)target.GetValue(PositionProperty);
    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State", typeof(Numbering), typeof(QueueRowNumbers));
    private static void EnabledChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        if (target is not ListBox list) return;
        if (list.GetValue(StateProperty) is Numbering previous) previous.Detach();
        list.SetValue(StateProperty, (bool)e.NewValue ? new Numbering(list) : null);
    }
    private sealed class Numbering
    {
        private readonly ListBox _list;
        private bool _scheduled;
        public Numbering(ListBox list)
        {
            _list = list;
            list.Loaded += Loaded;
            list.ItemContainerGenerator.StatusChanged += Generated;
            list.ItemContainerGenerator.ItemsChanged += ItemsChanged;
            list.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(Scrolled));
            Schedule();
        }
        public void Detach()
        {
            _list.Loaded -= Loaded;
            _list.ItemContainerGenerator.StatusChanged -= Generated;
            _list.ItemContainerGenerator.ItemsChanged -= ItemsChanged;
            _list.RemoveHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(Scrolled));
        }
        private void Loaded(object sender, RoutedEventArgs e) => Schedule();
        private void ItemsChanged(object sender, ItemsChangedEventArgs e) => Schedule();
        private void Scrolled(object sender, ScrollChangedEventArgs e) => Schedule();
        private void Generated(object? sender, EventArgs e)
        {
            if (_list.ItemContainerGenerator.Status == GeneratorStatus.ContainersGenerated) Update(_list);
            Schedule();
        }
        private void Schedule()
        {
            if (_scheduled) return;
            _scheduled = true;
            _list.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => { _scheduled = false; Update(_list); }));
        }
        private void Update(DependencyObject parent)
        {
            // Visit only realized rows, so long playlists remain virtualized.
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is ListBoxItem row)
                {
                    var position = _list.ItemContainerGenerator.IndexFromContainer(row) + 1;
                    if (GetPosition(row) != position) row.SetValue(PositionProperty, position);
                }
                else Update(child);
            }
        }
    }
}
