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
- Idle memory: with the GPU about 240–300 MB private (mostly the graphics driver's surfaces; the managed heap is
  under 10 MB), in software rendering about 90 MB. The 200 MB budget is met with hardware acceleration off.
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

## Compact mode
- [ ] Görünüm > Kompakt mod: at once the toolbars, the explorer rows, the query and result tabs, the inputs, the menus
      and the result grid rows become lower; the text size stays. The menu item shows a tick; clicking again goes back.
- [ ] Ayarlar > Görünüm > Arayüz yoğunluğu shows Normal / Kompakt (the same setting); the preview follows; Kaydet keeps
      it, Vazgeç puts the previous one back.
- [ ] Restart the application: the chosen density is back.
- [ ] Both themes and every colour scheme look right in compact mode (no clipped text in buttons, combos, tabs).

## In-app updates
- [ ] Ayarlar > Güncellemeler: Kanal, Güncelleme kaynağı, Açılışta denetle, the running version and "Şimdi denetle".
- [ ] Running from artifacts/publish (not installed): "Şimdi denetle" says updates work in the installed application.
- [ ] Installed with artifacts/releases/win-x64/JetQueries-win-stable-Setup.exe: set the source to that releases folder,
      then build a newer one (`scripts/pack.ps1 -Version 0.1.1`): "Şimdi denetle" says 0.1.1 is available; the status
      bar chip shows it with "İndir"; after the download "Yeniden başlat ve güncelle" closes the window, the update is
      applied and 0.1.1 starts with the same tabs.
- [ ] With "Açılışta denetle" on, a newer release shows in the status bar shortly after start; with none, nothing shows.

## Memory and start-up
- [ ] Ayarlar > Görünüm > Donanım hızlandırma (GPU) off, restart: Task Manager shows much less memory (about 150 MB
      working set instead of 300+); scrolling a large result is still usable. On again after a restart.
- [ ] `$env:JETQUERIES_BUDGET_EXE="…\artifacts\publish\win-x64\JetQueries.exe"; dotnet test tests/JetQueries.Wpf.Tests --filter BudgetTests`
      passes (window under 1.5 s, idle under 200 MB in software rendering).

## SSH tunnel
- [ ] Connection editor (SQL Server, PostgreSQL, Oracle by host): "SSH tüneli" opens the SSH fields: server, port,
      user, Parola / Özel anahtar, key file with Gözat…, the password or passphrase and "SSH parolasını … sakla".
      SQLite has no such section. The database host and port above are as the SSH server sees them.
- [ ] Bağlantıyı sına through a real bastion: succeeds; a wrong SSH password says "SSH: …".
- [ ] Without a saved SSH password, connecting from the explorer asks for "… için SSH parolası" and opens the session.
- [ ] While connected: queries, the explorer, completion and a second query tab all work (all go through the
      tunnel); disconnecting closes the tunnel.
- [ ] If the SSH server's key changes, the connection is refused (the key in ssh_known_hosts.json next to
      connections.json; delete its line to trust the new key).
- [ ] Deleting the connection removes its SSH password from Windows Credential Manager too.

## Export a result
- [ ] Run a SELECT, then "Dışa aktar…" in the result footer (or Sorgu > Dışa aktar…): the dialog offers CSV, Excel,
      JSON, SQL INSERT, Markdown, HTML; changing the format changes the file's extension; CSV shows separator, header
      and NULL options, SQL INSERT the table name.
- [ ] "Gösterilen satırlar": the file has the rows of the grid in its order, with its filters and only the visible
      columns. "Tüm sonuç" (single SELECT only): the query runs again and every row is written, even beyond the page
      fetched; the status bar counts the rows.
- [ ] CSV with ";" opens in Turkish Excel with columns split and Turkish characters right; the .xlsx opens with numbers
      as numbers, dates as dates, the header row frozen; "Bitince dosyayı aç" opens it.

## Import data from CSV
- [ ] Right-click a table: "Veri içe aktar…" (SQL Server, PostgreSQL, Oracle, SQLite). Choose a CSV: the first rows show,
      the separator is detected (or chosen), Turkish characters read right (UTF-8, or Windows-1254 for older files).
