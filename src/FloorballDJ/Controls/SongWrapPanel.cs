using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace FloorballDJ.Controls;

// Used only by the random-pool cards: all cards have the same width and one-line
// contents. Measure the real template (including margins/font), never estimate
// its height or constrain the draft's item count. Scroll units are pixels/DIPs.
public sealed class SongWrapPanel : VirtualizingPanel, IScrollInfo
{
    private Size _itemSize = new(268, 48), _extent, _viewport;
    private double _offset;
    private bool _reset;
    private object? _restoreFocus;
    public int Columns { get; private set; } = 1;
    internal int RealizedCount => InternalChildren.Count;
    internal double CardHeight => _itemSize.Height;
    internal void CancelFocusRestore() => _restoreFocus = null;
    internal void PreserveFocus(object? item) => _restoreFocus ??= item;
    internal (int Resets, int Recycled, int NewContainers) Work => (_resets, _recycled, _newContainers);
    private int _resets, _recycled, _newContainers;

    protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
    {
        // Defer sorting/filter resets to one measure, instead of building a new
        // tree for every ObservableCollection.Move. Preserve a focused identity.
        if (IsKeyboardFocusWithin && ItemsControl.GetItemsOwner(this) is VirtualizingSongItemsControl songs)
            _restoreFocus ??= songs.FocusedItem;
        _reset = true;
        InvalidateMeasure();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var owner = ItemsControl.GetItemsOwner(this);
        if (owner is null) return default;
        var generator = ItemContainerGenerator;
        if (_reset)
        {
            _resets++;
            if (IsKeyboardFocusWithin && owner is VirtualizingSongItemsControl focusOwner) focusOwner.HoldFocus();
            generator.RemoveAll();
            RemoveInternalChildRange(0, InternalChildren.Count);
            _reset = false;
            _offset = 0;
        }
        UIElement? sample = null;
        var intrinsicConstraint = new Size(double.PositiveInfinity, double.PositiveInfinity);
        if (owner.Items.Count > 0)
        {
            sample = InternalChildren.Count > 0 ? InternalChildren[0] : Realize(owner, 0);
            sample.Measure(intrinsicConstraint);
            _itemSize = new Size(Math.Max(1, sample.DesiredSize.Width), Math.Max(1, sample.DesiredSize.Height));
        }
        var width = double.IsFinite(availableSize.Width) ? availableSize.Width : _itemSize.Width;
        var height = double.IsFinite(availableSize.Height) ? availableSize.Height : _itemSize.Height;
        Columns = Math.Max(1, (int)Math.Floor(width / _itemSize.Width));
        if (owner is VirtualizingSongItemsControl recyclingOwner)
            recyclingOwner.SetContainerBudget(Math.Min(owner.Items.Count,
                ((int)Math.Ceiling(height / _itemSize.Height) + 3) * Columns + 1));
        var extent = new Size(width, Math.Ceiling((double)owner.Items.Count / Columns) * _itemSize.Height);
        var viewport = new Size(width, height);
        var offset = Math.Clamp(_offset, 0, Math.Max(0, extent.Height - height));
        if (_extent != extent || _viewport != viewport || _offset != offset)
        {
            _extent = extent; _viewport = viewport; _offset = offset;
            ScrollOwner?.InvalidateScrollInfo();
        }
        if (_restoreFocus is not null && owner.Items.IndexOf(_restoreFocus) is var focusIndex && focusIndex >= 0)
            ScrollToIndex(focusIndex);
        var first = Math.Max(0, ((int)Math.Floor(_offset / _itemSize.Height) - 1) * Columns);
        var last = Math.Min(owner.Items.Count - 1,
            ((int)Math.Ceiling((_offset + height) / _itemSize.Height) + 1) * Columns - 1);
        for (var childIndex = InternalChildren.Count - 1; childIndex >= 0; childIndex--)
        {
            var child = InternalChildren[childIndex];
            var index = owner.ItemContainerGenerator.IndexFromContainer(child);
            // A single focused card may stay alive while the user scrolls away.
            // Never recycle it into another song underneath keyboard focus.
            if (index >= first && index <= last || child.IsKeyboardFocusWithin) continue;
            if (index >= 0)
            {
                // The owner supplies one bounded pool across generator resets as
                // well as scrolling. Do not also enqueue into WPF's recycle queue.
                generator.Remove(generator.GeneratorPositionFromIndex(index), 1);
                _recycled++;
            }
            RemoveInternalChildRange(childIndex, 1);
        }
        for (var index = first; index <= last; index++)
        {
            var child = Realize(owner, index);
            // Keep the intrinsic sample's constraint stable so WPF can reuse its
            // valid measurement. Font/content/template/DPI invalidation remains
            // WPF-owned. Measure again with that constraint if it was recycled
            // and rebound during this pass; do not skip a dirty sample.
            child.Measure(ReferenceEquals(child, sample) ? intrinsicConstraint : _itemSize);
        }
        if (_restoreFocus is not null)
        {
            var item = _restoreFocus; _restoreFocus = null;
            if (owner is VirtualizingSongItemsControl songs) songs.RestoreFocusLater(item);
        }
        return viewport;
    }

