using System.Diagnostics;
using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using JetQueries.DataGrid.Spike;

namespace JetQueries.DataGrid.Tests;

/// <summary>
/// Renders the grid for real (Skia, software rasterizer, no display) over one million rows and measures the cost
/// of each frame. Software rasterization is slower than the GPU path the desktop app uses, so these numbers are a
/// conservative proxy for the 60 fps budget.
/// </summary>
public sealed class VirtualGridRenderTests
{
    private const int Rows = 1_000_000;
    private const int Columns = 30;
    private const int FramesPerScenario = 300;
    private const double FrameBudgetMs = 1000.0 / 60;

    [Fact]
    public async Task Scrolling_one_million_rows_stays_within_the_frame_budget()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(TestApp));
        var report = await session.Dispatch(RunScenarios, TestContext.Current.CancellationToken);

        var text = report.ToString();
        TestContext.Current.TestOutputHelper?.WriteLine(text);
        await File.WriteAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "grid-frame-report.txt"),
            text,
            TestContext.Current.CancellationToken);

        Assert.True(report.DistinctColours > 2, "The grid rendered a blank frame.");
        Assert.All(report.Scenarios, s => Assert.True(s.Rendered >= FramesPerScenario, $"{s.Name}: grid was not re-rendered every frame."));

#if !DEBUG
        // The grid's share of the UI thread (recording draw calls, shaping new text) must leave at least half
        // of every 16.7 ms frame free. Whole-frame times are reported, not asserted: they are dominated by the
        // software rasterizer and read-back, which the desktop app does not use.
        Assert.All(report.Scenarios, s => Assert.True(s.RecordP95 < FrameBudgetMs / 2, $"{s.Name}: UI-thread render p95 {s.RecordP95:F2} ms."));
