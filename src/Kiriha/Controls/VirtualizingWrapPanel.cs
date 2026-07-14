using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;

namespace Kiriha.Controls;

/// <summary>
/// 均一サイズのセルを折り返し配置し、表示行と前後のキャッシュ行だけを実体化するパネル。
/// ListBox の選択・キーボード操作・ドラッグ＆ドロップを保つため、ItemsPanel として使用する。
/// </summary>
public sealed class VirtualizingWrapPanel : VirtualizingPanel
{
    public static readonly StyledProperty<double> ItemWidthProperty =
        AvaloniaProperty.Register<VirtualizingWrapPanel, double>(
            nameof(ItemWidth),
            108,
            validate: value => double.IsFinite(value) && value > 0);

    public static readonly StyledProperty<double> ItemHeightProperty =
        AvaloniaProperty.Register<VirtualizingWrapPanel, double>(
            nameof(ItemHeight),
            70,
            validate: value => double.IsFinite(value) && value > 0);

    public static readonly StyledProperty<int> CacheRowsProperty =
        AvaloniaProperty.Register<VirtualizingWrapPanel, int>(
            nameof(CacheRows),
            2,
            validate: value => value >= 0);

    private readonly SortedDictionary<int, Control> _realized = [];
    private readonly Dictionary<Control, int> _indices = [];
    private readonly Dictionary<Control, object?> _recycleKeys = [];
    private readonly Dictionary<object, Stack<Control>> _recyclePool = [];
    private Rect _viewport;
    private int _columns = 1;

    static VirtualizingWrapPanel()
        => AffectsMeasure<VirtualizingWrapPanel>(ItemWidthProperty, ItemHeightProperty, CacheRowsProperty);

    public VirtualizingWrapPanel()
        => EffectiveViewportChanged += (_, e) =>
        {
            if (_viewport != e.EffectiveViewport)
            {
                _viewport = e.EffectiveViewport;
                InvalidateMeasure();
            }
        };

    public double ItemWidth
    {
        get => GetValue(ItemWidthProperty);
        set => SetValue(ItemWidthProperty, value);
    }

    public double ItemHeight
    {
        get => GetValue(ItemHeightProperty);
        set => SetValue(ItemHeightProperty, value);
    }

