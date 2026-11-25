using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using JetQueries.Presentation.ViewModels;
using UiKit.Theming;

namespace JetQueries.Wpf.Views;

/// <summary>
/// The colours of chart series, by position, for the light and the dark theme: the validated categorical palette
/// (blue, orange; checked for colour-vision deficiency and contrast against the UiKit surfaces). Text never wears
/// them: values and labels stay in the text brushes.
/// </summary>
internal static class ChartPalette
{
    private static readonly Color[] Light = [Color.FromRgb(0x2a, 0x78, 0xd6), Color.FromRgb(0xeb, 0x68, 0x34)];
    private static readonly Color[] Dark = [Color.FromRgb(0x39, 0x87, 0xe5), Color.FromRgb(0xd9, 0x59, 0x26)];

    public static Brush Series(int index)
    {
        var colours = ThemeManager.Current == AppTheme.Dark ? Dark : Light;
        var brush = new SolidColorBrush(colours[Math.Min(index, colours.Length - 1)]);
        brush.Freeze();
        return brush;
    }
}

/// <summary>Shared drawing of the charts: theme brushes, text, repainting on a theme change.</summary>
public abstract class ChartBase : FrameworkElement
{
    protected ChartBase()
    {
        Loaded += (_, _) => ThemeManager.ThemeChanged += InvalidateVisual;
        Unloaded += (_, _) => ThemeManager.ThemeChanged -= InvalidateVisual;
        SnapsToDevicePixels = true;
    }

    /// <summary>Text shown when there is nothing to draw.</summary>
    public string EmptyText
    {
        get => (string)GetValue(EmptyTextProperty);
        set => SetValue(EmptyTextProperty, value);
    }

    public static readonly DependencyProperty EmptyTextProperty =
        DependencyProperty.Register(nameof(EmptyText), typeof(string), typeof(ChartBase), new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsRender));

    protected Brush Resource(string key) => TryFindResource(key) as Brush ?? Brushes.Gray;

    protected FormattedText Text(string text, Brush brush, double size = 11.5, bool bold = false) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(TextElement.GetFontFamily(this), FontStyles.Normal, bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal),
            size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    protected void DrawEmpty(DrawingContext dc)
    {
        if (EmptyText.Length > 0)
        {
            var text = Text(EmptyText, Resource("Brush.TextMuted"));
            dc.DrawText(text, new Point((ActualWidth - text.Width) / 2, (ActualHeight - text.Height) / 2));
        }
    }
}

/// <summary>
/// A time series chart on one axis: 2px lines, a recessive grid, the legend with each series' latest value, and on
/// hover a crosshair with every series' value at that time.
/// </summary>
public sealed class LineChart : ChartBase
{
    private const double Left = 44;
    private const double Right = 10;
    private const double Bottom = 20;
    private Point? _mouse;

    public LineChart()
    {
        Cursor = Cursors.Cross;
    }

    public IReadOnlyList<ChartSeries>? Series
    {
        get => (IReadOnlyList<ChartSeries>?)GetValue(SeriesProperty);
        set => SetValue(SeriesProperty, value);
    }

