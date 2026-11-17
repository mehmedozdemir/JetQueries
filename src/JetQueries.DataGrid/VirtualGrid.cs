using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;

namespace JetQueries.DataGrid;

/// <summary>
/// A read-only grid that draws only the visible cells with <see cref="DrawingContext"/>. It creates no child
/// controls, so its cost depends on the size of the viewport, not on the number of rows.
/// Place it inside a <see cref="ScrollViewer"/>; it scrolls logically through <see cref="ILogicalScrollable"/>.
/// </summary>
/// <remarks>Phase 0 spike: fixed row height, cell and range selection, column resize. No editing, sorting or copy.</remarks>
public sealed class VirtualGrid : Control, ILogicalScrollable
{
    /// <summary>Height of a data row.</summary>
    public const double RowHeight = 22;

    /// <summary>Height of the header row.</summary>
    public const double HeaderHeight = 26;

    private const double CellPadding = 6;
    private const double ResizeTolerance = 4;
    private const int MaxCachedCells = 8192;
    private const int MaxCellTextLength = 256;

    // Share of a 16.7 ms frame the UI thread may spend shaping text for cells it has not drawn before.
    private static readonly TimeSpan TextBudget = TimeSpan.FromMilliseconds(6);

    /// <summary>Defines the <see cref="Source"/> property.</summary>
    public static readonly StyledProperty<IGridRowSource?> SourceProperty =
        AvaloniaProperty.Register<VirtualGrid, IGridRowSource?>(nameof(Source));

    /// <summary>Defines the <see cref="Background"/> property.</summary>
    public static readonly StyledProperty<IBrush> BackgroundProperty =
        AvaloniaProperty.Register<VirtualGrid, IBrush>(nameof(Background), Brushes.White);

    /// <summary>Defines the <see cref="Foreground"/> property.</summary>
    public static readonly StyledProperty<IBrush> ForegroundProperty =
        AvaloniaProperty.Register<VirtualGrid, IBrush>(nameof(Foreground), Brushes.Black);

    /// <summary>Defines the <see cref="MutedForeground"/> property.</summary>
    public static readonly StyledProperty<IBrush> MutedForegroundProperty =
        AvaloniaProperty.Register<VirtualGrid, IBrush>(nameof(MutedForeground), Brushes.Gray);

    /// <summary>Defines the <see cref="HeaderBackground"/> property.</summary>
    public static readonly StyledProperty<IBrush> HeaderBackgroundProperty =
        AvaloniaProperty.Register<VirtualGrid, IBrush>(nameof(HeaderBackground), Brushes.WhiteSmoke);

    /// <summary>Defines the <see cref="GridLineBrush"/> property.</summary>
    public static readonly StyledProperty<IBrush> GridLineBrushProperty =
        AvaloniaProperty.Register<VirtualGrid, IBrush>(nameof(GridLineBrush), Brushes.LightGray);

    /// <summary>Defines the <see cref="SelectionBrush"/> property.</summary>
    public static readonly StyledProperty<IBrush> SelectionBrushProperty =
        AvaloniaProperty.Register<VirtualGrid, IBrush>(nameof(SelectionBrush), Brushes.LightBlue);

    /// <summary>Defines the <see cref="AccentBrush"/> property.</summary>
    public static readonly StyledProperty<IBrush> AccentBrushProperty =
        AvaloniaProperty.Register<VirtualGrid, IBrush>(nameof(AccentBrush), Brushes.DodgerBlue);

    private static readonly Cursor ResizeCursor = new(StandardCursorType.SizeWestEast);

    private readonly ColumnLayout _columns = new();
    private readonly Dictionary<long, TextLayout> _cellTexts = [];
    private TextLayout?[] _headerTexts = [];
    private TextLayout? _nullText;
    private TextLayout? _ellipsisText;
    private bool _incomplete;
    private IPen? _gridPen;
    private IPen? _accentPen;
    private Vector _offset;
    private GridCell _current;
    private GridCell _anchor;
    private bool _selecting;
    private int _resizingColumn = -1;
    private double _resizeStartX;
    private double _resizeStartWidth;

