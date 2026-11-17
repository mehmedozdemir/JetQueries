# Spike findings — virtualized results grid (Phase 0)

- Branch: `spike/virtual-grid`
- Date: 2026-11-17
- Gate being tested: "grid scrolls 1M rows at 60 fps" (CLAUDE.md rule 7, ANALIZ.md §12)

## Verdict

**Go, on the machine measured.** Drawing only the visible cells with `DrawingContext` works: the grid's cost is
independent of the row count and its share of the UI thread is small and bounded. With the window presented
through a low-latency DXGI swap chain (ADR 0002), scrolling one million rows holds 88–90 fps on a ~90 Hz panel
with 0–1.3 % of frames skipping a refresh. With Avalonia's default composition mode the same build skipped
6–75 %.

Still open before the Phase 0 gate is signed off: the result comes from one laptop. It has to be repeated on a
60 Hz display, a second machine and the ReadyToRun build, and the row store is untested.

## What was built

`JetQueries.DataGrid.VirtualGrid` — one control, no child controls:

- `IGridRowSource` supplies columns, a row count and cell text on demand; the grid asks only for visible cells.
- Logical scrolling via `ILogicalScrollable` inside a normal `ScrollViewer`.
- Fixed row height (22), header row, cell + range selection (mouse drag, Shift, arrows, PageUp/Down, Home/End,
  Ctrl+Home/End, Ctrl+A), column resize by dragging a header edge, `NULL` marker, ellipsis on clipped text.
- Not built (out of spike scope): copy, sorting/filtering, cell viewers, variable row height, auto-scroll while
  drag-selecting, a real row store.

The data source is synthetic (`Spike/SyntheticRowSource`): values are computed from (row, column), so the
measurements isolate the grid. **How a million real rows are stored was not tested.**

## Measurements

### 1. UI thread (headless, Release, Skia software rasterizer, grid 1600×900)

`dotnet test tests/JetQueries.DataGrid.Tests -c Release` — 1,000,000 rows × 30 columns, 300 frames per scenario.
"record" is the time `Render()` spends on the UI thread; "frame" includes software rasterization and read-back,
which the desktop app does not use, so compare it with the baseline row rather than with 16.7 ms.

| Scenario | frame p50 | frame p95 | record p50 | record p95 | record max |
| --- | ---: | ---: | ---: | ---: | ---: |
| baseline: one solid rectangle | 9.49 | 12.72 | – | – | – |
| smooth vertical, 3 rows/frame | 11.04 | 16.67 | 0.70 | 1.50 | 5.96 |
| page jumps, every cell new | 21.44 | 50.34 | 3.19 | 5.18 | 5.95 |
| horizontal sweep, 40 px/frame | 10.53 | 15.09 | 0.23 | 0.53 | 9.34 |
| smooth vertical while 1000 rows/frame stream in | 10.86 | 17.50 | 0.71 | 1.62 | 5.98 |

(milliseconds)

- Normal scrolling costs the UI thread well under 1 ms per frame; the grid adds 1–1.5 ms to a whole headless
  frame over an empty window.
- Row count does not matter: the streaming scenario ends at 1.3 M rows with the same numbers.
- The test asserts record p95 < 8.3 ms (half a frame) in Release.

### 2. On screen, Avalonia's default composition mode (Release, real window)

Machine: Intel Iris Xe, 2880×1800 at 175 % scaling, panel refreshing at ~90 Hz (10.8 ms). Grid area 1074×453
DIP (≈1880×793 px). `JetQueries.exe --grid-benchmark=<file>` scrolls for 300 frames per phase and records the
interval between animation frames. Ranges over the runs taken (at least seven per phase; three for the blank control):

| Phase | avg fps | median frame | frames that skipped a refresh |
| --- | ---: | ---: | ---: |
| tiny redraw (one button) | 89–90 | 11.1 ms | 0–1 % |
| blank control, same area as the grid | 74–85 | 11.2 ms | 5–21 % (mean 13 %) |
| grid: redraw same cells | 61–84 | 11.2–12.3 ms | 7–47 % |
| grid: smooth vertical | 68–84 | 11.1–11.4 ms | 7–31 % |
| grid: page jumps | 66–81 | 11.1–11.4 ms | 9–35 % |
| grid: horizontal | 60–85 | 11.1–14.0 ms | 6–50 % |

