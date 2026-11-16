# ADR 0002 — Present through a low-latency DXGI swap chain on Windows

- Status: accepted on `spike/virtual-grid`; confirm on a second machine before the Phase 0 gate
- Date: 2026-11-17

## Context

The grid spike (docs/spikes/virtual-grid.md) showed that with Avalonia's default Windows composition mode
(WinUI composition) any control repainting a large area skipped display refreshes — a blank control as often as
the grid — even though the UI thread spent well under 1 ms per frame. JetQueries is made of large surfaces that
repaint while scrolling (results grid, editor), so this decides whether the 60 fps budget is reachable at all.

## Measurement

`JetQueries.exe --grid-benchmark=<file>`, Release, Intel Iris Xe, 2880×1800 at 175 %, ~90 Hz panel, grid area
≈1880×793 px, 300 frames per phase. Modes were run interleaved; ranges are over all rounds.

| Composition mode | Rounds | Scrolling (smooth / horizontal / redraw): frames that skipped a refresh | Page jumps | Avg fps while scrolling |
| --- | ---: | ---: | ---: | ---: |
| WinUIComposition (Avalonia default) | 7 | 6–75 % | 15–78 % | 51–85 |
| DirectComposition | 2 | 8–64 % (frames over 16.7 ms) | 16–19 % | 55–83 |
| RedirectionSurface | 2 | 28–42 %, and the small-redraw baseline also degrades | 29–31 % | 67–74 |
| **LowLatencyDxgiSwapChain** | 7 | **0–1.3 %** | 3–5 % | **88–90** |

With the swap chain, the 95th-percentile frame while scrolling is 11.5–12.3 ms on a 10.8 ms refresh; the
remaining page-jump misses are frames where ~500 new cells are shaped and rasterized at once.

## Decision

`Program.CreateWin32Options` sets the composition preference to
`LowLatencyDxgiSwapChain, WinUIComposition, DirectComposition, RedirectionSurface`. Avalonia falls through the
list when a mode is unavailable (the swap chain needs Direct3D feature level 11_3).

`JETQUERIES_COMPOSITION` and `JETQUERIES_RENDERING` override the lists (comma-separated enum names) for
measuring and as an escape hatch for driver problems.

## Consequences

- No window-level transparency, acrylic or blur (documented limitation of this mode). The design language in
  ANALIZ.md §9 does not use them; if that changes, this decision must be revisited.
- This is not Avalonia's default path, so it is less widely exercised. Watch for: artefacts while resizing the
  window, behaviour across monitors with different scaling or refresh rate, remote desktop sessions, popups and
  tooltips. None of these was tested in the spike.
- Evidence comes from one laptop. Before the Phase 0 gate: repeat the benchmark on a 60 Hz display and on a
  machine with a discrete GPU, and on the ReadyToRun build.
- macOS and Linux are unaffected; they need their own measurement when those platforms are packaged.
