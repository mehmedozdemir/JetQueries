# Phase 1 · WPF user interface — manual test checklist

The WPF application (`src/JetQueries.Wpf`) replaces the Avalonia one as the product (ADR 0007). It uses the
same view models, so behaviour should match the earlier checklists (`phase1-*.md`); this list covers what is new
or WPF-specific. Automated: view models (Presentation tests), result grid source, and renderings of the shell,
the history tab and the connection dialog in both themes (`tests/JetQueries.Wpf.Tests/.../screenshots`).

```bash
dotnet run --project src/JetQueries.Wpf
```

## Shell
- [ ] The window opens with the UiKit look: menu bar (File, Connection, Query, View, Tools) and toolbar, the
      explorer card on the left, query tabs on the right, status bar at the bottom.
- [ ] Every menu opens with a click and with Alt (Alt+D Dosya…); items show their shortcuts; disabled items
      are dimmed (Query → Run with no connection). View → Explorer hides and shows the explorer with its width.
      View → Dark theme switches the theme and shows a check in the dark theme.
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
- [ ] New connection opens on "Choose the database type": four cards (Oracle, PostgreSQL, SQL Server, SQLite) with
      their marks and a line each; Tab moves between cards, Enter or a click opens the details.
      "Change" next to the type name goes back (only for a new connection; editing opens on the details).
- [ ] Provider settings, sign-in, folder, environment and read-only fields work; Browse picks an SQLite file.
- [ ] "Connection string" (top right of the details): paste a string and "Read and fill in" — the fields, user
      name and password box are filled, the name is suggested (server / database) when empty, a note lists what
      was not taken, and the box is empty again. Try `Server=…;Database=…;User Id=…;Password=…`,
      `jdbc:oracle:thin:@//host:1521/FREEPDB1`, `postgresql://user@host:5432/db?sslmode=require`,
      `Data Source=C:\data\app.db`. A TNS alias (`Data Source=PROD`) is refused with a message; the form is unchanged.
- [ ] Save after pasting: the password is in Windows Credential Manager, not in `connections.json`.
- [ ] The explorer shows each connection with its type's coloured mark before the environment dot.
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

## Run in new tab, result tabs
- [ ] Run a query, then press the green "run in new tab" icon next to Run (or Ctrl+Shift+Enter / Ctrl+\): the
      first result stays and "Result 2" opens after it and is shown; again: "Result 3".
- [ ] Next page, Fetch all, Count, the SQL filter and Refresh (↻ under the grid) on "Result 1" use its own
      query, not the last one; Refresh reads the first page again in the same tab.
- [ ] Each result tab has an × (and closes with a middle click); Messages has none. Run (F5) replaces all
      results and starts again from "Result 1".

## Editor menu, formatting, shortcuts
- [ ] Right-click in the editor: the caret moves where clicked (unless inside the selection); the menu has Run,
      Run in new tab, Cut, Copy, Paste, Select all, Format SQL, Comment / uncomment, Upper / Lower case, Go to
      definition, Show completion, Undo, Redo. Cut / Copy are disabled without a selection.
- [ ] Format SQL (Ctrl+Shift+F) on a connected tab: the whole text, or only the selection, is laid out (clauses
      on their own lines, one column per line, AND / OR on their own lines, subqueries / CTE / CASE indented,
      keywords upper case). Comments, strings and [names] are unchanged; Ctrl+Z undoes it in one step.
- [ ] A procedure, function, package, trigger or `BEGIN … END` block is left as written; `GO` and Oracle `/`
      lines stay on their own lines.