    public static readonly DependencyProperty SeriesProperty =
        DependencyProperty.Register(nameof(Series), typeof(IReadOnlyList<ChartSeries>), typeof(LineChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>The top of the axis; NaN fits the data.</summary>
    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public static readonly DependencyProperty MaximumProperty =
        DependencyProperty.Register(nameof(Maximum), typeof(double), typeof(LineChart), new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>How values are written ("0", "0.0").</summary>
    public string Format
    {
        get => (string)GetValue(FormatProperty);
        set => SetValue(FormatProperty, value);
    }

    public static readonly DependencyProperty FormatProperty =
        DependencyProperty.Register(nameof(Format), typeof(string), typeof(LineChart), new FrameworkPropertyMetadata("0", FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Written after values (" %").</summary>
    public string Unit
    {
        get => (string)GetValue(UnitProperty);
        set => SetValue(UnitProperty, value);
    }

    public static readonly DependencyProperty UnitProperty =
        DependencyProperty.Register(nameof(Unit), typeof(string), typeof(LineChart), new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsRender));

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        _mouse = e.GetPosition(this);
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _mouse = null;
        InvalidateVisual();
    }

    private string Value(double value) => value.ToString(Format, CultureInfo.CurrentCulture) + Unit;

    protected override void OnRender(DrawingContext drawingContext)
    {
        var dc = drawingContext;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        var series = (Series ?? []).Where(static s => s.Points.Count > 0).ToList();
        if (series.Count == 0 || ActualWidth < Left + Right + 20 || ActualHeight < Bottom + 30)
        {
            DrawEmpty(dc);
            return;
        }

        var secondary = Resource("Brush.TextSecondary");
        var muted = Resource("Brush.TextMuted");
        var grid = new Pen(Resource("Brush.Border"), 1);
        var surface = Resource("Brush.Surface");

        // Legend: a short line in the series' colour, its name and its latest value (identity is never colour alone).
        var top = 0.0;
        var x = Left;
        for (var i = 0; i < series.Count; i++)
        {
            var label = Text(series[i].Name + "  " + Value(series[i].Points[^1].Value), secondary);
            dc.DrawLine(new Pen(ChartPalette.Series(i), 3) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, new Point(x, 8), new Point(x + 14, 8));
            dc.DrawText(label, new Point(x + 20, 8 - label.Height / 2));
            x += 20 + label.Width + 18;
        }

        top = 22;
        var plot = new Rect(Left, top, ActualWidth - Left - Right, ActualHeight - top - Bottom);
        var start = series.Min(static s => s.Points[0].At);
        var end = series.Max(static s => s.Points[^1].At);
        var span = Math.Max(1, (end - start).TotalSeconds);
        var max = double.IsNaN(Maximum) ? Nice(series.Max(static s => s.Points.Max(static p => p.Value))) : Maximum;
        double X(DateTimeOffset at) => plot.Left + (at - start).TotalSeconds / span * plot.Width;
        double Y(double value) => plot.Bottom - Math.Clamp(value / max, 0, 1) * plot.Height;

        // Grid: three recessive lines with their values.
        foreach (var fraction in new[] { 0, 0.5, 1 })
        {
            var y = Math.Round(plot.Bottom - fraction * plot.Height) + 0.5;
            dc.DrawLine(grid, new Point(plot.Left, y), new Point(plot.Right, y));
            var text = Text(Value(max * fraction), muted, 10.5);
            dc.DrawText(text, new Point(plot.Left - text.Width - 6, y - text.Height / 2));
        }

        var first = Text(start.ToLocalTime().ToString("t", CultureInfo.CurrentCulture), muted, 10.5);
        var last = Text(end.ToLocalTime().ToString("t", CultureInfo.CurrentCulture), muted, 10.5);
        dc.DrawText(first, new Point(plot.Left, plot.Bottom + 4));
        dc.DrawText(last, new Point(plot.Right - last.Width, plot.Bottom + 4));

        // The lines, 2px; a single point is a dot.
        for (var i = 0; i < series.Count; i++)
        {
            var brush = ChartPalette.Series(i);
            var points = series[i].Points;
            if (points.Count == 1)
            {
                dc.DrawEllipse(brush, new Pen(surface, 2), new Point(X(points[0].At), Y(points[0].Value)), 4, 4);
                continue;
            }

            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                context.BeginFigure(new Point(X(points[0].At), Y(points[0].Value)), false, false);
                context.PolyLineTo([.. points.Skip(1).Select(p => new Point(X(p.At), Y(p.Value)))], true, true);
            }

            geometry.Freeze();
            dc.DrawGeometry(null, new Pen(brush, 2) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, geometry);
        }

        // Hover: the crosshair at the nearest time, a marker on each series and their values.
        if (_mouse is { } mouse && plot.Contains(mouse))
        {
            var at = start + TimeSpan.FromSeconds((mouse.X - plot.Left) / plot.Width * span);
            var nearest = series[0].Points.MinBy(p => Math.Abs((p.At - at).TotalSeconds));
            var cx = Math.Round(X(nearest.At)) + 0.5;
            dc.DrawLine(new Pen(muted, 1), new Point(cx, plot.Top), new Point(cx, plot.Bottom));
            var lines = new List<(Brush Brush, FormattedText Text)>();
            for (var i = 0; i < series.Count; i++)
            {
                var point = series[i].Points.MinBy(p => Math.Abs((p.At - nearest.At).TotalSeconds));
                dc.DrawEllipse(ChartPalette.Series(i), new Pen(surface, 2), new Point(X(point.At), Y(point.Value)), 4, 4);
                lines.Add((ChartPalette.Series(i), Text(series[i].Name + "  " + Value(point.Value), Resource("Brush.Text"))));
            }

            var time = Text(nearest.At.ToLocalTime().ToString("T", CultureInfo.CurrentCulture), secondary, 11, bold: true);
            var width = Math.Max(time.Width, lines.Max(static l => l.Text.Width) + 16) + 16;
            var height = 10 + time.Height + lines.Sum(static l => l.Text.Height + 2) + 6;
            var bx = cx + 12 + width > plot.Right ? cx - 12 - width : cx + 12;
            var box = new Rect(Math.Max(0, bx), plot.Top + 4, width, height);
            dc.DrawRoundedRectangle(Resource("Brush.Card"), new Pen(Resource("Brush.BorderStrong"), 1), box, 6, 6);
            dc.DrawText(time, new Point(box.Left + 8, box.Top + 6));
            var y = box.Top + 8 + time.Height;
            foreach (var (brush, text) in lines)
            {
                dc.DrawRectangle(brush, null, new Rect(box.Left + 8, y + text.Height / 2 - 1.5, 10, 3));
                dc.DrawText(text, new Point(box.Left + 24, y));
                y += text.Height + 2;
            }
        }
    }

    // The next round number above the largest value (1, 2, 2.5, 5 × a power of ten).
    internal static double Nice(double value)
    {
        if (value <= 0 || double.IsNaN(value))
        {
            return 1;
        }

        var target = value * 1.1;
        var power = Math.Pow(10, Math.Floor(Math.Log10(target)));
        foreach (var step in new[] { 1, 2, 2.5, 5, 10 })
        {
            if (step * power >= target)
            {
                return step * power;
            }
        }

        return 10 * power;
    }
}

/// <summary>
/// Horizontal bars of one series, longest first: the label on the left, a 10px bar anchored at its start with a
/// rounded end, the value written after it; the row under the mouse is tinted.
/// </summary>
public sealed class BarList : ChartBase
{
    private const double RowHeight = 24;
    private int _hover = -1;

    public IReadOnlyList<BarItem>? Items
    {
        get => (IReadOnlyList<BarItem>?)GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    public static readonly DependencyProperty ItemsProperty =
        DependencyProperty.Register(nameof(Items), typeof(IReadOnlyList<BarItem>), typeof(BarList), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var row = (int)(e.GetPosition(this).Y / RowHeight);
        if (row != _hover)
        {
            _hover = row;
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = -1;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var dc = drawingContext;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        var items = Items ?? [];
        if (items.Count == 0)
        {
            DrawEmpty(dc);
            return;
        }

        var text = Resource("Brush.Text");
        var secondary = Resource("Brush.TextSecondary");
        var labels = items.Select(i => Text(i.Label, text, 11)).ToList();
        var values = items.Select(i => Text(i.Text, secondary, 11)).ToList();
        var labelWidth = Math.Min(ActualWidth * 0.45, labels.Max(static l => l.Width) + 12);
        var valueWidth = values.Max(static v => v.Width) + 8;
        var barSpace = Math.Max(10, ActualWidth - labelWidth - valueWidth - 4);
        var max = items.Max(static i => i.Value);
        var bar = ChartPalette.Series(0);
        for (var i = 0; i < items.Count && (i + 1) * RowHeight <= ActualHeight + 1; i++)
        {
            var top = i * RowHeight;
            if (i == _hover)
            {
                dc.DrawRoundedRectangle(Resource("Brush.Hover"), null, new Rect(0, top, ActualWidth, RowHeight), 4, 4);
            }

            labels[i].MaxTextWidth = Math.Max(10, labelWidth - 12);
            labels[i].Trimming = TextTrimming.CharacterEllipsis;
            labels[i].MaxLineCount = 1;
            dc.DrawText(labels[i], new Point(4, top + (RowHeight - labels[i].Height) / 2));
            var length = Math.Max(4, items[i].Value / max * barSpace);
            dc.DrawGeometry(bar, null, RightRounded(new Rect(labelWidth, top + (RowHeight - 10) / 2, length, 10), 4));
            dc.DrawText(values[i], new Point(labelWidth + length + 6, top + (RowHeight - values[i].Height) / 2));
        }
    }

    protected override Size MeasureOverride(Size availableSize) => new(0, Math.Max(RowHeight, (Items?.Count ?? 0) * RowHeight));

    // A bar anchored at the left (square) with its data end rounded.
    private static StreamGeometry RightRounded(Rect rect, double radius)
    {
        var r = Math.Min(radius, Math.Min(rect.Width, rect.Height) / 2);
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(rect.TopLeft, true, true);
            context.LineTo(new Point(rect.Right - r, rect.Top), false, false);
            context.ArcTo(new Point(rect.Right, rect.Top + r), new Size(r, r), 0, false, SweepDirection.Clockwise, false, false);
            context.LineTo(new Point(rect.Right, rect.Bottom - r), false, false);
            context.ArcTo(new Point(rect.Right - r, rect.Bottom), new Size(r, r), 0, false, SweepDirection.Clockwise, false, false);
            context.LineTo(rect.BottomLeft, false, false);
        }

        geometry.Freeze();
        return geometry;
    }
}
