# 0010 — Performance analysis: heavy queries and index advice through the SDK

- Status: accepted (2026-11-25)
- Relates to: ANALIZ item 10 (DBA paneli, Faz 3), 0009 (activity monitor)

## Context

After the activity monitor, the next DBA question is "what costs the server most, and which indexes are wrong?":
the heaviest statements, indexes the optimizer asks for, indexes nobody reads but every write maintains, indexes
that repeat another, and fragmentation. Each database keeps these statistics in its own views; the advice must be
safe (nothing is created or dropped by the tool) and cheap (fragmentation reads index pages).

## Decision

- **`IPerformanceAdvisor`** (SDK, preview) is an optional interface of `IDatabaseConnection` with four reads:
  `GetHeavyQueriesAsync` (ranked by total or average CPU, duration, reads, or by executions; optionally one
  database), `GetMissingIndexesAsync`, `GetIndexUsageAsync` (every index of a database's user tables with key and
  included columns, filter, reads, writes, size) and `GetFragmentationAsync`. Results carry the statements a DBA
  would run (`CREATE INDEX`, `DROP INDEX`, `ALTER INDEX … REBUILD | REORGANIZE`), quoted by the provider.
- **The host never runs them.** A row's script, or every shown row's scripts under a review header, opens in a new
  query tab on the same connection and database.
- **The host decides what is redundant** (`IndexAnalysis`), from facts only: unused (no reads since the counters
  started), duplicate (same keys and filter, holding everything; a unique index is never a duplicate of a plain one),
  covered (keys are the start of another index's, holding everything). Clustered indexes, primary keys and unique
  constraints are never proposed, nor compared against the clustered index (a narrow nonclustered one is cheaper to
  scan). Two identical indexes: the later one is reported.
- **Reads happen when asked**: a part is read when first shown (heavy queries, missing indexes, index use) on a
  channel of the screen's own; fragmentation only on "Ölç" with a minimum size (1 000 pages by default). The app's
  own statements carry a `/* JetQueries:advisor */` marker and are left out of the heavy queries.
- **SQL Server first**: `sys.dm_exec_query_stats` grouped by query hash (the costliest plan's text,
  `sys.dm_exec_plan_attributes` for the database), `sys.dm_db_missing_index_*` with a score of cost × impact × uses,
  `sys.dm_db_index_usage_stats` with `FOR XML PATH` column lists (SQL Server 2016 compatible),
  `sys.dm_db_index_physical_stats` in `LIMITED` mode. The screen says since when the server has been counting.

## Consequences

- Plan-cache figures vanish with evicted plans and at restart; Query Store as a source (per database, longer
  history) can be added behind the same contract.
- Oracle (`V$SQL`, no Diagnostics Pack; no missing-index statistics) and PostgreSQL (`pg_stat_statements`,
  `pg_stat_user_indexes`) implement the parts they can; the screen shows what a provider returns.