    static VirtualGrid()
    {
        AffectsRender<VirtualGrid>(
            BackgroundProperty,
            ForegroundProperty,
            MutedForegroundProperty,
            HeaderBackgroundProperty,
            GridLineBrushProperty,
            SelectionBrushProperty,
            AccentBrushProperty);
        FocusableProperty.OverrideDefaultValue<VirtualGrid>(true);
        ClipToBoundsProperty.OverrideDefaultValue<VirtualGrid>(true);
    }

    /// <inheritdoc />
    public event EventHandler? ScrollInvalidated;

    /// <summary>The rows and columns to show.</summary>
    public IGridRowSource? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    /// <summary>Background of the cells.</summary>
    public IBrush Background
    {
        get => GetValue(BackgroundProperty);
        set => SetValue(BackgroundProperty, value);
    }

    /// <summary>Colour of cell and header text.</summary>
    public IBrush Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    /// <summary>Colour of <c>NULL</c> markers.</summary>
    public IBrush MutedForeground
    {
        get => GetValue(MutedForegroundProperty);
        set => SetValue(MutedForegroundProperty, value);
    }

    /// <summary>Background of the header row.</summary>
    public IBrush HeaderBackground
    {
        get => GetValue(HeaderBackgroundProperty);
        set => SetValue(HeaderBackgroundProperty, value);
    }

    /// <summary>Colour of the grid lines.</summary>
    public IBrush GridLineBrush
    {
        get => GetValue(GridLineBrushProperty);
        set => SetValue(GridLineBrushProperty, value);
    }

    /// <summary>Fill of the selected range.</summary>
    public IBrush SelectionBrush
    {
        get => GetValue(SelectionBrushProperty);
        set => SetValue(SelectionBrushProperty, value);
    }

    /// <summary>Colour of the current-cell border.</summary>
    public IBrush AccentBrush
    {
        get => GetValue(AccentBrushProperty);
        set => SetValue(AccentBrushProperty, value);
    }

    /// <summary>The cell that has keyboard focus within the grid.</summary>
    public GridCell CurrentCell => _current;

    /// <summary>The selected range (a single cell when nothing was extended).</summary>
    public GridCellRange Selection => GridCellRange.FromCorners(_anchor, _current);

    /// <summary>Time the most recent <see cref="Render"/> call spent on the UI thread. Diagnostic.</summary>
    public TimeSpan LastRenderDuration { get; private set; }

    /// <summary>Number of <see cref="Render"/> calls so far. Diagnostic.</summary>
    public long RenderCount { get; private set; }

    /// <summary>Scroll position in content coordinates. Values are coerced into the scrollable range.</summary>
    public Vector Offset
    {
        get => _offset;
        set
        {
            var coerced = CoerceOffset(value);
            if (coerced != _offset)
            {
                _offset = coerced;
                InvalidateVisual();
                RaiseScrollInvalidated(EventArgs.Empty);
            }
        }
    }

    /// <inheritdoc />
    public Size Extent => new(_columns.TotalWidth, HeaderHeight + (RowCount * RowHeight));

    /// <inheritdoc />
    public Size Viewport => Bounds.Size;

    /// <inheritdoc />
    public bool CanHorizontallyScroll { get; set; }

    /// <inheritdoc />
    public bool CanVerticallyScroll { get; set; }

    /// <inheritdoc />
    public bool IsLogicalScrollEnabled => true;

    /// <inheritdoc />
    public Size ScrollSize => new(48, RowHeight * 3);

    /// <inheritdoc />
    public Size PageScrollSize => new(Bounds.Width, Math.Max(RowHeight, BodyHeight - RowHeight));

    private int RowCount => Source?.RowCount ?? 0;

    private double BodyHeight => Math.Max(0, Bounds.Height - HeaderHeight);

    /// <inheritdoc />
    public bool BringIntoView(Control target, Rect targetRect) => false;

    /// <inheritdoc />
    public Control? GetControlInDirection(NavigationDirection direction, Control? from) => null;