- [ ] Each table column takes the file column of the same name (ignoring case and spaces), else "(atla)"; without a
      header line, by position. Changing a mapping by hand works.
- [ ] "İçe aktar": the rows are written; the count grows while it runs; the result says how many. Numbers like
      1.250,50, dates like 15.01.2026 and evet/hayır are taken.
- [ ] A value that does not fit (text in a number column) stops with "Satır N, kolon X: …"; with "Ya hepsi ya hiçbiri"
      nothing stays in the table. "Vazgeç" while it runs stops it.
- [ ] On a production connection a confirmation comes first; on a read-only connection the import is refused.

## Data editor
- [ ] Run `SELECT * FROM <a table with a primary key>`: the footer shows "Satır ekle", "Satır sil", "Kaydet (0)",
      "Değişiklikleri geri al". A join, GROUP BY, DISTINCT, a result without the key columns or a read-only connection
      shows a "Salt okunur" badge whose tip says why.
- [ ] Double-click a cell (or F2, or start typing): a box opens over it; Enter keeps the value and goes down, Tab goes
      right, Esc drops it. The changed cell is highlighted and "Kaydet (1)" counts it. Typing text into a number column
      shows the reason in red and keeps the old value; an empty cell becomes NULL (also "NULL yap" in the menu).
- [ ] Delete (or "Satır sil") strikes the selected rows through; again unmarks them. Alt+Insert (or "Satır ekle") adds a
      highlighted row at the end and opens its first cell.
- [ ] "Kaydet" (or Ctrl+S in the grid) writes the changes and reads the rows again; the Messages tab says how many.
      Turkish text (İ, ş, ğ) is saved as typed. On a production connection a confirmation comes first.
- [ ] Change a row, delete it from another tab, then save: an error names the key ("id = …") and nothing is written.
- [ ] With unsaved changes, Next page / All / Refresh / a SQL filter ask before dropping them; "Değişiklikleri geri al"
      puts the read values back and removes added rows.

## Parameterized queries
- [ ] Run `SELECT * FROM <table> WHERE <column> = :value`: "Sorgu parametreleri" asks for `:value`; Otomatik sends 42 as
      a number, "0532…" and "İstanbul" as text; the result is filtered. Vazgeç sends nothing.
- [ ] `${table}` asks for text that goes into the SQL as typed (e.g. `SELECT * FROM ${table}`).
- [ ] The same name twice is asked once; running again offers the last values. Refresh / Next page / Count / Export
      do not ask again and use the same values.
- [ ] Not asked for: `':x'` in a string, `-- :x` in a comment, PostgreSQL `x::date`, Oracle `v := 1`, a trigger's
      `:new.col`.

## Saved queries
- [ ] Sorgu > Kütüphaneye kaydet (Ctrl+Shift+K): name, folder (existing ones offered, "a/b" for sub folders),
      description and "<connection> bağlantısında aç". The tab takes the name; saving again updates the same query
      without asking; "Kütüphaneye yeni olarak kaydet…" always asks.
- [ ] Araçlar > Kayıtlı sorgular (Ctrl+Shift+Q): queries grouped by folder, a pencil mark on those with parameters;
      the search finds any word of name, folder, description or SQL ("istanbul" finds "İstanbul").
- [ ] Enter / double-click / "Aç" opens it in a new tab on its connection; "Çalıştır" (Ctrl+Enter) opens and runs it
      (asking its parameters); "Düzenle…" (F2) renames or moves it; Delete asks, then removes it.
- [ ] After a restart the library is still there (%AppData%\JetQueries\saved-queries.json) and a restored tab still
      saves back to its query.

## Sync (two computers, or two Windows users)
- [ ] Settings > Eşitleme: turn it on, choose a folder both computers see (OneDrive or a share), Kaydet. The status says
      when it synced; the folder has JetQueries.sync.json. Open it: servers, databases, user names, saved queries; no
      password.
- [ ] On the second computer, the same folder: its explorer gets the connections and Kayıtlı sorgular gets the queries
      without a restart. Connecting asks for the password once (if "save password", it is kept there).
