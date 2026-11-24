# Changelog

All notable changes to this project are documented here.
Format: [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), versioning: [SemVer](https://semver.org/).

## [Unreleased]

### Added
- SSH tunnels: a connection can go through an SSH server (a bastion host) with a password or a private key (OpenSSH / PuTTY, with an optional passphrase). The database host and port are as the SSH server sees them; every connection of the session (queries, metadata, extra tabs) goes through the tunnel, which closes with the session. The SSH password is kept in the credential store under an entry of its own (or asked for when connecting); the server's key is remembered at the first connection (`ssh_known_hosts.json`) and a changed key is refused. SQL Server, PostgreSQL and Oracle (host and service name). New package: SSH.NET. SDK (preview): `IConnectionSettingsProvider.WithEndpoint`.
- Settings > Appearance > Hardware acceleration (GPU): off draws in software, about 160 MB less memory at idle (measured: private memory 91 MB in software, about 240–300 MB with the GPU; the managed heap is under 10 MB either way). Takes effect at the next start; `JETQUERIES_RENDER=software` does the same for one run.
- Performance budgets as tests: the published application is started with its data in a folder of its own and must show its window within 1.5 s and idle under 200 MB (software rendering); CI runs them after packaging. `JETQUERIES_MEMORY_REPORT` writes a memory report of a run.
- In-app updates (Velopack): Settings > Updates (channel stable / beta / nightly, the release feed as a URL or a folder, check at start-up), Tools > Check for updates, and a status bar chip when a version is found: Download, then "Restart and update" (the window closes the normal way, the open tabs are saved, Velopack applies the update and starts the new version). Only the installed application (Setup.exe) can update itself.
- Compact mode (JetBrains' compact UI): View > Compact mode, or Settings > Appearance > Interface density. Toolbars, explorer and grid rows, tabs, inputs and menus become one step lower with tighter padding; text keeps its size. Switches at once and is remembered.
- UiKit: density tokens (`Size.Control`, `Size.Input`, `Pad.*`) used by its control styles, and `UiKit.Theming.Density` to switch them between Normal and Compact at runtime.
- Execute a stored procedure or function: "Execute…" in the explorer's menu and "Execute" in the details view open a dialog (as SSMS's) listing the routine's parameters (each overload), each passed as a value, an expression, NULL or its default, with the script kept up to date; "Open script" puts it in a query tab, "Run" runs it there. SQL Server: `EXEC @return_value = …` with OUTPUT variables selected afterwards, a scalar function in a SELECT, a table function in a FROM. Oracle: an anonymous block with named arguments that prints OUT values and the result with DBMS_OUTPUT (a ref cursor comes back as a result set). PostgreSQL: `CALL` for a procedure, `SELECT * FROM` for a function. SDK (preview): `IRoutineExecutionScripting`.
- Oracle: "User and password from the wallet" for a TNS alias (secure external password store): no user or password is asked; the connection logs on as "/" and ODP.NET takes the credential from the wallet. A pasted `User Id=/` with an alias is read as such.
- SQLite: the details' Constraints tab lists CHECK constraints (column and table ones, with their names and conditions), read from the CREATE TABLE text.
- PostgreSQL: the explorer lists the server's databases (those the user may connect to); each one opens its schemas and objects, read on a connection to that database. Details, scripts and new query tabs opened from them start in that database.
- Oracle: a routine declared in a package has details and source of its own. Ctrl+Click / F12 on `pkg.member` (or the explorer's package member) opens its parameters (with an Overload column when it has several), the package's owner, status and dates, and its source: the declaration from the specification and the implementation from the body. SDK (preview): `DatabaseObjectName.Container`.
- Results: the SQL filter box above the grid is a one-line SQL editor: colouring, and completion of the result's columns (with their types) and of the words of a condition (AND, OR, LIKE, IS NULL…). Enter applies, Esc puts back the applied filter.
- Results: column filters can be combined with OR ("Herhangi biri (VEYA)" next to the filter chips, shown from two filters on).
- Oracle: connect by TNS alias. The connection form has "Connect with: host and service name / TNS alias"; for a TNS alias it asks for the alias (listed from tnsnames.ora with where each one points), the TNS folder (default TNS_ADMIN, then `ORACLE_HOME\network\admin`) and an optional wallet folder (TCPS, Autonomous Database). A pasted connection string with a bare alias (`Data Source=PRODDB`, `hr/pw@PRODDB`) and `Tns_Admin` / `Wallet_Location` is read as such.
- SDK (preview): connection settings can depend on another setting (`VisibleWhen`), take a folder (`ConnectionSettingKind.FolderPath`) and offer suggested values (`HasSuggestions`, `IConnectionSettingSuggestions`).
- UiKit: the ComboBox style supports `IsEditable` (typed text with the list still available).
- Completion: built-ins called without brackets (Oracle SYSDATE, SYSTIMESTAMP, USER, ROWNUM…; CURRENT_TIMESTAMP, CURRENT_USER… in every dialect) come with their type and description and are inserted without brackets.
- SDK (preview): `IProgressiveObjectDetailsProvider` reports object details as they are read; the details view shows the properties and columns first and adds the other tabs as they arrive (Oracle).
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
- Object details window (Ctrl+Click / F12 in the editor, F4 and "Details" in the explorer): properties, columns, constraints, foreign keys, references, indexes, triggers, parameters, dependencies and DDL, for SQL Server, Oracle, PostgreSQL and SQLite. SDK (preview) gains `IObjectDetailsProvider`.
- Explorer Script menu: CREATE, ALTER (CREATE OR REPLACE on Oracle and PostgreSQL) and DROP next to the DML templates. SDK (preview) gains `IObjectScripting`.
- Main menu (File, Connection, Query, View, Tools); the query history moved from the explorer panel into its own window (Tools → Query history, Ctrl+Shift+H) with a full-text preview.
- Result grid headers show each column's data type under its name (UiKit `IGridColumnCaptions`).
- Connection editor redesigned as a compact two-column form with an "Advanced" section.
- `docs/ux-review.md`: a UX review of the whole application with prioritised findings.
- Keyboard shortcuts overview (Tools → Keyboard shortcuts, F1), searchable.
- Command palette (Ctrl+Shift+P): every menu command by name.
- IntelliSense: built-in function catalogs with signatures and descriptions (English/Turkish) for SQL Server, Oracle, PostgreSQL and SQLite; signature help (Ctrl+Shift+Space, and on "(" / ","), with the current parameter highlighted and overloads; values offered for parameters that take a fixed set (dateparts, styles, formats, units…); user function and procedure parameters read from the database. SDK (preview): `ISignatureHelpProvider`, `IRoutineMetadataProvider`, `SqlSignature`, `CompletionItem.Documentation` / `OpensCall`, `CompletionItemKind.Value`.
- Completion list redesigned (kind badges, signatures at the right, a description card, hint line); Ctrl+hover underlines names; Ctrl+Click shows a "finding the object" chip and a waiting cursor.
- Connection folders in the explorer: new, rename, delete (contents move up), drag and drop of connections and folders; empty folders are kept (settings section `folders`).
- Import starts with the source (DBeaver, SSMS, a file), reads only that one, and can keep the tool's folders or put everything into one folder.
- Colour schemes for both themes (UiKit `Themes/Schemes`): dark Midnight (new default), Graphite, Ocean, High contrast; light Paper, Sky, Sand, High contrast; chosen in Settings → Appearance.
- Explorer favourites: pin tables, views and routines; they appear under "Favourites" at the top of the connection. Object groups show their count once loaded.
- Setting: data types under result headers can be turned off. The explorer's visibility and width are remembered.
- Object details open as document tabs beside the queries instead of separate windows.
- History window filters by connection and by failed runs (in the store, not only in the loaded page).
- Results: a toolbar with Grid / Text / Record and an on-demand SQL filter bar; the footer's actions are labelled.
- Query tabs show which connection they run on: an environment-coloured dot on the tab, the connection name on the query toolbar, and a coloured "PRODUCTION" / "TEST" strip above it for those environments.

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
- Explorer scripts: right-click a table or view for "Select first 1000 rows" (opens a tab and runs it, in the
  dialect's words: TOP / FETCH FIRST / LIMIT) and Script → SELECT, INSERT, UPDATE, DELETE templates (NULL
  placeholders with each column's type; UPDATE and DELETE by primary key, `WHERE 1 = 0` without one) and
  CREATE (the definition); routines, packages and sequences get CREATE. SDK preview: `IRowLimitedSelect`.
- Go to object (Ctrl+Shift+N, or the search icon over the explorer): finds tables, views, routines and packages
  of the open connections by part of their name (Turkish letters in any case) and reveals the chosen one in the
  explorer; lists not loaded yet load in the background through the metadata cache.
- Result tools: totals of the selected cells under the grid (count; for numbers sum, average, min, max, summed
  exactly in decimal, in the background); cell menu "Copy as" CSV, JSON, Markdown table, SQL INSERT, SQL IN
  list; a value panel beside the grid with the current cell in full (JSON and XML laid out, binary as a hex
  dump, PNG / JPEG / GIF / BMP shown as images). UiKit: `VirtualGrid.CellMenuOpening`.
- Snippets: type a shortcut and Tab in the editor (`sel`, `selw`, `ins`, `upd`, `del`, `ij`, `lj`, `cte`,
  `case`, …); the snippet takes the line's indentation and the caret goes to its `$0`; one undo takes it back.
  Editor menu "Insert snippet" lists them. A Snippets page in Settings adds, edits and removes them (shortcuts
  checked on Save); they are kept in `%AppData%\JetQueries\snippets.json`.
- Import connections (the import icon over the explorer): finds DBeaver's `data-sources*.json` and SSMS's
  registered servers (`RegSrvr*.xml`) on the computer, or a file chosen, and saves the connections ticked:
  folders / server groups and environment kept, production ones read-only, already saved ones marked. Passwords
  are never imported. SDK preview: `IConnectionImport` (each provider maps a neutral `ImportedEndpoint` to its
  own settings).
- New connection in two steps: first the database type, as cards with each type's mark and a line about it, then
  its details. "Connection string" next to the details reads a pasted string into the form: the driver's own
  syntax, JDBC URLs (SQL Server, Oracle thin, PostgreSQL, SQLite), `postgresql://` URLs and libpq keyword strings,
  Oracle EZConnect / connect descriptors / `user/password@host:port/service`. The user name fills its box; a
  password goes to the password box and from there only to the protected store; keywords without a matching
  setting are listed as not taken; the pasted text is cleared once read. The explorer shows each connection with
  its type's mark. SDK preview: `IConnectionStringImport` (`ParsedConnectionString`) and `IProviderAppearance`
  (mark as SVG path data, colour, description).

### Changed
- One title bar: the main window draws its own caption (WindowChrome) with the menu in it; the toolbar's new connection / new query / open icons are gone and a "+" ends the query tab row.
- Results: the view modes and the SQL filter toggle moved into the result tab row (QueryResultSet.IsSqlFilterOpen); Commit / Rollback are neutral with coloured icons; the empty results area shows the run shortcuts.
- Explorer: Ctrl+click multi-select for moving connections, a drop caption while dragging; Edit / Delete connection in the context menu, the Connection menu, F2 and Del.
- Connection editor's Advanced section: values first, switches in one row. Scheme cards preview the editor's keyword and string colours.
- A query is framed in its connection's environment colour instead of the environment strip and the toolbar's connection name.
- The connection editor's environment is a row of coloured chips; the query toolbar no longer repeats the status bar; the editor's zoom badge shows only away from 100 %; loading rows in the explorer show a small spinner instead of a progress bar; keyboard focus is visible on custom controls.
- A result set may hold up to 1,000,000 rows (was 100,000), fetched page by page.
- View models, their service contracts and strings moved to `JetQueries.Presentation`, shared by both interfaces.
- The main toolbar is icon-only (the menu names the commands); Commit and Rollback are labelled; the explorer's "connected" badge is gone and script errors show as a dismissible banner at its top.
- Accessibility: icon buttons give screen readers their tooltip as name; muted text is darker in both themes (≥ 4.5:1 for small text).

### Fixed
- PostgreSQL: after switching a tab's database, completion, Ctrl+Click and the details view still read the connection's own database. They now read the selected one, on a metadata connection to it, with its own cache. SDK (preview): `IDatabaseContext.MetadataSpansDatabases`.
- Security: the open tabs' text (the workspace, which may hold a typed password) was saved as plain JSON. It is now encrypted for the current Windows user with DPAPI (`workspace.bin`); a plain `workspace.json` from an earlier version is read once and removed at the next save.
- Oracle object details (Ctrl+Click) were slow: the dictionary queries ran ALL_CONSTRAINTS nested per column and per row. They are now plain single-view reads joined in code; a first look at a table on a cold instance went from about 28 s to under 6 s (columns after well under 1 s), later looks take tens of milliseconds. Oracle column lists for completion use the same cheaper key lookup.
- The SQL editor's text no longer touches the line numbers' rule.
- An empty completion card (only the hint line) stayed open after "." or after typing what no row matched; "." now opens the columns of the alias, and a list filtered to nothing closes.
- The editor/results splitter had no resize cursor or visible line for rows (UiKit splitter template), so the results could not be found to drag up.
- The explorer's toolbar was cut off at the default panel width; its actions are icons that wrap.
- Connection editor choices, hints and Oracle's field labels were English in the Turkish UI.
- Submenus did not open (UiKit menu item had no submenu popup): the explorer's Script, the grid's Copy as and the editor's Insert snippet now work.
- The result column filter panel closed when the mouse button was released; it now opens on release and stays.
- Oracle: Cancel now stops a statement that is waiting (on a row lock, a sleep) also through Docker, NAT and VPN
  tunnels, which drop the driver's out-of-band break: if the statement still runs 1.5 s later, a second
  connection asks the server to cancel it (`ALTER SYSTEM CANCEL SQL`; needs that privilege). A canceled UPDATE
  waiting on a lock never runs afterwards. A cancel that takes long says why in the status line.
- History panel: two loads finishing together could fill the list twice.
- The × of a query tab did nothing (its command binding did not resolve inside the tab header); closing the last
  tab now leaves the empty page with "New query" instead of opening a new tab at once.
