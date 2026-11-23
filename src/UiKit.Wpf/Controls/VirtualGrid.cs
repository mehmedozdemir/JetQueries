using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace UiKit.Controls;

/// <summary>Rectangular selection in view coordinates (rows of the source, column indices of the source).</summary>
public readonly record struct GridSelection(int FirstRow, int LastRow, IReadOnlyList<int> Columns, int CurrentRow, int CurrentColumn)
{
    public int RowCount => LastRow - FirstRow + 1;
}

/// <summary>User facing texts of the grid (override for localization).</summary>
public static class GridStrings
{
    public static string SortAscending { get; set; } = "Artan sırala";
    public static string SortDescending { get; set; } = "Azalan sırala";
    public static string ClearSort { get; set; } = "Sıralamayı kaldır";
    public static string Filter { get; set; } = "Filtrele…";
    public static string FitColumn { get; set; } = "Sütunu sığdır";
    public static string FitAllColumns { get; set; } = "Tüm sütunları sığdır";
    public static string HideColumn { get; set; } = "Sütunu gizle";
    public static string CopyColumnName { get; set; } = "Sütun adını kopyala";
    public static string Copy { get; set; } = "Kopyala";
    public static string CopyWithHeaders { get; set; } = "Başlıklarla kopyala";
    public static string FilterByValue { get; set; } = "Bu değere göre filtrele";
    public static string ExcludeValue { get; set; } = "Bu değeri hariç tut";
    public static string SelectAll { get; set; } = "Tümünü seç";
    public static string NoRows { get; set; } = "Gösterilecek kayıt yok";
}

/// <summary>
/// Fully virtualized, custom drawn data grid. Only the visible cells are rendered (GlyphRuns, no per-cell
/// elements), so scrolling stays smooth for tens of millions of rows. Rows are scrolled logically by index.
/// Theme colors come from the UiKit "Brush.*" resources.
/// </summary>
public sealed class VirtualGrid : FrameworkElement
{
    private const double HeaderHeight = 36;
    private const double CellPadding = 10;
    private const double MinColumnWidth = 40;
    private const double ResizeGrip = 4;
    private const double SortIconWidth = 18;

    private enum Area { None, Corner, Header, Gutter, Cell }

    private enum DragMode { None, Select, RowSelect, Resize }

    private readonly ScrollBar _vbar;
    private readonly ScrollBar _hbar;
    private readonly VisualCollection _children;
    private readonly DispatcherTimer _autoScroll;
    private readonly DispatcherTimer _tipTimer;
    private readonly ToolTip _tip;

    private IGridDataSource _source = ArrayGridDataSource.Empty;
    private int[] _display = [];
    private double[] _widths = [];
    private double[] _lefts = [0];
    private int _firstRow;
    private double _hOffset;
    private double _gutterWidth;
    private double _pixelsPerDip = 1;

    private GlyphTextRenderer? _cellText;
    private GlyphTextRenderer? _headerText;
    private GlyphTextRenderer? _smallText;
    private GlyphTextRenderer? _iconText;

    // Selection (display column indices).
    private int _anchorRow = -1, _anchorCol = -1, _curRow = -1, _curCol = -1;

    // Hover / drag state.
    private int _hoverRow = -1;
    private int _hoverHeaderCol = -1;
    private bool _hoverFilterIcon;
    private DragMode _drag;
    private int _resizeCol;
    private double _resizeStartX;
    private double _resizeStartWidth;
    private Point _lastMouse;
    private (int Row, int Col) _tipCell = (-1, -1);

    public VirtualGrid()
    {
        Focusable = true;
        FocusVisualStyle = null;
        ClipToBounds = true;
        SnapsToDevicePixels = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        RenderOptions.SetClearTypeHint(this, ClearTypeHint.Enabled);

        _vbar = new ScrollBar { Orientation = Orientation.Vertical, Minimum = 0, SmallChange = 1, Cursor = Cursors.Arrow };
        _hbar = new ScrollBar { Orientation = Orientation.Horizontal, Minimum = 0, SmallChange = 48, Cursor = Cursors.Arrow };
        _vbar.ValueChanged += (_, e) => SetFirstRowCore((int)Math.Round(e.NewValue));
        _hbar.ValueChanged += (_, e) => SetHorizontalOffsetCore(e.NewValue);

        _children = new VisualCollection(this) { _vbar, _hbar };
        AddLogicalChild(_vbar);
        AddLogicalChild(_hbar);

        _autoScroll = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(40) };
        _autoScroll.Tick += (_, _) => AutoScrollTick();

