# Phase 1 · WPF user interface — manual test checklist

The WPF application (`src/JetQueries.Wpf`) replaces the Avalonia one as the product (ADR 0007). It uses the
same view models, so behaviour should match the earlier checklists (`phase1-*.md`); this list covers what is new
or WPF-specific. Automated: view models (Presentation tests), result grid source, and renderings of the shell,
the history tab and the connection dialog in both themes (`tests/JetQueries.Wpf.Tests/.../screenshots`).

```bash
dotnet run --project src/JetQueries.Wpf
```

## Shell
- [ ] The window opens with the UiKit look: toolbar (New connection, New query, Run, Cancel, theme button on
      the right), Explorer/History card on the left, query tabs on the right, status bar at the bottom.
- [ ] The theme button switches dark/light at once, including the title bar; the choice is remembered at the
      next start (`%AppData%\JetQueries\theme.json`). With nothing saved the Windows setting is followed.
- [ ] The left card can be resized with the splitter; the editor/results split too.
- [ ] Ctrl+N opens a new query tab; closing every tab shows the "No open queries" state with a button.
- [ ] Turkish and English UI texts, including the grid's own menus (right-click a header or a cell).

## SQL files and the workspace
- [ ] Toolbar Open / Ctrl+O opens an `.sql` file in a tab named after the file (tool tip: full path); the
      untouched empty tab of the start makes way for it; opening the same file again selects its tab.
- [ ] Dropping `.sql` files on the window opens them.
- [ ] Turkish text survives a round trip in UTF-8 (with and without BOM), UTF-16 and old Windows-1254 files:
      open, edit, save, open in Notepad/SSMS — the encoding of the file is kept.
- [ ] Typing marks the tab "Sorgu 1 *"; Ctrl+S saves (a new query asks where, offering "Sorgu 1.sql");
      Ctrl+Shift+S saves as; the star goes away; the status bar says where it was saved.
- [ ] Closing a changed tab (× or middle click) asks Save / Don't save / Cancel; Cancel keeps it; Save on a new
      query asks for a file and keeps the tab if that is canceled. An empty new query closes without asking.
- [ ] Close the application with unsaved and saved tabs, open it again: the same tabs, texts, selected tab,
      stars. A saved file changed in the meantime shows its new content; a deleted file keeps its text as unsaved.
- [ ] Restored tabs are on no connection until their connection is opened in the explorer; then they go back to
      it and to their database. They never fall back to another connection by themselves.
- [ ] New tabs continue the numbering of restored ones (Sorgu 3 after Sorgu 1, 2).

## Settings, density and zoom
- [ ] The layout is tighter than before (toolbar, margins, tabs, tree rows) and nothing is clipped.
- [ ] Toolbar → Settings: theme; UI font and text size (11–16); SQL editor font and size (9–24) with a sample
      line; result grid text size (11–18). Every change shows at once in the window behind; Cancel or the
      title bar's × puts everything back; Save keeps it and it is there after a restart
      (`%AppData%\JetQueries\appearance.json`); Defaults fills in the original values.
- [ ] Editor: Ctrl+mouse wheel and Ctrl+plus/minus zoom in 10 % steps (50–400 %); the zoom shows bottom
      right ("%110"); a click on it or Ctrl+0 goes back to 100 %.
- [ ] Result grid: Ctrl+mouse wheel zooms, a "%120" badge appears bottom right while zoomed; a click on it or
      Ctrl+0 goes back to 100 % (100 % = the grid text size of the settings).

## Active database (SSMS-style)
- [ ] A query tab shows its connection and, next to it, its current database (Oracle: current schema). The list
      offers the databases the login can enter; choosing one switches only this tab.
- [ ] Run `USE master` (SQL Server): the selector follows. Choose a database and press F5 at once: the query runs
      in the database just chosen.
- [ ] Two tabs on the same connection can be in different databases at the same time; a long query in one does
      not block the other.
- [ ] A database that cannot be entered: the server's message in the status bar, the selector goes back.
- [ ] PostgreSQL: choosing another database reconnects the tab to it (`SELECT current_database()`).
      SQLite: no database selector.
- [ ] Completion after switching database (SQL Server) offers the tables of the new database.

## Connections and explorer
- [ ] New connection opens the dialog; the provider list shows names (not type names); provider settings,
      sign-in, folder, environment and read-only fields work; Browse picks an SQLite file.