    /// <inheritdoc />
    public void RaiseScrollInvalidated(EventArgs e) => ScrollInvalidated?.Invoke(this, e);

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        var started = Stopwatch.GetTimestamp();
        var width = Bounds.Width;
        var height = Bounds.Height;
        context.FillRectangle(Background, new Rect(0, 0, width, height));

        if (Source is { } source && _columns.Count > 0)
        {
            // Whole-pixel offsets keep text and 1 px lines crisp while scrolling.
            var offsetX = Math.Round(_offset.X);
            var offsetY = Math.Round(_offset.Y);
            var (firstColumn, lastColumn) = _columns.VisibleRange(offsetX, width);
            var (firstRow, lastRow) = RowLayout.VisibleRange(offsetY, BodyHeight, RowHeight, source.RowCount);
            _gridPen ??= new Pen(GridLineBrush);

            _incomplete = false;
            TrimTextCache(firstRow, lastRow, _columns.Count);
            RenderBody(context, source, offsetX, offsetY, firstRow, lastRow, firstColumn, lastColumn, started);
            RenderHeader(context, source, offsetX, firstColumn, lastColumn);

            if (_incomplete)
            {
                Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Render);
            }
        }

        RenderCount++;
        LastRenderDuration = Stopwatch.GetElapsedTime(started);
    }

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize)
    {
        var extent = Extent;
        return new Size(
            double.IsInfinity(availableSize.Width) ? extent.Width : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? extent.Height : availableSize.Height);
    }

    /// <inheritdoc />
    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        _offset = CoerceOffset(_offset);
        RaiseScrollInvalidated(EventArgs.Empty);
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == SourceProperty)
        {
            OnSourceChanged(change.GetOldValue<IGridRowSource?>(), change.GetNewValue<IGridRowSource?>());
        }
        else if (change.Property == ForegroundProperty ||
                 change.Property == MutedForegroundProperty ||
                 change.Property == TextElement.FontFamilyProperty ||
                 change.Property == TextElement.FontSizeProperty)
        {
            // Brushes and fonts are baked into the cached text layouts.
            ClearTextCache();
            InvalidateVisual();
        }
        else if (change.Property == GridLineBrushProperty)
        {
            _gridPen = null;
        }
        else if (change.Property == AccentBrushProperty)
        {
            _accentPen = null;
        }
    }

    /// <inheritdoc />
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Source is null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        Focus();
        var position = e.GetPosition(this);
        if (position.Y < HeaderHeight)
        {
            var column = _columns.ResizeHandleAt(position.X + _offset.X, ResizeTolerance);
            if (column >= 0)
            {
                _resizingColumn = column;
                _resizeStartX = position.X;
                _resizeStartWidth = _columns.Width(column);
                e.Pointer.Capture(this);
                e.Handled = true;
            }

            return;
        }

        if (TryGetNearestCell(position, out var cell))
        {
            MoveCurrent(cell, extend: e.KeyModifiers.HasFlag(KeyModifiers.Shift));
            _selecting = true;
            e.Pointer.Capture(this);
            e.Handled = true;
        }
    }

    /// <inheritdoc />
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var position = e.GetPosition(this);

        if (_resizingColumn >= 0)
        {
            _columns.SetWidth(_resizingColumn, _resizeStartWidth + position.X - _resizeStartX);
            _offset = CoerceOffset(_offset);
            RaiseScrollInvalidated(EventArgs.Empty);
            InvalidateVisual();
        }
        else if (_selecting)
        {
            if (TryGetNearestCell(position, out var cell) && cell != _current)
            {
                MoveCurrent(cell, extend: true);
            }
        }
        else
        {
            var overHandle = position.Y < HeaderHeight &&
                             _columns.ResizeHandleAt(position.X + _offset.X, ResizeTolerance) >= 0;
            Cursor = overHandle ? ResizeCursor : Cursor.Default;
        }
    }

    /// <inheritdoc />
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_selecting || _resizingColumn >= 0)
        {
            e.Pointer.Capture(null);
            EndPointerGesture();
        }
    }

    /// <inheritdoc />
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        EndPointerGesture();
    }

    /// <inheritdoc />
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || RowCount == 0 || _columns.Count == 0)
        {
            return;
        }

        var control = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var page = Math.Max(1, (int)(BodyHeight / RowHeight) - 1);
        var (row, column) = (_current.Row, _current.Column);

        switch (e.Key)
        {
            case Key.Up: row--; break;
            case Key.Down: row++; break;
            case Key.Left: column--; break;
            case Key.Right: column++; break;
            case Key.PageUp: row -= page; break;
            case Key.PageDown: row += page; break;
            case Key.Home when control: row = 0; break;
            case Key.End when control: row = RowCount - 1; break;
            case Key.Home: column = 0; break;
            case Key.End: column = _columns.Count - 1; break;
            case Key.A when control:
                _anchor = new GridCell(0, 0);
                _current = new GridCell(RowCount - 1, _columns.Count - 1);
                InvalidateVisual();
                e.Handled = true;
                return;
            default:
                return;
        }

        var target = new GridCell(Math.Clamp(row, 0, RowCount - 1), Math.Clamp(column, 0, _columns.Count - 1));
        MoveCurrent(target, extend: e.KeyModifiers.HasFlag(KeyModifiers.Shift));
        e.Handled = true;
    }

    private void RenderBody(
        DrawingContext context,
        IGridRowSource source,
        double offsetX,
        double offsetY,
        int firstRow,
        int lastRow,
        int firstColumn,
        int lastColumn,
        long started)
    {
        var selection = Selection;
        var columnCount = _columns.Count;
        var contentRight = Math.Min(Bounds.Width, _columns.TotalWidth - offsetX);
        var bottom = lastRow < firstRow ? HeaderHeight : HeaderHeight + ((lastRow + 1) * RowHeight) - offsetY;

        // Pass 1: selection fill, one rectangle for the whole visible part of the range.
        if (selection.LastRow >= firstRow && selection.FirstRow <= lastRow)
        {
            var top = HeaderHeight + (Math.Max(firstRow, selection.FirstRow) * RowHeight) - offsetY;
            var end = HeaderHeight + ((Math.Min(lastRow, selection.LastRow) + 1) * RowHeight) - offsetY;
            var left = _columns.Left(selection.FirstColumn) - offsetX;
            var right = _columns.Right(selection.LastColumn) - offsetX;
            context.FillRectangle(SelectionBrush, new Rect(left, top, right - left, end - top));
        }

        // Pass 2: text, column by column, so one clip per column replaces per-cell trimming. Untrimmed layouts
        // are cheaper to create and stay valid when a column is resized.
        for (var column = firstColumn; column <= lastColumn; column++)
        {
            var cellWidth = _columns.Width(column) - (2 * CellPadding);
            if (cellWidth < 4)
            {
                continue;
            }

            var cellLeft = _columns.Left(column) - offsetX + CellPadding;
            var rightAligned = source.Columns[column].Alignment == GridTextAlignment.Right;
            var columnSelected = column >= selection.FirstColumn && column <= selection.LastColumn;

            using (context.PushClip(new Rect(cellLeft, HeaderHeight, cellWidth, Math.Max(0, bottom - HeaderHeight))))
            {
                for (var row = firstRow; row <= lastRow; row++)
                {
                    var text = GetCellText(source, row, column, columnCount, started);
                    if (text is null)
                    {
                        continue;
                    }

                    var y = HeaderHeight + (row * RowHeight) - offsetY;
                    var overflow = text.Width > cellWidth;
                    var x = rightAligned && !overflow ? cellLeft + cellWidth - text.Width : cellLeft;
                    text.Draw(context, new Point(x, y + Math.Floor((RowHeight - text.Height) / 2)));

                    if (overflow)
                    {
                        var selected = columnSelected && row >= selection.FirstRow && row <= selection.LastRow;
                        DrawEllipsis(context, cellLeft + cellWidth, y, RowHeight, selected ? SelectionBrush : Background);
                    }
                }
            }
        }

        // Pass 3: grid lines. +0.5 centres a 1 px line on a device pixel at 100 % scaling.
        for (var row = firstRow; row <= lastRow; row++)
        {
            var lineY = HeaderHeight + ((row + 1) * RowHeight) - offsetY - 0.5;
            context.DrawLine(_gridPen!, new Point(0, lineY), new Point(contentRight, lineY));
        }

        for (var column = firstColumn; column <= lastColumn; column++)
        {
            var lineX = _columns.Right(column) - offsetX - 0.5;
            context.DrawLine(_gridPen!, new Point(lineX, HeaderHeight), new Point(lineX, bottom));
        }

        if (_current.Row >= firstRow && _current.Row <= lastRow && _current.Column < columnCount)
        {
            _accentPen ??= new Pen(AccentBrush, 2);
            var rect = new Rect(
                _columns.Left(_current.Column) - offsetX,
                HeaderHeight + (_current.Row * RowHeight) - offsetY,
                _columns.Width(_current.Column) - 1,
                RowHeight - 1);
            context.DrawRectangle(_accentPen, rect.Deflate(0.5));
        }
    }

    private void RenderHeader(DrawingContext context, IGridRowSource source, double offsetX, int firstColumn, int lastColumn)
    {
        // Drawn after the body, so it covers the partially scrolled first row.
        context.FillRectangle(HeaderBackground, new Rect(0, 0, Bounds.Width, HeaderHeight));

        for (var column = firstColumn; column <= lastColumn; column++)
        {
            var cellWidth = _columns.Width(column) - (2 * CellPadding);
            if (cellWidth >= 4)
            {
                var text = _headerTexts[column] ??= CreateText(source.Columns[column].Header, Foreground);
                var cellLeft = _columns.Left(column) - offsetX + CellPadding;
                using (context.PushClip(new Rect(cellLeft, 0, cellWidth, HeaderHeight)))
                {
                    text.Draw(context, new Point(cellLeft, Math.Floor((HeaderHeight - text.Height) / 2)));
                    if (text.Width > cellWidth)
                    {
                        DrawEllipsis(context, cellLeft + cellWidth, 0, HeaderHeight, HeaderBackground);
                    }
                }
            }

            var lineX = _columns.Right(column) - offsetX - 0.5;
            context.DrawLine(_gridPen!, new Point(lineX, 0), new Point(lineX, HeaderHeight));
        }

        context.DrawLine(_gridPen!, new Point(0, HeaderHeight - 0.5), new Point(Bounds.Width, HeaderHeight - 0.5));
    }

    /// <summary>Marks clipped text: paints the cell background over its tail and an ellipsis on top.</summary>
    private void DrawEllipsis(DrawingContext context, double cellRight, double y, double height, IBrush background)
    {
        var ellipsis = _ellipsisText ??= CreateText("…", MutedForeground);
        var x = cellRight - ellipsis.Width;
        context.FillRectangle(background, new Rect(x - 1, y + 1, ellipsis.Width + 1, height - 2));
        ellipsis.Draw(context, new Point(x, y + Math.Floor((height - ellipsis.Height) / 2)));
    }

    private TextLayout? GetCellText(IGridRowSource source, int row, int column, int columnCount, long renderStarted)
    {
        var key = ((long)row * columnCount) + column;
        if (_cellTexts.TryGetValue(key, out var cached))
        {
            return cached;
        }

        // Shaping text is the only expensive step. When a frame needs more new cells than fit in the budget
        // (dragging the scrollbar thumb), draw what we have and fill in the rest on the next frame.
        if (Stopwatch.GetElapsedTime(renderStarted) > TextBudget)
        {
            _incomplete = true;
            return null;
        }

        var value = source.GetText(row, column);
        if (value is null)
        {
            return _nullText ??= CreateText("NULL", MutedForeground); // One shared layout for every NULL cell.
        }

        if (value.Length == 0)
        {
            return null;
        }

        // Laying out more text than can ever be visible in one cell is wasted work.
        var visible = value.Length > MaxCellTextLength ? value[..MaxCellTextLength] : value;
        var text = CreateText(visible, Foreground);
        _cellTexts[key] = text;
        return text;
    }

    /// <summary>Drops cached cells outside the visible rows once the cache is full.</summary>
    private void TrimTextCache(int firstRow, int lastRow, int columnCount)
    {
        if (_cellTexts.Count < MaxCachedCells)
        {
            return;
        }

        var firstKey = (long)firstRow * columnCount;
        var lastKey = (((long)lastRow + 1) * columnCount) - 1;
        foreach (var key in _cellTexts.Keys)
        {
            if (key < firstKey || key > lastKey)
            {
                _cellTexts.Remove(key); // Removing while enumerating is supported by Dictionary.
            }
        }
    }

    // TextLayout keeps the shaped glyph runs, so drawing a cached cell does no text shaping at all.
    // (FormattedText re-formats its lines on every draw, which is far too slow for a grid.)
    private TextLayout CreateText(string text, IBrush brush) =>
        new(
            text,
            new Typeface(GetValue(TextElement.FontFamilyProperty)),
            GetValue(TextElement.FontSizeProperty),
            brush,
            maxLines: 1);

    private void OnSourceChanged(IGridRowSource? oldSource, IGridRowSource? newSource)
    {
        if (oldSource is not null)
        {
            oldSource.RowCountChanged -= OnRowCountChanged;
        }

        if (newSource is not null)
        {
            newSource.RowCountChanged += OnRowCountChanged;
        }

        _columns.Reset(newSource?.Columns.Select(static c => c.Width).ToArray() ?? []);
        _headerTexts = new TextLayout?[_columns.Count];
        _cellTexts.Clear();
        _offset = default;
        _current = default;
        _anchor = default;
        InvalidateMeasure();
        InvalidateVisual();
        RaiseScrollInvalidated(EventArgs.Empty);
    }

    private void OnRowCountChanged(object? sender, EventArgs e)
    {
        var rowCount = RowCount;
        if (rowCount == 0)
        {
            _cellTexts.Clear();
            _current = default;
            _anchor = default;
        }

        _offset = CoerceOffset(_offset);
        RaiseScrollInvalidated(EventArgs.Empty);
        InvalidateVisual();
    }

    private void MoveCurrent(GridCell cell, bool extend)
    {
        _current = cell;
        if (!extend)
        {
            _anchor = cell;
        }

        EnsureVisible(cell);
        InvalidateVisual();
    }

    private void EnsureVisible(GridCell cell)
    {
        var x = _offset.X;
        var y = _offset.Y;
        var top = cell.Row * RowHeight;
        var left = _columns.Left(cell.Column);
        var right = _columns.Right(cell.Column);

        if (top < y)
        {
            y = top;
        }
        else if (top + RowHeight > y + BodyHeight)
        {
            y = top + RowHeight - BodyHeight;
        }

        if (left < x || right - left > Bounds.Width)
        {
            x = left;
        }
        else if (right > x + Bounds.Width)
        {
            x = right - Bounds.Width;
        }

        Offset = new Vector(x, y);
    }

    private bool TryGetNearestCell(Point position, out GridCell cell)
    {
        cell = default;
        if (RowCount == 0 || _columns.Count == 0)
        {
            return false;
        }

        var row = (int)Math.Floor((position.Y - HeaderHeight + _offset.Y) / RowHeight);
        var x = Math.Clamp(position.X + _offset.X, 0, _columns.TotalWidth - 0.001);
        cell = new GridCell(Math.Clamp(row, 0, RowCount - 1), _columns.ColumnAt(x));
        return true;
    }

    private void EndPointerGesture()
    {
        _selecting = false;
        _resizingColumn = -1;
    }

    private void ClearTextCache()
    {
        _cellTexts.Clear();
        _nullText = null;
        _ellipsisText = null;
        Array.Clear(_headerTexts);
    }

    private Vector CoerceOffset(Vector value)
    {
        var extent = Extent;
        return new Vector(
            Math.Clamp(value.X, 0, Math.Max(0, extent.Width - Bounds.Width)),
            Math.Clamp(value.Y, 0, Math.Max(0, extent.Height - Bounds.Height)));
    }
}
