using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using JetQueries.DataGrid.Spike;

namespace JetQueries.Desktop.Views;

/// <summary>
/// Phase 0 spike harness: shows the virtualized grid over one million synthetic rows and measures real frame
/// pacing in the running window, because 60 fps can only be judged on an actual display.
/// </summary>
public partial class GridSpikeView : UserControl
{
    private const int FramesPerPhase = 300;
    private const int WarmUpFrames = 120;
    private const int StreamBatchRows = 1000;

    /// <summary>
    /// <c>--grid-benchmark=&lt;file&gt;</c>: run the scroll benchmark right after start-up, write the result to the
    /// file and exit. Lets the spike be measured on a real display without anyone clicking.
    /// </summary>
    private const string BenchmarkSwitch = "--grid-benchmark=";

    private static readonly CompositeFormat RowCountFormat =
        CompositeFormat.Parse(JetQueries.Presentation.Resources.Strings.GridSpikeRowCount);

    private static readonly string[] PhaseNames = ["tiny redraw (one button)", "redraw (same cells)", "smooth", "page jumps", "horizontal"];

    private readonly SyntheticRowSource _source = new(1_000_000, 30);
    private readonly DispatcherTimer _streamTimer;
    private readonly double[] _intervals = new double[PhaseNames.Length * FramesPerPhase];
    private readonly double[] _records = new double[PhaseNames.Length * FramesPerPhase];
    private int _frame = -1;
    private int _warmUp;
    private double _horizontalDirection = 1;
    private TimeSpan _lastTimestamp;
    private string? _reportPath;