- [ ] The password box is styled like the other inputs; saving with "save password" stores it in Windows
      Credential Manager (as before).
- [ ] The explorer shows folders, environment dots (green / amber / red), the "connected" and "read-only"
      badges; double-click connects; expanding shows databases/schemas/groups/tables/columns with icons.
- [ ] A connection error is shown in red with the message as a tool tip.
- [ ] The filter box under the explorer's buttons hides what does not match, ignoring case (Turkish rules);
      parents of matches stay, everything under a match stays; nodes expanded later are filtered too; Esc or ×
      clears it.
- [ ] Right-click on a connected connection, a database, a schema, a group or an object: "New query" opens a tab
      on that connection, in that database (Oracle: that schema); the menu also has Connect/Disconnect and
      Refresh. Right-click selects the node first.

## Editor and results
- [ ] Syntax colours follow UiKit's tokens (keywords blue, strings green, numbers orange, comments italic).
- [ ] Completion opens while typing and with Ctrl+Space; the list is styled like a UiKit card; Enter/Tab
      inserts, Esc closes (and does not cancel a running query).
- [ ] A syntax error is underlined; hovering shows the message. Ctrl+Click / F12 opens a definition tab.
- [ ] F5 / Ctrl+Enter runs (the selection if there is one); Esc or the toolbar's Cancel stops a long query.
- [ ] Results stream into the grid while the query runs; numbers right-aligned, NULL muted, booleans
      coloured; Ctrl+C copies.
- [ ] The Messages tab shows server messages; error lines in red; text can be selected and copied.

