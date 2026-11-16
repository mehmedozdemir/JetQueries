# Claude Code — first session prompt (Phase 0, step 1)

Paste this into Claude Code after running `claude` in C:\AI\JetQueries:

---
Read CLAUDE.md and docs/ANALIZ.md. We are starting Phase 0.

Step 1 only (stop and report when done):
1. `git init`, branch `main`, add a `global.json` pinning the installed .NET 10 SDK.
2. Create `JetQueries.slnx` and these projects per the Layout in CLAUDE.md:
   JetQueries.Desktop (Avalonia 12 app), JetQueries.Core, JetQueries.Sdk, JetQueries.Editor, JetQueries.DataGrid,
   Providers/JetQueries.Provider.SqlServer, Providers/JetQueries.Provider.Oracle,
   tests/JetQueries.Core.Tests, tests/JetQueries.Sdk.Tests.
3. Desktop shows an empty shell window with our own light/dark design tokens (Styles/Tokens.axaml),
   a left explorer placeholder, center document area and bottom results area. Generic host + DI wired.
4. JetQueries.Core: a job queue on System.Threading.Channels (priority, progress, cancel) with unit tests.
5. JetQueries.Sdk v0: IDatabaseProvider, IMetadataProvider, IQueryExecutor (IAsyncEnumerable rows, cancel),
   ISqlLanguageService — XML-documented, no implementations yet.
6. `dotnet build` and `dotnet test` green; first commit `chore: repository skeleton`.
7. Write docs/testing/phase0-step1.md (what I should click/check) and an ADR for the layered architecture.

Do not start the grid or PL/SQL spikes yet; propose a plan for them at the end.
---
