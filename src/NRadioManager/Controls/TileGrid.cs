using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using NRadioManager.ViewModels;

namespace NRadioManager.Controls;

/// <summary>
/// 磁贴网格面板。
///
/// 布局使用占用位图装箱，保证磁贴永不重叠。编辑时只允许从标题拖动把手移动；
/// 拖动过程中不修改数据源，仅让被拖磁贴跟随指针并高亮目标位置，松开后才一次性重排并落盘。
/// 这样可以避免拖动时其他磁贴反复重排，也避免容器重建导致鼠标捕获丢失和保存不执行。
/// </summary>
public sealed class TileGrid : Panel
{
    public const int Columns = 4;
    private const double MinRowHeight = 92;

    private const double DragThreshold = 5;
    private int _dragFrom = -1;
    private int _dragTarget = -1;
    private bool _dragArmed;
    private bool _dragging;
    private Point _dragStartPoint;
    private UIElement? _dragChild;
    private Transform? _dragOriginalTransform;
    private double _dragOriginalOpacity = 1;
    private int _dragOriginalZIndex;
    private double[] _rowHeights = Array.Empty<double>();
    private readonly Dictionary<UIElement, Rect> _arrangedBounds = new();
    private DashboardViewModel? _hookedOwner;
    private bool _editMode;

    /// <summary>编辑状态（由视图模型同步而来）。</summary>
    public bool IsEditing => _editMode;

    /// <summary>诊断出口：写到 %APPDATA%\NRadioManager\ui-trace.txt，用来确认事件是否真的触发。</summary>
    private static void T(string m)
        => (Application.Current?.MainWindow?.DataContext as MainViewModel)?.Trace(m);

    private double Spacing => TryFindResource("R.TileSpacing") is double d ? d : 12;

    private DashboardViewModel? Owner
        => Children.OfType<FrameworkElement>()
            .Select(c => (c.DataContext as TileViewModel)?.Owner)
            .FirstOrDefault(o => o is not null);

    // ==================== 编辑状态同步（只在非布局时机订阅） ====================

    protected override void OnVisualChildrenChanged(DependencyObject? added, DependencyObject? removed)
    {
        base.OnVisualChildrenChanged(added, removed);
        // 容器刚创建时子项还没拿到 DataContext，必须在 DataContext 就绪后再挂接订阅
        if (removed is FrameworkElement oldFe && oldFe.DataContext is TileViewModel oldTile)
            oldTile.PropertyChanged -= OnTilePropertyChanged;
        if (added is FrameworkElement fe)
        {
            fe.DataContextChanged -= OnChildDataContextChanged;
            fe.DataContextChanged += OnChildDataContextChanged;
            AttachChild(fe);
        }
        HookOwner();
        InvalidateMeasure();
    }

