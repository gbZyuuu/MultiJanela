using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Size = System.Windows.Size;
using Point = System.Windows.Point;

namespace RobloxMultiManager;

/// <summary>Pixel-scrolling session rows. Materializes only the viewport and a small overscan.</summary>
public sealed class SessionTilePanel : VirtualizingPanel, IScrollInfo
{
    public const double TileHeight = 86;
    public static readonly DependencyProperty MinimumItemWidthProperty = DependencyProperty.Register(nameof(MinimumItemWidth), typeof(double), typeof(SessionTilePanel), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty ItemHeightProperty = DependencyProperty.Register(nameof(ItemHeight), typeof(double), typeof(SessionTilePanel), new FrameworkPropertyMetadata(86d, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public double MinimumItemWidth { get => (double)GetValue(MinimumItemWidthProperty); set => SetValue(MinimumItemWidthProperty, value); }
    public double ItemHeight { get => (double)GetValue(ItemHeightProperty); set => SetValue(ItemHeightProperty, value); }
    private double RowHeight => Math.Max(60, ItemHeight);

    private int _columns = 1;
    private double _tileWidth;
    public int Columns => _columns;
    protected override Size MeasureOverride(Size availableSize)
    {
        var owner = ItemsControl.GetItemsOwner(this);
        if (owner is null) return new Size();
        var width = double.IsInfinity(availableSize.Width) ? Math.Max(300, ActualWidth) : availableSize.Width;
        var height = double.IsInfinity(availableSize.Height) ? Math.Max(RowHeight, ActualHeight) : availableSize.Height;
        _columns = MinimumItemWidth > 0 ? Math.Max(1, (int)(width / MinimumItemWidth)) : 1;
        _tileWidth = width / _columns;
        ViewportWidth = width; ViewportHeight = height;
        ExtentWidth = width; ExtentHeight = Math.Ceiling(owner.Items.Count / (double)_columns) * RowHeight;
        VerticalOffset = Math.Clamp(VerticalOffset, 0, Math.Max(0, ExtentHeight - height));
        ScrollOwner?.InvalidateScrollInfo();
        var first = Math.Max(0, (int)(VerticalOffset / RowHeight) * _columns);
        var last = Math.Min(owner.Items.Count - 1, ((int)((VerticalOffset + height) / RowHeight) + 2) * _columns - 1);
        var generator = ItemContainerGenerator;
        // A detached ItemsPresenter can retain its owner for a layout pass after
        // its generator has disconnected (page switches or ItemsSource reset).
        if (generator is null) return new Size(width, height);
        for (var i = InternalChildren.Count - 1; i >= 0; i--)
        {
            var index = owner.ItemContainerGenerator.IndexFromContainer(InternalChildren[i]);
            if (index >= first && index <= last) continue;
            var position = new GeneratorPosition(i, 0);
            if (generator is IRecyclingItemContainerGenerator recycling) recycling.Recycle(position, 1);
            else generator.Remove(position, 1);
            RemoveInternalChildRange(i, 1);
        }
        if (last >= first)
        {
            var position = generator.GeneratorPositionFromIndex(first);
            var childIndex = position.Offset == 0 ? position.Index : position.Index + 1;
            using (generator.StartAt(position, GeneratorDirection.Forward, true))
                for (var index = first; index <= last; index++, childIndex++)
                {
                    var child = (UIElement)generator.GenerateNext(out var created);
                    // ScrollIntoView can pre-generate a container before this
                    // panel measures. It is then "existing" to the generator,
                    // but still has to be attached to our visual children.
                    if (childIndex >= InternalChildren.Count || !ReferenceEquals(InternalChildren[childIndex], child))
                    {
                        var previous = InternalChildren.IndexOf(child);
                        if (previous >= 0) RemoveInternalChildRange(previous, 1);
                        if (childIndex >= InternalChildren.Count) AddInternalChild(child);
                        else InsertInternalChild(childIndex, child);
                        generator.PrepareItemContainer(child);
                    }
                    else if (created) generator.PrepareItemContainer(child);
                    child.Measure(new Size(Math.Max(0, _tileWidth), RowHeight));
                }
        }
        return new Size(width, height);
    }
    protected override Size ArrangeOverride(Size finalSize)
    {
        var owner = ItemsControl.GetItemsOwner(this);
        if (owner is null) return finalSize;
        foreach (UIElement child in InternalChildren)
        {
            var index = owner.ItemContainerGenerator.IndexFromContainer(child);
            if (index >= 0) child.Arrange(new Rect(index % _columns * _tileWidth,
                index / _columns * RowHeight - VerticalOffset, Math.Max(0, _tileWidth), RowHeight));
        }
        return finalSize;
    }
    protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
    {
        base.OnItemsChanged(sender, args);
        if (args.Action is System.Collections.Specialized.NotifyCollectionChangedAction.Remove
            or System.Collections.Specialized.NotifyCollectionChangedAction.Replace
            or System.Collections.Specialized.NotifyCollectionChangedAction.Move)
        {
            var position = args.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Move ? args.OldPosition : args.Position;
            if (args.ItemUICount > 0 && position.Index >= 0 && position.Index < InternalChildren.Count)
                RemoveInternalChildRange(position.Index, Math.Min(args.ItemUICount, InternalChildren.Count - position.Index));
        }
        InvalidateMeasure();
    }
    protected override void BringIndexIntoView(int index)
    {
        var top = index / _columns * RowHeight;
        if (top < VerticalOffset) SetVerticalOffset(top);
        else if (top + RowHeight > VerticalOffset + ViewportHeight)
            SetVerticalOffset(ViewportHeight < RowHeight ? top : top + RowHeight - ViewportHeight);
    }
    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
        DependencyObject? current = visual;
        while (current is not null && current is not ListBoxItem) current = VisualTreeHelper.GetParent(current);
        if (current is not null)
        {
            var index = ItemsControl.GetItemsOwner(this)?.ItemContainerGenerator.IndexFromContainer(current) ?? -1;
            if (index >= 0) BringIndexIntoView(index);
        }
        return rectangle;
    }
    public void SetVerticalOffset(double offset) { VerticalOffset = Math.Clamp(offset, 0, Math.Max(0, ExtentHeight - ViewportHeight)); ScrollOwner?.InvalidateScrollInfo(); InvalidateMeasure(); }
    public void LineUp() => SetVerticalOffset(VerticalOffset - 40);
    public void LineDown() => SetVerticalOffset(VerticalOffset + 40);
    public void MouseWheelUp() => SetVerticalOffset(VerticalOffset - 100);
    public void MouseWheelDown() => SetVerticalOffset(VerticalOffset + 100);
    public void PageUp() => SetVerticalOffset(VerticalOffset - ViewportHeight);
    public void PageDown() => SetVerticalOffset(VerticalOffset + ViewportHeight);
    public bool CanHorizontallyScroll { get; set; }
    public bool CanVerticallyScroll { get; set; }
    public double ExtentWidth { get; private set; }
    public double ExtentHeight { get; private set; }
    public double ViewportWidth { get; private set; }
    public double ViewportHeight { get; private set; }
    public double HorizontalOffset => 0;
    public double VerticalOffset { get; private set; }
    public ScrollViewer? ScrollOwner { get; set; }
    public void SetHorizontalOffset(double offset) { }
    public void LineLeft() { } public void LineRight() { }
    public void PageLeft() { } public void PageRight() { }
    public void MouseWheelLeft() { } public void MouseWheelRight() { }
}