    private UIElement Realize(ItemsControl owner, int index)
    {
        var generator = ItemContainerGenerator;
        using (generator.StartAt(generator.GeneratorPositionFromIndex(index), GeneratorDirection.Forward, true))
        {
            var recyclingOwner = owner as VirtualizingSongItemsControl;
            var created = recyclingOwner?.CreatedContainers;
            var child = (UIElement)generator.GenerateNext(out var isNew);
            _newContainers += created is { } count ? recyclingOwner!.CreatedContainers - count : isNew ? 1 : 0;
            if (!InternalChildren.Contains(child))
            {
                InsertInternalChild(generator.GeneratorPositionFromIndex(index).Index, child);
                // A new generator mapping may use an existing owner-pooled
                // presenter. Every inserted container still needs preparation
                // so its Content/template describe the current song.
                generator.PrepareItemContainer(child);
            }
            return child;
        }
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var owner = ItemsControl.GetItemsOwner(this);
        foreach (UIElement child in InternalChildren)
        {
            var index = owner.ItemContainerGenerator.IndexFromContainer(child);
            if (index >= 0) child.Arrange(new Rect(index % Columns * _itemSize.Width,
                index / Columns * _itemSize.Height - _offset, _itemSize.Width, _itemSize.Height));
        }
        return finalSize;
    }

    protected override void BringIndexIntoView(int index) => ScrollToIndex(index);
    internal void ScrollToIndex(int index)
    {
        var owner = ItemsControl.GetItemsOwner(this);
        if (owner is null || index < 0 || index >= owner.Items.Count) return;
        var top = index / Columns * _itemSize.Height;
        if (top < _offset) SetVerticalOffset(top);
        else if (top + _itemSize.Height > _offset + _viewport.Height)
            SetVerticalOffset(top + _itemSize.Height - _viewport.Height);
    }

    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
        var owner = ItemsControl.GetItemsOwner(this);
        if (owner is null || visual == this) return rectangle;
        var container = ItemsControl.ContainerFromElement(owner, visual);
        var index = container is null ? -1 : owner.ItemContainerGenerator.IndexFromContainer(container);
        if (index < 0) return Rect.Empty;
        ScrollToIndex(index);
        var bounds = new Rect(index % Columns * _itemSize.Width, index / Columns * _itemSize.Height - _offset,
            _itemSize.Width, _itemSize.Height);
        bounds.Intersect(new Rect(_viewport));
        return bounds;
    }
    public void SetVerticalOffset(double offset)
    {
        if (double.IsNaN(offset)) return;
        var next = Math.Clamp(offset, 0, Math.Max(0, _extent.Height - _viewport.Height));
        if (_offset == next) return;
        _offset = next;
        InvalidateMeasure();
        ScrollOwner?.InvalidateScrollInfo();
    }
    public void LineUp() => SetVerticalOffset(_offset - 16);
    public void LineDown() => SetVerticalOffset(_offset + 16);
    public void PageUp() => SetVerticalOffset(_offset - _viewport.Height);
    public void PageDown() => SetVerticalOffset(_offset + _viewport.Height);
    public void MouseWheelUp() => SetVerticalOffset(_offset - WheelDelta);
    public void MouseWheelDown() => SetVerticalOffset(_offset + WheelDelta);
    private double WheelDelta => SystemParameters.WheelScrollLines < 0 ? _viewport.Height : 16 * SystemParameters.WheelScrollLines;
    public void LineLeft() { }
    public void LineRight() { }
    public void MouseWheelLeft() { }
    public void MouseWheelRight() { }
    public void PageLeft() { }
    public void PageRight() { }
    public void SetHorizontalOffset(double offset) { }
    public bool CanHorizontallyScroll { get; set; }
    public bool CanVerticallyScroll { get; set; }
    public double ExtentWidth => _extent.Width;
    public double ExtentHeight => _extent.Height;
    public double ViewportWidth => _viewport.Width;
    public double ViewportHeight => _viewport.Height;
    public double HorizontalOffset => 0;
    public double VerticalOffset => _offset;
    public ScrollViewer? ScrollOwner { get; set; }
}

