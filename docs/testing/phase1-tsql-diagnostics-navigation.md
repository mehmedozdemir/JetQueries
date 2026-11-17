# Manual checklist — Phase 1 · T-SQL error squiggles and go to definition

Branch `feature/tsql-diagnostics-navigation`. Builds on the T-SQL language service
([checklist](phase1-tsql-language.md)); use the same SQL Server test database, with the `dbo.jq_demo` table and
`dbo.jq_demo_count` procedure from that checklist.

## 1. Automated checks

- [ ] `dotnet test` reports Sdk 7, Core 88, Desktop 38, DataGrid 28, Provider.SqlServer 124 passed and 11 skipped;
      with `$env:JETQUERIES_MSSQL_ACCEPT_EULA = 'Y'`, Provider.SqlServer reports 135 passed.
- [ ] `tests/JetQueries.Desktop.Tests/bin/Debug/net10.0/screenshots/editor-diagnostics.png` shows red wavy lines
      under two words.

## 2. Error squiggles

With a SQL Server connection chosen in the query's connection box:

- [ ] Type `SELECT id, FROM dbo.jq_demo` and stop typing: after about half a second a red wavy line appears
      under `FROM`. Resting the mouse on it shows the parser's message ("Incorrect syntax near …").
- [ ] Fix the text: the line disappears after the next pause.
- [ ] A script with `GO` and `GO 3` lines has no squiggles on those lines.
- [ ] A misspelt table name is **not** underlined: only syntax is checked; the server reports unknown names
      when the text runs.
- [ ] Typing stays fluent in a long script while squiggles come and go.
- [ ] With no connection chosen there are no squiggles.

## 3. Go to definition (Ctrl+Click or F12)

- [ ] `EXEC dbo.jq_demo_count` — Ctrl+Click on the name (or the caret in it and F12) opens a new tab titled
      `jq_demo_count` with the procedure's source, on the same connection.
- [ ] On a view name: its `CREATE VIEW` text.
- [ ] On a table name, on an alias of it, or on `alias.column`: a tab with a `CREATE TABLE` script that begins
      with a comment saying it was written from the catalog and lists columns and primary key only.
- [ ] On a bare column name in a statement with several tables: the table that has that column.
- [ ] On a keyword, a number or an unknown name: nothing opens and the status at the right of the toolbar
      reads "Nothing to go to here."
- [ ] An encrypted procedure, or one you may not view: the status says its definition is not available.
- [ ] **Ctrl+Click itself was never exercised, by hand or by a test** (F12 was, by simulated key press) —
      please try it first.

## Not covered yet

- No underline or hand cursor while Ctrl is held over a name.
- The definition opens as a new query tab; it is ordinary text and running it would try to create the object.
- No "find references", no rename, no jump to an alias's place in the same text.
- The table script omits defaults, identity, computed columns, indexes and constraints other than the primary
  key; the full script belongs to the table inspector planned from SsmsToolkit.
- Warnings (unused variables, deprecated syntax) are not produced; only syntax errors.