- [ ] Rename a connection on one, delete a saved query on the other; within about 2 minutes (or "Şimdi eşitle") both
      show the same. Edit on one while deleting on the other: the edited item stays on both.
- [ ] Disconnect the share / rename the folder: the status says it is not reachable; nothing local is lost; it syncs
      again when the folder is back.

## Redis
- [ ] New connection > Redis: host, port, database, authentication (none / password / ACL user), TLS. Test and
      connect; the explorer shows db0 (and every database that has keys) with its key count.
- [ ] Expand a database: keys grouped by ":" with counts ("user (120)"), keys at the leaves. Over 5 000 keys a
      message says to filter; right-click the database > "Anahtarları filtrele…" with user:* lists only those.
- [ ] Double-click a key (or right-click > Aç): a tab with its type, TTL and size. A hash: fields and values,
      the selected one in the editor; change the value > "Öğeyi kaydet", "Yeni olarak ekle", the bin icon removes it.
      A list (index), set, sorted set (score) and stream (field=value lines) work the same way.
- [ ] A string: edit and Kaydet (its TTL stays); "JSON biçimlendir" indents JSON. "TTL…" sets seconds (empty: none),
      "Yeniden adlandır…" refuses a name in use, "Anahtarı sil" asks first.
- [ ] New query on the connection is the console: `HGETALL user:1`, `LRANGE queue 0 -1`, `INFO memory`, `SELECT 2`
      (the database selector follows), completion offers commands with arguments. `KEYS *` is refused with the
      reason. Ctrl+Enter runs the lines around the cursor up to an empty line.
- [ ] On a read-only connection `SET x 1` is refused and the key tab's buttons are off. On a production connection
      `DEL x` / `FLUSHDB` ask first, and so does removing an item or a key.

## Application icon
- [ ] The installed app shows the blue database-and-bolt icon on its Start menu and desktop shortcuts, in the taskbar
      (also pinned), in Alt+Tab and in Explorer for JetQueries.exe; Setup.exe has it too.
- [ ] The same mark is at the left of the title bar, sharp at 100 %, 150 % and 200 % scaling, in light and dark themes.

## Editor assistance (value comparison, hover)
- [ ] `SELECT * FROM <table> WHERE ` then choose a text column from the list (Enter or Tab): ` = ''` is added and the
      caret is between the quotes; an nvarchar column on SQL Server gets ` = N''`. Same after AND / OR / ON / HAVING
      and in `UPDATE … SET a = 1, ` for the next column. A number or bit column, the select list and ORDER BY get
      nothing added.
- [ ] Rest the mouse on a table name or alias: a card with "TABLO", its name and columns (key icon, types,
      NOT NULL). On a column: its type and table, primary key and NULL / NOT NULL. Keywords and strings show nothing.
- [ ] Settings > SQL editor > Yardımcılar: turning either off stops it at once; both are on in a new installation.

## Explorer pin, expand / collapse, compact result tools
- [ ] The explorer's filter box is smaller; the clear (x) button stays inside it.
- [ ] Expand all (down chevron beside the filter): folders and every already loaded node open; closed connections stay closed, unopened tables are not loaded.
- [ ] Collapse all (up chevron): the whole tree closes; opening a connection again shows its first level only.
- [ ] The pin (right of the filter) is on by default. Click it: the explorer disappears, the tabs take the room, a "Gezgin" rail stays at the left edge.
- [ ] Click the rail: the explorer opens over the tabs with a shadow; double-click a table, work in it; click in the editor or press Esc: it closes.
- [ ] Restart the app: it comes back unpinned; pin it again: it sits beside the tabs at its former width.
- [ ] View > Gezgin hides both the panel and the rail.
- [ ] Results: the filter icon and the grid / text / record icons sit inside the result tab row and do not cover the column headers; tooltips name them; the chosen view is highlighted.

