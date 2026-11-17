# Manual checklist — Phase 1 · SQL editor and query execution

Branch `feature/sql-editor`. Builds on the connection manager and object explorer; use the same test database
([how to start one](phase1-connection-manager.md)).

## 1. Automated checks

- [ ] `dotnet test` reports Sdk 7, Core 88, Desktop 32, DataGrid 28, Provider.SqlServer 50 passed and 10 skipped;
      with `$env:JETQUERIES_MSSQL_ACCEPT_EULA = 'Y'`, Provider.SqlServer reports 60 passed.
- [ ] `tests/JetQueries.Desktop.Tests/bin/Debug/net10.0/screenshots/window-light.png` and `window-dark.png` show
      the whole window with a query and its result, rendered by the real views.

## 2. Write and run

- [ ] The centre of the window is a "Query 1" tab with an editor (line numbers, monospaced) above and a
      "Messages" tab below. Run is disabled: there is no open connection, and the connection box says so.
- [ ] Connect in the explorer: the connection appears in the query's connection box and is selected.
- [ ] Type `SELECT name, database_id, create_date FROM sys.databases ORDER BY name;` — keywords, strings,
      numbers, comments and `@parameters` are coloured; a `/* … */` comment over several lines stays coloured
      and loses its colour again when you delete the `/*`.
- [ ] F5, Ctrl+Enter or the Run button: a "Result 1" tab appears with the rows; the status at the right of the
      toolbar reads `n rows · … ms`. Numbers are right-aligned, `NULL` is greyed, dates read `2026-01-15`.
- [ ] Select only part of the text and run: only the selection is sent.
- [ ] Two statements (`SELECT 1; SELECT 2;`) give "Result 1" and "Result 2".
- [ ] `PRINT 'hello'; UPDATE …` shows `hello` and `n rows affected` under Messages.
- [ ] A mistake (`SELECT * FROM nope`) switches to Messages and shows the server's error in red.

## 3. Large results and cancel

- [ ] `SELECT a.* FROM sys.all_objects a CROSS JOIN sys.all_objects b` — rows keep arriving while the window
      stays responsive; you can scroll the grid during loading; the status counts up.
- [ ] After 100,000 rows the result stops and Messages says "Only the first 100,000 rows are shown."
- [ ] `WAITFOR DELAY '00:01:00'` then Cancel (or Esc): it stops within a second or two and the status reads
      "Canceled · …". The connection still works afterwards.
- [ ] While a query runs, expanding nodes in the explorer still works.

## 4. Tabs

- [ ] Ctrl+N opens "Query 2", starting on the connection selected in the explorer. Each tab keeps its own text,
      connection and results. The × closes a tab; closing the last one leaves a fresh empty tab.
- [ ] Disconnect in the explorer: the connection disappears from the connection boxes.

## 5. Safety

- [ ] On a connection saved as **read-only**, `DELETE FROM t WHERE 1 = 0` is not sent: Messages explains why
      and the status reads "Not sent". `SELECT` still runs.
- [ ] On a connection saved as **Production**, `DELETE FROM t`, `UPDATE t SET …` without `WHERE`, `DROP …` and
      `TRUNCATE …` ask for confirmation, naming the connection and what was found. Cancel sends nothing.
      `DELETE … WHERE …` is not questioned.

The read-only check works on keywords and is deliberately strict: `EXEC`, `SELECT … INTO` and
`SELECT … FOR UPDATE` count as writing too. It prevents accidents; it is not a security boundary — use a login
without write permission for that.

## Not covered yet

- **`GO` is not understood**: the text is sent as one batch, so a script with `GO` lines fails on SQL Server.
- No IntelliSense, no Ctrl+Click, no error squiggles, no formatting: colouring uses a keyword list common to
  all dialects until the T-SQL and PL/SQL language services are built.
- Query text is not saved: closing the application loses it. No open/save file, no query history.
- No copy from the results grid, no sorting, filtering, export or column resizing; long values are cut at the
  column width (at most 420 px).
- No transaction controls, no execution plan, no row limit setting (fixed at 100,000 per result set).
- Tabs cannot be dragged or docked (Dock.Avalonia is planned for the shell work).
- Typing copies the whole text into the view model on every keystroke; fine for scripts, to be revisited for
  very large files.
- The Phase 0 grid benchmark is now shown only when the application is started with `--grid-spike` or
  `--grid-benchmark=<file>`.
