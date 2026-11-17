# Manual checklist — Phase 1 · SQL Server provider

Branch `feature/sqlserver-provider`. The provider connects, runs SQL and reads metadata, but nothing in the UI
uses it yet (no connection manager, explorer or editor), so the checks are tests and one status-bar number.

## 1. Build and unit tests

- [ ] `dotnet build JetQueries.slnx` succeeds with 0 warnings.
- [ ] `dotnet test` reports Sdk 7, Core 40, DataGrid 28, Provider.SqlServer 36 passed and **7 skipped**.

## 2. The provider ships as a built-in extension

- [ ] After a build, `src/JetQueries.Desktop/bin/Debug/net10.0/extensions/sqlserver/` exists and contains
      `extension.json`, `JetQueries.Provider.SqlServer.dll` and `Microsoft.Data.SqlClient.dll`, but no
      `JetQueries.Sdk.dll`. The application folder itself contains no SqlClient file.
- [ ] `dotnet run --project src/JetQueries.Desktop`: the status bar shows `Extensions: 1`.

## 3. Integration tests against a real SQL Server — never run so far

They start SQL Server 2022 in Docker (image download of roughly 1.5 GB the first time). The container image is
under Microsoft's licence terms, so the tests only run when you opt in:

```
$env:JETQUERIES_MSSQL_ACCEPT_EULA = 'Y'
dotnet test tests/JetQueries.Provider.SqlServer.Tests
```

- [ ] 43 passed, 0 skipped. They cover: event order of rows, messages and row counts; parameters; `MaxRows`;
      cancelling a running statement; abandoning a large result; errors; metadata across databases
      (databases, schemas, tables, views, procedures, triggers, columns with formatted types, definitions),
      with Turkish characters in names and data.
- [ ] If any fails, send the test name and message: this code has only been compiled, not executed against a
      server.

## 4. Against one of your own servers (optional, later)

Not possible from the UI yet. It becomes the first check of the connection manager.

## Connection settings understood by the provider

`server` (required), `database`, `authentication` (`sql` or `windows`), `encrypt` (`true`, `false`, `strict`;
default `true`), `trustServerCertificate` (default `false`), `readOnlyIntent`, `connectTimeoutSeconds`.
With the default `encrypt=true`, servers with a self-signed certificate need `trustServerCertificate=true`.

## Not covered yet

- Microsoft Entra authentication, `GO` batch splitting, execution plans, transactions in the UI.
- The T-SQL language service (colouring, IntelliSense, Ctrl+Click): `LanguageService` is still `null`.
- Large values are read whole (no streaming of `varbinary(max)` / `nvarchar(max)` cells).
- The extension folder carries native libraries for every platform (11 MB); trimming to the published runtime
  is left for the packaging work.
