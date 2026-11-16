# Manual test checklist — Phase 0, step 1 (repository skeleton)

Automated checks already pass: `dotnet build JetQueries.slnx` (0 warnings, warnings are errors) and `dotnet test`
(19 tests). The items below need a human, because Claude Code cannot see or click the UI.

## 1. Build and run

```
dotnet build JetQueries.slnx
dotnet test
dotnet run --project src/JetQueries.Desktop
```

- [ ] Build finishes with 0 warnings and 0 errors.
- [ ] `dotnet test` reports 12 passed in `JetQueries.Core.Tests` and 7 passed in `JetQueries.Sdk.Tests`.
- [ ] The app starts and shows one window titled **JetQueries**, centred on the screen.

## 2. Shell layout

- [ ] Top bar: "JetQueries" in the accent colour on the left, a **Toggle theme** button on the right.
- [ ] Left: **EXPLORER** panel with a placeholder sentence.
- [ ] Centre: **DOCUMENTS** area with a placeholder in a monospace font.
- [ ] Bottom of the centre: **RESULTS** area with a placeholder.
- [ ] Bottom edge: status bar showing **Ready**.
- [ ] Drag the vertical line between Explorer and Documents: both sides resize; the Explorer cannot shrink below ~160 px.
- [ ] Drag the horizontal line between Documents and Results: both resize; neither collapses completely.
- [ ] The splitter line turns to the accent colour while the mouse is over it.
- [ ] Resize the window down to its minimum (800×500): nothing overlaps or is clipped oddly.

## 3. Light / dark tokens

- [ ] On start the app follows the Windows app theme (Settings → Personalisation → Colours → "Choose your mode").
- [ ] Click **Toggle theme**: every region switches at once — backgrounds, borders, panel headers, text, accent.
      No area keeps the old theme's colour.
- [ ] Click it again: it switches back.
- [ ] In both themes the placeholder (secondary) text is clearly readable against its background.
- [ ] In both themes the three regions are distinguishable: side panel and bars are slightly different from the
      document/results surfaces, separated by 1 px borders.

## 4. Language (resources)

- [ ] With a Turkish Windows display language the labels are Turkish (GEZGİN, BELGELER, SONUÇLAR, Hazır,
      "Temayı değiştir"); with any other language they are English. There is no in-app language switch yet,
      so checking the other language means changing the Windows display language.
- [ ] `GEZGİN` is rendered with a dotted capital İ (Turkish-safe text).

## 5. Shutdown

- [ ] Close the window with the X button: the process exits within a second or two
      (no `JetQueries.exe` left in Task Manager).
- [ ] Alt+F4 does the same.

## 6. Repository

- [ ] `git log` shows a single commit `chore: repository skeleton` on branch `main`.
- [ ] `git status` is clean after a build (no `bin/`, `obj/` noise).
- [ ] Opening `JetQueries.slnx` in Visual Studio / Rider shows folders `src`, `src/Providers`, `tests` with 9 projects.

## Known gaps (expected at this step)

- Explorer, Documents and Results are placeholders; docking (Dock.Avalonia) is not wired yet.
- Control templates still come from Avalonia's Fluent theme; only our own shell elements use the tokens.
- No app icon, no Velopack packaging, no CI yet.
- Working set of the Debug build was ~212 MB five seconds after start. The < 200 MB budget is measured on the
  ReadyToRun Release build once the benchmark exists; treat this number as an early warning, not a result.
