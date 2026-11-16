# Manual test checklist — Phase 0 grid spike

Branch `spike/virtual-grid`. Automated: `dotnet build JetQueries.slnx` (0 warnings) and `dotnet test` (47 tests).
Findings and numbers measured so far: [docs/spikes/virtual-grid.md](../spikes/virtual-grid.md).

Run the Release build; Debug is noticeably slower.

```
dotnet run -c Release --project src/JetQueries.Desktop
```

## 1. First look

- [ ] The RESULTS area shows a grid with a header row (`id`, `name_1`, `amount_1`, `created_at_1`, …) and data.
- [ ] The line above the grid reads "1,000,000 rows × 30 columns (synthetic)" (Turkish on a Turkish system).
- [ ] Text is sharp, numbers are right-aligned, some `quantity_*` cells show a grey `NULL`.
- [ ] Long `notes_*` cells end with a grey `…` instead of running into the next column.
- [ ] Turkish characters in `name_*` (İ, ş, ğ, ç, ö, ü) render correctly.

## 2. Scrolling — the point of the spike

- [ ] Mouse wheel: rows move smoothly, no blank flashes, header stays in place.
- [ ] Drag the vertical scrollbar thumb top to bottom quickly: the grid keeps up; the last row's `id` is 1,000,000.
      Cells may appear a frame late while dragging very fast — that is the text budget, expected.
- [ ] Drag the horizontal scrollbar: all 30 columns are reachable; header and cells stay aligned.
- [ ] Shift+wheel (or a touchpad) scrolls sideways.
- [ ] Subjective: does it *feel* like 60 fps on your display? Note the display's refresh rate and scaling.

## 3. Built-in benchmark

- [ ] Click **Scroll benchmark**. The grid scrolls by itself for ~20 seconds, then the line above it shows five
      rows of numbers (selectable — copy and send them back).
- [ ] For each phase note `avg fps`, `missed refreshes` and `slower than 60 fps`. "tiny redraw" is the machine's
      own baseline; the grid phases are what matter.
- [ ] Repeat with the window maximized, and once on an external 60 Hz monitor if you have one.
- [ ] Expected with the default settings on the development laptop: 88–90 fps average while scrolling and at
      most a handful of missed refreshes per 300 frames (page jumps: 10–15).
- [ ] Comparison run with Avalonia's old default, to see the difference on your machine (PowerShell):
      `$env:JETQUERIES_COMPOSITION='WinUIComposition'; dotnet run -c Release --project src/JetQueries.Desktop`,
      then `Remove-Item Env:JETQUERIES_COMPOSITION`.
- [ ] Unattended variant, writes the same numbers to a file and exits:
      `src\JetQueries.Desktop\bin\Release\net10.0\JetQueries.exe --grid-benchmark=C:\Temp\grid.txt`

## 4. Streaming

- [ ] Click **Stream rows**: the row count climbs by ~60,000 per second; the scrollbar thumb shrinks/moves.
- [ ] Scrolling stays smooth while rows stream in; rows already on screen do not flicker.
- [ ] Click it again: the count stops.

## 5. Selection and keyboard

- [ ] Click a cell: it gets an accent-coloured border.
- [ ] Drag across cells: a rectangular range is highlighted. Shift+click extends from the first cell.
- [ ] Arrow keys move the current cell; the grid scrolls to keep it visible. Shift+arrows extend the range.
- [ ] PageUp/PageDown move by a page; Home/End go to the first/last column; Ctrl+Home/Ctrl+End to the
      first/last row; Ctrl+A selects everything.

## 6. Column resize

- [ ] Hover a column's right edge in the header: the cursor becomes a horizontal resize cursor.
- [ ] Drag: the column resizes live, later columns shift, text re-clips, the horizontal scrollbar adjusts.
- [ ] A column cannot be made narrower than a few pixels.

## 7. Theme

- [ ] **Toggle theme**: the grid's background, header, lines, text and selection switch with the rest of the app.

## 8. Window behaviour under the DXGI swap chain (new default, ADR 0002)

- [ ] Resize the window by dragging an edge, quickly: no black bars, stretched or stale content after release.
- [ ] Maximize, restore, minimize and bring back: content is intact.
- [ ] Move the window between monitors with different scaling or refresh rate, if available: it re-renders
      sharply at the new scale.
- [ ] Hover the toolbar buttons and drag the splitters: hover states and the resize cursor behave as before.
- [ ] If you use Remote Desktop to this machine: the window still renders.

## Known limits of the spike

- No copy, sort, filter or cell viewers; dragging a selection past the edge does not auto-scroll.
- Data is synthetic and computed on the fly; memory use says nothing about real result sets.
- Grid lines can look slightly soft at 125/150/175 % display scaling.
