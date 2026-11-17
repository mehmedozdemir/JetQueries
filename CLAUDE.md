# CLAUDE.md — JetQueries

Guidance for Claude Code working in this repository. The full product analysis (Turkish) is in `docs/ANALIZ.md`; approved decisions are in its last section. Read it before starting a phase.

## What this is
JetQueries is an open-source, cross-platform **database IDE** (DBeaver/DataGrip class) built with **.NET 10 + Avalonia 12**.
Initial databases: **SQL Server, Oracle** (MVP, must be excellent), PostgreSQL, SQLite (basic in MVP), Redis (Phase 2).
Pillars: elegant professional UI · never-blocking async UI · extension-based core · first-class SQL editor (IntelliSense, colouring, Ctrl+Click navigation) · versioned releases with auto-update.

## Hard rules
1. **Stay inside the current approved phase.** Phases are in `docs/ANALIZ.md` (Yol haritası). Phase 0 must be validated by the user before Phase 1 starts. Ask before expanding scope.
2. **The UI thread never does I/O or heavy CPU work.** Every DB call, parse, metadata load and export runs through the job queue with `CancellationToken`, progress and cancel. Service/core code uses `ConfigureAwait(false)`; only view models marshal back to the UI. No `.Result`, `.Wait()` or sync-over-async anywhere.
3. **Everything is an extension.** Database providers and built-in features are written against `JetQueries.Sdk` only. Core and Desktop never reference a concrete provider assembly. If a built-in feature needs something the SDK lacks, extend the SDK — don't bypass it.
4. **SDK is a public contract.** Changes to `JetQueries.Sdk` are deliberate: XML docs on every public member, no breaking change without `[Obsolete]` first. Mark it `preview` until SDK 1.0.
5. **Never persist secrets in plain text.** Passwords go to the OS credential store (Windows Credential Manager/DPAPI, macOS Keychain, libsecret). Never log connection strings with credentials. Synced connection definitions never contain secrets.
6. **Safety in queries.** Metadata and data-preview queries are parameterized; identifiers are always quoted by the provider's quoting helper, never concatenated raw. Prod-tagged connections confirm `UPDATE/DELETE` without `WHERE`, `DROP`, `TRUNCATE`.
7. **Performance budgets are tests.** Cold start < 1.5 s (ReadyToRun), idle memory < 200 MB, grid scrolls 1M rows at 60 fps, IntelliSense popup < 100 ms with warm cache. Benchmarks live in `tests/` and run in CI.
8. **Turkish-safe text handling.** Case-insensitive comparisons of SQL keywords/identifiers use `StringComparison.OrdinalIgnoreCase` / `CultureInfo.InvariantCulture` (the `i/İ` problem).
9. Code, comments, commit messages and identifiers in **English**. User-facing strings go through resources (Turkish + English).
10. Prefer few, well-known dependencies. Add a NuGet package only via `Directory.Packages.props` and say why in the commit message. Verify a package version supports Avalonia 12 / .NET 10 before adding it.

## Tech
- .NET 10, C# latest, nullable enabled, warnings as errors, central package management, lock files.
- Avalonia 12.x + custom theme (design tokens in `Styles/`), light and dark from day one.
- MVVM: CommunityToolkit.Mvvm (source generators). DI/hosting: Microsoft.Extensions.Hosting.
- Editor: AvaloniaEdit. Docking: Dock.Avalonia. Results grid: **custom virtualized grid** (`JetQueries.DataGrid`), drawn with `DrawingContext`.
- Parsers: T-SQL → Microsoft.SqlServer.TransactSql.ScriptDom; PL/SQL → hand-written structural parser + token-based completion context (ADR 0003; no ANTLR); PostgreSQL, SQLite → ANTLR4 (grammars-v4); Redis → command table.
- Drivers: Microsoft.Data.SqlClient, Oracle.ManagedDataAccess.Core, Npgsql, Microsoft.Data.Sqlite, StackExchange.Redis.
- Local store: SQLite (settings, query history, metadata cache). Logging: Microsoft.Extensions.Logging + Serilog.
- Updates: Velopack (channels: stable / beta / nightly). Versioning from git tags (MinVer).
- Tests: xUnit v3, Avalonia.Headless for UI, Testcontainers for real databases (skipped when Docker is unavailable), BenchmarkDotNet.

