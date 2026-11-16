# ADR 0001 — Layered architecture with an SDK boundary

- Status: accepted
- Date: 2026-11-17
- Scope: Phase 0, step 1 (repository skeleton)

## Context

JetQueries must keep three promises from `docs/ANALIZ.md`: the UI never freezes, the core stays small with
everything else delivered as extensions, and the editor's intelligence is independent of the editor control.
These only hold if the project structure makes the wrong dependency impossible to write, rather than relying on
discipline.

## Decision

The solution is split into layers with a one-way dependency rule, enforced by project references:

```
JetQueries.Desktop ──► JetQueries.Core ──► JetQueries.Sdk ◄── Providers/* and Extensions/*
        │                                        ▲
        ├──► JetQueries.Editor ──────────────────┘
        └──► JetQueries.DataGrid   (Avalonia only; knows nothing about databases)
```

| Project | Role | May reference |
| --- | --- | --- |
| `JetQueries.Sdk` | Public extension contract (`IDatabaseProvider`, `IMetadataProvider`, `IQueryExecutor`, `ISqlLanguageService`) and its data types. Preview until 1.0. | BCL only |
| `JetQueries.Core` | UI-free application core: job queue now; sessions, metadata cache, history/settings store and extension host later. | Sdk |
| `JetQueries.Editor` | SQL editor control; talks to `ISqlLanguageService`, never to a parser. | Sdk, Avalonia |
| `JetQueries.DataGrid` | Virtualized results grid control. | Avalonia |
| `JetQueries.Provider.*` | Database providers, loaded by the extension host. | Sdk (+ their driver/parser) |
| `JetQueries.Desktop` | Avalonia host: generic host + DI composition root, shell window, theme, view models. | Core, Sdk, Editor, DataGrid |

Rules that follow:

1. **Core and Desktop never reference a concrete provider.** Providers are discovered and loaded at run time
   (collectible `AssemblyLoadContext`, later in Phase 0). Built-in features use the same SDK as third parties;
   if the SDK lacks something, the SDK is extended.
2. **All background work goes through `IJobQueue`** (`System.Threading.Channels`, prioritized, with progress and
   cancellation). Core and provider code never touches the UI thread and uses `ConfigureAwait(false)`
   (CA2007 is an error in `JetQueries.Core`). Only view models marshal back to the UI thread.
3. **The SDK has no dependencies** beyond the BCL, documents every public member (missing XML docs fail the
   build) and takes a `CancellationToken` as the last parameter of every asynchronous method. A contract test in
   `JetQueries.Sdk.Tests` guards the first and last points.
4. **Secrets never enter shareable types.** `ConnectionProfile` holds non-secret settings only;
   `ConnectionCredential` is passed separately at connect time and redacts itself in `ToString()`.
5. **Desktop is the only composition root.** Services are registered through extension methods
   (`AddJetQueriesCore()`); nothing below Desktop resolves services from a container.

## SDK v0 shape (and why)

- `IQueryExecutor.ExecuteAsync` returns one `IAsyncEnumerable<QueryEvent>` carrying result-set boundaries, row
  batches and server messages in order. A flat event stream handles multiple result sets and interleaved
  `PRINT`/`DBMS_OUTPUT` output without nested enumerables, and row *batches* match how the grid consumes data
  (500–1000 rows per UI update).
- `IMetadataProvider` loads one level at a time (`GetObjectsAsync(parent, kinds)`), so a 10,000-table schema is
  never loaded up front. The host's metadata cache will implement the same interface, so language services see
  cached and live metadata identically.
- `ISqlLanguageService` v0 has only what the first editor milestone needs (line tokenizer, diagnostics,
  completion, definition). Hover, signature help, formatting, folding and find-references are added when a
  consumer exists, to avoid freezing guesses into the contract.
- `IDatabaseProvider` is a factory for `IDatabaseConnection`; the host opens separate connections for queries
  and metadata so a long query cannot block IntelliSense.

## Consequences

- Positive: illegal dependencies fail at compile time; providers can be developed, tested and versioned
  independently; the Core is testable without Avalonia.
- Negative: anything a built-in feature needs must first be expressed in the SDK, which slows the first
  implementation of each capability.
- Open: one shared worker pool means enough long-running queries could delay short jobs. The default
  concurrency is generous (≥ 8) and jobs are asynchronous, but separate lanes (interactive / query / background)
  may be needed once real workloads exist. Revisit when the query executor lands.
- Open: the UI currently uses Avalonia's Fluent control templates under our tokens; a fully custom control theme
  is a later decision.
