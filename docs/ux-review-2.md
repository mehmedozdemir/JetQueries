# UX review of the WPF application, round 2 (2026-11-24)

A second pass after the first review (`docs/ux-review.md`, all of it fixed), from the user's own screenshot of a
test connection and the renderings in `tests/JetQueries.Wpf.Tests/.../screenshots` (every colour scheme, both
themes). Items fixed in the same change are marked **fixed**; the rest are open, ordered by priority inside each
area.

Priorities: **P1** hurts daily work or safety · **P2** friction or inconsistency · **P3** polish.
Status: every finding below is fixed (follow-up change on 2026-11-24).

## Fixed in this pass
- **fixed** The environment was said three times on a query (a "TEST · name" strip, the connection name with
  a dot on the toolbar, and the tab's dot). The strip and the toolbar name are gone; the whole query (toolbar,
  editor, results) is framed in the environment's colour — green, amber or red — and the tab keeps its dot.
  An unmarked connection has no frame. The frame's tooltip says "TEST · name".
- **fixed** The editor/results splitter could not be found: UiKit's splitter template drew a 2 px vertical line
  and kept the arrow cursor for row splitters too. A row splitter now shows the ↕ cursor, a horizontal accent
  line on hover and a small grip at rest; both panes can be made as small as 60 px.
- **fixed** Folders could only be typed into a connection's form. The explorer now has "New folder" (toolbar and
  menu), "Rename folder" and "Delete folder" (what is inside moves up a level, nothing is deleted), and
  drag-and-drop of connections and folders onto folders, onto a connection (its folder) or onto empty space
  (the top). Empty folders are kept; folders are sorted by name and keep their open/closed state.
- **fixed** Import read every tool at once. It now starts with the sources — DBeaver, SQL Server Management
  Studio, a file — each saying whether its settings were found, reads only the chosen one, and offers "Keep
  their folders" and "Into folder".
- **fixed** The dark theme looked washed out. Each theme has colour schemes (Settings → Appearance):
  dark — Midnight (the new default: deeper background, crisper text, a stronger accent), Graphite (the previous
  look), Ocean, High contrast; light — Paper (default), Sky, Sand, High contrast. The choice is remembered with
  the theme.

## Shell and navigation
- **fixed** (P2) Two title bars: Windows' caption plus the app's menu/toolbar row. Drawing the menu into the caption
  (as VS Code and DataGrip do) would give the editor about 30 px more.
- **fixed** (P3) The main toolbar's three icons (new connection, new query, open) sit right after the menu with no
  grouping or labels; with the command palette and the menu they could go, or move to the explorer/tab bar.

## Query and results
- **fixed** (P2) The results take three rows before the first data row: result tabs, the result toolbar (view modes,
  SQL filter) and a two-line header. The view modes and the SQL filter button could sit at the right end of the
  result tab row.
- **fixed** (P2) The query toolbar's transaction controls use four colours at once (amber mode, green Commit, red
  Rollback, amber badge); in manual-commit mode it reads busy. A neutral Commit/Rollback pair with coloured
  icons only would calm it down.
- **fixed** (P3) Before a first run the results area is an empty card; a short hint (F5 runs, Ctrl+Enter runs the
  statement at the cursor) would teach the shortcuts where they matter.

## Explorer
- **fixed** (P1) Deleting a connection is effectively missing: it exists only as the trash icon in the explorer's
  toolbar (icon-only since the first review). It is not in the connection's right-click menu, not in the
  main menu (Connection) nor the command palette, and the Delete key does nothing on a selected connection.
  It should be in all of these (with the existing confirmation; the connection is closed, its saved password
  removed), next to "Edit connection", which has the same problem.
- **fixed** (P2) Drag-and-drop shows only a highlighted target; an insertion caption ("Move to Customer A") next to
  the cursor would confirm what will happen, and Esc should cancel visibly.
- **fixed** (P3) No multi-select: moving several connections into a folder takes one drag each.

## Dialogs
- **fixed** (P3) The connection editor's "Advanced" section puts switches and the timeout box in one two-column grid;
  the switch row is as tall as the labelled box next to it, leaving a gap.
- **fixed** (P3) Settings: the scheme cards show a small picture of the colours; a live preview of the editor's syntax
  colours in each scheme would help choose.