// ItemsControl still owns the complete filtered view. Explicit navigation can
// realize a card that has no UI yet; normal CheckBox Space/click/bindings remain
// in charge of toggling. Leaving the list never loops through recycled children.
public sealed class VirtualizingSongItemsControl : ItemsControl
{
    // One owner-local pool shares the viewport budget with active containers.
    // WPF still clears, links and prepares every container; no item data is cached.
    private readonly Queue<ContentPresenter> _containers = new();
    private int _containerBudget, _issuedContainers;
    internal int CreatedContainers { get; private set; }
    internal int ReusedContainers { get; private set; }
    internal (int Active, int Cached, int Budget) Containers => (_issuedContainers, _containers.Count, _containerBudget);
    private bool _movingFocus;
    private DispatcherOperation? _restoreOperation;
    internal object? FocusedItem { get; private set; }
    public VirtualizingSongItemsControl()
    {
        Focusable = true;
        FocusVisualStyle = null;
        VirtualizingPanel.SetVirtualizationMode(this, VirtualizationMode.Recycling);
        // The owner is the external Tab stop; entering it realizes the first
        // (or, for Shift+Tab, last) song, irrespective of the scroll position.
        KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.None);
        KeyboardNavigation.SetDirectionalNavigation(this, KeyboardNavigationMode.Contained);
        Unloaded += (_, _) => { _restoreOperation?.Abort(); _restoreOperation = null; _containers.Clear(); };
    }

    internal void SetContainerBudget(int budget)
    {
        _containerBudget = budget;
        while (_containers.Count > Math.Max(0, budget - _issuedContainers)) _containers.Dequeue();
    }

    protected override DependencyObject GetContainerForItemOverride()
    {
        _issuedContainers++;
        while (_containers.TryDequeue(out var container))
            if (MatchesTemplate(container)) { ReusedContainers++; return container; }
        CreatedContainers++;
        return base.GetContainerForItemOverride();
    }

    private bool MatchesTemplate(ContentPresenter container) =>
        ReferenceEquals(container.ContentTemplate, ItemTemplate) &&
        ReferenceEquals(container.ContentTemplateSelector, ItemTemplateSelector) && container.ContentStringFormat == ItemStringFormat;

    protected override void ClearContainerForItemOverride(DependencyObject element, object item)
    {
        // WPF can remember a descendant for later tab/focus navigation. Never
        // rebind the last focused card's tree to a different song underneath it.
        var hadFocus = ReferenceEquals(item, FocusedItem) || element is UIElement { IsKeyboardFocusWithin: true } ||
            element is UIElement { IsMouseCaptureWithin: true };
        if (element is UIElement { IsKeyboardFocusWithin: true })
        {
            Panel?.PreserveFocus(FocusedItem);
            HoldFocus();
        }
        if (element is UIElement { IsMouseCaptureWithin: true }) Mouse.Capture(null);
        base.ClearContainerForItemOverride(element, item);
        if (element is ContentPresenter container && !ReferenceEquals(element, item))
        {
            _issuedContainers--;
            if (!hadFocus && MatchesTemplate(container) && _issuedContainers + _containers.Count < _containerBudget) _containers.Enqueue(container);
        }
    }

    internal void HoldFocus()
    {
        _movingFocus = true;
        try { Keyboard.Focus(this); }
        finally { _movingFocus = false; }
    }

    internal void RestoreFocusLater(object item)
    {
        _restoreOperation?.Abort();
        // Restoring inside Measure would recursively request layout. Run after
        // layout, and only if the user has not moved focus to another control.
        _restoreOperation = Dispatcher.BeginInvoke(() =>
        {
            _restoreOperation = null;
            if (IsLoaded && ReferenceEquals(Keyboard.FocusedElement, this)) FocusItem(Items.IndexOf(item));
        }, DispatcherPriority.Loaded);
    }

    internal SongWrapPanel? Panel => FindChild<SongWrapPanel>(this);
    protected override void OnItemsSourceChanged(IEnumerable oldValue, IEnumerable newValue)
    {
        // A retained deck view can now change groups without Unloaded. A queued
        // focus restore belongs to its former source, never to the new group.
        _restoreOperation?.Abort(); _restoreOperation = null;
        FocusedItem = null;
        Panel?.CancelFocusRestore();
        base.OnItemsSourceChanged(oldValue, newValue);
    }
    internal bool FocusItem(int index)
    {
        if (index < 0 || index >= Items.Count || Panel is not { } panel) return false;
        _movingFocus = true;
        try
        {
            // Temporarily keep focus on the owner, so an old focused card's
            // MakeVisible request cannot undo a distant keyboard scroll.
            if (IsKeyboardFocusWithin) Keyboard.Focus(this);
            panel.ScrollToIndex(index);
            UpdateLayout();
            var container = ItemContainerGenerator.ContainerFromIndex(index);
            var check = container is null ? null : FindChild<CheckBox>(container);
            return check is { IsEnabled: true } && check.Focus();
        }
        finally { _movingFocus = false; }
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        if (e.NewFocus is CheckBox check) FocusedItem = check.DataContext;
        if (!_movingFocus && ReferenceEquals(e.NewFocus, this) && !FocusItem((Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? Items.Count - 1 : 0))
            LeaveList((Keyboard.Modifiers & ModifierKeys.Shift) != 0);
    }

    private void LeaveList(bool backwards)
    {
        _movingFocus = true;
        try
        {
            KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.None);
            Keyboard.Focus(this);
            MoveFocus(new TraversalRequest(backwards ? FocusNavigationDirection.Previous : FocusNavigationDirection.Next));
        }
        finally
        {
            KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.None);
            _movingFocus = false;
        }
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Handled || e.OriginalSource is not DependencyObject element ||
            ItemsControl.ContainerFromElement(this, element) is not { } container || Panel is not { } panel) return;
        var index = ItemContainerGenerator.IndexFromContainer(container);
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) != 0) return;
        var next = e.Key switch
        {
            Key.Tab => index + (shift ? -1 : 1),
            Key.Left => index - 1, Key.Right => index + 1,
            Key.Up => index - panel.Columns, Key.Down => index + panel.Columns,
            Key.Home => 0, Key.End => Items.Count - 1,
            Key.PageUp => Math.Max(0, index - Math.Max(1, (int)(panel.ViewportHeight / panel.CardHeight)) * panel.Columns),
            Key.PageDown => Math.Min(Items.Count - 1, index + Math.Max(1, (int)(panel.ViewportHeight / panel.CardHeight)) * panel.Columns),
            _ => index
        };
        if (next == index) return;
        if (next < 0 || next >= Items.Count)
        {
            if (e.Key == Key.Tab)
            {
                e.Handled = true;
                LeaveList(shift);
            }
            return;
        }
        e.Handled = FocusItem(next);
    }

    private static T? FindChild<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (FindChild<T>(child) is { } nested) return nested;
        }
        return null;
    }
}