## UX review pass
- [ ] Query toolbar: "Çalıştır" with a label; the connection box shows the tab's connection with its colour dot (green / amber / red). Pick another open connection: the database box and IntelliSense follow it. While a query runs or a transaction is open, the box is disabled.
- [ ] Hover the tab: file or name, then "connection · database".
- [ ] Status bar right: "Sat 5, Süt 12"; select text: "· 42 seçili".
- [ ] A decimal(12,2) column on SQL Server and a NUMBER(12,2) on Oracle show 1500.00 (grid, Text, Record, copy as Markdown, export CSV).
- [ ] Result footer: "Satır/sayfa" with a smaller box; hovering the fetch status shows it in full.
- [ ] Explorer toolbar: +, connect, refresh, search, "…"; "…" opens New folder, Edit, Delete, Import, each working on the selection.
- [ ] A SQL Server connection: user databases first, then a closed "Sistem veritabanları" folder with master, model, msdb, tempdb.
- [ ] History: a SELECT says "satır alındı", an UPDATE "satır etkilendi"; dates line up on the right; the chosen row is tinted, not boxed.
- [ ] Settings > Görünüm > Dil: choose English, restart: menus and messages are English, numbers still Turkish style; back to "Windows dili".
- [ ] Turkish screens say "Sütun", "Veri türü" (details, import, export, record view).

## Database picker with a filter
- [ ] Click the database box in a query tab: a list opens with the caret in "Veritabanlarını filtrele…", the current database highlighted.
- [ ] Type part of a name (e.g. "arş" for "Arşiv"): only matching databases remain; nothing matches: "Eşleşen veritabanı yok."
- [ ] Enter takes the first match (or the one chosen with Down / Up); a click takes one; the editor gets the focus back and the next run goes to that database.
- [ ] Esc or a click outside closes without changing; clicking the box again while open closes it.
- [ ] Oracle: the same picker lists schemas.

## Explorer starts collapsed
- [ ] Start the app: only the top-level folders and connections are shown, every folder closed.
- [ ] Open a folder, refresh the explorer (or add a connection): it stays open.
- [ ] Ctrl+T (Go to object) on a table of a connection inside a closed folder: the folders open and the table is selected.

