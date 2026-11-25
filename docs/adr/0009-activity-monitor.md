# 0009 — Activity monitor: sessions, blocking and ending a session through the SDK

- Status: accepted (2026-11-25)
- Relates to: ANALIZ item 10 (DBA paneli, Faz 3), 0001 (layered architecture), 0004 (extension host)

## Context

Phase 3 starts the DBA tools. The first one is the activity monitor a DBA keeps open on a busy server: who is
connected, what each session runs and waits for, who blocks whom (the head of a blocking chain is usually an idle
session with an open transaction), and ending the head blocker. Every provider answers this from its own views
(SQL Server `sys.dm_exec_*`, Oracle `V$SESSION`, PostgreSQL `pg_stat_activity`), so the host needs one contract
and must not know any of them. The monitor reads every few seconds for as long as it is open, so it must not
hold up query tabs or the explorer, nor load the server.

## Decision

- **`IActivityMonitor`** (SDK, preview) is an optional interface of the provider's `IDatabaseConnection`:
  `GetActivityAsync` returns an `ActivitySnapshot` (every visible session with login, host, program, database,
  status, command, wait and wait time, blocked-by, CPU, reads, writes, elapsed time, open transactions, current or
  last SQL; the monitor's own session id; a `Limitation` text when the login sees only part of the server), and
  `KillSessionAsync` ends one session. `ConnectionSession.SupportsActivity` and `QueryChannel.Activity` expose it;
  nothing in Core or the UI references a provider.
- **The monitor opens a query channel of its own** (`ConnectionSession.OpenChannelAsync`), so a running query or
  an open transaction in a tab never waits for it, and it closes the channel when its tab closes.
- **It reads only while shown**: every 5 s by default (2–60 s, or by hand with F5), through the job queue; hidden
  or closed, it stops. One read is one round trip of constant text over the server's dynamic views.
- **Ending a session is guarded**: never the monitor's own session, never on a read-only connection, always after
  a confirmation naming the session, login, host and program, and a second one on a production connection. On SQL
  Server the id is parsed as an integer before `KILL n` is written (KILL takes no parameter).
- **The host computes the blocking chains** from `BlockedBy` (head blockers first, then blocked, then running
  sessions), so providers only report facts.
- **SQL Server first** (`SqlServerActivity`): sessions joined with their requests, the running statement (or the
  last one sent) through `sys.dm_exec_sql_text`, `HAS_PERMS_BY_NAME(… 'VIEW SERVER STATE')` for the limitation.
  Oracle (`V$SESSION`, `V$SQL`, no Diagnostics Pack views) and PostgreSQL (`pg_stat_activity`,
  `pg_blocking_pids`) follow on the same contract.

## Consequences

- A provider without `IActivityMonitor` simply has no "Activity monitor" menu item.
- The monitor costs one connection per open monitor tab while it is open.
- Locks per object, deadlock history and heavy queries are separate screens on separate optional interfaces,
  so each can ship per provider when ready.
