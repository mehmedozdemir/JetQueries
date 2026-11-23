# Changelog

All notable changes to this project are documented here.
Format: [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), versioning: [SemVer](https://semver.org/).

## [Unreleased]

### Added
- Repository foundation: CLAUDE.md, product analysis, build props.
- Solution skeleton (`JetQueries.slnx`): Desktop, Core, Sdk, Editor, DataGrid, SQL Server and Oracle provider projects, Core and Sdk test projects.
- Desktop shell window with light/dark design tokens (`Styles/Tokens.axaml`), explorer / documents / results placeholders, generic host and DI.
- Core job queue on `System.Threading.Channels` with priority, progress and cancellation.
- SDK v0 (preview) contracts: `IDatabaseProvider`, `IMetadataProvider`, `IQueryExecutor`, `ISqlLanguageService`.
- Spike: virtualized results grid (`JetQueries.DataGrid.VirtualGrid`) over one million rows, with headless and on-screen frame benchmarks. Findings in `docs/spikes/virtual-grid.md`.
- Windows: present through a low-latency DXGI swap chain by default (ADR 0002); `JETQUERIES_COMPOSITION` / `JETQUERIES_RENDERING` override it.
- Spike: PL/SQL parsing with the grammars-v4 ANTLR grammar (coverage, speed, memory, error recovery, token-based caret context). Findings in `docs/spikes/plsql-parser.md`.
- Spike: hand-written structural PL/SQL parser, chosen over ANTLR for the Oracle language service (ADR 0003).
- Extension host: extensions are discovered from `extension.json` manifests and loaded into their own `AssemblyLoadContext` as a start-up job (ADR 0004); SDK gains `IExtension`, `IExtensionContext`, `ExtensionManifest` and `SdkContract`.
- Packaging: `scripts/pack.ps1` publishes win-x64 (self-contained, ReadyToRun) and builds a Velopack installer; versions come from git tags through MinVer.
- CI workflow (GitHub Actions): locked restore, Release build, tests, and a Velopack package from `main` and version tags.
- Metadata cache: in-memory layer over a disposable SQLite file, per connection, with read-through helpers.
- SQL Server provider (built-in extension): connections, streaming query execution with messages and cancel, catalog metadata.
- Connection manager: saved connections with folders, environment colour and read-only mark; passwords in Windows Credential Manager; connection test with a reachability check; connect and disconnect from the explorer panel (ADR 0005).
- Object explorer: databases, schemas, object groups, objects and columns load lazily under an open connection, on a separate metadata connection, through the metadata cache; Refresh bypasses the cache.
- SQL editor and query execution: query tabs with syntax colouring, run (F5 / Ctrl+Enter) and cancel, results streamed into the virtual grid, messages, read-only enforcement and confirmation of destructive statements on production connections.
- T-SQL language service: `GO` batch splitting (with `GO n`), T-SQL colouring (functions, data types, nested comments), and completion for tables, columns of tables in scope, alias members, schema and database members, procedures, functions and keywords, backed by cached metadata.
- T-SQL syntax errors are underlined in the editor (Microsoft ScriptDom parser) with the message on hover; Ctrl+Click and F12 open the source of a procedure, function or view, or a catalog-written script of a table, in a new tab.
- Oracle provider (built-in extension, managed ODP.NET): connections, SQL and PL/SQL script execution split the SQL*Plus way, DBMS_OUTPUT, compile errors from the catalog, data dictionary metadata and DDL. The hand-written PL/SQL structural parser moved from the spike into the provider.
- Oracle SQL and PL/SQL language service: colouring, completion (tables, columns, schema objects, package members, and the parameters and variables of the routine being edited), structural error squiggles and go to definition. T-SQL and Oracle completion share one engine.
- Query history: every run that reaches a database is recorded (text, connection, outcome, rows, duration) in a local SQLite file under `%AppData%\JetQueries`, with password literals masked; a History tab next to the explorer lists and searches the runs, reopens one in a new query tab, deletes one or clears all.
- PostgreSQL provider (built-in extension, Npgsql): connections with SSL modes, scripts split per statement (dollar quotes and `BEGIN ATOMIC` bodies kept whole), NOTICE messages, server errors with line and column, server-side cancel, catalog metadata and DDL for tables, views, routines, sequences and types.
- SQLite provider (built-in extension, Microsoft.Data.Sqlite): database files with read-only and create modes, a Browse button for file settings, scripts split per statement (trigger bodies kept whole), cancel through `sqlite3_interrupt`, tables, views and columns from `sqlite_schema` and pragmas.
- PostgreSQL and SQLite language support on a shared token-based engine: colouring, completion, go to definition and unclosed string or comment errors (ADR 0006).
- The object explorer shows Synonyms and Types groups only for providers that have them.
- WPF user interface on UiKit (ADR 0007): shell, connection dialog, explorer, history, query tabs with an AvalonEdit SQL editor and UiKit's virtual results grid, light and dark themes following Windows. It is now the packaged application; the Avalonia interface is paused.
- Result grid sorting and filtering (WPF): header click and Shift+click sort, a filter panel per column, filter and
  exclude by a cell's value, active filter chips, hidden columns; computed on the client in the background, by
  value and by Turkish text rules.
- Settings screen (WPF): theme, user interface font and text size, SQL editor font and size, result grid text
  size; previewed live, saved to `%AppData%\JetQueries\appearance.json`. A tighter layout.
- Ctrl+mouse wheel zoom in the SQL editor (zoom shown, click or Ctrl+0 for 100 %) and in result grids.
- SSMS-style database selector per query tab: each tab has its own connection and current database (Oracle:
  schema), follows `USE`, switches on choice. SDK (preview): `IDatabaseContext`, `SqlDocument.DefaultDatabase`;
  Core: `QueryChannel`, `ISettingsStore`.
- Explorer filter by name, and a context menu with New query (on that database), Connect/Disconnect, Refresh.
- SQL files: open (Ctrl+O, drag and drop), save (Ctrl+S), save as (Ctrl+Shift+S), keeping the file's encoding
  (UTF-8 with or without BOM, UTF-16, the ANSI code page such as Windows-1254); unsaved changes shown as "*" and
  asked about when a tab closes; middle click closes a tab.
- Workspace: open tabs (texts, files, connections, databases, selection) are saved while the application runs and
  restored at the next start; restored tabs wait for their own connection.
- Paged results: a single SELECT stops after its first page (fetch size, default 200) and is closed at once, so
  nothing stays open on the server (no locks, no snapshot); Next page and Fetch all run it again. Scripts and
  procedure calls are read to their end. A footer under the grid with Refresh, page size, Next page, Fetch all
  (up to 1,000,000 rows), total row count
  without fetching (SDK preview: `IRowCountQueryBuilder`, for SQL Server, Oracle, PostgreSQL, SQLite) and the
  fetch line (rows, time, fetch time, clock).
- Run, Stop and Save as icons in each query tab by default (Settings can move them to the main toolbar); the
  query tab's connection list is gone, the database selector stays.
- Text and Record views of a result (DBeaver-style strip left of the grid): the text view is an aligned
  plain-text table of the rows in the grid's order and columns, built in the background; the record view
  lists one row's columns with value and type, with first / previous / next / last, and keeps the grid's row.
- SQL filter over the grid ("Enter a SQL expression to filter results"): the query runs again on the server
  wrapped as `SELECT * FROM (query) jq_filter WHERE (condition)`, keeping its order, and pages, counts and
  refreshes with the filter; nothing stays open between pages. The condition must be one expression (no `;`,
  no comments, closed parentheses and strings). SDK preview: `IResultFilterQueryBuilder`, for SQL Server,
  Oracle, PostgreSQL and SQLite.
- Run in new tab (button next to Run, Ctrl+Shift+Enter or Ctrl+\): the results shown stay and the new ones follow
  them ("Result 2", "Result 3"…); each result pages, counts, filters and refreshes over its own query. Result tabs
  close with × or a middle click.
- SQL formatting (Ctrl+Shift+F, editor menu) of the selection or the whole text, as one undo step: clauses on
  their own lines, select lists, conditions, subqueries, CTEs and CASE indented, keywords upper-cased. Comments,
  literals and identifiers are kept; procedural code is left as written; a result that would not tokenize back
  to the same tokens is never applied. SDK preview: `ISqlFormatter`, for SQL Server, Oracle, PostgreSQL, SQLite.
- Editor right-click menu: run, run in new tab, cut / copy / paste / select all, format, comment / uncomment
  lines (Ctrl+/), upper / lower case (Ctrl+Shift+U / Ctrl+U), go to definition, completion, undo / redo.
- Query tab right-click menu: close, close others, close all, copy file path.
- Settings screen redesigned: pages listed on the left (Appearance, SQL editor, Results, Query tabs, SQL
  formatting), cards of rows with a line about each setting, theme cards, segmented choices and switches, a
  coloured editor preview. New SQL formatting page: keyword case (upper / lower / as typed), indentation (2, 4,
  8 spaces or a tab), clause content on its own lines, AND / OR on new lines, with a live preview. Query tabs
  format with the options in effect. SDK preview: `SqlFormatOptions` gains `SqlKeywordCase`, `UseTabs`,
  `ContentOnNewLine`, `ConditionsOnNewLine` (`UppercaseKeywords`, never released, is replaced by `KeywordCase`).

- Transactions: each query tab has auto-commit (default) or manual commit. In manual mode text that can change
  data starts a transaction that waits for Commit or Rollback (a single SELECT never opens one); an "Open
  transaction" badge shows while one is open, also one begun in SQL. Closing a tab or the application with an
  open transaction asks Commit / Rollback / Cancel; the database cannot be switched while one is open; a tab
  never hands an open transaction to the next one. SDK preview: `ITransactionControl`, for SQL Server, Oracle,
  PostgreSQL and SQLite.
- Ctrl+Enter runs the statement at the cursor (F5 still runs the selection or the whole text), as DBeaver does:
  statements end at a semicolon, an empty line or a GO / `/` line; strings, comments and quoted names never end
  one; BEGIN … END and CASE blocks, procedures, packages and DECLARE scripts keep together. The statement that
  runs flashes in the editor. Editor menu: "Run statement".

### Changed
- A result set may hold up to 1,000,000 rows (was 100,000), fetched page by page.

### Changed
- View models, their service contracts and strings moved to `JetQueries.Presentation`, shared by both interfaces.

### Fixed
- History panel: two loads finishing together could fill the list twice.
- The × of a query tab did nothing (its command binding did not resolve inside the tab header); closing the last
  tab now leaves the empty page with "New query" instead of opening a new tab at once.