    public GridSpikeView()
    {
        InitializeComponent();
        ResultGrid.Source = _source;
        ShowRowCount();

        // One batch per ~16 ms: the cadence at which the real executor will hand row batches to the grid.
        _streamTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(16) };
        _streamTimer.Tick += OnStreamTick;
        StreamToggle.IsCheckedChanged += (_, _) => _streamTimer.IsEnabled = StreamToggle.IsChecked == true;
        BenchmarkButton.Click += (_, _) => StartBenchmark();
    }

    private bool BenchmarkRunning => _frame >= 0;

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _reportPath = Environment.GetCommandLineArgs()
            .FirstOrDefault(static a => a.StartsWith(BenchmarkSwitch, StringComparison.Ordinal))?[BenchmarkSwitch.Length..];
        if (_reportPath is not null)
        {
            // Give the window two seconds to finish its first layout and start-up work.
            DispatcherTimer.RunOnce(StartBenchmark, TimeSpan.FromSeconds(2));
        }
    }

    private void OnStreamTick(object? sender, EventArgs e)
    {
        if (_source.RowCount > int.MaxValue - StreamBatchRows)
        {
            StreamToggle.IsChecked = false;
            return;
        }

        _source.AppendRows(StreamBatchRows);
        if (!BenchmarkRunning)
        {
            ShowRowCount();
        }
    }

    private void ShowRowCount() =>
        StatsText.Text = string.Format(CultureInfo.CurrentCulture, RowCountFormat, _source.RowCount, _source.Columns.Count);

    private void StartBenchmark()
    {
        if (BenchmarkRunning || TopLevel.GetTopLevel(this) is not { } topLevel)
        {
            return;
        }

        BenchmarkButton.IsEnabled = false;
        ResultGrid.Offset = default;
        _frame = 0;
        _warmUp = WarmUpFrames;
        _horizontalDirection = 1;
        _lastTimestamp = TimeSpan.Zero;
        topLevel.RequestAnimationFrame(OnAnimationFrame);
    }

    private void OnAnimationFrame(TimeSpan timestamp)
    {
        if (TopLevel.GetTopLevel(this) is not { } topLevel)
        {
            _frame = -1; // Detached mid-run.
            return;
        }

        if (_warmUp > 0)
        {
            // JIT, glyph atlas and scroll bar templates settle here, outside the measurement.
            _warmUp--;
            ResultGrid.Offset = NextOffset(2 + (_warmUp % 3), ResultGrid.Offset);
            topLevel.RequestAnimationFrame(OnAnimationFrame);
            return;
        }

        if (_lastTimestamp != TimeSpan.Zero)
        {
            _intervals[_frame] = (timestamp - _lastTimestamp).TotalMilliseconds;
            _records[_frame] = ResultGrid.LastRenderDuration.TotalMilliseconds;
            _frame++;
        }

        _lastTimestamp = timestamp;
        if (_frame >= _intervals.Length)
        {
            FinishBenchmark();
            return;
        }

        ResultGrid.Offset = NextOffset(_frame / FramesPerPhase, ResultGrid.Offset);
        topLevel.RequestAnimationFrame(OnAnimationFrame);
    }

    private Vector NextOffset(int phase, Vector offset)
    {
        var maxX = Math.Max(1, ResultGrid.Extent.Width - ResultGrid.Bounds.Width);
        var maxY = Math.Max(1, ResultGrid.Extent.Height - ResultGrid.Bounds.Height);
        switch (phase)
        {
            case 0: // Baseline: frame cadence of this machine and window when only a small control is redrawn.
                BenchmarkButton.InvalidateVisual();
                return offset;
            case 1: // Baseline: the grid redraws the same, fully cached cells every frame.
                ResultGrid.InvalidateVisual();
                return offset;
            case 2: // Three rows per frame: a fast wheel scroll.
                return new Vector(offset.X, (offset.Y + 66) % maxY);
            case 3: // A page per frame: dragging the scrollbar thumb; every visible cell is new.
                return new Vector(offset.X, (offset.Y + ResultGrid.Bounds.Height) % maxY);
            default: // Sideways sweep, bouncing at both ends.
                var x = offset.X + (40 * _horizontalDirection);
                if (x <= 0 || x >= maxX)
                {
                    _horizontalDirection = -_horizontalDirection;
                }

                return new Vector(Math.Clamp(x, 0, maxX), offset.Y);
        }
    }

    private void FinishBenchmark()
    {
        // The display's refresh interval is the shortest interval the compositor delivers; the 10th percentile
        // of the whole run is a robust estimate of it.
        var all = (double[])_intervals.Clone();
        Array.Sort(all);
        var refresh = Percentile(all, 0.10);

        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"display refresh ~{1000 / refresh:F0} Hz ({refresh:F1} ms)");
        for (var phase = 0; phase < PhaseNames.Length; phase++)
        {
            var intervals = _intervals.AsSpan(phase * FramesPerPhase, FramesPerPhase).ToArray();
            var records = _records.AsSpan(phase * FramesPerPhase, FramesPerPhase).ToArray();
            Array.Sort(intervals);
            Array.Sort(records);

            // An interval longer than 1.5 refresh periods means at least one refresh was missed.
            var missed = intervals.Count(i => i > refresh * 1.5);
            var over60 = intervals.Count(static i => i > 1000.0 / 60 * 1.1);
            text.AppendLine();
            text.Append(CultureInfo.InvariantCulture, $"{PhaseNames[phase]}: avg {1000 / intervals.Average():F0} fps, frame p50 {Percentile(intervals, 0.50):F1} / p95 {Percentile(intervals, 0.95):F1} / p99 {Percentile(intervals, 0.99):F1} / max {intervals[^1]:F1} ms, missed refreshes {missed}/{FramesPerPhase}, slower than 60 fps {over60}/{FramesPerPhase}, UI render p50 {Percentile(records, 0.50):F2} / p95 {Percentile(records, 0.95):F2} ms");
        }

        StatsText.Text = text.ToString();
        BenchmarkButton.IsEnabled = true;
        _frame = -1;

        if (_reportPath is not null)
        {
            _ = WriteReportAndExitAsync(_reportPath, text.ToString());
        }
    }

    private async Task WriteReportAndExitAsync(string path, string stats)
    {
        var top = TopLevel.GetTopLevel(this);
        var header = string.Create(
            CultureInfo.InvariantCulture,
            $"grid {ResultGrid.Bounds.Width:F0}x{ResultGrid.Bounds.Height:F0} at scale {top?.RenderScaling:F2}, {_source.RowCount:N0} rows x {_source.Columns.Count} columns, {FramesPerPhase} frames per phase");
        try
        {
            await File.WriteAllTextAsync(path, header + Environment.NewLine + stats + Environment.NewLine);
        }
        catch (IOException)
        {
            // Diagnostic output only; the numbers are still on screen.
        }

        (top as Window)?.Close();
    }

    private static double Percentile(double[] sorted, double p) => sorted[(int)Math.Ceiling(p * sorted.Length) - 1];
}
