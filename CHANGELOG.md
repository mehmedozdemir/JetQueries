# Changelog

All notable changes to this project are documented here.
Format: [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), versioning: [SemVer](https://semver.org/).

## [Unreleased]

### Added
- IntelliSense reads ahead: right after a query tab gets its connection and database, the tables and views of the default schema and every one of their columns are read in the background, in one query (SQL Server, Oracle, PostgreSQL; SQLite lists its tables). The first table and column lists then come from the cache. The status bar shows "… yapısı IntelliSense için okunuyor…" while this runs; a list still waiting for the database shows "Veritabanı yapısı okunuyor…" at the caret.
- SDK (preview): `IBulkColumnMetadataProvider` (a schema's columns in one read), `IMetadataPreloader` and `ISqlMetadataWarmUp`.

### Changed
- The metadata connection is opened in the background right after connecting, not on the first completion.
- The same metadata asked for at once (completion while the explorer loads, a request per keystroke) is read once and shared, instead of the same query queueing on the metadata connection several times.
- A completion list that arrives after more of the word was typed opens anyway, filtered by what was typed, instead of being dropped.
- The table card shown when the mouse rests on a table, view, alias or column stays open while the mouse moves onto it: a table's columns scroll (all of them, not only the first 20), and "Ayrıntıları aç (Ctrl+Tık)" opens the object's details. It closes a moment after the mouse leaves both the name and the card, and at once on typing, a click, scrolling or Esc.
- Interface density on five levels (Settings > Appearance > Yoğunluk: Çok sıkı, Sıkı, Normal, Rahat, Geniş, with − and + buttons); toolbars, tabs, rows, inputs and the result grid's rows follow at once. Settings saved with the old compact switch keep their look.
- Explorer grouping ("…" > Gruplama): by folders (as before, the default), by environment (production first, then test, development, none) or by database type; folders stay inside each group. Groups start open; the ones closed and the chosen grouping are remembered.
- Folder colours: a folder's menu > Renk (nine colours, or none) colours its icon; also for the other folders picked with Ctrl+click. Colours follow renames and moves.
- Picking several in the explorer: Ctrl+click picks connections and folders, Shift+click everything between; the menu then connects, disconnects or deletes the picked connections together (Del too), and they can still be dragged into a folder at once.
- Easy-on-the-eye colour schemes: light Sepya, Adaçayı and Sis (soft surfaces without white glare, softened text) and dark Arduvaz, Kırağı, Mokka and Orman (dim rather than near-black). Every text, accent and syntax colour meets WCAG contrast (text ≥ 7:1, secondary and muted ≥ 4.5:1).
- Database types show their product's logo (SQL Server, Oracle, PostgreSQL, SQLite, Redis; Simple Icons, CC0) in the brand colour on a light tile, so that they are told apart at a glance in the explorer, tabs and the connection dialog.
- Databases a connection shows: in the connection dialog, "Gezginde görünen veritabanları" is All or "Yalnızca seçtiklerim"; the list is read from the server (a short connection), filtered and checked. The explorer shows only those (and how many are hidden); the query tabs' database lists follow. The start database ("Veritabanı" field) is now a pick-or-type list filled from the server and always among those shown.
- Query toolbar: Format, comment lines, UPPER/lower case, undo and redo beside the run buttons; Settings > Query tabs can hide them (shortcuts unchanged).
- DBA tables sort (header click, Shift for more columns; numbers with units such as ms, s, MB and % sort as numbers), filter per column (the header's filter button, "filter by this value") and export the rows as shown (cell menu, or the Export button of the activity monitor and the performance analysis) through the result Export dialog. Sorts and filters stay across refreshes.
- Excel export options: sheet name, a title row, frozen header, bold or coloured header, filter buttons, an Excel table with a style (banded rows), cell borders, column widths fitted to the contents, thousands separators, ISO or regional dates, and a totals row (SUBTOTAL, follows Excel's filter). Excel is now the first format of the Export dialog, and the last choices come back. No new package: the workbook is still written row by row.
- DBA screens: a stop button beside the progress bar cancels the read in progress (the query on the server is cancelled).
- DBA screens for Oracle (no Diagnostics Pack needed; the login needs SELECT_CATALOG_ROLE, ending a session ALTER SYSTEM): the activity monitor (V$SESSION: sessions as sid,serial#, waits, blocking, CPU and logical reads, open transactions; ending a session with ALTER SYSTEM KILL SESSION … IMMEDIATE), the performance analysis (heavy statements from V$SQLAREA; big tables read by full scans in the cursor cache; index reads from cached plans with DROP INDEX proposals; fragmentation explained as not measured), the server dashboard (V$SYSMETRIC CPU history, user calls, hard parses, commits, I/O from V$SYSSTAT and V$FILESTAT, SGA + PGA, non-idle waits), a health check of 11 rules (ARCHIVELOG, RMAN backups, recovery area, tablespace use, invalid objects, unusable indexes, stale statistics, default passwords, DBA role, process/session limits, uptime) and a schema's detail (account, tablespaces, size, tables, data files, redo, RMAN backups). On Oracle a schema node of the explorer offers Performans analizi and Veritabanı ayrıntıları.
- DBA screens for PostgreSQL: the activity monitor (pg_stat_activity, blocking through pg_blocking_pids, ending a session with pg_terminate_backend), the performance analysis (heavy queries from pg_stat_statements when the extension is loaded, else an explanation; tables read by many sequential scans in place of missing-index statistics; index use from pg_stat_user_indexes with duplicate / covered / unused findings and DROP INDEX CONCURRENTLY scripts; index fragmentation through pgstattuple with REINDEX CONCURRENTLY), the dashboard (transactions a second, file reads and writes, I/O time, connections, running and blocked requests, shared buffers; PostgreSQL keeps no CPU or cumulative wait figures), the health check (13 PostgreSQL rules: shared_buffers, work_mem, random_page_cost, autovacuum, track_io_timing, pg_stat_statements, WAL archiving, transaction id wraparound, inactive replication slots, sessions idle in a transaction, dead rows, never-analyzed tables, superusers; plus uptime) and the database detail (size, owner, encoding, tables, tablespaces, WAL size). Another database is read on a short-lived connection; the window's connection stays where it is.
- Database detail (Phase 3, DBA; SQL Server): a database node's DBA menu or DBA > Veritabanı ayrıntıları opens one database in depth: state, recovery, compatibility, collation, owner, created; tiles for data (used of allocated), log (fullness and what holds it), virtual log files, last full and log backup, last clean CHECKDB and tables (rows), each marked Uyarı / Kritik when it needs attention; the ten largest tables as bars, the settings, the 50 largest tables (rows, reserved, data, indexes), the files (size, used, free, growth, maximum, path) and the last 30 backups (kind, finished, took, size, compressed, copy only, where). Read inside the database without switching the connection; every tile and section has its "i". SDK (preview): `IDatabaseDetails` (ADR 0014).
- Health check (Phase 3, DBA; SQL Server): DBA > Sağlık kontrolü (also in the explorer's DBA menu) checks the server against 22 rules: max server memory, MAXDOP, cost threshold, optimize for ad hoc, priority boost, auto shrink, auto close, page verify, automatic statistics, TRUSTWORTHY, compatibility level, file growth, logs that cannot be reused, tempdb files, full and log backup age, last good CHECKDB, suspect pages, sa enabled, sysadmin count, uptime. Counts by severity (Kritik, Uyarı, Bilgi, Sorunsuz, each with an icon), most severe first, filter by category or only problems; the chosen finding explains why it matters, the value now and recommended (computed for this server), where it was found, what to do, and a fix script that opens in a query tab, never run ("Görünenlerin betiği" collects all). Rule texts in Turkish and English; an "i" explains the screen. SDK (preview): `IHealthCheck` (ADR 0013).
- DBA menu: a top-level "DBA" menu (Sunucu panosu, Etkinlik izleyici, Performans analizi, Açıklamalar) and a "DBA" submenu in the explorer's menu of a connection (Performans analizi also on a database); the screens left the Tools menu.
- Explanations on DBA screens: a small "i" beside every tile, chart, list and risky action of the dashboard, the activity monitor and the performance analysis opens a card for developers who are not DBAs: what it shows, how to read it (Normal / Dikkat / Sorun with concrete values, each with an icon and a word) and what to do. 20 topics in Turkish and English. On by default; Settings > Görünüm or DBA > Açıklamalar (i) hides them (ADR 0012).
- Server dashboard (Phase 3, DBA; SQL Server): right-click an open connection > "Sunucu panosu" (also Tools). The server (product, edition, version, host, CPUs, memory, uptime); tiles for CPU (server and the rest of the machine), memory in use (target, free), batches a second (compilations), running requests (connections), blocked requests (with an icon when any), page life expectancy and I/O latency (MB/s read and written); charts of CPU for the last hour (one point a minute, history from the server's own record), batches a second and file I/O for the last 10 minutes, with a crosshair and values on hover; the top waits since the dashboard opened (harmless waits left out); the databases with state, recovery model, data and log size, last full and log backup, and a warning when a backup is missing or old. Reads every 5 s while shown (2–30 s), on a connection of its own. Charts are drawn by the app in a palette validated for colour-vision deficiency in both themes. SDK (preview): `IServerDashboard` (ADR 0011).
- Performance analysis (Phase 3, DBA; SQL Server): right-click an open connection or a database > "Performans analizi" (also Tools). One tab, four parts, for all databases or one:
  - Ağır sorgular: the costliest statements from the plan cache, statements differing only in their values counted together, ranked by total or average CPU, duration, reads, or executions (top 25–200), with executions, CPU, time, reads, writes and last run; since when the server counts.
  - Eksik index'ler: what the optimizer asked for (equality, inequality, include columns, impact, uses, score) with a CREATE INDEX proposal.
  - Index kullanımı: every index with seeks, scans, lookups, updates, size and last read; findings: unused, same as another, covered by another (never clustered, primary key or unique constraint indexes) with a DROP INDEX proposal; "Yalnızca bulgular" on by default.
  - Parçalanma: measured only on "Ölç" (LIMITED mode, indexes from 1 000 pages): REBUILD above 30 %, REORGANIZE from 5 %.
  Nothing is run: a row's SQL or script, or "Görünenlerin betiği" (all shown, under a review header), opens in a new query tab on that database. SDK (preview): `IPerformanceAdvisor` (ADR 0010).
- Activity monitor (Phase 3, DBA; SQL Server): right-click an open connection > "Etkinlik izleyici" or Tools > Etkinlik izleyici (Ctrl+Shift+A) opens a tab with the server's sessions: login, host, program, database, state, command, wait and wait time, blocked by, running time, CPU, reads, writes, open transactions and the SQL (running, else last sent). Counters for sessions, running, blocked and head blockers; blocking chains as a tree (head blocker first, an idle session with an open transaction says so); a filter over every column, idle and system sessions on request. It reads every 5 s while shown (2–60 s, F5 by hand) on a connection of its own and stops when hidden or closed. "Sorgu sekmesinde aç" puts a session's SQL in a new query tab; "Oturumu sonlandır…" ends one (KILL) after a confirmation, twice on production, never the monitor's own session, never on a read-only connection. Without VIEW SERVER STATE it says that only your own sessions are visible. SDK (preview): `IActivityMonitor` (ADR 0009).
- Query toolbar: the database (schema) picker has a filter box: type any part of a name (Turkish-aware, ignoring case); Down goes into the list, Enter takes the highlighted or first match, Esc closes. For servers with hundreds of databases.
- Query toolbar: the tab's connection, with its environment's colour, beside the database; another open connection can be chosen there (not while running or with a transaction open). "Çalıştır" is a named button. The tab's tooltip names the connection and the database.
- Status bar: the caret's line and column, and how many characters are selected.
- Explorer: SQL Server's own databases (master, model, msdb, tempdb) are kept in a closed "Sistem veritabanları" folder after the user's. SDK (preview): `ISystemObjectClassifier`.
- Settings > Appearance > Language: Windows' language, Türkçe or English (takes effect at the next start; numbers and dates keep Windows' regional format).
- Explorer: "Tümünü genişlet" opens every folder and every node already loaded (nothing new is read from a database), "Tümünü daralt" closes the tree to its top level. Both sit beside the filter box, which is now smaller (26 px, small text).
- Explorer pin: pinned by default, beside the tabs. Unpinned, the tabs get its room and a narrow "Gezgin" rail stays at the left edge; clicking it opens the explorer over the tabs, and a click elsewhere or Esc closes it. Remembered with the layout.
- SQL editor: a text, date, time or identifier column chosen from the completion list after WHERE, AND, OR, NOT, ON, HAVING, WHEN or in UPDATE … SET (also after a comma there) gets ` = ''` with the caret between the quotes; Unicode columns (nvarchar, nchar, ntext, nvarchar2, nclob) get ` = N''`. Numbers, bits and binary columns, the select list, ORDER BY and INSERT column lists are left alone.
- SQL editor: resting the mouse on a table, view or alias shows a card with its kind and columns (types, primary key, NOT NULL); on a column, its type, table, key and nullability. Resolved as Ctrl+Click is, from cached metadata.
- Settings > SQL editor > Yardımcılar: both can be turned off (on by default).
- Application icon: the title bar's blue database mark, now drawn as vectors with an amber bolt, is the icon of the exe, the window and taskbar, shortcuts and the installer (`src/JetQueries.Wpf/Assets/Logo.xaml`, rendered to `JetQueries.ico` by `scripts/make-icon.ps1`).
- Licensed under MIT; published on GitHub.
- Redis (Phase 2): a new provider on StackExchange.Redis (host, port, database, no auth / password / ACL user, TLS). The explorer lists the logical databases with their key counts and the keys grouped by ":" (read with SCAN, never KEYS; up to 5 000 per database, "Anahtarları filtrele…" narrows with a pattern such as user:*). A key opens in a tab by its type: a string's text (with "JSON biçimlendir"), a hash's fields, a list's elements, a set's or sorted set's members (with scores), a stream's entries, its TTL and memory size; items are added, changed and removed one at a time, the key renamed, given a TTL or deleted. A query tab is the command console: one command per line with redis-cli quoting, answers as result sets (lists numbered, hashes as field/value, INFO as section/name/value), completion of commands with their arguments, SELECT n switching the window's database; blocking commands (KEYS, MONITOR, SUBSCRIBE, BLPOP…) are refused. Read-only connections refuse writes; production connections confirm DEL, FLUSHDB, FLUSHALL. New packages: StackExchange.Redis, Testcontainers.Redis (tests). SDK (preview): `IKeyValueStore`, `IStatementRiskAnalyzer` (ADR 0008).
- Sync (Phase 2): Settings > Eşitleme keeps connection definitions and saved queries the same on your computers through a shared folder (OneDrive, Dropbox, a network share) holding one file, `JetQueries.sync.json`. At start-up, every 2 minutes and on "Şimdi eşitle"; what arrives shows in the explorer and the library at once. Each item is merged three ways (this computer, the file, both at the last sync): a change or deletion on one side wins; an edit beats a deletion; when both edited, the newer saved query wins and, for a connection, the computer syncing. Passwords are never in it: each computer keeps its own in Windows Credential Manager. An unreadable or newer-format file is left alone and the reason shown. The merge base is kept per computer (`%LocalAppData%\JetQueries\sync-state.json`).
- Parameterized queries (Phase 2): `:name` in a query is asked for before it runs (Sorgu parametreleri: each name once, sent as automatic / text / number / date / NULL) and bound as a parameter, never written into the SQL; `${name}` puts the typed text into the SQL (a table or column name). Strings, comments, quoted names, PostgreSQL casts (`::int`) and dollar-quoted bodies, Oracle `:=` and trigger `:new.`/`:old.` are not placeholders. The values are offered again the next time; Refresh, next page, count and export reuse them. SDK (preview): `IQueryParameterSyntax`.
- Saved queries (Phase 2): Sorgu > Kütüphaneye kaydet (Ctrl+Shift+K) keeps the query with a name, a folder (sub folders with /), a description and optionally its connection and database; a tab opened from the library saves back to its query. Araçlar > Kayıtlı sorgular (Ctrl+Shift+Q) lists them by folder, finds them by any word of their name, folder, description or text (Turkish-aware), opens or runs them in a new tab, edits and deletes them. Kept in `saved-queries.json` next to the connections (roaming profile, no secrets).
- Data editor (Phase 2): the result of a SELECT of one table whose primary key it includes can be edited in the grid. Double-click, F2 or typing opens a cell (Enter keeps and goes down, Tab right, Esc drops; empty is NULL, "NULL yap" in the menu); values are converted to the column's type as typed. Delete marks the selected rows for deletion (struck through), Alt+Insert adds a row; changed cells and added rows are highlighted. "Kaydet (n)" (Ctrl+S in the grid) writes everything in one transaction on a connection of its own: parameterized UPDATE and DELETE by the key, INSERT for added rows; a key that does not find exactly one row rolls everything back and says which. A production connection asks first, a read-only one stays read-only; other results show "Salt okunur" with the reason. Reading the rows again with unsaved changes asks first. SDK (preview): `IRowEditScripting`.
- UiKit: `VirtualGrid` edits cells of an `IEditableGridDataSource` (an overlay text box; `BeginEdit`, `CommitEdit`, `CancelEdit`, `CellEdited`, `MoveTo`) and draws its marks (changed cells, added and deleted rows).
- Import data (Phase 2): "Veri içe aktar…" on a table in the explorer reads a CSV file (UTF-8 or Windows-1254, separator detected or chosen, header line), shows its first rows, maps file columns to table columns (by name, else by position) and imports in the background: values are converted to the columns' types (numbers and dates also in Turkish notation, evet/hayır) and sent as parameters in multi-row INSERTs (Oracle: INSERT ALL), in one transaction by default. A bad value names its line and column; a production connection asks first. SDK (preview): `IBulkInsertScripting`.
- Export a result (Phase 2): "Dışa aktar…" in the result footer and the Query menu writes CSV (separator, header, NULL, UTF-8 with BOM for Excel), Excel .xlsx (real numbers and dates, frozen header row), JSON, SQL INSERT, Markdown or HTML. Either the rows shown (the grid's order, filters and visible columns) or the whole result: the query runs again on a connection of its own and every row is streamed to the file, with progress in the status bar. The file can be opened afterwards.
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
- Activity monitor: the grid measures its columns from the first sessions read, not from the empty table it starts with.
- Explorer: at start-up every connection folder is closed; folders opened during the session stay open through a refresh, and Go to object opens the folders of what it reveals.
- Results: decimal(p,s), numeric and NUMBER(p,s) values are shown with their declared scale (1500.00, not 1500.0) in the grid, text, record, copy and export.
- Result footer: "Satır/sayfa" instead of "Sayfa", a smaller box, the fetch status with its full text as a tooltip, separators between groups.
- Explorer: the toolbar keeps New, Connect, Refresh, Go to object; New folder, Edit, Delete and Import moved under "…".
- History: "3 satır alındı" for queries, "3 satır etkilendi" for INSERT / UPDATE / DELETE / MERGE; rows use the full width, the chosen one in the accent's soft colour (also in saved queries, go to object and the command palette).
- Turkish: "Sütun" and "Veri türü" everywhere (no more "Kolon" / "Veri tipi" / "Tip").
- Results: the filter and the grid / text / record switch beside the result tabs are icons only and no taller than the tab row, so they no longer reach over the grid's header.
- UiKit: SearchBox's clear button is 22 px, so a smaller search box keeps it inside.
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
- With Turkish chosen in Settings on an English Windows, texts made after start-up (the "i" cards of DBA screens, health check findings, messages) came out in English: the language was set only inside the async start-up. The providers' DBA messages (missing permissions or extensions, Oracle fragmentation) are now Turkish and English too.
- Export: a late progress report no longer replaces the "rows written" status at the end of an export (the cause of an intermittent test failure).
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
