# Manual checklist — Phase 1 · Object explorer

Branch `feature/object-explorer`. Builds on the connection manager
([checklist](phase1-connection-manager.md)); use the same test database.

## 1. Automated checks

- [ ] `dotnet test` reports Sdk 7, Core 53, Desktop 22, DataGrid 28, Provider.SqlServer 50 passed and 9 skipped;
      with `$env:JETQUERIES_MSSQL_ACCEPT_EULA = 'Y'`, Provider.SqlServer reports 59 passed.
- [ ] `tests/JetQueries.Desktop.Tests/bin/Debug/net10.0/screenshots/explorer-light.png` and `explorer-dark.png`
      show the opened tree as rendered by the real views.

## 2. Browse

Connect to a SQL Server connection (double-click it).

- [ ] The connection opens by itself and lists the databases you may access; a thin progress line shows while
      a level is loading and the rest of the window stays responsive.
- [ ] Database → schemas (no `sys`, `INFORMATION_SCHEMA`, `db_owner` …) → groups Tables, Views, Procedures,
      Functions, Sequences, Synonyms, Types → objects → for tables and views, their columns.
- [ ] Columns show name, data type as SQL Server tools write it (`nvarchar(50)`, `decimal(18,2)`), `PK` for
      primary-key columns and `null` for nullable ones.
- [ ] An empty group shows "(empty)". A database you cannot open shows the server's error in red under it; the
      rest of the tree keeps working.
- [ ] Names with Turkish characters and spaces are shown correctly.

## 3. Cache and refresh

- [ ] Collapse and expand a node: instant, no progress line.
- [ ] Disconnect and connect again, or restart the application: levels you opened before appear at once.
- [ ] Create a table in the database with another tool. It does not appear until you select its "Tables" group
      (or any node above it) and press Refresh; then it does.
- [ ] Refresh is disabled when nothing loadable is selected (a folder, a disconnected connection, a column).

## 4. A long query does not block the tree

Cannot be checked by hand until there is an editor; covered by an integration test (metadata is read on a
second connection while a `WAITFOR` runs on the first).

## Not covered yet

- No icons: kinds are told apart by their group and by weight (databases and schemas are bold).
- The tree control is not virtualized: a schema with several thousand tables will open slowly. The data layer
  streams and caches, so this is a control to replace, not a redesign.
- Nothing happens on double-clicking an object yet (later: open definition, select top rows).
- Triggers, indexes, keys and parameters are not shown; object definitions are not cached.
- Cached lists are used for 12 hours unless refreshed; there is no automatic detection of schema changes.
- Oracle, PostgreSQL and SQLite will get their tree shape from the same code: schemas first where there are no
  databases, a "Packages" group where the provider has packages. Only tested with a fake provider so far.
