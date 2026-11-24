# 0008 — Key-value databases (Redis) through the same provider contract

- Status: accepted (2026-11-24)
- Relates to: ANALIZ "Redis için ayrı `IKeyValueProvider`", 0001 (layered architecture), 0004 (extension host)

## Context

Redis has no catalog, no SQL and no tables: what a user wants from it is a key tree grouped by `:`, a view of a
key by its type, TTL editing, a command console, and never a `KEYS *` on a production server (ANALIZ, item 12).
The host is built around `IDatabaseProvider` → `IDatabaseConnection` (`Executor`, `Metadata`), with sessions,
query channels, credentials, SSH tunnels, read-only and production guards on top. A second, separate provider
model for Redis would duplicate all of that.

## Decision

- **Redis is an ordinary `IDatabaseProvider`** (`JetQueries.Provider.Redis`, on StackExchange.Redis). Connections,
  credentials (password or ACL user), TLS, tunnels and the connection dialog work unchanged.
- **The query tab is the console.** `IQueryExecutor` runs each line as a command (redis-cli quoting) and returns
  each answer as a result set. Commands that block or never return (`KEYS`, `MONITOR`, `SUBSCRIBE`, blocking
  pops, `MULTI`…) are refused with the reason; `SELECT n` switches the window's database (`IDatabaseContext`).
  Its language service colours commands and completes them from a command table (no parser, as CLAUDE.md says).
- **`IKeyValueStore`** (SDK, preview) is the "separate key-value provider" of ANALIZ, but as an optional interface
  of the connection's `Metadata`, the way `IRoutineMetadataProvider` is: logical databases with key counts,
  `SCAN` pages (never all keys), a key's value by type (at most N items), and one change at a time
  (set text, add, replace or remove an item, rename unless taken, expire, delete). The explorer and the key tab
  use it when `ConnectionSession.IsKeyValue`; nothing in Core or the UI references Redis.
- **`IStatementRiskAnalyzer`** (SDK, preview) lets a provider whose language is not SQL classify its own text.
  The host's read-only refusal and production confirmation use it instead of the SQL keyword guard; Redis marks
  `DEL`, `UNLINK`, `GETDEL`, `FLUSHDB` and `FLUSHALL` as destructive and every unknown command as writing.
- The key tab refuses changes on a read-only connection by itself (the change does not go through a query
  channel), and asks before deleting a key or, on production, an item.

## Consequences

- Cluster and Sentinel are not supported yet (one endpoint); a cluster's other nodes' keys are not listed.
- The explorer lists at most 5 000 keys per database node; a pattern (`user:*`) narrows it.
- Large collections show their first 1 000 items; the console reads the rest with ranges or `*SCAN`.