    public int CacheRows
    {
        get => GetValue(CacheRowsProperty);
        set => SetValue(CacheRowsProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var itemCount = Items.Count;
        if (itemCount == 0)
        {
            RecycleAll();
            return default;
        }

        var availableWidth = ResolveAvailableWidth(availableSize.Width);
        _columns = Math.Max(1, (int)Math.Floor(availableWidth / ItemWidth));
        var rowCount = (itemCount + _columns - 1) / _columns;

        var viewportTop = Math.Max(0, _viewport.Top);
        var viewportHeight = _viewport.Height;
        if (!double.IsFinite(viewportHeight) || viewportHeight <= 0)
        {
            viewportHeight = double.IsFinite(availableSize.Height) && availableSize.Height > 0
                ? availableSize.Height
                : ItemHeight * 6;
        }

        var firstRow = Math.Max(0, (int)Math.Floor(viewportTop / ItemHeight) - CacheRows);
        var lastRow = Math.Min(
            rowCount - 1,
            (int)Math.Floor((viewportTop + viewportHeight - double.Epsilon) / ItemHeight) + CacheRows);
        var firstIndex = firstRow * _columns;
        var lastIndex = Math.Min(itemCount - 1, ((lastRow + 1) * _columns) - 1);

        RecycleOutside(firstIndex, lastIndex);
        for (var index = firstIndex; index <= lastIndex; index++)
        {
            Realize(index);
        }

        var constraint = new Size(ItemWidth, ItemHeight);
        foreach (var control in _realized.Values)
        {
            control.Measure(constraint);
        }

        return new Size(availableWidth, rowCount * ItemHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (var (index, control) in _realized)
        {
            control.Arrange(GetItemRect(index));
        }

        return finalSize;
    }

    protected override void OnItemsChanged(
        IReadOnlyList<object?> items,
        NotifyCollectionChangedEventArgs e)
    {
        RecycleAll();
        InvalidateMeasure();
    }

    protected override Control? ScrollIntoView(int index)
    {
        if (index < 0 || index >= Items.Count)
        {
            return null;
        }

        var control = Realize(index);
        control.Measure(new Size(ItemWidth, ItemHeight));
        control.Arrange(GetItemRect(index));
        control.BringIntoView();
        return control;
    }

    protected override Control? ContainerFromIndex(int index)
        => _realized.GetValueOrDefault(index);

    protected override int IndexFromContainer(Control container)
        => _indices.GetValueOrDefault(container, -1);

    protected override IEnumerable<Control> GetRealizedContainers()
        => _realized.Values;

    protected override IInputElement? GetControl(
        NavigationDirection direction,
        IInputElement? from,
        bool wrap)
    {
        var count = Items.Count;
        if (count == 0)
        {
            return null;
        }

        var current = from is Control control ? IndexFromContainer(control) : -1;
        var visibleRows = Math.Max(1, (int)Math.Floor(_viewport.Height / ItemHeight));
        var target = direction switch
        {
            NavigationDirection.First => 0,
            NavigationDirection.Last => count - 1,
            NavigationDirection.Next or NavigationDirection.Right => current + 1,
            NavigationDirection.Previous or NavigationDirection.Left => current - 1,
            NavigationDirection.Up => current - _columns,
            NavigationDirection.Down => current + _columns,
            NavigationDirection.PageUp => current - (_columns * visibleRows),
            NavigationDirection.PageDown => current + (_columns * visibleRows),
            _ => current,
        };

        if (current < 0 && direction is not NavigationDirection.First and not NavigationDirection.Last)
        {
            return null;
        }

        if (wrap)
        {
            target = ((target % count) + count) % count;
        }
        else
        {
            target = Math.Clamp(target, 0, count - 1);
        }

        return target == current ? from : ScrollIntoView(target);
    }

    private double ResolveAvailableWidth(double measuredWidth)
    {
        if (double.IsFinite(measuredWidth) && measuredWidth > 0)
        {
            return measuredWidth;
        }

        if (double.IsFinite(_viewport.Width) && _viewport.Width > 0)
        {
            return _viewport.Width;
        }

        return Bounds.Width > 0 ? Bounds.Width : ItemWidth;
    }

    private Rect GetItemRect(int index)
    {
        var column = index % _columns;
        var row = index / _columns;
        return new Rect(column * ItemWidth, row * ItemHeight, ItemWidth, ItemHeight);
    }

    private Control Realize(int index)
    {
        if (_realized.TryGetValue(index, out var realized))
        {
            return realized;
        }

        var item = Items[index];
        var generator = ItemContainerGenerator!;
        Control control;
        object? recycleKey = null;
        if (generator.NeedsContainer(item, index, out recycleKey))
        {
            if (recycleKey is not null
                && _recyclePool.TryGetValue(recycleKey, out var pool)
                && pool.TryPop(out var recycled))
            {
                control = recycled;
                generator.PrepareItemContainer(control, item, index);
            }
            else
            {
                control = generator.CreateContainer(item, index, recycleKey);
                generator.PrepareItemContainer(control, item, index);
            }
        }
        else
        {
            control = (Control)item!;
            generator.PrepareItemContainer(control, item, index);
        }

        AddInternalChild(control);
        generator.ItemContainerPrepared(control, item, index);
        _realized.Add(index, control);
        _indices.Add(control, index);
        _recycleKeys[control] = recycleKey;
        return control;
    }

    private void RecycleOutside(int firstIndex, int lastIndex)
    {
        foreach (var pair in _realized
                     .Where(pair => (pair.Key < firstIndex || pair.Key > lastIndex)
                                    && !pair.Value.IsKeyboardFocusWithin)
                     .ToArray())
        {
            Recycle(pair.Key, pair.Value);
        }
    }

    private void RecycleAll()
    {
        foreach (var pair in _realized.ToArray())
        {
            Recycle(pair.Key, pair.Value);
        }
    }

    private void Recycle(int index, Control control)
    {
        var recycleKey = _recycleKeys.GetValueOrDefault(control);
        ItemContainerGenerator!.ClearItemContainer(control);
        RemoveInternalChild(control);
        _realized.Remove(index);
        _indices.Remove(control);
        _recycleKeys.Remove(control);

        if (recycleKey is not null)
        {
            if (!_recyclePool.TryGetValue(recycleKey, out var pool))
            {
                pool = [];
                _recyclePool.Add(recycleKey, pool);
            }

            pool.Push(control);
        }
    }
}