#endif
    }

    private static Report RunScenarios()
    {
        var baseline = MeasureBaseline();
        var source = new SyntheticRowSource(Rows, Columns);
        var grid = new VirtualGrid { Source = source };
        var window = new Window { Width = 1600, Height = 900, Content = grid };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var maxY = grid.Extent.Height - grid.Bounds.Height;
        var maxX = grid.Extent.Width - grid.Bounds.Width;
        var page = grid.Bounds.Height - VirtualGrid.HeaderHeight;
        var direction = 1.0;

        // Warm-up: JIT, font and glyph caches.
        Measure(window, grid, "warm-up", 60, o => new Vector(0, o.Y + 66));
        grid.Offset = default;

        var scenarios = new List<Scenario>
        {
            baseline,

            // Three rows per frame: what a fast wheel or touchpad scroll produces. Few new cells per frame.
            Measure(window, grid, "smooth vertical (3 rows/frame)", FramesPerScenario, o => new Vector(0, (o.Y + 66) % maxY)),

            // A full page per frame: dragging the scrollbar thumb. Every visible cell is new - the worst case.
            Measure(window, grid, "page jumps (all cells new)", FramesPerScenario, o => new Vector(0, (o.Y + page) % maxY)),

            // Sideways sweep that bounces at both ends.
            Measure(window, grid, "horizontal sweep (40 px/frame)", FramesPerScenario, o =>
            {
                var x = o.X + (40 * direction);
                if (x <= 0 || x >= maxX)
                {
                    direction = -direction;
                }

                return new Vector(Math.Clamp(x, 0, maxX), o.Y);
            }),

            // Rows keep arriving (1000 per frame) while the user scrolls.
            Measure(window, grid, "smooth vertical while streaming", FramesPerScenario, o =>
            {
                source.AppendRows(1000);
                return new Vector(0, o.Y + 66);
            }),
        };

        using var frame = window.CaptureRenderedFrame();
        var report = new Report(scenarios, CountDistinctColours(frame!), grid.Bounds.Size, source.RowCount);
        window.Close();
        return report;
    }

    private static Scenario Measure(Window window, VirtualGrid grid, string name, int frames, Func<Vector, Vector> next)
    {
        var frameMs = new double[frames];
        var recordMs = new double[frames];
        var rendersBefore = grid.RenderCount;

        for (var i = 0; i < frames; i++)
        {
            var started = Stopwatch.GetTimestamp();
            grid.Offset = next(grid.Offset);
            Dispatcher.UIThread.RunJobs();
            using (window.CaptureRenderedFrame())
            {
                frameMs[i] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            }

            recordMs[i] = grid.LastRenderDuration.TotalMilliseconds;
        }

        Array.Sort(frameMs);
        Array.Sort(recordMs);
        return new Scenario(
            name,
            (int)(grid.RenderCount - rendersBefore),
            Percentile(frameMs, 0.50),
            Percentile(frameMs, 0.95),
            frameMs[^1],
            Percentile(recordMs, 0.50),
            Percentile(recordMs, 0.95),
            recordMs[^1]);
    }

    /// <summary>
    /// The fixed cost of the headless pipeline: a window of the same size whose only content is one rectangle
    /// that changes colour every frame. Whatever the grid costs on top of this is the grid's own.
    /// </summary>
    private static Scenario MeasureBaseline()
    {
        var blank = new BlankControl();
        var window = new Window { Width = 1600, Height = 900, Content = blank };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var frameMs = new double[FramesPerScenario];
        for (var i = -60; i < FramesPerScenario; i++)
        {
            var started = Stopwatch.GetTimestamp();
            blank.Toggle();
            Dispatcher.UIThread.RunJobs();
            using (window.CaptureRenderedFrame())
            {
                if (i >= 0)
                {
                    frameMs[i] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                }
            }
        }

        window.Close();
        Array.Sort(frameMs);
        return new Scenario(
            "baseline (one solid rectangle)",
            FramesPerScenario,
            Percentile(frameMs, 0.50),
            Percentile(frameMs, 0.95),
            frameMs[^1],
            0,
            0,
            0);
    }

    private static double Percentile(double[] sorted, double p) => sorted[(int)Math.Ceiling(p * sorted.Length) - 1];

    private static unsafe int CountDistinctColours(Avalonia.Media.Imaging.WriteableBitmap bitmap)
    {
        using var buffer = bitmap.Lock();
        var colours = new HashSet<uint>();
        var pixels = (uint*)buffer.Address;
        var stride = buffer.RowBytes / sizeof(uint);
        for (var y = 0; y < buffer.Size.Height; y += 7)
        {
            for (var x = 0; x < buffer.Size.Width; x += 3)
            {
                colours.Add(pixels[(y * stride) + x]);
            }
        }

        return colours.Count;
    }

    private sealed record Scenario(
        string Name,
        int Rendered,
        double FrameP50,
        double FrameP95,
        double FrameMax,
        double RecordP50,
        double RecordP95,
        double RecordMax);

    private sealed record Report(IReadOnlyList<Scenario> Scenarios, int DistinctColours, Size GridSize, int FinalRowCount)
    {
        public override string ToString()
        {
            var text = new StringBuilder();
#if DEBUG
            const string configuration = "Debug";
#else
            const string configuration = "Release";
#endif
            text.AppendLine(CultureInfo.InvariantCulture, $"VirtualGrid headless frame report ({configuration}, Skia software rasterizer)");
            text.AppendLine(CultureInfo.InvariantCulture, $"{Rows:N0} rows x {Columns} columns, grid {GridSize.Width:F0}x{GridSize.Height:F0}, {FramesPerScenario} frames per scenario, final row count {FinalRowCount:N0}");
            text.AppendLine("frame = set offset + layout + render + rasterize + read back; record = UI-thread Render() only. Milliseconds.");
            text.AppendLine();
            text.AppendLine(CultureInfo.InvariantCulture, $"{"scenario",-34} {"frame p50",9} {"p95",7} {"max",7}   {"record p50",10} {"p95",7} {"max",7}");
            foreach (var s in Scenarios)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"{s.Name,-34} {s.FrameP50,9:F2} {s.FrameP95,7:F2} {s.FrameMax,7:F2}   {s.RecordP50,10:F2} {s.RecordP95,7:F2} {s.RecordMax,7:F2}");
            }

            return text.ToString();
        }
    }

    private sealed class BlankControl : Control
    {
        private bool _toggled;

        public void Toggle()
        {
            _toggled = !_toggled;
            InvalidateVisual();
        }

        public override void Render(Avalonia.Media.DrawingContext context) =>
            context.FillRectangle(_toggled ? Avalonia.Media.Brushes.White : Avalonia.Media.Brushes.WhiteSmoke, new Rect(Bounds.Size));
    }

    private sealed class TestApp : Application
    {
        public static AppBuilder BuildAvaloniaApp() =>
            AppBuilder.Configure<TestApp>()
                .UseSkia()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });

        public override void Initialize() => Styles.Add(new FluentTheme());
    }
}