One further run, taken when the laptop was visibly throttling, dropped to 51–61 fps in every grid phase.

Reading:

- A skipped refresh on this panel is a 22 ms frame, i.e. slower than the 16.7 ms a 60 fps target allows. So
  "every frame within 16.7 ms" is **not** met here, although the average frame rate is above 60.
- The UI-thread cost during those phases was 0.2–1.1 ms (4 ms for page jumps), so the skips are not caused by
  `Render()`.
- A blank control that only fills its background skips almost as often. Removing all text, or all clips, from
  the grid did not remove the skips. The cost follows the *repainted area*, not the grid's content.
- Not measured: a 60 Hz display, a desktop GPU, 100 % scaling, the ReadyToRun build, memory.

### 3. On screen, by composition mode

The area-dependent skips pointed at how the window is presented, so the same benchmark was run with each of
Avalonia's Windows composition modes (interleaved rounds, same machine):

| Composition mode | Rounds | Scrolling phases: frames that skipped a refresh | Page jumps | Avg fps while scrolling |
| --- | ---: | ---: | ---: | ---: |
| WinUIComposition (default) | 7 | 6–75 % | 15–78 % | 51–85 |
| DirectComposition | 2 | 8–64 % (frames over 16.7 ms) | 16–19 % | 55–83 |
| RedirectionSurface | 2 | 28–42 % | 29–31 % | 67–74 |
| LowLatencyDxgiSwapChain | 7 | 0–1.3 % | 3–5 % | 88–90 |

With the swap chain the 95th-percentile frame while scrolling is 11.5–12.3 ms against a 10.8 ms refresh, and
no scrolling phase had more than 4 of 300 frames slower than 16.7 ms. The page-jump phase (every visible cell
new, every frame) still misses 10–15 of 300. The swap chain is now the default; see
[ADR 0002](../adr/0002-windows-composition-mode.md) for the trade-offs.

## What made the difference

1. **`FormattedText` is unusable for a grid.** It re-formats its lines on every draw; caching the object caches
   nothing. First measurement: 5–30 ms per frame on the UI thread, degrading over time. `TextLayout` keeps the
   shaped glyph runs: a cached cell costs ~1 µs to draw.
2. **Clip per column instead of trimming per cell.** Untrimmed layouts are cheaper to create and survive column
   resizes; the ellipsis is painted separately for cells that overflow.
3. **A text budget per frame (6 ms).** Shaping ~500 new cells takes 3–7 ms. When a frame needs more than the
   budget allows (dragging the scrollbar thumb), the grid draws what it has and fills in the rest next frame, so
   the UI thread stays responsive in the worst case.
4. **Evict by visibility, not "clear all".** Clearing the whole text cache when full caused a periodic 15 ms
   spike.
5. Whole-pixel scroll offsets keep text and lines crisp.
6. **Presenting through a DXGI swap chain** instead of WinUI composition removed the skipped refreshes. This
   was the largest single effect and is not specific to the grid.

## Risks and open questions

| # | Item | Why it matters | Proposed next step |
| --- | --- | --- | --- |
| 1 | The 60 fps result rests on one laptop and on a non-default composition mode | This is the 60 fps gate | Repeat on a 60 Hz display, a second machine and the ReadyToRun publish; check resize, multi-monitor, remote desktop and popups under the swap chain |
| 2 | Lines and text are placed on DIP boundaries, which are fractional device pixels at 125/150/175 % | Slight blur of 1 px lines at fractional scaling | Snap to device pixels using the render scaling |
| 3 | No real row store | 1M × 30 boxed values in memory is several GB | Design the paged store (first page in memory, rest on disk — ANALIZ.md §7) as its own task |
| 4 | Page jumps show partially filled cells for a frame | Visible while dragging the thumb fast | Acceptable; alternatively shape text on a worker thread |
| 5 | Text cache is keyed by cell | Low-cardinality columns re-shape identical strings | Add a small per-string cache if profiling shows it matters |
| 6 | Accessibility | A custom-drawn control exposes nothing to screen readers | Automation peer in Phase 1 |

## Recommendation

Keep the custom grid (no need to evaluate TreeDataGrid) and keep the DXGI swap chain as the Windows default.
Treat the 60 fps gate as passed on this machine and pending confirmation elsewhere (risk 1). The next grid work
is the row store (risk 3), which this spike deliberately left out.
