# 0014 — Database detail screen through the SDK

- Status: accepted (2026-11-25)
- Relates to: 0011 (server dashboard), 0012 (DBA menu and explanations), 0013 (health check)

## Context

The dashboard shows a server; the object details show a table. In between, a developer or DBA wants one database
in depth: how big it is and what fills it, whether its log is in trouble, when it was last backed up and checked,
its files and settings.

## Decision

- **`IDatabaseDetails`** (SDK, preview) is an optional interface of `IDatabaseConnection`:
  `GetDatabaseDetailAsync(database)` returns a `DatabaseDetail`: state, recovery model, compatibility, collation,
  owner, created; settings as key / label / value (`DbProp_<key>` translated by the host); data and log space, log
  fullness and what holds it, virtual log files; table count and rows; last clean CHECKDB; files; the 50 largest
  tables (reserved, data, index space); the last 30 backups.
- **Read inside the database without switching the connection**: SQL Server runs the database-scoped batch through
  `[db].sys.sp_executesql @batch` (the batch is a parameter; only the bracketed name is in the text), so the screen's
  connection keeps its own database. Backups come from msdb, unknown (not missing) without access; an offline
  database returns what the server knows.
- **The host judges**: log above 75 % warning, above 90 % critical; VLFs above 300 warning, above 1 000 critical;
  full backup older than a day warning, a week (or none) critical; FULL recovery log backup older than an hour
  warning, a day (or none) critical, SIMPLE needs none; last clean CHECKDB older than a week warning, a month or
  never critical. Statuses carry an icon and a word.
- **Opened** from a database node's DBA menu and from DBA > Veritabanı ayrıntıları (the explorer's database, else
  the query tab's current database); one tab per connection and database; read when opened and on F5.
- Per ADR 0012 every tile and section has its "i" (8 topics, Turkish and English).

## Consequences

- Oracle (schema / tablespace view) and PostgreSQL (`pg_database_size`, `pg_stat_user_tables`, WAL) can fill the
  same record; fields they lack stay empty and their tiles show "—".
