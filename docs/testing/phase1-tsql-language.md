# Manual checklist — Phase 1 · T-SQL language service (GO, colouring, completion)

Branch `feature/tsql-language-service`. Builds on the SQL editor ([checklist](phase1-sql-editor.md)); use the
same SQL Server test database and create a few tables in it first.

## 1. Automated checks

- [ ] `dotnet test` reports Sdk 7, Core 88, Desktop 34, DataGrid 28, Provider.SqlServer 89 passed and 11 skipped;
      with `$env:JETQUERIES_MSSQL_ACCEPT_EULA = 'Y'`, Provider.SqlServer reports 100 passed.

## 2. GO

- [ ] Run this as one script; it only works if each `GO` starts a new batch:

  ```sql
  CREATE TABLE dbo.jq_demo (id int IDENTITY PRIMARY KEY, note nvarchar(40) NULL);
  GO
  CREATE OR ALTER PROCEDURE dbo.jq_demo_count AS SELECT COUNT(*) AS n FROM dbo.jq_demo;
  GO
  INSERT INTO dbo.jq_demo DEFAULT VALUES;
  GO 3
  EXEC dbo.jq_demo_count;
  ```

  Result 1 shows `3`; Messages shows "1 rows affected" three times.
- [ ] `GO` inside a string or a comment, and a column called `go`, do not split the script.
- [ ] A `SELECT` no longer adds an "n rows affected" line to Messages; only statements without a result do.

## 3. Colouring on a SQL Server connection

- [ ] With a SQL Server connection chosen in the query's connection box: functions (`ISNULL`, `GETDATE`) and
      data types (`nvarchar`, `int`) have their own colour, `@variables` another; `/* a /* b */ c */` is one
      comment; a string that continues on the next line stays a string.
- [ ] With no connection chosen, only the general keyword colouring is used.

## 4. Completion

- [ ] `SELECT * FROM ` — after the space a list of the tables and views of `dbo` appears. Typing narrows it;
      Enter or Tab inserts; Esc closes. Names with spaces are inserted in brackets.
- [ ] `SELECT c. FROM dbo.jq_demo c` with the caret after `c.` — the table's columns, in table order, with
      their types.
- [ ] `SELECT  FROM jq_demo d JOIN … t ON …` with the caret after `SELECT ` — columns of all tables in the
      statement, then the aliases.
- [ ] `WHERE co` — matching columns, then functions (`COALESCE`, `CONVERT`), then keywords.
- [ ] `SELECT * FROM sys.` — objects of that schema; `SELECT * FROM tempdb.` — its schemas;
      `EXEC ` — procedures of `dbo`.
- [ ] Ctrl+Space asks for the list anywhere. Nothing is offered inside a string or a comment.
- [ ] The first list on a table takes as long as one metadata query; after that it is immediate, also after a
      restart (the explorer's cache is shared).
- [ ] Typing fast never freezes or inserts something by itself. **This part was only tested by simulated key
      presses, never by hand — please try it and say what feels wrong.**

## Not covered yet

- No error squiggles and no Ctrl+Click yet (they need the ScriptDom parser; next step).
- Completion does not know: CTE names and their columns, derived tables, table variables and temp tables'
  columns, parameters of procedures, the schemas of the current database in an unqualified list, snippets.
- Tables are offered from the login's default schema assumed to be `dbo`.
- New tables appear in completion only after Refresh in the explorer (the 12-hour metadata cache).
- When a batch fails, the batches after it are not run (SSMS continues with the next batch).
- The completion list is AvaloniaEdit's standard one: no icons per kind, the detail (type, table) shows as a
  tooltip beside the selected row.