## Query tab layout and paged results (DBeaver-style)
- [ ] A query tab has Run (green), Stop and Save as icons at its top left, then the database selector; there is
      no connection list (the connection is in the status bar and in the tab's tool tip). A tab without a
      connection says so.
- [ ] Settings → Query → "Run, Cancel, Save": "In the main toolbar" moves them there with text and removes them
      from the tabs; switching back restores the icons.
- [ ] Run a single SELECT returning more rows than the page size (default 200, Settings → Query → Rows per
      page): the first 200 rows show, the counter under the grid says "200+", the status line says rows · time
      (fetch time) · clock. On the server the statement is already closed: in SSMS's Activity Monitor or
      `sys.dm_exec_requests` / `pg_stat_activity` the session is idle, holding no locks.
- [ ] "Next page" (↓) runs the query again and shows the first 400 rows; "Fetch all" runs it again and reads all
      of it (up to 1,000,000 rows); the counter loses its "+".
- [ ] A procedure call or a script returning many rows is read to its end at once, without pages (running it
      again for a page would repeat what it does).
- [ ] The page size box under the grid changes the size for the next run and the next page (Enter applies).
- [ ] Count (calculator icon) shows "200 / 12,345" without fetching more rows; while a page is open it uses a
      second connection; a script of several statements, a procedure call or DML says "Only a single SELECT
      can be counted.". SQL Server: a query ending in ORDER BY is counted; WITH queries are not (SQL Server rule).
- [ ] Refresh (↻ under the grid) runs the last query again from its first page.
- [ ] A long-running query can be stopped with Stop / Esc; a page of millions of rows stops at once.

## Sorting and filtering results
Sorting and filtering work on the rows already received (at most 100,000), on the client, in the background.
- [ ] Click a header: ascending; click again: descending (arrow in the header). Shift+click adds the column
      as a further key (numbered arrows). Header right-click: sort ascending / descending / clear sort.
- [ ] NULL sorts first ascending, last descending; numbers sort by value; text by Turkish rules
      (ç after c, ı before i, İstanbul = istanbul ignoring case).
- [ ] The funnel in a header opens the filter panel under it: column name and database type, a condition
      (text: contains, not contains, equals, not equal, starts with, ends with, NULL, not NULL; numbers and
      dates: equals, not equal, greater/less (or equal), contains, NULL, not NULL) and a value. Enter applies,
      Esc closes, Clear removes the column's filter; an empty value shows "Enter a value.".
- [ ] Numbers can be typed as `1250,5` or `1250.5`; dates as `15.01.2026` or `2026-01-15`.
- [ ] A filtered column's funnel is highlighted; a chip per filter appears above the grid with an ×; "Clear all"
      and Ctrl+Shift+L remove every filter (the sort stays); the bar shows "36 / 60 rows".
- [ ] Cell right-click: "Filter by this value" / "Exclude this value" (on a NULL cell: NULL / not NULL).
- [ ] Header right-click "Hide column": the column disappears; "1 hidden column · show" brings it back.
- [ ] Sort or filter while a long query is still returning rows: the new rows follow the order and the filters.
- [ ] On 100,000 rows a sort or filter takes well under a second and the window never freezes.

## SQL filter over the grid (server side)
- [ ] Run a single SELECT: the box above the grid says "Enter a SQL expression to filter results"; for a script,
      a procedure call or DML it is disabled and its tool tip says only a single SELECT can be filtered.
- [ ] Type a condition over the result's columns (`balance > 100 AND city LIKE 'İ%'`) and press Enter or the
      funnel: the query runs again with the condition, the first page shows, the box gets an accent border.
      The filter covers every row of the table, not only those fetched.
- [ ] The query's ORDER BY is kept (also `ORDER BY t.col`, `TOP`, `OFFSET … FETCH`, `LIMIT`).
- [ ] Next page, Fetch all, Count and Refresh use the filter; running the editor again (F5) removes it.
- [ ] Esc in the box puts back the applied filter; × removes the filter and shows the plain first page.
- [ ] `1 = 1; DROP TABLE x`, a `--` comment, an open parenthesis or an unclosed string is refused with
      "The filter must be one SQL condition…", nothing is sent and the rows stay.
- [ ] A wrong column name shows the server's error under Messages; the rows shown stay.
- [ ] After a filtered page nothing is left open on the server (session idle, no locks).

## Grid, Text and Record views
- [ ] The strip left of the grid has Grid, Text and Record; the current one is highlighted.
- [ ] Text shows the rows as a plain-text table: header, a dashed line, numbers aligned right, NULL as
      `[NULL]`, line breaks inside a value as a space, values longer than 80 characters cut with "…". It follows
      the grid's sort, filters and hidden columns, and can be selected and copied. More than 100,000 rows end
      with a "… N" line.
- [ ] Record starts on the grid's current row ("Record 5 / 40"); the arrows move to first / previous / next /
      last; NULL shows as a muted NULL; hidden columns are left out; long values can be selected.
- [ ] Back to Grid: the grid's current row is the record shown last.
- [ ] New rows (Next page, Fetch all), sorting and filtering update the text and the record view.

## History
- [ ] The History tab lists runs; search filters after a short pause; double-click or Open reopens a run;
      Clear all asks first.

## Known limits
- Header sorting and column filters apply to the rows received; the SQL filter box above the grid is the one
  that filters on the server.
- Nothing stays open on the server between pages (checked on SQL Server and PostgreSQL: DDL on the table is not
  blocked, no snapshot is held). While a page is being read the statement takes the usual short-lived read locks
  of any SELECT. "Next page" and "Fetch all" run the query again: each costs a new execution, and the rows shown
  are those of that execution (they may differ from the first page if the data changed meanwhile).
- SQL Server: a query starting with WITH is not paged (SQL Server cannot count it) and is read to its end.
- Filters on several columns are combined with AND; there is no OR (the SQL filter box takes any condition).
- SQL filter: no completion in the box yet. SQL Server cannot wrap a query starting with WITH, nor one whose
  columns repeat a name (`SELECT a.id, b.id …`): the server's error is shown. A query ordered by an expression
  is wrapped with its ORDER BY inside (SQL Server: `OFFSET 0 ROWS`), where the order is kept in practice but not
  guaranteed by the SQL standard.
- Idle memory of the WPF application is about 370 MB (budget: 200 MB); not investigated yet.
- The editor's zoom is shared by the query tabs (one editor view shows the selected tab).
- The explorer filter searches what has been loaded (expanded) so far, not the whole catalog.
- PostgreSQL: after switching a tab's database, completion and the explorer still read the connection's own
  database (their metadata connection is not switched).
- Each query tab after the first holds its own server connection while it is open (as in SSMS).
- The workspace (`%AppData%\JetQueries\workspace.json`) holds the open tabs' texts as typed, like an editor's
  recovery file: anything typed into a query (including a password) is stored there in plain text until the tab
  is closed. Query history masks password literals; the workspace does not change the user's text.
- Files larger than 50 MB are not opened in an editor tab.
- The Avalonia application still builds (`JetQueries.Avalonia.exe`) but is no longer packaged or developed.
