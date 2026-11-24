# JetQueries

A fast, extension-based database IDE for **SQL Server, Oracle, PostgreSQL, SQLite and Redis**, built with .NET 10
and WPF (Windows).

> Status: early preview (`0.1.0-alpha`). Phase 1 (MVP) is done and Phase 2 is under way; expect rough edges.

## What it does

- **Connections:** folders, environment colours (development / test / production), read-only connections,
  passwords in Windows Credential Manager, SSH tunnels, import from DBeaver and SSMS, Oracle TNS aliases and
  wallets, sync of connections and saved queries through a shared folder (no passwords).
- **SQL editor:** IntelliSense from the live catalog, Ctrl+Click / F12 to an object's details or source, snippets,
  formatting, parameterized queries (`:name`, `${name}`), a query library.
- **Results:** a virtualized grid for a million rows, server-side paging, counting and SQL filters, sorting and
  column filters, editing rows in place and saving them in one transaction, export (CSV, Excel, JSON, SQL,
  Markdown, HTML) and CSV import.
- **Object explorer:** databases, schemas, tables, views, routines, packages; details, DDL, executing procedures.
- **Redis:** key tree grouped by `:` (SCAN only), a key view by type with TTL and item editing, a command console.
- **Safety:** production connections confirm destructive statements; read-only connections refuse writes.
- **Updates:** in-app updates through Velopack (stable / beta / nightly channels).

## Build and run

Requires the .NET 10 SDK on Windows.

```
dotnet build JetQueries.slnx
dotnet run --project src/JetQueries.Wpf
dotnet test
```

Integration tests start real databases with Testcontainers and are opt-in (`JETQUERIES_MSSQL_ACCEPT_EULA=Y`,
`JETQUERIES_ORACLE_TESTS=Y`, `JETQUERIES_POSTGRES_TESTS=Y`, `JETQUERIES_REDIS_TESTS=Y`); without Docker they are
skipped. A local package is built with `scripts/pack.ps1` (Velopack).

## Architecture

Database providers and features are extensions written against the public SDK (`src/JetQueries.Sdk`, preview);
the core and the user interface never reference a provider. See [docs/adr](docs/adr) for the decisions,
[docs/ANALIZ.md](docs/ANALIZ.md) for the product analysis (Turkish) and [CLAUDE.md](CLAUDE.md) for the
contributor guide.

## License

[MIT](LICENSE)
