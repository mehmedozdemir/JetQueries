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