- [ ] Without a connection, Format says to connect first and changes nothing.
- [ ] Ctrl+/ comments the selected lines (or the caret's line) with `-- `, again removes it; Ctrl+Shift+U /
      Ctrl+U upper / lower case the selection or the word at the caret (`id` → `ID`, never `İD`).

## Import connections
- [ ] The import icon over the explorer opens "Import connections": SSMS registered servers (and DBeaver, if
      installed) are listed with name, server / database, group, login and source; connections of a database
      JetQueries has no provider for are listed but cannot be ticked.
- [ ] "Choose file…" reads a `data-sources.json` or `RegSrvr.xml` from elsewhere.
- [ ] Import: the ticked ones appear in the explorer in their folders, with the environment colour; a production
      one is read-only; the first connect asks for the password. Opening the window again marks them "already
      saved".

## Snippets and column editing
- [ ] Type `sel` then Tab: `SELECT *` / `FROM |` (caret after FROM); on an indented line every line keeps the
      indent; Ctrl+Z restores `sel`. `t.sel` + Tab or `xsel` + Tab: a normal Tab. With the completion list open,
      Tab picks from the list.
- [ ] Right-click → Insert snippet → a list with each shortcut; choosing one writes it at the cursor (in place of
      a selection).
- [ ] Settings → Snippets: +, edit shortcut / description / text (coloured), remove; a repeated or empty shortcut
      is refused on Save with a message on that page; saved snippets work at once in open tabs and after a restart;
      Defaults brings the built-in ones back.
- [ ] Column editing: Alt+drag, or Alt+Shift+arrows, selects a box; typing writes on every line of it.

## Result tools
- [ ] Select several cells: under the grid at the right "12 cells · Sum … · Avg … · Min … · Max …" (numbers only;
      text and NULL count as cells). Select all on a large result: the window stays responsive.
- [ ] Cell right-click → Copy as → CSV / JSON / Markdown table / SQL INSERT / SQL IN list; paste into Notepad:
      quotes doubled, NULL as NULL, numbers with a point.
- [ ] The eye icon under the grid (or cell menu → Show value) opens the value panel; it follows the current cell.
      A JSON or XML text is laid out; varbinary shows a hex dump with its size; an image column shows the image;
      NULL shows NULL. The panel can be widened with its splitter and closed with ×.

## Explorer scripts and Go to object
- [ ] Right-click a table: "Select first 1000 rows" opens a tab named after it, in its database, and runs it
      (SQL Server `TOP (1000)`, Oracle `FETCH FIRST`, PostgreSQL / SQLite `LIMIT`).
- [ ] Script → SELECT / INSERT / UPDATE / DELETE open templates with quoted names; run as written, UPDATE and
      DELETE change nothing (`= NULL` on the key, or `WHERE 1 = 0` without a primary key).
- [ ] Script → CREATE opens the definition of a table, view, procedure, function, package; an encrypted one says
      so under the explorer. A database, schema or group has no Script item.
- [ ] Ctrl+Shift+N (or the search icon): type part of a name ("müşteri" finds "Müşteri İşlemleri"); ↑↓ moves,
      Enter or a double click selects the object in the explorer (its parents open), Esc closes. With no open
      connection it says to connect first. A large server: results appear while lists are loading.

## Run the statement at the cursor
- [ ] Several statements separated by `;` or empty lines: Ctrl+Enter with the cursor inside one runs only that
      one (it flashes for a moment); with the cursor right after its `;` too; on an empty line, the one above.
- [ ] A selection: Ctrl+Enter runs the selection. F5 runs the selection or the whole text, as before.
- [ ] `'a;b'`, `-- ;`, `[x;y]` do not end a statement; an `IF … BEGIN … END` block, a `CREATE PROCEDURE` up to
      `GO`, an Oracle `BEGIN … END;` block or package up to `/`, a script starting with DECLARE run whole.
- [ ] Only comments at the cursor: "No statement at the cursor.", nothing is sent.

## Cancel
- [ ] Oracle over VPN: in tab A `UPDATE t SET … WHERE id = 1` in manual commit (keeps the row lock); in tab B
      the same UPDATE waits. Stop in tab B: with ALTER SYSTEM it stops within ~2 s; without it the status says it
      stops when its wait ends. Roll back tab A: tab B's update was not applied.
- [ ] Any provider: a long query, Stop: "Canceling…", then "canceled".

## Transactions
- [ ] A connected tab shows "Auto-commit" after the database selector. Click: "Manual commit" (amber), with
      Commit (✓) and Rollback (↶) next to it.
- [ ] Manual: a SELECT runs as before (no badge). An UPDATE / INSERT / DELETE / EXEC: "Open transaction" badge;
      from another tab (or SSMS) the change is not visible (and a write to the same rows waits).
- [ ] Commit: the badge goes, "Transaction committed." under Messages, the change is visible elsewhere.
      Rollback: the change is gone.
- [ ] Auto-commit with `BEGIN TRAN; UPDATE …` typed in the editor: the badge shows too; Commit / Rollback end it.
- [ ] With a transaction open: switching the database is refused with a message; switching back to
      auto-commit asks Commit / Rollback / Cancel; closing the tab asks; closing the application asks once for
      all tabs (Cancel keeps the window open). A failing commit keeps the tab, with the server's reason.
- [ ] PostgreSQL: after an error inside the transaction the badge stays until Rollback (PostgreSQL rule).

## Settings screen
- [ ] Settings opens a wider window: pages on the left (Appearance, SQL editor, Results, Query tabs, SQL
      formatting), the chosen one highlighted with a bar; the page's title and one line about it at the top;
      Defaults, Cancel and Save at the bottom. Both themes look right; the window can be resized.
- [ ] Appearance: the Dark / Light cards switch the theme at once (the chosen one has a check); font and size
      change the whole window at once, with a sample line (Turkish letters) under them.
- [ ] SQL editor: font and size change the coloured preview at once.
- [ ] Results: grid text size and rows per page; Query tabs: where Run / Cancel / Save are (segmented choice).
- [ ] SQL formatting: keyword case (SELECT / select / As typed), indentation (2 / 4 / 8 / Tab), the two switches;
      the preview below lays the sample query out again at each change.
- [ ] Save keeps everything (also after a restart); Cancel or the title bar's × puts everything back;
      Defaults shows the defaults (kept only with Save).
- [ ] After saving format options, Ctrl+Shift+F in an already open tab uses them.

## Query tabs
- [ ] The × of a query tab closes it (a changed text asks to save first); middle click too.
- [ ] Closing the last tab shows the empty page with "New query" (Ctrl+N).
- [ ] Right-click a tab: Close, Close others, Close all (each changed tab asks; Cancel stops), Copy file path
      (only for a saved file).

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
- [ ] Tools → Query history (Ctrl+Shift+H) opens the history window; a second time brings the same window
      forward. Runs are listed newest first; the selected run's full text is shown on the right with how it
      ended (a failure in red with its message). Search filters after a short pause; Enter, double-click or
      "Open in new tab" reopens a run; Delete removes it; Copy copies its text; Clear all asks first; Esc closes.

## Submenus, Script menu, Copy as
- [ ] Right-click a table: Script opens a submenu (CREATE, DROP, SELECT, INSERT, UPDATE, DELETE); a view or
      routine also has ALTER. Each opens a new tab and runs nothing.
- [ ] ALTER of a SQL Server view/procedure/function/trigger starts with ALTER; Oracle and PostgreSQL views and
      routines start with CREATE OR REPLACE; run as written it succeeds. DROP is schema-qualified and quoted
      (SQL Server adds GO). Tables have no ALTER; SQLite has none.
- [ ] Right-click a grid cell: Copy as opens its submenu; CSV, JSON, Markdown, INSERT and IN list each put the
      selection on the clipboard. The editor's "Insert snippet" submenu opens too.

## Grid headers and column filter
- [ ] Every result column shows its data type under its name (int, nvarchar(50), NUMBER(10,2)…); long types
      are cut with an ellipsis; widths fit the type line.
- [ ] A click on a header's filter icon opens the filter panel and it stays open after the mouse is released;
      a click outside closes it.

## Object details (Ctrl+Click, F12, F4, explorer "Details")
- [ ] Ctrl+Click (or F12) on a table name in the editor opens a details window: header with the type mark,
      schema.name and "Table · connection · database"; tabs Properties, Columns, Constraints, Foreign keys,
      References, Indexes, Triggers, Dependencies (as far as the database has them; empty ones are left out),
      DDL. Counts are shown in the tab titles.
- [ ] The same from the explorer: right-click → Details, or select and press F4. Opening the same object again
      brings its window forward instead of a second one.
- [ ] Double-clicking a row in Foreign keys, References or Dependencies opens that object's details.
- [ ] Triggers: the selected trigger's source is shown under the list.
- [ ] A procedure/function shows Parameters (with RETURN for a function's result, OUT for output parameters);
      an Oracle package lists its routines' parameters with an extra Routine column; a sequence shows start,
      increment, limits, cache and current value in Properties.
- [ ] "Open data" runs the first 1000 rows in a new query tab; "Open in editor" opens the DDL in a tab; F5 or
      the refresh icon reads everything again; Esc closes.
- [ ] Each database: SQL Server (rows and size from sys.partitions, filegroup), Oracle (NUM_ROWS statistics,
      tablespace, status, ALL_DEPENDENCIES), PostgreSQL (reltuples estimate, pg_size_pretty, views through
      pg_rewrite), SQLite (no row count; foreign keys and indexes from PRAGMAs).
- [ ] "Open source in a new tab" in the editor menu still opens the source text in a query tab.

## Which connection a tab runs on
- [ ] A query tab on a connection shows a dot in the connection's environment colour before its title; the
      query toolbar shows the connection's name with the same dot (tooltip: the connection this tab runs on).
- [ ] On a Test or Production connection a strip in the environment's colour sits above the toolbar
      ("ÜRETİM · Kartlar — canlı"); Development and unmarked connections have none. Switching the tab's
      connection updates dot, name and strip at once.
- [ ] The explorer's action icons all fit at the default panel width; a narrower panel wraps them to a second
      line. Connect/disconnect is an icon whose tooltip says which.
- [ ] Connection editor in Turkish: SQL Server's Authentication (SQL Server oturumu / Windows (tümleşik)) and
      Encryption (Evet / Hayır / Katı), PostgreSQL's SSL, SQLite's open mode, Oracle's labels and hints are
      Turkish; the saved values are unchanged (an existing connection opens with the same choices).

## P2 refinements
- [ ] The main toolbar shows icons only (new connection, new query, open; save/run/run in new tab/cancel when
      the settings put them there; settings, theme); every icon has a tooltip.
- [ ] Tools → Keyboard shortcuts (F1) lists the shortcuts under General, Editor, Results, Explorer; typing in
      the search box narrows by keys ("ctrl+shift") or action ("biçim"); Esc closes; F1 again brings it forward.
- [ ] Explorer: a connected connection is bold with no "connected" badge. A script that cannot be written shows
      a banner at the top of the explorer with an × that hides it.
- [ ] Query toolbar: in manual-commit mode with an open transaction, "Commit" and "Rollback" are written out next
      to their icons. The database box is narrower.
- [ ] Results: a toolbar above the grid with Izgara / Metin / Kayıt and "SQL filtresi". The SQL filter bar is
      hidden until that button is pressed (the box gets the focus); while a filter is applied it stays open, and
      clearing the filter hides it again. The footer reads "Sayfa [200] Sonraki Tümü | Say [n] Değer".
- [ ] Object details (Ctrl+Click, F4, explorer Details) open as a tab beside the queries with the object's icon
      and environment dot; asking again for the same object selects its tab; middle click, × and the tab menu
      (Close, Close others, Close all) close it. With a details tab selected, Run and Save in the menus do nothing.
- [ ] History window: "Tüm bağlantılar" lists the connections with runs; choosing one shows only its runs;
      "Yalnızca hatalılar" shows failed runs only; filters combine with the search; with no match it says so.
- [ ] Light theme: captions, placeholders and NULLs are darker than before and readable on white and grey.

## P3 polish
- [ ] Ctrl+Shift+P (or Tools → Command palette) opens a search card over the window: typing narrows the menu's
      commands (each word must match the name, the menu or the shortcut; "içe" finds "İçe aktar"); ↑↓ and Enter
      run one, Esc or a click elsewhere closes it; commands that cannot run now are listed last and do nothing.
- [ ] The run's status ("3 rows · 28 ms", errors) is in the status bar only, not also on the query toolbar.
- [ ] View → Explorer off, quit, start: the explorer stays hidden; on again, resize it, quit, start: the width
      is kept.
- [ ] Expanding a large group shows a small turning arrow after its name while it loads; then the group shows
      its count ("Tablolar (128)").
- [ ] Right-click a table, view or routine → "Favorilere ekle": a "Favoriler" group appears at the top of the
      connection with the object (a star after its name everywhere); "Favorilerden çıkar" on it, or on its copy
      under Favoriler, removes it, and the group goes with the last one. Favourites are kept after a restart
      (`%AppData%\JetQueries\favorites.json`, names only).
- [ ] The editor's zoom badge appears only away from 100 %.
- [ ] Settings → Sonuçlar → "Başlıklarda veri tipi" off: result headers lose the type line at once (and get
      lower); on: it comes back.
- [ ] Connection editor: the environment is a row of chips (—, Geliştirme, Test, Üretim) with their colours.
- [ ] Keyboard only (Tab): type cards, segments, switches, check boxes, the Advanced toggle and tabs show an
      accent focus ring when reached with the keyboard (not on mouse clicks).

## Round 2 (frame, splitter, folders, import, colour schemes)
- [ ] A query on a Development / Test / Production connection is framed (toolbar, editor and results) in
      green / amber / red; there is no "TEST · name" strip and no connection name on the toolbar. Hovering the
      frame says "TEST · name". An unmarked connection: no frame. Switching the tab's connection changes it.
- [ ] Between editor and results: the cursor turns ↕ over the gap, a small grip is visible, dragging up and
      down resizes both (down to 60 px each).
- [ ] Explorer: the folder icon in the toolbar (or right-click → New folder) asks a name and adds an empty
      folder under the selected folder (or at the top); it stays after a restart. A name with "/" is refused.
- [ ] Drag a connection onto a folder: it moves there (the folder is highlighted while over it); onto another
      connection: into that connection's folder; onto empty space: to the top. Drag a folder into another: it
      moves with everything inside; never into itself or its own subfolder (no highlight, no drop).
- [ ] Right-click a folder → Rename folder: connections and subfolders follow. Delete folder asks first; what
      was inside moves up one level, no connection is deleted.
- [ ] Connection → Import connections: three cards (DBeaver, SQL Server Management Studio, Dosyadan) saying
      whether settings were found here; choosing one lists only its connections; "Kaynaklar" goes back. "Klasörlerini
      koru" off and "Hedef klasör: DBeaver" puts every imported connection directly into "DBeaver".
- [ ] Settings → Görünüm: under the theme, colour schemes with a small picture each — dark: Gece (default),
      Grafit, Okyanus, Yüksek kontrast; light: Kağıt (default), Gökyüzü, Kum, Yüksek kontrast. A click applies at
      once (title bar too); Vazgeç puts back the previous scheme; the choice survives a restart.

## Round 2 findings
- [ ] Right-click a connection: "Bağlantıyı düzenle… (F2)" and "Bağlantıyı sil (Del)"; the same in Bağlantı menu
      and the command palette. Del on a selected connection asks and deletes it (its saved password too); Del
      on a folder deletes the folder (contents move up); F2 edits a connection or renames a folder.
- [ ] One title bar: the menu row is the caption. Drag it to move the window, double-click to maximize; the
      minimize / maximize (restore when maximized) / close buttons at the right; close turns red on hover.
      Maximized, nothing is cut off at the screen edges; Windows snap (Win+arrows) works.
- [ ] The main toolbar has no new connection / new query / open icons; a "+" at the right of the query tab row
      opens a new query.
- [ ] Results: "SQL filtresi" and Izgara / Metin / Kayıt are at the right of the result tab row (hidden on
      Mesajlar); there is no separate result toolbar row.
- [ ] Manual commit with an open transaction: Commit and Rollback are plain text with a green / red icon.
- [ ] Before the first run the results area shows F5, Ctrl+Enter and Ctrl+Shift+Enter as keys with what they do.
- [ ] Explorer: Ctrl+click picks several connections (highlighted); dragging one of them moves all; a plain
      click forgets the picks. While dragging, a caption beside the cursor says where the drop lands ("Şuraya
      taşı: Müşteri A", "3 bağlantıyı taşı: en üst düzey"); Esc cancels.
- [ ] Connection editor → Gelişmiş: the timeout box, then the switches on one row, without gaps.
- [ ] Settings → Görünüm: each scheme card shows a line of SQL in the scheme's keyword and string colours.

## IntelliSense: list, signature help, parameter values, Ctrl+Click feedback
- [ ] Typing "SELECT DATEA" opens the list as a card: each row has a coloured badge for its kind (function,
      table, column, keyword…), the name in the code font and, at the right, the signature or type. The first row
      is selected; the selected row has an accent bar. A card beside the list explains the selected function
      ("Tarihe verilen birimden belirtilen kadar ekler", → datetime). The hint line says ↑↓ / Enter / Tab / Esc.
- [ ] With the list open after "AND ts", typing "." closes it and opens the alias's columns; typing letters no
      row matches closes the list (no empty card with only the hint line is ever left).
- [ ] Enter on DATEADD inserts "DATEADD()" with the caret between the brackets; a card above the line shows
      DATEADD(datepart datepart, number int, date datetime) → datetime with the current parameter underlined, and
      the list of dateparts (year, quarter, month…) with their meaning opens at once.
- [ ] Typing "," moves the underline to the next parameter; "(" inside an argument (ABS(…)) shows that call;
      leaving the call (")" or moving the caret out) closes the card; Esc closes it; Ctrl+Shift+Space (or the
      editor menu "Parameter info") opens it again.
- [ ] Values: CONVERT(varchar(10), GETDATE(), ‸ offers the date styles (104 = dd.mm.yyyy …); FORMAT(x, '‸ offers
      quoted formats; ROUND's third argument 0 / 1; JSON_VALUE paths; HASHBYTES algorithms.
- [ ] Overloads (functions with several forms, e.g. in PostgreSQL/Oracle catalogs): ▲▼ 1/2 in the card; ↑↓ switch
      while no list is open.
- [ ] User routines: "EXEC dbo.SomeProc " then "," shows the procedure's parameters (OUT marked); "SELECT
      dbo.SomeFunction(" shows the function's parameters and return type (read from the database).
- [ ] Oracle: TO_CHAR(d, '‸ offers date masks; TRUNC(d, '‸ units; SYS_CONTEXT('USERENV', '‸ parameters; a package
      function's parameters. PostgreSQL: date_trunc('‸ fields, to_char formats, overloads of a function.
      SQLite: strftime formats, date() modifiers.
- [ ] Ctrl over a name: the name is underlined in the accent colour and the cursor is a hand (keywords and
      strings are not). Ctrl+Click: a chip "Nesne aranıyor…" with a turning arrow at the editor's top right and a
      waiting cursor until the details tab opens; for a name that is not found the chip says so for 3 s.

## Connection editor (compact)
- [ ] The form is two columns: Name + Environment (with its colour) on top, the type's main settings two to a
      row, user and password side by side, "Save password" and "Read-only" switches, then "Advanced" (folder,
      timeouts, certificate trust, other switches) closed by default.
- [ ] Advanced opens by itself when a saved connection or a pasted connection string sets one of its fields.
- [ ] SQLite's file field takes the full width with Browse. Inputs and combo boxes line up at one height.
- [ ] Test connection: the result appears in a card above the footer, green on success, red on failure.

## Known limits
- Oracle parameter defaults show only whether there is one; PostgreSQL keeps no created/modified dates.
- Header sorting and column filters apply to the rows received; the SQL filter box above the grid is the one
  that filters on the server.
- Nothing stays open on the server between pages (checked on SQL Server and PostgreSQL: DDL on the table is not
  blocked, no snapshot is held). While a page is being read the statement takes the usual short-lived read locks
  of any SELECT. "Next page" and "Fetch all" run the query again: each costs a new execution, and the rows shown
  are those of that execution (they may differ from the first page if the data changed meanwhile).
- SQL Server: a query starting with WITH is not paged (SQL Server cannot count it) and is read to its end.
- Column filters are combined all with AND or all with OR (no mixed groups; the SQL filter box takes any condition).
- Multiple independent cursors (Ctrl+click carets) are not supported by the editor component (AvalonEdit); box
  selection (Alt+drag) covers editing several lines at once.
- Connection strings: TNS aliases (tnsnames.ora), Azure AD authentication other than integrated, Oracle wallets
  and encrypted SQLite files are not read (listed as not taken or refused).
- Import reads DBeaver and SSMS (registered servers); DataGrip, and SSMS's recent-server list, are not read.
  Passwords are never imported. SSH tunnels are not supported yet.
- Go to object searches what the explorer can list (all databases of a connection, loaded once and cached);
  favourites in the explorer are not there yet.
- Disconnecting a connection from the explorer while a tab has an open transaction does not ask: the server
  rolls it back.
- Oracle and PostgreSQL count a transaction as open once it has changed something (a BEGIN alone is not).
- Formatting needs a connection (the formatter follows its dialect). A statement it does not recognise (DDL,
  EXEC, SET…) is left as written. The settings previews use the SQL Server language when it is loaded.
- Ctrl+/ may need another key on keyboards where "/" is typed with Shift (Turkish Q: use the menu, or numpad /).
- SQL filter: SQL Server cannot wrap a query starting with WITH, nor one whose
  columns repeat a name (`SELECT a.id, b.id …`): the server's error is shown. A query ordered by an expression
  is wrapped with its ORDER BY inside (SQL Server: `OFFSET 0 ROWS`), where the order is kept in practice but not
  guaranteed by the SQL standard.
- Idle memory of the WPF application is about 370 MB (budget: 200 MB); not investigated yet.
- The editor's zoom is shared by the query tabs (one editor view shows the selected tab).
- The explorer filter searches what has been loaded (expanded) so far, not the whole catalog.
- PostgreSQL: each database read (explorer, completion, details) holds a metadata connection to it for the rest of
  the session.
- Each query tab after the first holds its own server connection while it is open (as in SSMS).
- The workspace (`%AppData%\JetQueries\workspace.bin`) holds the open tabs' texts as typed, encrypted for the
  current Windows user (DPAPI). Query history masks password literals; the workspace does not change the user's text.
- Files larger than 50 MB are not opened in an editor tab.
- The Avalonia application still builds (`JetQueries.Avalonia.exe`) but is no longer packaged or developed.

## Object details speed (Oracle) and editor spacing
- [ ] Ctrl+Click a table on an Oracle connection: the details tab opens at once with the moving bar; the Columns tab
      fills in first, then Constraints, Foreign keys, Indexes and References appear; the bar stops when all are in.
- [ ] While the tabs fill in, the tab you are on stays selected (Properties, DDL or a section tab).
- [ ] The second look at the same table (Refresh, or Ctrl+Click again) is near instant.
- [ ] The PK/FK marks in the Columns tab appear (a moment after the columns on a large dictionary).
- [ ] In the query editor the text has a clear gap from the dotted rule after the line numbers.

## Workspace encryption
- [ ] Type a query in a tab and close the app: %AppData%\JetQueries has workspace.bin (unreadable bytes) and no
      workspace.json; at the next start the tab and its text are back.
- [ ] Starting a build after this change with an old workspace.json: the tabs come back and workspace.json is gone
      after the first change.

## Functions without brackets
- [ ] Oracle: "SELECT SYSD" offers SYSDATE as a function (badge, "Veritabanı sunucusunun şu anki tarih ve saati");
      Enter inserts SYSDATE with no brackets and no parameter card. Same for SYSTIMESTAMP, USER, ROWNUM.
- [ ] SQL Server CURRENT_TIMESTAMP, PostgreSQL current_date / current_user, SQLite current_timestamp: one row each
      (no second "keyword" row), inserted without brackets.

## PostgreSQL: completion after switching database
- [ ] Open a PostgreSQL connection, switch the tab's database to another one: "SELECT * FROM " offers that
      database's tables, "alias." its columns; switching back offers the first database's tables again.
- [ ] Ctrl+Click a table of the other database: its details open (subtitle shows that database), with columns.

## Oracle TNS aliases and wallet
- [ ] New connection > Oracle: "Bağlantı biçimi" is "Sunucu ve servis adı" with host, port, service, SID as before.
- [ ] Switch it to "TNS adı (tnsnames.ora)": host/port/service/SID disappear (port also from "Gelişmiş"); TNS adı,
      TNS klasörü and Wallet klasörü appear, each folder on a row with "Gözat…" opening a folder picker.
- [ ] Choose the folder of your tnsnames.ora: the TNS adı list shows its aliases as "ALIAS — host:port/service";
      picking one fills the box; any other name can still be typed. With the folder empty, TNS_ADMIN is used.
- [ ] Save without an alias: "TNS adı" is reported as required. Save with one: the connection opens and runs a query
      (with VPN, against a real alias). Reopen the editor: the TNS fields are back, no stale host is kept.
- [ ] Wallet (Autonomous Database / TCPS): point both folders at the unzipped wallet and use a *_high alias.
- [ ] Secure external password store: with a wallet made by `mkstore -createCredential <alias> <user>` and a
      sqlnet.ora with `SQLNET.WALLET_OVERRIDE = TRUE`, tick "Kullanıcı ve parola wallet'tan" (under Gelişmiş): the user
      and password fields go away, and the connection opens with the wallet's credential.
- [ ] Paste "User Id=hr;Password=…;Data Source=PRODDB;Tns_Admin=C:\oracle\network\admin" in the connection string
      tab: the form switches to TNS adı with PRODDB and the folder filled in.

## Result filters: SQL box completion and OR
- [ ] Open the SQL filter box (funnel in the result tab row): it is a one-line SQL box with the code font and
      colouring; empty, it shows "Enter a SQL expression to filter results".
- [ ] Ctrl+Space (or typing a letter) at the start lists the result's columns with their types; after a column and
      a space the list offers AND, OR, NOT, LIKE, IN, IS NULL, IS NOT NULL, BETWEEN; after AND / OR the columns again.
      A column whose name has spaces is inserted quoted. Nothing is offered inside a 'string'.
- [ ] Enter applies the filter (with the list open, Enter picks the item instead); Esc puts back the applied
      filter. Pasting text with line breaks puts it on one line.
- [ ] Add two column filters (header funnel or "Bu değere göre filtrele"): "Hepsi (VE)" appears before the chips;
      clicking it turns to "Herhangi biri (VEYA)" and the rows matching either filter show; the row count follows.
      With one filter the switch is hidden.

## Oracle package members
- [ ] In the editor, Ctrl+Click the member part of `pkg.proc(…)`: the details of PROC open (title OWNER.PKG.PROC,
      Package property, Parameters with an Overload column when the routine is overloaded). Ctrl+Click on the `pkg`
      part opens the package as before.
- [ ] F12 on `pkg.proc` opens a tab with its source: "-- PACKAGE OWNER.PKG" and the declaration(s), then
      "-- PACKAGE BODY OWNER.PKG" and the implementation(s); the other routines of the package are not there.
- [ ] In the explorer, expand a package: its routines open their own details (Details / F4) and DDL tab.
- [ ] Signature help on `pkg.proc(` still shows each overload.

## PostgreSQL explorer: databases
- [ ] Expand a PostgreSQL connection: its first level is the server's databases (templates and those you may not
      connect to are left out). Expanding one shows its schemas, then Tables, Views, Functions… of that database.
- [ ] A table of another database: Details (F4) and DDL show it; "Select rows" opens a query tab whose database
      selector is that database, and the query runs there.
- [ ] Refresh on a database node reads it again.

## SQLite CHECK constraints
- [ ] Details of an SQLite table with CHECK constraints: Constraints lists them as CHECK with their name (when given),
      the column (for a column constraint) and the condition; the selected one's condition shows under the list.

## Execute a stored procedure or function
- [ ] Right-click a procedure or function in the explorer: "Çalıştır…" opens the dialog: header with the routine, then
      one row per parameter (name, type, IN / OUT / IN OUT, how it is passed, value) and the script below.
- [ ] Optional parameters start as "Varsayılan", the others as NULL; typing a value turns the row to "Değer"; "İfade"
      writes the text as it is (GETDATE(), SYSDATE - 1); a pure OUT parameter takes no value. The script follows
      every change.
- [ ] SQL Server procedure: DECLARE @return_value and a variable per OUTPUT parameter, EXEC with named arguments,
      then SELECT of the return value and the outputs. "Çalıştır" opens a query tab in the routine's database and
      runs it: the grid shows them. Scalar function: SELECT …(…) AS [Result]; table function: SELECT * FROM …(…).
- [ ] Oracle (also a package member): an anonymous block; after "Çalıştır" the Messages show "P_OUT = …" and
      "Result = …"; a SYS_REFCURSOR OUT parameter comes back as a result tab.
- [ ] PostgreSQL: CALL for a procedure (OUT values as a row), SELECT * FROM for a function.
- [ ] A routine with overloads (Oracle, PostgreSQL) has a "Biçim" list in the header; each form has its own rows.
- [ ] "Betiği aç" only opens the script; Vazgeç / Esc does nothing. The details view of a routine has "Çalıştır" too.