        _tip = new ToolTip { Placement = PlacementMode.Relative, PlacementTarget = this };
        _tipTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(650) };
        _tipTimer.Tick += (_, _) => ShowCellTip();

        SetResourceReference(SurfaceBrushProperty, "Brush.Surface");
        SetResourceReference(AltRowBrushProperty, "Brush.SurfaceAlt");
        SetResourceReference(HeaderBrushProperty, "Brush.Header");
        SetResourceReference(TextBrushProperty, "Brush.Text");
        SetResourceReference(SecondaryTextBrushProperty, "Brush.TextSecondary");
        SetResourceReference(MutedTextBrushProperty, "Brush.TextMuted");
        SetResourceReference(GridLineBrushProperty, "Brush.GridLine");
        SetResourceReference(BorderBrushProperty, "Brush.Border");
        SetResourceReference(SelectionBrushProperty, "Brush.Selection");
        SetResourceReference(AccentBrushProperty, "Brush.Accent");
        SetResourceReference(AccentSoftBrushProperty, "Brush.AccentSoft");
        SetResourceReference(HoverBrushProperty, "Brush.Hover");
        SetResourceReference(TrueBrushProperty, "Brush.TrueValue");
    }

    #region Events

    /// <summary>Header clicked: (column, additive multi-sort with Shift).</summary>
    public event Action<int, bool>? SortRequested;

    /// <summary>Header context menu sort: (column, direction or null to clear).</summary>
    public event Action<int, GridSortDirection?>? SortDirectionRequested;

    /// <summary>Filter icon clicked: (column, header rectangle relative to the grid — use for popup placement).</summary>
    public event Action<int, Rect>? FilterRequested;

    public event Action<int>? HideColumnRequested;

    /// <summary>Cell context menu: (column, row, exclude).</summary>
    public event Action<int, int, bool>? FilterByValueRequested;

    /// <summary>Copy the selection (argument: include headers). Use <see cref="Selection"/> to build the text.</summary>
    public event Action<bool>? CopyRequested;

    public event Action? SelectionChanged;

    /// <summary>The cell menu is about to open (arguments: the menu, the row, the source column): add items to it.</summary>
    public event Action<ContextMenu, int, int>? CellMenuOpening;

    public event Action? CurrentCellChanged;

    #endregion

    #region Dependency properties

    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(nameof(Source), typeof(IGridDataSource), typeof(VirtualGrid),
        new PropertyMetadata(ArrayGridDataSource.Empty, (d, e) => ((VirtualGrid)d).OnSourceChanged((IGridDataSource?)e.OldValue, (IGridDataSource?)e.NewValue)));

    public IGridDataSource Source
    {
        get => (IGridDataSource)GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    /// <summary>Visible columns in display order (null = all, in source order).</summary>
    public static readonly DependencyProperty VisibleColumnsProperty = DependencyProperty.Register(nameof(VisibleColumns), typeof(IReadOnlyList<int>),
        typeof(VirtualGrid), new PropertyMetadata(null, (d, _) => ((VirtualGrid)d).OnColumnsChanged()));

    public IReadOnlyList<int>? VisibleColumns
    {
        get => (IReadOnlyList<int>?)GetValue(VisibleColumnsProperty);
        set => SetValue(VisibleColumnsProperty, value);
    }

    /// <summary>Sort indicators shown in the header (the grid does not sort; the owner does).</summary>
    public static readonly DependencyProperty SortsProperty = DependencyProperty.Register(nameof(Sorts), typeof(IReadOnlyList<GridSort>),
        typeof(VirtualGrid), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<GridSort>? Sorts
    {
        get => (IReadOnlyList<GridSort>?)GetValue(SortsProperty);
        set => SetValue(SortsProperty, value);
    }

    /// <summary>Columns drawn with an active (accent) filter icon.</summary>
    public static readonly DependencyProperty FilteredColumnsProperty = DependencyProperty.Register(nameof(FilteredColumns), typeof(IReadOnlyCollection<int>),
        typeof(VirtualGrid), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyCollection<int>? FilteredColumns
    {
        get => (IReadOnlyCollection<int>?)GetValue(FilteredColumnsProperty);
        set => SetValue(FilteredColumnsProperty, value);
    }

    public static readonly DependencyProperty RowHeightProperty = DependencyProperty.Register(nameof(RowHeight), typeof(double), typeof(VirtualGrid),
        new FrameworkPropertyMetadata(28.0, FrameworkPropertyMetadataOptions.AffectsArrange | FrameworkPropertyMetadataOptions.AffectsRender));

    public double RowHeight
    {
        get => (double)GetValue(RowHeightProperty);
        set => SetValue(RowHeightProperty, value);
    }

    /// <summary>Text drawn (muted) for NULL cells, e.g. "NULL" or "[NULL]". Empty = nothing.</summary>
    public static readonly DependencyProperty NullTextProperty = DependencyProperty.Register(nameof(NullText), typeof(string), typeof(VirtualGrid),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsRender));

    public string NullText
    {
        get => (string)GetValue(NullTextProperty);
        set => SetValue(NullTextProperty, value);
    }

    public static readonly DependencyProperty ShowFilterButtonsProperty = DependencyProperty.Register(nameof(ShowFilterButtons), typeof(bool), typeof(VirtualGrid),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public bool ShowFilterButtons
    {
        get => (bool)GetValue(ShowFilterButtonsProperty);
        set => SetValue(ShowFilterButtonsProperty, value);
    }

    public static readonly DependencyProperty ShowRowNumbersProperty = DependencyProperty.Register(nameof(ShowRowNumbers), typeof(bool), typeof(VirtualGrid),
        new FrameworkPropertyMetadata(true, (d, _) => ((VirtualGrid)d).RecomputeLayout()));

    public bool ShowRowNumbers
    {
        get => (bool)GetValue(ShowRowNumbersProperty);
        set => SetValue(ShowRowNumbersProperty, value);
    }

    private static DependencyProperty BrushProperty(string name) => DependencyProperty.Register(name, typeof(Brush), typeof(VirtualGrid),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SurfaceBrushProperty = BrushProperty(nameof(SurfaceBrush));
    public static readonly DependencyProperty AltRowBrushProperty = BrushProperty(nameof(AltRowBrush));
    public static readonly DependencyProperty HeaderBrushProperty = BrushProperty(nameof(HeaderBrush));
    public static readonly DependencyProperty TextBrushProperty = BrushProperty(nameof(TextBrush));
    public static readonly DependencyProperty SecondaryTextBrushProperty = BrushProperty(nameof(SecondaryTextBrush));
    public static readonly DependencyProperty MutedTextBrushProperty = BrushProperty(nameof(MutedTextBrush));
    public static readonly DependencyProperty GridLineBrushProperty = BrushProperty(nameof(GridLineBrush));
    public static readonly DependencyProperty BorderBrushProperty = BrushProperty(nameof(BorderBrush));
    public static readonly DependencyProperty SelectionBrushProperty = BrushProperty(nameof(SelectionBrush));
    public static readonly DependencyProperty AccentBrushProperty = BrushProperty(nameof(AccentBrush));
    public static readonly DependencyProperty AccentSoftBrushProperty = BrushProperty(nameof(AccentSoftBrush));
    public static readonly DependencyProperty HoverBrushProperty = BrushProperty(nameof(HoverBrush));
    public static readonly DependencyProperty TrueBrushProperty = BrushProperty(nameof(TrueBrush));

    public Brush SurfaceBrush { get => (Brush)GetValue(SurfaceBrushProperty); set => SetValue(SurfaceBrushProperty, value); }
    public Brush AltRowBrush { get => (Brush)GetValue(AltRowBrushProperty); set => SetValue(AltRowBrushProperty, value); }
    public Brush HeaderBrush { get => (Brush)GetValue(HeaderBrushProperty); set => SetValue(HeaderBrushProperty, value); }
    public Brush TextBrush { get => (Brush)GetValue(TextBrushProperty); set => SetValue(TextBrushProperty, value); }
    public Brush SecondaryTextBrush { get => (Brush)GetValue(SecondaryTextBrushProperty); set => SetValue(SecondaryTextBrushProperty, value); }
    public Brush MutedTextBrush { get => (Brush)GetValue(MutedTextBrushProperty); set => SetValue(MutedTextBrushProperty, value); }
    public Brush GridLineBrush { get => (Brush)GetValue(GridLineBrushProperty); set => SetValue(GridLineBrushProperty, value); }
    public Brush BorderBrush { get => (Brush)GetValue(BorderBrushProperty); set => SetValue(BorderBrushProperty, value); }
    public Brush SelectionBrush { get => (Brush)GetValue(SelectionBrushProperty); set => SetValue(SelectionBrushProperty, value); }
    public Brush AccentBrush { get => (Brush)GetValue(AccentBrushProperty); set => SetValue(AccentBrushProperty, value); }
    public Brush AccentSoftBrush { get => (Brush)GetValue(AccentSoftBrushProperty); set => SetValue(AccentSoftBrushProperty, value); }
    public Brush HoverBrush { get => (Brush)GetValue(HoverBrushProperty); set => SetValue(HoverBrushProperty, value); }
    public Brush TrueBrush { get => (Brush)GetValue(TrueBrushProperty); set => SetValue(TrueBrushProperty, value); }

    #endregion

    #region Public API

    public int CurrentRow => _curRow;

    /// <summary>Source index of the current column, or -1.</summary>
    public int CurrentColumn => _curCol >= 0 && _curCol < _display.Length ? _display[_curCol] : -1;

    public GridSelection? Selection
    {
        get
        {
            int count = _source.RowCount;
            if (_curRow < 0 || _anchorRow < 0 || count == 0 || _display.Length == 0)
                return null;
            int r1 = Math.Clamp(Math.Min(_anchorRow, _curRow), 0, count - 1);
            int r2 = Math.Clamp(Math.Max(_anchorRow, _curRow), 0, count - 1);
            int c1 = Math.Clamp(Math.Min(_anchorCol, _curCol), 0, _display.Length - 1);
            int c2 = Math.Clamp(Math.Max(_anchorCol, _curCol), 0, _display.Length - 1);
            return new GridSelection(r1, r2, _display[c1..(c2 + 1)], _curRow, CurrentColumn);
        }
    }

    /// <summary>Makes a row current, keeping the current column, and scrolls it into view.</summary>
    public void SelectRow(int row) => SetCurrent(row, Math.Max(0, _curCol), keepAnchor: false);

    public void SelectAll()
    {
        if (_source.RowCount == 0 || _display.Length == 0) return;
        _anchorRow = 0;
        _anchorCol = 0;
        _curRow = _source.RowCount - 1;
        _curCol = _display.Length - 1;
        InvalidateVisual();
        SelectionChanged?.Invoke();
    }

    public void AutoFitAllColumns()
    {
        foreach (int column in _display)
            _widths[column] = MeasureColumn(column, sampleRows: 1000, maxWidth: 600);
        RecomputeLayout();
    }

    /// <summary>Builds tab separated text of the current selection (display text, NULL as empty).</summary>
    public string GetSelectionText(bool includeHeaders)
    {
        if (Selection is not { } s) return string.Empty;
        var sb = new System.Text.StringBuilder();
        if (includeHeaders)
            sb.AppendJoin('\t', s.Columns.Select(_source.GetColumnName)).Append("\r\n");
        for (int r = s.FirstRow; r <= s.LastRow; r++)
        {
            for (int i = 0; i < s.Columns.Count; i++)
            {
                if (i > 0) sb.Append('\t');
                int c = s.Columns[i];
                if (!_source.IsNull(r, c)) sb.Append(_source.GetText(r, c).Replace('\t', ' ').Replace("\r", " ").Replace('\n', ' '));
            }
            sb.Append("\r\n");
        }
        return sb.ToString();
    }

    #endregion

    #region Source / columns

    private void OnSourceChanged(IGridDataSource? oldSource, IGridDataSource? newSource)
    {
        newSource ??= ArrayGridDataSource.Empty;
        bool sameSchema = oldSource is not null && Equals(oldSource.SchemaKey, newSource.SchemaKey) && oldSource.ColumnCount == newSource.ColumnCount;
        _source = newSource;

        if (!sameSchema)
        {
            _widths = new double[newSource.ColumnCount];
            _firstRow = 0;
            _hOffset = 0;
            _anchorRow = _curRow = newSource.RowCount > 0 ? 0 : -1;
            UpdateDisplayColumns();
            _anchorCol = _curCol = _display.Length > 0 ? 0 : -1;
            EnsureRenderers();
            for (int i = 0; i < _widths.Length; i++)
                _widths[i] = MeasureColumn(i, sampleRows: 300, maxWidth: 360);
        }
        else
        {
            _firstRow = 0;
            _anchorRow = _curRow = newSource.RowCount > 0 ? 0 : -1;
            if (_curCol < 0 && _display.Length > 0) _curCol = 0;
            _anchorCol = _curCol;
        }

        RecomputeLayout();
        _vbar.Value = 0;
        CurrentCellChanged?.Invoke();
        SelectionChanged?.Invoke();
    }

    private void OnColumnsChanged()
    {
        int current = CurrentColumn;
        UpdateDisplayColumns();
        int index = Array.IndexOf(_display, current);
        _curCol = _anchorCol = index >= 0 ? index : _display.Length > 0 ? 0 : -1;
        RecomputeLayout();
        SelectionChanged?.Invoke();
    }

    private void UpdateDisplayColumns()
    {
        int count = _source.ColumnCount;
        var visible = VisibleColumns;
        _display = visible is null
            ? Enumerable.Range(0, count).ToArray()
            : visible.Where(c => c >= 0 && c < count).ToArray();
    }

    private void RecomputeLayout()
    {
        if (_widths.Length < _source.ColumnCount)
            Array.Resize(ref _widths, _source.ColumnCount);
        _lefts = new double[_display.Length + 1];
        for (int i = 0; i < _display.Length; i++)
            _lefts[i + 1] = _lefts[i] + _widths[_display[i]];

        EnsureRenderers();
        if (ShowRowNumbers)
        {
            int digits = Math.Max(2, (int)Math.Floor(Math.Log10(Math.Max(1, _source.RowCount))) + 1);
            _gutterWidth = Math.Ceiling((_smallText?.Measure(new string('8', digits), _pixelsPerDip) ?? digits * 7) + 24);
        }
        else
        {
            _gutterWidth = 0;
        }

        InvalidateArrange();
        InvalidateVisual();
    }

    private static bool IsNumeric(GridColumnKind kind) => kind is GridColumnKind.Integer or GridColumnKind.Number;

    private double FilterIconWidth => ShowFilterButtons ? 22 : 0;

    private double MeasureColumn(int column, int sampleRows, double maxWidth)
    {
        if (_cellText is null || _headerText is null)
            return 120;
        double header = _headerText.Measure(_source.GetColumnName(column), _pixelsPerDip) + CellPadding + TypeTagWidth(column) + 6
                        + SortIconWidth + 4 + FilterIconWidth + 10;

        double content = 0;
        int count = _source.RowCount;
        int step = Math.Max(1, count / Math.Max(1, sampleRows));
        int samples = 0;
        for (int r = _firstRow; r < Math.Min(count, _firstRow + 60); r++)
            content = Math.Max(content, MeasureCell(r, column));
        for (int r = 0; r < count && samples < sampleRows; r += step, samples++)
            content = Math.Max(content, MeasureCell(r, column));

        double width = Math.Max(header, content + CellPadding * 2 + 2);
        return Math.Ceiling(Math.Clamp(width, 64, Math.Max(64, maxWidth)));
    }

    private double MeasureCell(int row, int column) =>
        _source.IsNull(row, column)
            ? (NullText.Length > 0 ? _cellText!.Measure(NullText, _pixelsPerDip) : 0)
            : _cellText!.Measure(_source.GetText(row, column), _pixelsPerDip, 160);

    private void EnsureRenderers()
    {
        if (_cellText is not null)
            return;
        var ui = new FontFamily("Segoe UI");
        _cellText = new GlyphTextRenderer(new Typeface(ui, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), 13);
        _headerText = new GlyphTextRenderer(new Typeface(ui, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal), 12.5);
        _smallText = new GlyphTextRenderer(new Typeface(ui, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), 11.5);
        _iconText = CreateIconRenderer(11);
    }

    private static GlyphTextRenderer CreateIconRenderer(double size)
    {
        foreach (var name in new[] { "Segoe Fluent Icons", "Segoe MDL2 Assets" })
        {
            var typeface = new Typeface(new FontFamily(name), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            if (typeface.TryGetGlyphTypeface(out var gt) && gt.CharacterToGlyphMap.ContainsKey(0xE71C))
                return new GlyphTextRenderer(typeface, size);
        }
        return new GlyphTextRenderer(new Typeface("Segoe UI"), size);
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        _pixelsPerDip = newDpi.PixelsPerDip;
        InvalidateVisual();
        base.OnDpiChanged(oldDpi, newDpi);
    }

    #endregion

    #region Layout / scrolling

    protected override int VisualChildrenCount => _children.Count;

    protected override Visual GetVisualChild(int index) => _children[index];

    protected override System.Collections.IEnumerator LogicalChildren => new object[] { _vbar, _hbar }.GetEnumerator();

    private static double ScrollBarSize => 12;

    private bool VBarVisible => _vbar.Visibility == Visibility.Visible;

    private bool HBarVisible => _hbar.Visibility == Visibility.Visible;

    private double DataRight => ActualWidth - (VBarVisible ? ScrollBarSize : 0);

    private double DataBottom => ActualHeight - (HBarVisible ? ScrollBarSize : 0);

    private int FullyVisibleRows => Math.Max(1, (int)Math.Floor((DataBottom - HeaderHeight) / RowHeight));

    protected override Size MeasureOverride(Size availableSize)
    {
        _vbar.Measure(availableSize);
        _hbar.Measure(availableSize);
        return new Size(double.IsInfinity(availableSize.Width) ? 600 : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? 400 : availableSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        double width = finalSize.Width, height = finalSize.Height;
        double totalWidth = _lefts[^1];

        bool needH = false, needV = false;
        for (int pass = 0; pass < 2; pass++)
        {
            double dataWidth = width - _gutterWidth - (needV ? ScrollBarSize : 0);
            double dataHeight = height - HeaderHeight - (needH ? ScrollBarSize : 0);
            needH = totalWidth > dataWidth + 0.5;
            needV = _source.RowCount * RowHeight > dataHeight + 0.5;
        }
        _vbar.Visibility = needV ? Visibility.Visible : Visibility.Collapsed;
        _hbar.Visibility = needH ? Visibility.Visible : Visibility.Collapsed;

        _vbar.Arrange(new Rect(width - ScrollBarSize, HeaderHeight, ScrollBarSize, Math.Max(0, height - HeaderHeight - (needH ? ScrollBarSize : 0))));
        _hbar.Arrange(new Rect(_gutterWidth, height - ScrollBarSize, Math.Max(0, width - _gutterWidth - (needV ? ScrollBarSize : 0)), ScrollBarSize));

        UpdateScrollBars(width, height);
        return finalSize;
    }

    private void UpdateScrollBars(double width, double height)
    {
        int visibleRows = Math.Max(1, (int)Math.Floor((height - HeaderHeight - (HBarVisible ? ScrollBarSize : 0)) / RowHeight));
        int maxFirst = Math.Max(0, _source.RowCount - visibleRows);
        _vbar.Maximum = maxFirst;
        _vbar.ViewportSize = visibleRows;
        _vbar.LargeChange = Math.Max(1, visibleRows - 1);
        if (_firstRow > maxFirst)
            _vbar.Value = maxFirst;

        double dataWidth = Math.Max(0, width - _gutterWidth - (VBarVisible ? ScrollBarSize : 0));
        double maxOffset = Math.Max(0, _lefts[^1] - dataWidth);
        _hbar.Maximum = maxOffset;
        _hbar.ViewportSize = dataWidth;
        _hbar.LargeChange = Math.Max(48, dataWidth * 0.9);
        if (_hOffset > maxOffset)
            _hbar.Value = maxOffset;
    }

    private void SetFirstRowCore(int value)
    {
        value = Math.Clamp(value, 0, Math.Max(0, _source.RowCount - 1));
        if (value == _firstRow) return;
        _firstRow = value;
        HideTip();
        InvalidateVisual();
    }

    private void SetHorizontalOffsetCore(double value)
    {
        if (Math.Abs(value - _hOffset) < 0.01) return;
        _hOffset = value;
        HideTip();
        InvalidateVisual();
    }

    private void ScrollToRow(int first) => _vbar.Value = Math.Clamp(first, 0, (int)_vbar.Maximum);

    private void EnsureVisible(int row, int col)
    {
        if (row >= 0)
        {
            int visible = FullyVisibleRows;
            if (row < _firstRow) ScrollToRow(row);
            else if (row >= _firstRow + visible) ScrollToRow(row - visible + 1);
        }
        if (col >= 0 && col < _display.Length)
        {
            double dataWidth = DataRight - _gutterWidth;
            double left = _lefts[col], right = _lefts[col + 1];
            if (left < _hOffset) _hbar.Value = left;
            else if (right > _hOffset + dataWidth) _hbar.Value = Math.Min(_hbar.Maximum, Math.Min(left, right - dataWidth));
        }
    }

    #endregion

    #region Rendering

    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth, height = ActualHeight;
        if (width <= 0 || height <= 0) return;
        EnsureRenderers();
        var cell = _cellText!;

        double dataRight = DataRight, dataBottom = DataBottom;
        double rowHeight = RowHeight;
        double ppd = _pixelsPerDip;
        double line = 1 / ppd;
        int rowCount = _source.RowCount;
        string nullText = NullText;

        dc.DrawRectangle(SurfaceBrush, null, new Rect(0, 0, width, height));

        int visibleRows = Math.Max(0, (int)Math.Ceiling((dataBottom - HeaderHeight) / rowHeight));
        int lastRow = Math.Min(rowCount - 1, _firstRow + visibleRows - 1);
        int rows = Math.Max(0, lastRow - _firstRow + 1);

        var (firstCol, lastCol) = VisibleColumnRange(dataRight);
        var selection = Selection;
        int selC1 = selection is null ? -1 : Math.Min(_anchorCol, _curCol);
        int selC2 = selection is null ? -1 : Math.Max(_anchorCol, _curCol);

        // ---- Row backgrounds, hover, selection ----
        dc.PushClip(new RectangleGeometry(new Rect(0, HeaderHeight, dataRight, Math.Max(0, dataBottom - HeaderHeight))));
        for (int i = 0; i < rows; i++)
        {
            int r = _firstRow + i;
            double y = HeaderHeight + i * rowHeight;
            if ((r & 1) == 1)
                dc.DrawRectangle(AltRowBrush, null, new Rect(0, y, dataRight, rowHeight));
            if (r == _hoverRow && _drag == DragMode.None)
                dc.DrawRectangle(HoverBrush, null, new Rect(0, y, dataRight, rowHeight));
        }

        if (selection is { } sel && rows > 0)
        {
            int r1 = Math.Max(sel.FirstRow, _firstRow), r2 = Math.Min(sel.LastRow, lastRow);
            if (r1 <= r2)
            {
                double y1 = HeaderHeight + (r1 - _firstRow) * rowHeight;
                double y2 = HeaderHeight + (r2 - _firstRow + 1) * rowHeight;
                double x1 = Math.Max(_gutterWidth, _gutterWidth + _lefts[selC1] - _hOffset);
                double x2 = Math.Min(dataRight, _gutterWidth + _lefts[selC2 + 1] - _hOffset);
                if (x2 > x1)
                    dc.DrawRectangle(SelectionBrush, null, new Rect(x1, y1, x2 - x1, y2 - y1));
            }
        }

        // ---- Cells ----
        dc.PushClip(new RectangleGeometry(new Rect(_gutterWidth, HeaderHeight, Math.Max(0, dataRight - _gutterWidth), Math.Max(0, dataBottom - HeaderHeight))));
        double textOffsetY = (rowHeight - cell.LineHeight) / 2 + cell.Ascent;
        var gridPen = new Pen(GridLineBrush, line);
        double rowsBottom = HeaderHeight + rows * rowHeight;

        for (int c = firstCol; c <= lastCol; c++)
        {
            int column = _display[c];
            var kind = _source.GetColumnKind(column);
            double x = _gutterWidth + _lefts[c] - _hOffset;
            double w = _widths[column];
            bool right = IsNumeric(kind);
            double textWidth = w - CellPadding * 2;

            for (int i = 0; i < rows; i++)
            {
                int r = _firstRow + i;
                double baseline = HeaderHeight + i * rowHeight + textOffsetY;
                if (_source.IsNull(r, column))
                {
                    if (nullText.Length > 0)
                        cell.Draw(dc, nullText, MutedTextBrush, x + CellPadding, baseline, textWidth, right, ppd);
                    continue;
                }
                string text = _source.GetText(r, column);
                Brush brush = TextBrush;
                if (kind == GridColumnKind.Boolean)
                    brush = IsTrue(text) ? TrueBrush : MutedTextBrush;
                cell.Draw(dc, text, brush, x + CellPadding, baseline, textWidth, right, ppd);
            }

            double lineX = Snap(x + w) - line / 2;
            dc.DrawLine(gridPen, new Point(lineX, HeaderHeight), new Point(lineX, rowsBottom));
        }

        for (int i = 1; i <= rows; i++)
        {
            double y = Snap(HeaderHeight + i * rowHeight) - line / 2;
            dc.DrawLine(gridPen, new Point(_gutterWidth, y), new Point(dataRight, y));
        }

        if (_curRow >= _firstRow && _curRow <= lastRow && _curCol >= 0 && _curCol < _display.Length)
        {
            double x = _gutterWidth + _lefts[_curCol] - _hOffset;
            double y = HeaderHeight + (_curRow - _firstRow) * rowHeight;
            var pen = new Pen(AccentBrush, IsKeyboardFocusWithin ? 2 : 1.2);
            dc.DrawRoundedRectangle(null, pen, new Rect(Snap(x) + 1, Snap(y) + 1, Math.Max(0, _widths[_display[_curCol]] - 2), rowHeight - 2), 2, 2);
        }
        dc.Pop();

        // ---- Gutter (row numbers) ----
        if (_gutterWidth > 0)
        {
            var small = _smallText!;
            var borderPen = new Pen(BorderBrush, line);
            dc.DrawRectangle(HeaderBrush, null, new Rect(0, HeaderHeight, _gutterWidth, Math.Max(0, dataBottom - HeaderHeight)));
            if (selection is { } s2)
            {
                int r1 = Math.Max(s2.FirstRow, _firstRow), r2 = Math.Min(s2.LastRow, lastRow);
                if (r1 <= r2)
                    dc.DrawRectangle(SelectionBrush, null, new Rect(0, HeaderHeight + (r1 - _firstRow) * rowHeight, _gutterWidth, (r2 - r1 + 1) * rowHeight));
            }
            double smallOffsetY = (rowHeight - small.LineHeight) / 2 + small.Ascent;
            for (int i = 0; i < rows; i++)
            {
                int r = _firstRow + i;
                var brush = r == _curRow ? SecondaryTextBrush : MutedTextBrush;
                small.Draw(dc, (r + 1).ToString("N0"), brush, 4, HeaderHeight + i * rowHeight + smallOffsetY, _gutterWidth - 14, true, ppd);
            }
            double gx = Snap(_gutterWidth) - line / 2;
            dc.DrawLine(borderPen, new Point(gx, HeaderHeight), new Point(gx, dataBottom));
        }
        dc.Pop();

        DrawHeader(dc, firstCol, lastCol, dataRight, line);

        if (VBarVisible && HBarVisible)
            dc.DrawRectangle(SurfaceBrush, null, new Rect(width - ScrollBarSize, height - ScrollBarSize, ScrollBarSize, ScrollBarSize));

        if (rowCount == 0 && _source.ColumnCount > 0)
        {
            string empty = GridStrings.NoRows;
            double tw = cell.Measure(empty, ppd);
            cell.Draw(dc, empty, MutedTextBrush, Math.Max(_gutterWidth + 16, (width - tw) / 2), HeaderHeight + 40, tw + 4, false, ppd);
        }
    }

    private static bool IsTrue(string text) =>
        text.Equals("true", StringComparison.OrdinalIgnoreCase) || text == "1" || text.Equals("evet", StringComparison.OrdinalIgnoreCase);

    private void DrawHeader(DrawingContext dc, int firstCol, int lastCol, double dataRight, double line)
    {
        var header = _headerText!;
        var icon = _iconText!;
        var small = _smallText!;
        double ppd = _pixelsPerDip;
        var borderPen = new Pen(BorderBrush, line);
        double filterWidth = FilterIconWidth;

        dc.DrawRectangle(HeaderBrush, null, new Rect(0, 0, ActualWidth, HeaderHeight));
        dc.PushClip(new RectangleGeometry(new Rect(_gutterWidth, 0, Math.Max(0, dataRight - _gutterWidth), HeaderHeight)));

        var sorts = Sorts ?? [];
        var filtered = FilteredColumns;
        double headerBaseline = (HeaderHeight - header.LineHeight) / 2 + header.Ascent;
        double iconBaseline = (HeaderHeight - icon.LineHeight) / 2 + icon.Ascent;
        double smallBaseline = (HeaderHeight - small.LineHeight) / 2 + small.Ascent;

        for (int c = firstCol; c <= lastCol; c++)
        {
            int column = _display[c];
            double x = _gutterWidth + _lefts[c] - _hOffset;
            double w = _widths[column];
            bool hovered = c == _hoverHeaderCol;

            if (hovered && _drag == DragMode.None)
                dc.DrawRectangle(HoverBrush, null, new Rect(x, 0, w, HeaderHeight));

            double tx = x + CellPadding;
            DrawTypeTag(dc, column, tx, smallBaseline, iconBaseline);
            tx += TypeTagWidth(column) + 6;

            double rightEdge = x + w - 6;
            bool isFiltered = filtered?.Contains(column) == true;
            if (filterWidth > 0)
            {
                if (isFiltered || hovered)
                {
                    var brush = isFiltered ? AccentBrush : (_hoverFilterIcon ? TextBrush : MutedTextBrush);
                    if (isFiltered || _hoverFilterIcon)
                        dc.DrawRoundedRectangle(isFiltered ? AccentSoftBrush : HoverBrush, null,
                            new Rect(rightEdge - filterWidth + 1, 7, filterWidth - 2, HeaderHeight - 14), 4, 4);
                    icon.Draw(dc, Icons.Filter, brush, rightEdge - filterWidth + 5, iconBaseline, filterWidth, false, ppd);
                }
                rightEdge -= filterWidth;
            }

            int sortIndex = -1;
            for (int i = 0; i < sorts.Count; i++)
            {
                if (sorts[i].Column == column) { sortIndex = i; break; }
            }
            if (sortIndex >= 0)
            {
                string arrow = sorts[sortIndex].Direction == GridSortDirection.Ascending ? Icons.SortUp : Icons.SortDown;
                double sw = SortIconWidth;
                if (sorts.Count > 1)
                {
                    small.Draw(dc, (sortIndex + 1).ToString(), AccentBrush, rightEdge - 8, smallBaseline, 10, false, ppd);
                    sw += 2;
                }
                icon.Draw(dc, arrow, AccentBrush, rightEdge - sw, iconBaseline, 14, false, ppd);
                rightEdge -= sw + 4;
            }

            header.Draw(dc, _source.GetColumnName(column), sortIndex >= 0 || isFiltered ? TextBrush : SecondaryTextBrush, tx, headerBaseline,
                Math.Max(0, rightEdge - tx - 2), false, ppd);

            double lineX = Snap(x + w) - line / 2;
            dc.DrawLine(borderPen, new Point(lineX, 8), new Point(lineX, HeaderHeight - 8));
        }
        dc.Pop();

        double by = Snap(HeaderHeight) - line / 2;
        dc.DrawLine(borderPen, new Point(0, by), new Point(ActualWidth, by));
        if (_gutterWidth > 0)
        {
            double gx = Snap(_gutterWidth) - line / 2;
            dc.DrawLine(borderPen, new Point(gx, 0), new Point(gx, HeaderHeight));
        }
    }

    private double TypeTagWidth(int column) => _source.GetColumnKind(column) switch
    {
        GridColumnKind.DateTime or GridColumnKind.Boolean => 13,
        var kind => _smallText?.Measure(TypeTag(kind), _pixelsPerDip) ?? 14,
    };

    private static string TypeTag(GridColumnKind kind) => kind switch
    {
        GridColumnKind.Integer => "#",
        GridColumnKind.Number => "1.2",
        _ => "Aa",
    };

    private void DrawTypeTag(DrawingContext dc, int column, double x, double smallBaseline, double iconBaseline)
    {
        switch (_source.GetColumnKind(column))
        {
            case GridColumnKind.DateTime:
                _iconText!.Draw(dc, Icons.Calendar, MutedTextBrush, x, iconBaseline, 16, false, _pixelsPerDip);
                break;
            case GridColumnKind.Boolean:
                _iconText!.Draw(dc, Icons.Check, MutedTextBrush, x, iconBaseline, 16, false, _pixelsPerDip);
                break;
            case var kind:
                _smallText!.Draw(dc, TypeTag(kind), MutedTextBrush, x, smallBaseline, 30, false, _pixelsPerDip);
                break;
        }
    }

    private (int First, int Last) VisibleColumnRange(double dataRight)
    {
        if (_display.Length == 0)
            return (0, -1);
        int first = Array.BinarySearch(_lefts, _hOffset);
        first = first >= 0 ? first : ~first - 1;
        first = Math.Clamp(first, 0, _display.Length - 1);
        int last = first;
        while (last + 1 < _display.Length && _gutterWidth + _lefts[last + 1] - _hOffset < dataRight)
            last++;
        return (first, last);
    }

    private double Snap(double value) => Math.Round(value * _pixelsPerDip) / _pixelsPerDip;

    #endregion

    #region Hit testing

    private (Area Area, int Row, int Col) HitTest(Point p, bool clamp = false)
    {
        double dataRight = DataRight, dataBottom = DataBottom;
        if (!clamp && (p.X >= dataRight || p.Y >= dataBottom))
            return (Area.None, -1, -1);

        int col = ColumnAt(p.X, clamp);
        if (p.Y < HeaderHeight && !clamp)
            return p.X < _gutterWidth ? (Area.Corner, -1, -1) : (Area.Header, -1, col);

        int row = _firstRow + (int)Math.Floor((p.Y - HeaderHeight) / RowHeight);
        if (clamp)
            row = Math.Clamp(row, 0, Math.Max(0, _source.RowCount - 1));
        else if (row >= _source.RowCount)
            row = -1;

        if (p.X < _gutterWidth && !clamp)
            return (Area.Gutter, row, -1);
        return row < 0 ? (Area.None, -1, col) : (Area.Cell, row, col);
    }

    private int ColumnAt(double x, bool clamp)
    {
        if (_display.Length == 0) return -1;
        double content = x - _gutterWidth + _hOffset;
        if (content < 0) return clamp ? 0 : -1;
        int i = Array.BinarySearch(_lefts, content);
        i = i >= 0 ? i : ~i - 1;
        if (i >= _display.Length) return clamp ? _display.Length - 1 : -1;
        return Math.Max(0, i);
    }

    private int ResizeColumnAt(double x)
    {
        var (first, last) = VisibleColumnRange(DataRight);
        for (int c = first; c <= last; c++)
        {
            double edge = _gutterWidth + _lefts[c + 1] - _hOffset;
            if (Math.Abs(x - edge) <= ResizeGrip)
                return c;
        }
        return -1;
    }

    private Rect HeaderRect(int displayCol)
    {
        double x = _gutterWidth + _lefts[displayCol] - _hOffset;
        return new Rect(x, 0, _widths[_display[displayCol]], HeaderHeight);
    }

    private bool IsOverFilterIcon(Point p, int displayCol)
    {
        if (!ShowFilterButtons) return false;
        var rect = HeaderRect(displayCol);
        return p.X >= rect.Right - 6 - FilterIconWidth && p.X <= rect.Right - 4;
    }

    #endregion

    #region Mouse

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var p = e.GetPosition(this);
        _lastMouse = p;

        switch (_drag)
        {
            case DragMode.Resize:
                _widths[_display[_resizeCol]] = Math.Max(MinColumnWidth, _resizeStartWidth + p.X - _resizeStartX);
                RecomputeLayout();
                return;
            case DragMode.Select:
            case DragMode.RowSelect:
            {
                var hit = HitTest(p, clamp: true);
                bool outside = p.Y < HeaderHeight || p.Y > DataBottom || p.X < _gutterWidth || p.X > DataRight;
                if (outside && !_autoScroll.IsEnabled) _autoScroll.Start();
                else if (!outside) _autoScroll.Stop();
                ExtendSelection(hit.Row, _drag == DragMode.RowSelect ? _display.Length - 1 : hit.Col);
                return;
            }
        }

        var h = HitTest(p);
        int hoverRow = h.Area is Area.Cell or Area.Gutter ? h.Row : -1;
        int hoverHeader = h.Area == Area.Header ? h.Col : -1;
        bool overFilter = hoverHeader >= 0 && IsOverFilterIcon(p, hoverHeader);
        int resize = p.Y < HeaderHeight ? ResizeColumnAt(p.X) : -1;
        Cursor = resize >= 0 ? Cursors.SizeWE : null;

        if (hoverRow != _hoverRow || hoverHeader != _hoverHeaderCol || overFilter != _hoverFilterIcon)
        {
            _hoverRow = hoverRow;
            _hoverHeaderCol = hoverHeader;
            _hoverFilterIcon = overFilter;
            InvalidateVisual();
        }

        if (h.Area == Area.Cell && (h.Row, h.Col) != _tipCell)
        {
            HideTip();
            _tipCell = (h.Row, h.Col);
            _tipTimer.Start();
        }
        else if (h.Area != Area.Cell)
        {
            HideTip();
            _tipCell = (-1, -1);
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoverRow != -1 || _hoverHeaderCol != -1)
        {
            _hoverRow = _hoverHeaderCol = -1;
            _hoverFilterIcon = false;
            InvalidateVisual();
        }
        HideTip();
        _tipCell = (-1, -1);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        HideTip();
        var p = e.GetPosition(this);
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        var hit = HitTest(p);

        if (p.Y < HeaderHeight)
        {
            int resize = ResizeColumnAt(p.X);
            if (resize >= 0)
            {
                if (e.ClickCount == 2)
                {
                    _widths[_display[resize]] = MeasureColumn(_display[resize], sampleRows: 2000, maxWidth: 800);
                    RecomputeLayout();
                }
                else
                {
                    _drag = DragMode.Resize;
                    _resizeCol = resize;
                    _resizeStartX = p.X;
                    _resizeStartWidth = _widths[_display[resize]];
                    CaptureMouse();
                }
                e.Handled = true;
                return;
            }
        }

        switch (hit.Area)
        {
            case Area.Corner:
                SelectAll();
                break;
            case Area.Header when hit.Col >= 0:
                if (IsOverFilterIcon(p, hit.Col))
                    FilterRequested?.Invoke(_display[hit.Col], HeaderRect(hit.Col));
                else
                    SortRequested?.Invoke(_display[hit.Col], shift);
                break;
            case Area.Gutter when hit.Row >= 0:
                if (!shift || _anchorRow < 0) _anchorRow = hit.Row;
                _anchorCol = 0;
                SetCurrent(hit.Row, _display.Length - 1, keepAnchor: true);
                _drag = DragMode.RowSelect;
                CaptureMouse();
                break;
            case Area.Cell when hit.Col >= 0:
                SetCurrent(hit.Row, hit.Col, keepAnchor: shift);
                _drag = DragMode.Select;
                CaptureMouse();
                break;
        }
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_drag != DragMode.None)
        {
            bool selecting = _drag is DragMode.Select or DragMode.RowSelect;
            _drag = DragMode.None;
            _autoScroll.Stop();
            ReleaseMouseCapture();
            if (selecting) SelectionChanged?.Invoke();
        }
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        _drag = DragMode.None;
        _autoScroll.Stop();
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        var hit = HitTest(e.GetPosition(this));
        if (hit.Area == Area.Header && hit.Col >= 0)
        {
            ShowHeaderMenu(hit.Col);
            e.Handled = true;
        }
        else if (hit.Area == Area.Cell && hit.Col >= 0)
        {
            if (!IsInSelection(hit.Row, hit.Col))
                SetCurrent(hit.Row, hit.Col, keepAnchor: false);
            ShowCellMenu(hit.Row, hit.Col);
            e.Handled = true;
        }
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        HideTip();
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            _hbar.Value = Math.Clamp(_hbar.Value - e.Delta / 120.0 * 80, 0, _hbar.Maximum);
        }
        else
        {
            int lines = SystemParameters.WheelScrollLines > 0 ? SystemParameters.WheelScrollLines : 3;
            ScrollToRow(_firstRow - e.Delta / 120 * lines);
        }
        e.Handled = true;
    }

    private void AutoScrollTick()
    {
        if (_drag is not (DragMode.Select or DragMode.RowSelect))
        {
            _autoScroll.Stop();
            return;
        }
        var p = _lastMouse;
        if (p.Y < HeaderHeight) ScrollToRow(_firstRow - Math.Max(1, (int)((HeaderHeight - p.Y) / 8)));
        else if (p.Y > DataBottom) ScrollToRow(_firstRow + Math.Max(1, (int)((p.Y - DataBottom) / 8)));
        if (p.X < _gutterWidth) _hbar.Value = Math.Max(0, _hbar.Value - 30);
        else if (p.X > DataRight) _hbar.Value = Math.Min(_hbar.Maximum, _hbar.Value + 30);

        var hit = HitTest(p, clamp: true);
        ExtendSelection(hit.Row, _drag == DragMode.RowSelect ? _display.Length - 1 : hit.Col);
    }

    #endregion

    #region Selection / keyboard

    private bool IsInSelection(int row, int col) =>
        row >= Math.Min(_anchorRow, _curRow) && row <= Math.Max(_anchorRow, _curRow) &&
        col >= Math.Min(_anchorCol, _curCol) && col <= Math.Max(_anchorCol, _curCol);

    private void SetCurrent(int row, int col, bool keepAnchor)
    {
        if (_source.RowCount == 0 || _display.Length == 0) return;
        row = Math.Clamp(row, 0, _source.RowCount - 1);
        col = Math.Clamp(col, 0, _display.Length - 1);
        bool changed = row != _curRow || col != _curCol;
        _curRow = row;
        _curCol = col;
        if (!keepAnchor || _anchorRow < 0)
        {
            _anchorRow = row;
            _anchorCol = col;
        }
        EnsureVisible(row, col);
        InvalidateVisual();
        if (changed) CurrentCellChanged?.Invoke();
        if (_drag == DragMode.None) SelectionChanged?.Invoke();
    }

    private void ExtendSelection(int row, int col)
    {
        if (row < 0 || col < 0) return;
        if (row == _curRow && col == _curCol) return;
        _curRow = row;
        _curCol = col;
        EnsureVisible(row, col);
        InvalidateVisual();
        CurrentCellChanged?.Invoke();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_source.RowCount == 0 || _display.Length == 0) return;

        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        int row = Math.Max(0, _curRow), col = Math.Max(0, _curCol);
        int page = Math.Max(1, FullyVisibleRows - 1);

        switch (e.Key)
        {
            case Key.Up: row = ctrl ? 0 : row - 1; break;
            case Key.Down: row = ctrl ? _source.RowCount - 1 : row + 1; break;
            case Key.Left: col = ctrl ? 0 : col - 1; break;
            case Key.Right: col = ctrl ? _display.Length - 1 : col + 1; break;
            case Key.Tab: col = shift ? col - 1 : col + 1; shift = false; break;
            case Key.PageUp: row -= page; break;
            case Key.PageDown: row += page; break;
            case Key.Home:
                if (ctrl) row = 0;
                col = 0;
                break;
            case Key.End:
                if (ctrl) row = _source.RowCount - 1;
                col = _display.Length - 1;
                break;
            case Key.A when ctrl:
                SelectAll();
                e.Handled = true;
                return;
            case Key.C when ctrl:
                Copy(shift);
                e.Handled = true;
                return;
            case Key.Apps:
                ShowCellMenu(row, col);
                e.Handled = true;
                return;
            default:
                return;
        }

        SetCurrent(row, col, keepAnchor: shift);
        e.Handled = true;
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        InvalidateVisual();
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        InvalidateVisual();
    }

    #endregion

    #region Tooltip

    private void ShowCellTip()
    {
        _tipTimer.Stop();
        var (row, col) = _tipCell;
        if (row < 0 || col < 0 || row >= _source.RowCount || col >= _display.Length || _drag != DragMode.None)
            return;
        int column = _display[col];
        if (_source.IsNull(row, column) || _cellText is null)
            return;
        string text = _source.GetText(row, column);
        if (text.Length == 0)
            return;
        double available = _widths[column] - CellPadding * 2;
        if (!text.Contains('\n') && _cellText.Measure(text, _pixelsPerDip, 2000) <= available)
            return;

        _tip.Content = text.Length > 4000 ? text[..4000] + "…" : text;
        _tip.HorizontalOffset = Math.Max(_gutterWidth, _gutterWidth + _lefts[col] - _hOffset);
        _tip.VerticalOffset = HeaderHeight + (row - _firstRow + 1) * RowHeight + 2;
        _tip.IsOpen = true;
    }

    private void HideTip()
    {
        _tipTimer.Stop();
        if (_tip.IsOpen) _tip.IsOpen = false;
    }

    #endregion

    #region Context menus

    private static MenuItem Item(string header, string icon, Action action, string? gesture = null, bool enabled = true)
    {
        var item = new MenuItem { Header = header, InputGestureText = gesture ?? string.Empty, IsEnabled = enabled };
        Ui.SetIcon(item, icon);
        item.Click += (_, _) => action();
        return item;
    }

    private void ShowHeaderMenu(int displayCol)
    {
        int column = _display[displayCol];
        bool sorted = Sorts?.Any(s => s.Column == column) == true;
        var menu = new ContextMenu { PlacementTarget = this };
        menu.Items.Add(Item(GridStrings.SortAscending, Icons.SortUp, () => SortDirectionRequested?.Invoke(column, GridSortDirection.Ascending)));
        menu.Items.Add(Item(GridStrings.SortDescending, Icons.SortDown, () => SortDirectionRequested?.Invoke(column, GridSortDirection.Descending)));
        menu.Items.Add(Item(GridStrings.ClearSort, Icons.Clear, () => SortDirectionRequested?.Invoke(column, null), enabled: sorted));
        if (ShowFilterButtons)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(Item(GridStrings.Filter, Icons.Filter, () => FilterRequested?.Invoke(column, HeaderRect(displayCol))));
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(GridStrings.FitColumn, Icons.Fit, () =>
        {
            _widths[column] = MeasureColumn(column, 2000, 800);
            RecomputeLayout();
        }));
        menu.Items.Add(Item(GridStrings.FitAllColumns, "", AutoFitAllColumns));
        if (HideColumnRequested is not null)
            menu.Items.Add(Item(GridStrings.HideColumn, Icons.Hide, () => HideColumnRequested?.Invoke(column), enabled: _display.Length > 1));
        menu.Items.Add(Item(GridStrings.CopyColumnName, Icons.Copy, () => TrySetClipboard(_source.GetColumnName(column))));
        menu.IsOpen = true;
    }

    private void ShowCellMenu(int row, int displayCol)
    {
        int column = _display[displayCol];
        var menu = new ContextMenu { PlacementTarget = this };
        menu.Items.Add(Item(GridStrings.Copy, Icons.Copy, () => Copy(false), "Ctrl+C"));
        menu.Items.Add(Item(GridStrings.CopyWithHeaders, Icons.Copy, () => Copy(true), "Ctrl+Shift+C"));
        if (FilterByValueRequested is not null)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(Item(GridStrings.FilterByValue, Icons.Filter, () => FilterByValueRequested?.Invoke(column, row, false)));
            menu.Items.Add(Item(GridStrings.ExcludeValue, "", () => FilterByValueRequested?.Invoke(column, row, true)));
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(GridStrings.SelectAll, Icons.SelectAll, SelectAll, "Ctrl+A"));
        CellMenuOpening?.Invoke(menu, row, column);
        menu.IsOpen = true;
    }

    /// <summary>Raises <see cref="CopyRequested"/>, or copies the selection directly when nobody handles it.</summary>
    private void Copy(bool withHeaders)
    {
        if (CopyRequested is not null) CopyRequested(withHeaders);
        else TrySetClipboard(GetSelectionText(withHeaders));
    }

    private static void TrySetClipboard(string text)
    {
        try
        {
            Clipboard.SetDataObject(text, true);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
        }
    }

    #endregion
}