## Activity monitor (SQL Server)
- [ ] Connect to a SQL Server; right-click the connection: "Etkinlik izleyici" is there (not on a disconnected one, not on PostgreSQL/Oracle/SQLite/Redis yet). Tools > Etkinlik izleyici and Ctrl+Shift+A open the same tab for the selected connection (or the query tab's).
- [ ] The tab shows counters, the sessions grid, and updates every 5 s ("Güncellendi hh:mm:ss"); change to 2 s / 30 s; untick "Her": it stops; F5 reads once.
- [ ] Switch to a query tab and back: it pauses while hidden (no reads in a profiler / Extended Events), resumes when shown.
- [ ] Blocking: in tab A `BEGIN TRAN; UPDATE t SET x = x WHERE id = 1;` (do not commit), in tab B `SELECT * FROM t WHERE id = 1`. The monitor shows A's session first as "engelliyor (1)", B's as "engellendi" with LCK_M_S and the wait time; "Engellenen 1" is outlined red; the chain card shows A (boşta, transaction açık) → B.
- [ ] Choose B in the chain: the grid and the side panel follow; the side panel shows the SQL; "Sorgu sekmesinde aç" opens it in a new tab on the same connection and database (not run).
- [ ] Choose A, "Oturumu sonlandır…": the confirmation names the session, login, host and program; Cancel changes nothing; OK ends it, B finishes, A's tab gets an error on its next statement. On a production connection a second confirmation asks.
- [ ] The monitor's own session ("Bu izleyicinin kendi oturumu") cannot be ended; on a read-only connection there is no end button.
- [ ] Filter "lck" / a login / a host / part of the SQL; "Boştaki oturumlar" and "Sistem oturumları" add those sessions.
- [ ] A login without VIEW SERVER STATE: a yellow note says only its own sessions are visible.
- [ ] Disconnect the connection while the monitor is open: it says the connection was closed and stops refreshing.
- [ ] Close the monitor tab: no more reads (its connection is closed; check sys.dm_exec_sessions from another tab).

## Performance analysis (SQL Server)
- [ ] Right-click a connected SQL Server: "Performans analizi"; right-click a database under it: the same with that database chosen. Tools > Performans analizi works for the selected connection or the query tab's.
- [ ] Ağır sorgular: the list fills for all databases; change the order (Ortalama süre…) and "İlk 100": it reads again; choose a database: only its statements. The note says since when the server counts. JetQueries' own monitoring queries are not in the list.
- [ ] Choose a row: its full SQL is on the right; "Sorgu sekmesinde aç" puts it in a new tab on the right database, not run.
- [ ] Eksik index'ler: after running a query on an unindexed column a few times, a proposal appears with impact and score; its CREATE INDEX opens in a tab; "Görünenlerin betiği" opens all of them under a review comment.
- [ ] Index kullanımı: with "Tüm veritabanları" it asks for a database; choose one: unused / same as / covered by findings with DROP INDEX scripts; untick "Yalnızca bulgular": every index, primary keys and clustered ones without a script.
- [ ] Parçalanma: nothing is read until "Ölç"; then fragmented indexes with REBUILD / REORGANIZE; change "En az sayfa" to 100 and measure again.
- [ ] A login without VIEW SERVER STATE: the error says what is missing; the screen stays usable.
- [ ] Switch parts quickly while one is loading: the part shown last is read.
- [ ] Close the tab: its connection is closed.

## Server dashboard (SQL Server)
- [ ] Right-click a connected SQL Server: "Sunucu panosu"; also Tools > Sunucu panosu. The header shows product, edition, version, host, CPUs, memory and uptime.
- [ ] The tiles fill at once; batches a second and I/O latency after the second reading (5 s). The CPU chart already shows up to an hour of history.
- [ ] Hover a chart: a vertical line with the time and each series' value; leaving the chart removes it. Switch theme: the charts repaint in the other theme's colours.
- [ ] Run a heavy query in another tab: CPU, batches and I/O move; make a blocking pair (BEGIN TRAN; UPDATE … / SELECT the same row): "Engellenen" shows the count with a warning icon and a red outline.
- [ ] Waits: "Sunucu açıldığından beri" first, then "Pano açıldığından beri" with what was waited for since.
- [ ] Databases: sizes, recovery model, last full / log backup; a FULL database without a recent log backup, one without a full backup, or one older than a week is flagged; tempdb never. A login without msdb access shows "?" and no warnings. The refresh icon reads databases again.
- [ ] Switch to another tab and back: it pauses while hidden. Close it: its connection is closed.

## DBA menu and explanations
- [ ] The main menu has "DBA" between Sorgu and Görünüm with Sunucu panosu, Etkinlik izleyici (Ctrl+Shift+A), Performans analizi and "Açıklamalar (i)"; Araçlar no longer lists them.
- [ ] Right-click a connected SQL Server: one "DBA" item with the three screens; on a database node only Performans analizi; on a disconnected connection, a folder or a table no DBA item.
- [ ] Dashboard: an "i" in each of the 7 tiles, beside each chart title, the waits and Veritabanları. Click one: a card with Ne gösterir / Nasıl okunur (Normal, Dikkat, Sorun with icons) / Ne yapmalı; Esc, the X or a click elsewhere closes it.
- [ ] Activity monitor: "i" beside the counters, Kilitlenme zincirleri, the chosen session's title and the end-session button. Performance analysis: one "i" before the note; it explains the part shown (heavy queries, missing indexes, index use, fragmentation).
- [ ] DBA > Açıklamalar (i) unticked: every "i" disappears at once; ticked again: back. The same switch is in Settings > Görünüm and is remembered after a restart.
- [ ] In English (Settings > Dil > English, restart) the cards are in English.

## Health check (SQL Server)
- [ ] DBA > Sağlık kontrolü, or the explorer's DBA menu on a connected SQL Server: the tab opens and checks at once; "Kontrol edildi hh:mm:ss".
- [ ] A fresh server shows at least: Azami sunucu belleği ayarlanmamış (with a recommendation from the machine's memory), Paralellik eşiği varsayılan 5, sa etkin (with SQL logins), missing backups (Kritik), integrity not checked.
- [ ] Make a test database with AUTO_SHRINK ON, PAGE_VERIFY TORN_PAGE_DETECTION and 10 % file growth: each appears with the database (and file) named; "Yeniden kontrol et" after fixing one: it moves to Sorunsuz.
- [ ] Choose a finding: Neden önemli, Şu an / Önerilen, Bulunduğu yerler, Ne yapmalı and the script; "Sorgu sekmesinde aç" opens the script in a new tab, not run; "Görünenlerin betiği" collects all shown problems' scripts under a review comment.
- [ ] Untick "Yalnızca sorunlar": passing rules appear (Sorunsuz, green check); the category box narrows the list.
- [ ] A login without msdb access: "Yedekler okunamadı" (Bilgi) instead of missing backups.
- [ ] The "i" beside the title explains the screen and the severities; in English the rules are in English.

## Database detail (SQL Server)
- [ ] Right-click a database under a connected SQL Server > DBA > Veritabanı ayrıntıları: a tab "<database> · <connection>" opens and fills; the same again selects it. DBA > Veritabanı ayrıntıları works for the selected database node, else the query tab's current database.
- [ ] The header shows state, recovery, compatibility, collation, owner and creation date.
- [ ] Tiles: data used of allocated; log size with "% dolu" and, when held, "tutan: LOG_BACKUP"; VLF count; last full / log backup as "N gün önce" with the date; last clean CHECKDB; tables and rows. A FULL database without log backups shows Kritik with an icon; a SIMPLE one says log backups are not needed.
- [ ] Largest tables as bars with size and rows; Ayarlar lists page verification, auto shrink, Query Store…; Tablolar, Dosyalar (with path, growth, max) and Yedek geçmişi fill.
- [ ] Take a backup in a query tab and press F5: it appears first in the history and the tile updates.
- [ ] Every tile and section has an "i" that explains it (11 on the screen).
- [ ] The query tab using that connection keeps its database (the screen does not switch it).

## DBA screens on PostgreSQL
- [ ] On a connected PostgreSQL the DBA menu offers Sunucu panosu, Etkinlik izleyici, Performans analizi, Sağlık kontrolü; on a database node Performans analizi and Veritabanı ayrıntıları.
- [ ] Activity monitor: two sessions blocking each other (BEGIN; UPDATE … in one, the same UPDATE in another): the chain shows the holder as "idle in transaction", the waiter with Lock:transactionid; ending the holder releases the waiter.
- [ ] Heavy queries without pg_stat_statements: a clear message how to enable it; with it (shared_preload_libraries + CREATE EXTENSION) the statements list fills.
- [ ] Eksik index'ler lists tables read by many sequential scans with an example CREATE INDEX CONCURRENTLY comment; Index kullanımı shows scans and findings; Parçalanma asks for pgstattuple when it is missing.
- [ ] Dashboard: CPU, PLE and waits show "—" / nothing (PostgreSQL does not report them); transactions a second, I/O and connections move.
- [ ] Health check shows PostgreSQL rules (shared_buffers 128 MB, random_page_cost 4…), each explained in Turkish.
- [ ] Database detail of another database than the query tab's: it fills (size, tables, tablespaces) and the tab's database does not change; backups say "yedek geçmişi alınamıyor".

## DBA screens on Oracle
- [ ] Connect as a user with SELECT_CATALOG_ROLE (or SYSTEM): the DBA menu offers Sunucu panosu, Etkinlik izleyici, Performans analizi, Sağlık kontrolü; a schema node's DBA submenu offers Performans analizi and Veritabanı ayrıntıları.
- [ ] Connect as a plain application user: each screen says the login needs SELECT_CATALOG_ROLE instead of failing silently.
- [ ] Activity monitor: an uncommitted UPDATE in one tab (transaction mode) and the same UPDATE in another: the waiter shows "enq: TX - row lock contention" blocked by the holder (sid,serial#); ending the holder releases the waiter.
- [ ] Heavy queries list your statements (none of Oracle's own or JetQueries' advisor queries); Eksik index'ler lists big tables read by full scans with an example CREATE INDEX comment; Index kullanımı shows reads of cached plans; Parçalanma explains why Oracle is not measured.
- [ ] Dashboard: the CPU chart has the last hour; calls a second, commits, I/O and waits move while something runs.
- [ ] Health check shows the Oracle rules (NOARCHIVELOG, RMAN backup, tablespace use…), each explained in Turkish and English.
- [ ] Veritabanı ayrıntıları of a schema: account status, default tablespace, size, largest tables, data files, redo size; backups list RMAN jobs (empty without RMAN).

## Language, cancel, DBA tables, Excel export, editing toolbar, databases shown
- [ ] Settings > Appearance > Türkçe on an English Windows, restart: the DBA "i" cards, health check findings and messages (e.g. pg_stat_statements missing, Oracle without SELECT_CATALOG_ROLE) are Turkish.
- [ ] A slow DBA read (health check on a big server, performance analysis): the stop button beside the progress bar ends it; the screen stays usable and a refresh works again. Long queries: the Stop button (or Esc) of the query tab.
- [ ] Activity monitor, performance analysis, dashboard databases, database detail tables: a header click sorts (again: descending, again: off; Shift adds a column); durations (850 ms < 1,2 sn < 04:10) and sizes (800 KB < 2 MB < 1,5 GB) sort as numbers. The header's filter button: içerir / büyüktür "500 ms" … narrows the rows and the column's icon stays lit; right-click a cell: filter by this value, Filtreleri temizle. An auto-refresh keeps the order and the filters; the chosen session stays chosen.
- [ ] Export from a DBA table (cell menu "Dışa aktar…", or the toolbar button of the activity monitor and the performance analysis): the Export dialog; the file has the rows as shown.
- [ ] Export a query result to Excel: the dialog opens on Excel; sheet name, title, freeze header, bold, header colour, filter, Excel table + style, borders, fitted widths, thousands separators, dates, totals row. Open in Excel: no repair prompt, the options are there; the totals follow Excel's filter. Export again: the last choices come back.
- [ ] Query toolbar: Format (Ctrl+Shift+F), -- (Ctrl+/), AB, ab, undo, redo act on the editor; Settings > Sorgu sekmeleri > "Sorgu araç çubuğunda düzenleme düğmeleri" off hides them.
- [ ] Edit a connection > "Gezginde görünen veritabanları" > Yalnızca seçtiklerim: the list is read from the server; check a few, filter, save. The explorer shows only those and "N gizli — …"; the query tab's database list too (its current database stays). The "Veritabanı" field's list fills from the server when opened; the chosen start database is always shown. Back to Tümü: all return.

## Soft colour schemes and database logos
- [ ] Settings > Appearance: the light theme offers Sepya, Adaçayı, Sis; the dark one Arduvaz, Kırağı, Mokka, Orman. Each applies at once: editor, grid, explorer, dialogs readable; primary buttons' text readable on the accent.
- [ ] The explorer, the connection type chooser and query tabs show the SQL Server, Oracle, PostgreSQL, SQLite and Redis logos, told apart at a glance in both themes.

## Density levels, explorer grouping, folder colours, picking several
- [ ] Settings > Appearance > Yoğunluk: − and + step through Çok sıkı … Geniş; the window follows at once (toolbars, tabs, explorer rows, result rows). Cancel brings back the previous level; restart keeps the saved one. View > Kompakt mod switches compact and normal.
- [ ] Explorer "…" > Gruplama > Ortam: groups Üretim, Test, Geliştirme, Ortamı belirtilmemiş with counts, folders inside; close one, restart: still closed, grouping kept. > Veritabanı türü: groups with the logos. > Klasörler: back to the usual tree (empty folders too).
- [ ] A folder's menu > Renk > Kırmızı: its icon turns red; Ctrl+click another folder, colour both; Renk yok removes it. Rename or move the folder: the colour stays.
- [ ] Ctrl+click several connections (and Shift+click a range): right-click offers Seçilen bağlantıları bağla / bağlantısını kes / sil; Del asks once for all; dragging one of them moves them all into a folder.