## Layout (target — create it in Phase 0)
```
src/JetQueries.Desktop/            Avalonia host: windows, docking, theme, view models for shell
src/JetQueries.Core/               job queue, sessions, metadata cache, history/settings store, extension host
src/JetQueries.Sdk/                public extension contracts (IDatabaseProvider, IMetadataProvider, IQueryExecutor,
                                   ISqlLanguageService, contribution points) — published as NuGet
src/JetQueries.Editor/             SQL editor control on AvaloniaEdit, talks to ISqlLanguageService
src/JetQueries.DataGrid/           virtualized results grid control
src/Providers/JetQueries.Provider.SqlServer/  .Oracle/  .PostgreSql/  .Sqlite/  .Redis/
src/Extensions/                    built-in feature extensions (export, ER diagram, …)
tests/                             unit, integration (Testcontainers), UI (headless), benchmarks
docs/                              ANALIZ.md, adr/, testing/ (manual test checklists)
```

## Reuse from SsmsToolkit
`C:\AI\SSMSExtensions` contains working Ctrl+Click table-inspector logic (ScriptDom name resolver, SQL Server metadata queries, table details tabs). Port the SSMS-independent parts (`SsmsToolkit.Data`) into `JetQueries.Provider.SqlServer` and the language service — never reference SSMS assemblies.

## Phases (summary — details in docs/ANALIZ.md)
- **Phase 0 · Foundation & spikes:** solution skeleton, CI, Velopack packaging, design tokens + shell window, job queue, SDK v0, extension host (AssemblyLoadContext), **virtual grid spike (1M rows)**, **PL/SQL parse spike** on a real-package corpus. Gate: both spikes meet targets.
- **Phase 1 · MVP:** connection manager (folders, env colour, read-only, VPN reachability check), object explorer, editor with IntelliSense + Ctrl+Click, results grid, query history; SQL Server + Oracle deep, PostgreSQL + SQLite basic. Gate: user's daily work without SSMS/DBeaver.
- **Phase 2:** data editor, export/import, saved & parameterized queries, **sync of connection definitions and saved queries (no secrets)**, Redis explorer, table designer, SDK preview.
- **Phase 3:** execution plans, DBA panel, schema compare, ER diagram. SDK 1.0.
- **Phase 4:** reporting, extension catalog, AI features (last), new providers.

## Build / run / test
- Build: `dotnet build JetQueries.slnx`
- Run: `dotnet run --project src/JetQueries.Desktop`
- Test: `dotnet test` (integration tests need Docker; they skip otherwise)
- Package locally: `scripts/pack.ps1` (Velopack `vpk pack`) — create in Phase 0.
- Logs: `%LocalAppData%\JetQueries\logs`. Settings and history: `%LocalAppData%\JetQueries\`.
- You (Claude Code) cannot click through the UI. At the end of each task, write/refresh a short manual test checklist in `docs/testing/`.

## Git & versioning
- Open-source GitHub repo. License not yet chosen — **ask before the first public push** (MIT suggested).
- SemVer, `0.x` until the MVP gate. Version comes from the git tag (MinVer); never hand-edit versions.
- Branch per task: `feature/<name>`, `fix/<name>`, `spike/<name>`; merge to `main` via PR. `main` always builds.
- Conventional Commits (`feat:`, `fix:`, `docs:`, `refactor:`, `chore:`, `test:`, `perf:`).
- Release: update `CHANGELOG.md`, tag `vX.Y.Z`, CI builds Velopack packages for win-x64 (first), later osx-arm64, linux-x64.
- Record significant decisions as ADRs in `docs/adr/NNNN-title.md`.
