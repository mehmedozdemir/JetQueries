# Phase 1 · PostgreSQL and SQLite (basic) — manual test checklist

Automated coverage: SQLite end to end against real files (always runs); PostgreSQL settings, splitting,
colouring and completion as unit tests; PostgreSQL against a real server in Docker only with
`JETQUERIES_POSTGRES_TESTS=Y` (image `postgres:16-alpine`, started and removed by the tests).

```bash
JETQUERIES_POSTGRES_TESTS=Y dotnet test tests/JetQueries.Provider.PostgreSql.Tests
```

## PostgreSQL
- [ ] New connection → provider **PostgreSQL**: host, port (5432), database (postgres), SSL, timeout, user and
      password. Test reports success; a wrong port reports "unreachable" within a few seconds.
- [ ] The explorer shows the schemas (no `pg_*`, no `information_schema`), and under a schema Tables, Views,
      Procedures, Functions, Sequences, Types. Columns show types such as `character varying(19)`, `text[]`.
- [ ] Run a script of several statements separated by `;` including a `CREATE FUNCTION … AS $$ … $$`:
      each statement runs on its own, the function body is not cut at its inner semicolons.
- [ ] `DO $$ BEGIN RAISE NOTICE 'hi'; END $$;` → "hi" appears under Messages.
- [ ] `SELECT * FROM missing_table;` → the error reads `ERROR 42P01: relation "missing_table" does not exist
      (statement line 1, column 15)`.
- [ ] `SELECT pg_sleep(30);` then Cancel → stops within a second; the next query works.
- [ ] Values: `timestamptz` shows its offset (`+00:00`), arrays show as `{a,b}`, `interval '1 year 2 mons'`
      shows as text, a 30-digit `numeric` shows all digits, `jsonb` shows as text.
- [ ] Completion: `SELECT * FROM ` offers tables of the current schema; `schema.` offers that schema's objects;
      `alias.` offers columns; a name with capitals is inserted quoted (`"Balance"`). `CALL ` offers procedures.
- [ ] Ctrl+Click / F12 on a table opens a `CREATE TABLE` written from the catalog (columns, identity, defaults,
      constraints, indexes); on a view `CREATE OR REPLACE VIEW`; on a function every overload.
- [ ] An unclosed `$$` or `/*` is underlined with "never closed".
- [ ] Disconnect closes the server sessions (no pooled connections left in `pg_stat_activity`).

## SQLite
- [ ] New connection → provider **SQLite**: no user or password fields; **Browse…** picks a `.db` / `.sqlite`
      file; Open: Read and write (default), Read only, Create the file if it does not exist.
- [ ] A path that does not exist with "Read and write" → "The database file … does not exist." and no file is
      created. With "Create" a new empty database is made.
- [ ] The explorer shows Tables and Views directly under the connection (no schemas, no empty groups);
      `sqlite_sequence` / `sqlite_stat1` are hidden. Columns include generated ones.
- [ ] A script with `CREATE TRIGGER … BEGIN …; …; END;` runs as one statement.
- [ ] `INSERT`/`UPDATE`/`DELETE` report their row count; `CREATE TABLE` reports none.
- [ ] `WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x+1 FROM n) SELECT count(*) FROM n;` then Cancel →
      stops within a second.
- [ ] Read only: a `DELETE` fails with "attempt to write a readonly database".
- [ ] Ctrl+Click on a table opens its `CREATE TABLE` followed by its indexes and triggers.
- [ ] After Disconnect the file can be renamed or deleted in Explorer (no handle left open).
- [ ] Both themes, Turkish UI: setting labels read "Veritabanı dosyası", "Açılış", "Yabancı anahtarları uygula";
      the button reads "Gözat…".

## Known limits
- No syntax check before running for these two dialects (ADR 0006): errors come from the database.
- PL/pgSQL inside `$$ … $$` gets no completion of its own variables.
- PostgreSQL: one database per connection (switching databases means another connection); partitions are not
  listed under their partitioned table; table DDL leaves out partitioning, storage options, comments and grants.
- SQLite: only the `main` database is listed in the explorer; attached databases are reachable in SQL and in
  completion as `name.`. In-memory databases are refused. While a write transaction is open on the query
  connection, explorer reads wait up to 10 s and may then report "database is locked".