    private void OnChildDataContextChanged(object? sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is TileViewModel old) old.PropertyChanged -= OnTilePropertyChanged;
        if (sender is FrameworkElement fe) AttachChild(fe);
        HookOwner();
        InvalidateMeasure();
    }

    private void AttachChild(FrameworkElement fe)
    {
        if (fe.DataContext is not TileViewModel tile) return;
        tile.PropertyChanged -= OnTilePropertyChanged;   // 避免重复订阅
        tile.PropertyChanged += OnTilePropertyChanged;
    }

    private void OnTilePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TileViewModel.ColumnSpan) or nameof(TileViewModel.RowSpan)
            or nameof(TileViewModel.ShowTitle))
            InvalidateMeasure();
    }

    private void HookOwner()
    {
        var owner = Owner;
        if (owner is null || ReferenceEquals(owner, _hookedOwner)) return;
        if (_hookedOwner is not null) _hookedOwner.PropertyChanged -= OnOwnerPropertyChanged;
        _hookedOwner = owner;
        owner.PropertyChanged += OnOwnerPropertyChanged;
        SyncEditMode(owner.IsEditing);
    }

    private void OnOwnerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DashboardViewModel.IsEditing)) SyncEditMode(_hookedOwner?.IsEditing ?? false);
    }

    private void SyncEditMode(bool editing)
    {
        if (_editMode == editing) return;
        _editMode = editing;
        T($"TileGrid: 编辑态切换为 {editing}");
        if (!editing)
        {
            CommitDrag(persist: true);
            _dragArmed = false;
        }
        Cursor = editing ? Cursors.Arrow : null;
    }

    private void ReleaseMouseCaptureOnce()
    {
        if (IsMouseCaptured) ReleaseMouseCapture();
    }

    // ==================== 布局 ====================

    protected override Size MeasureOverride(Size availableSize)
    {
        // 注意：布局过程中不订阅、不改状态、不启动动画，避免重新进入布局造成回环
        var spacing = Spacing;
        var width = double.IsInfinity(availableSize.Width) || availableSize.Width <= 0 ? 1000 : availableSize.Width;
        var cellWidth = Math.Max(80, (width - spacing * (Columns - 1)) / Columns);

        var placements = ComputePlacements();
        var needed = new List<double>();

        foreach (var (child, _, row, colSpan, rowSpan) in placements)
        {
            var w = cellWidth * colSpan + spacing * (colSpan - 1);
            child.Measure(new Size(w, double.PositiveInfinity));
            var desired = (child as FrameworkElement)?.DesiredSize.Height ?? MinRowHeight;
            while (needed.Count < row + rowSpan) needed.Add(MinRowHeight);
            var perRow = Math.Max(MinRowHeight, (desired + spacing * (rowSpan - 1)) / rowSpan);
            for (var r = row; r < row + rowSpan; r++) needed[r] = Math.Max(needed[r], perRow);
        }

        if (!double.IsInfinity(availableSize.Height) && availableSize.Height > 0 && needed.Count > 0)
        {
            var fixedRow = Math.Max(MinRowHeight,
                (availableSize.Height - spacing * (needed.Count - 1)) / needed.Count);
            _rowHeights = Enumerable.Repeat(fixedRow, needed.Count).ToArray();
        }
        else
        {
            _rowHeights = needed.ToArray();
        }

        foreach (var (child, _, row, colSpan, rowSpan) in placements)
            child.Measure(new Size(cellWidth * colSpan + spacing * (colSpan - 1), RowBlockHeight(row, rowSpan)));

        var total = 0.0;
        for (var i = 0; i < _rowHeights.Length; i++) total += _rowHeights[i] + (i > 0 ? spacing : 0);
        return new Size(width, double.IsInfinity(availableSize.Height)
            ? Math.Max(MinRowHeight, total)
            : Math.Max(MinRowHeight, availableSize.Height));
    }

    private double RowBlockHeight(int row, int rowSpan)
    {
        var h = 0.0;
        for (var r = row; r < row + rowSpan && r < _rowHeights.Length; r++) h += _rowHeights[r] + Spacing;
        return Math.Max(MinRowHeight, h - Spacing);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var spacing = Spacing;
        var cellWidth = Math.Max(80, (finalSize.Width - spacing * (Columns - 1)) / Columns);
        _arrangedBounds.Clear();

        var rowTops = new double[_rowHeights.Length + 1];
        var y = 0.0;
        for (var i = 0; i < _rowHeights.Length; i++) { rowTops[i] = y; y += _rowHeights[i] + spacing; }
        rowTops[^1] = y;

        foreach (var (child, col, row, colSpan, rowSpan) in ComputePlacements())
        {
            var w = cellWidth * colSpan + spacing * (colSpan - 1);
            var top = row < rowTops.Length ? rowTops[row] : y;
            var bounds = new Rect(col * (cellWidth + spacing), top, w, RowBlockHeight(row, rowSpan));
            child.Arrange(bounds);
            _arrangedBounds[child] = bounds;
        }
        return finalSize;
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (!_dragging || _dragTarget < 0 || _dragTarget == _dragFrom ||
            _dragTarget >= Children.Count || Children[_dragTarget] is not UIElement target ||
            !_arrangedBounds.TryGetValue(target, out var bounds))
            return;

        var accent = TryFindResource("B.Accent") as Brush ?? Brushes.DodgerBlue;
        Brush fill = accent is SolidColorBrush solid
            ? new SolidColorBrush(solid.Color) { Opacity = 0.14 }
            : accent;
        var pen = new Pen(accent, 2) { DashStyle = new DashStyle(new double[] { 5, 3 }, 0) };
        pen.Freeze();
        dc.DrawRoundedRectangle(fill, pen, bounds, 12, 12);
    }

    /// <summary>占用位图 + 首次适配装箱：任何顺序/尺寸组合都不会重叠。</summary>
    private List<(UIElement child, int col, int row, int colSpan, int rowSpan)> ComputePlacements()
    {
        var result = new List<(UIElement, int, int, int, int)>();
        var occupied = new List<bool[]>();

        bool Free(int col, int row, int colSpan, int rowSpan)
        {
            for (var r = row; r < row + rowSpan; r++)
            {
                if (r >= occupied.Count) continue;
                for (var c = col; c < col + colSpan; c++) if (occupied[r][c]) return false;
            }
            return true;
        }

        void Occupy(int col, int row, int colSpan, int rowSpan)
        {
            while (occupied.Count < row + rowSpan) occupied.Add(new bool[Columns]);
            for (var r = row; r < row + rowSpan; r++)
                for (var c = col; c < col + colSpan; c++) occupied[r][c] = true;
        }

        foreach (var child in Children.OfType<UIElement>())
        {
            var tile = (child as FrameworkElement)?.DataContext as TileViewModel;
            var colSpan = Math.Clamp(tile?.ColumnSpan ?? 1, 1, Columns);
            var rowSpan = Math.Clamp(tile?.RowSpan ?? 1, 1, 4);

            var placed = false;
            for (var row = 0; !placed; row++)
                for (var col = 0; col + colSpan <= Columns; col++)
                {
                    if (!Free(col, row, colSpan, rowSpan)) continue;
                    Occupy(col, row, colSpan, rowSpan);
                    result.Add((child, col, row, colSpan, rowSpan));
                    placed = true;
                    break;
                }
        }
        return result;
    }

    // ==================== 交互：拖动预览 + 落点高亮 + 松手单次提交 ====================

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonDown(e);
        T($"TileGrid: 按下 编辑态={_editMode}");
        if (!_editMode) return;
        if (e.OriginalSource is not DependencyObject src) return;
        if (IsInteractive(src)) return;                        // 磁贴内的按钮/输入框不参与拖动
        if (FindChildIndex(src) is not { } index) return;
        var localPoint = e.GetPosition(Children[index]);
        if (!IsDragHandle(src) && localPoint.Y > 42) return;   // 只从标题区域拖动，避免误触内容区

        _dragFrom = index;
        _dragTarget = index;
        _dragArmed = true;
        _dragging = false;
        _dragStartPoint = e.GetPosition(this);
        CaptureMouse();
        T($"TileGrid: 准备拖动 磁贴{index}");
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragArmed || _dragFrom < 0) return;
        var position = e.GetPosition(this);
        if (!_dragging)
        {
            var delta = position - _dragStartPoint;
            if (Math.Abs(delta.X) < DragThreshold && Math.Abs(delta.Y) < DragThreshold) return;
            BeginDragPreview();
        }

        ApplyDragPreview(position);
        var target = TargetIndexAt(position);
        if (target == _dragTarget) return;
        _dragTarget = target;
        T($"TileGrid: 预览落点 {target}");
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_dragArmed) return;
        CommitDrag(persist: true);
    }

    /// <summary>退出编辑（由「完成」或空白处点击调用），保证一定落盘。</summary>
    public void ExitEditing()
    {
        CommitDrag(persist: true);
        _dragArmed = false;
        _editMode = false;
        Owner?.PersistTiles();
        T("TileGrid: ExitEditing 已保存");
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_dragArmed) CommitDrag(persist: true);
    }

    private void BeginDragPreview()
    {
        if (_dragFrom < 0 || _dragFrom >= Children.Count) return;
        _dragging = true;
        _dragChild = Children[_dragFrom];
        _dragOriginalTransform = _dragChild.RenderTransform;
        _dragOriginalOpacity = _dragChild.Opacity;
        _dragOriginalZIndex = Panel.GetZIndex(_dragChild);
        _dragChild.RenderTransform = new TranslateTransform();
        _dragChild.Opacity = 0.82;
        Panel.SetZIndex(_dragChild, 1000);
        T($"TileGrid: 开始拖动 磁贴{_dragFrom}");
    }

    private void ApplyDragPreview(Point position)
    {
        if (!_dragging || _dragChild?.RenderTransform is not TranslateTransform transform) return;
        var delta = position - _dragStartPoint;
        transform.X = delta.X;
        transform.Y = delta.Y;
    }

    private int TargetIndexAt(Point position)
    {
        if (_dragChild is null || !_arrangedBounds.TryGetValue(_dragChild, out var original) ||
            original.Contains(position))
            return _dragFrom;

        var bestIndex = _dragFrom;
        var bestDistance = double.MaxValue;
        for (var i = 0; i < Children.Count; i++)
        {
            var child = Children[i];
            if (ReferenceEquals(child, _dragChild) || !_arrangedBounds.TryGetValue(child, out var bounds))
                continue;
            if (bounds.Contains(position)) return i;

            var center = new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
            var dx = position.X - center.X;
            var dy = position.Y - center.Y;
            var distance = dx * dx + dy * dy;
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            bestIndex = i;
        }
        return bestIndex;
    }

    private void CommitDrag(bool persist)
    {
        if (!_dragArmed && !_dragging) return;
        var from = _dragFrom;
        var to = _dragTarget;
        ResetDragPreview();
        if (from >= 0 && to >= 0 && from != to)
        {
            Owner?.MoveTile(from, to);
            T($"TileGrid: 松手提交 磁贴{from} → {to}");
        }
        else if (persist)
        {
            Owner?.PersistTiles();
            T("TileGrid: 未改变顺序，已确认当前布局");
        }
    }

    private void ResetDragPreview()
    {
        _dragArmed = false;
        _dragging = false;
        _dragFrom = -1;
        _dragTarget = -1;
        if (_dragChild is not null)
        {
            _dragChild.RenderTransform = _dragOriginalTransform;
            _dragChild.Opacity = _dragOriginalOpacity;
            Panel.SetZIndex(_dragChild, _dragOriginalZIndex);
        }
        _dragChild = null;
        _dragOriginalTransform = null;
        _dragOriginalOpacity = 1;
        _dragOriginalZIndex = 0;
        ReleaseMouseCaptureOnce();
        InvalidateVisual();
    }

    internal int DragTargetIndexForTest => _dragTarget;
    internal bool IsDraggingForTest => _dragging;
    internal Rect TileBoundsForTest(int index)
        => index >= 0 && index < Children.Count && _arrangedBounds.TryGetValue(Children[index], out var rect)
            ? rect
            : Rect.Empty;

    internal void BeginDragForTest(int index)
    {
        if (index < 0 || index >= Children.Count) return;
        _editMode = true;
        _dragFrom = index;
        _dragTarget = index;
        _dragArmed = true;
        _dragStartPoint = new Point(
            _arrangedBounds[Children[index]].Left + 20,
            _arrangedBounds[Children[index]].Top + 12);
        BeginDragPreview();
    }

    internal void MoveDragForTest(Point position)
    {
        if (!_dragArmed) return;
        ApplyDragPreview(position);
        _dragTarget = TargetIndexAt(position);
        InvalidateVisual();
    }

    internal void DropDragForTest() => CommitDrag(persist: true);

    internal static bool IsDragHandleTagForTest(object? tag) => tag is string value && value == "TileDragHandle";

    private static bool IsDragHandle(DependencyObject source)
    {
        var node = source;
        while (node is not null)
        {
            if (node is FrameworkElement { Tag: "TileDragHandle" }) return true;
            if (node is TileGrid) return false;
            node = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }
        return false;
    }

    private static bool IsInteractive(DependencyObject source)
    {
        var node = source;
        while (node is not null)
        {
            if (node is Button or TextBox or PasswordBox or ComboBox or Slider or CheckBox or RadioButton or ScrollBar)
                return true;
            if (node is TileGrid) return false;
            node = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }
        return false;
    }

    private int? FindChildIndex(DependencyObject source)
    {
        var node = source;
        while (node is not null)
        {
            if (node is FrameworkElement fe && Children.Contains(fe)) return Children.IndexOf(fe);
            node = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }
        return null;
    }

}
