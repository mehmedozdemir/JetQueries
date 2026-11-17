# Manual checklist — Phase 1 · Oracle provider

Branch `feature/oracle-provider`. The provider connects, runs SQL and PL/SQL scripts and reads the data
dictionary; the connection dialog, object explorer and query tabs work with it through the same generic code
as SQL Server. It has no language service yet: colouring is the general keyword list, and there is no
completion, squiggles or Ctrl+Click for Oracle.

## 1. Automated checks

- [ ] `dotnet test` reports Provider.Oracle 39 passed and 6 skipped (plus Sdk 7, Core 91, Desktop 38,
      DataGrid 28, Provider.SqlServer 124 passed / 11 skipped).
- [ ] With `$env:JETQUERIES_ORACLE_TESTS = 'Y'`, Provider.Oracle reports 45 passed in about 80 seconds. A
      throwaway Oracle Free 23 container is started from the local image `gvenzl/oracle-free:23-slim` and
      removed again; no other container is touched.
- [ ] After a build, `src/JetQueries.Desktop/bin/Debug/net10.0/extensions/oracle/` contains `extension.json`,
      `JetQueries.Provider.Oracle.dll` and `Oracle.ManagedDataAccess.dll`. The status bar shows `Extensions: 2`.

## 2. A database to try with

Any Oracle database you can reach. For a local one that touches none of your existing containers:

```
docker run --rm -d --name jq-ora -e ORACLE_PASSWORD=<a password> -e APP_USER=jq -e APP_USER_PASSWORD=<a password> -p 127.0.0.1:15211:1521 gvenzl/oracle-free:23-slim
```

Host `127.0.0.1`, port `15211`, service name `FREEPDB1`, user `jq`. It needs about a minute before it accepts
connections. Remove it with `docker stop jq-ora`.

## 3. Connect

- [ ] New connection: "Oracle" is offered next to "SQL Server", with its own fields (Host, Port, Service name,
      SID, Connect as, Connect timeout). No Oracle client installation is needed.
- [ ] Test connection: success; a wrong password shows `ORA-01017`; a wrong port shows the "cannot be reached"
      message within about 5 seconds.
- [ ] Giving both a service name and a SID, or neither, is refused with a message before anything connects.

## 4. Explore

- [ ] The connection opens into schemas (no SYS, SYSTEM and other Oracle-owned schemas; your own is there),
      and each schema into Tables, Views, Procedures, Functions, **Packages**, Sequences, Synonyms, Types.
- [ ] Table columns show Oracle types: `NUMBER(12,2)`, `VARCHAR2(50 CHAR)`, `TIMESTAMP(6)`, with `PK` and `null`.

## 5. Run scripts

- [ ] This runs as one script: statements end at `;`, PL/SQL units at the `/` line.

  ```sql
  CREATE TABLE jq_demo (id NUMBER PRIMARY KEY, note VARCHAR2(40 CHAR));
  INSERT INTO jq_demo VALUES (1, 'bir;iki');
  CREATE OR REPLACE PROCEDURE jq_report IS
    l_count NUMBER;
  BEGIN
    SELECT COUNT(*) INTO l_count FROM jq_demo;
    DBMS_OUTPUT.PUT_LINE('rows: ' || l_count);
  END jq_report;
  /
  EXEC jq_report
  SELECT * FROM jq_demo;
  ```

  Messages shows "1 rows affected" and `rows: 1`; Result 1 shows the row.
- [ ] A procedure with a mistake (`this is not plsql;` in its body): Messages shows the compiler's error in red
      — "PROCEDURE …, line n, column m: PLS-…" — although Oracle reported the CREATE as done.
- [ ] `SET …`, `PROMPT …`, `SHOW ERRORS` lines in a script are skipped without an error.
- [ ] A number with more than 28 digits is shown in full, not as an error.
- [ ] Cancel stops a long-running query or a PL/SQL loop within a second.
- [ ] On a read-only connection, `BEGIN … END;` blocks and `EXEC` are refused as well as DML and DDL.

## Known limits

- **Cancel does not interrupt a statement that is only waiting** — for a row lock held by another session, or
  in `DBMS_SESSION.SLEEP` — when the network between you and the server drops TCP urgent data. Measured on
  Docker's port forwarding: such a statement ends when its wait ends. VPN tunnels and NAT often behave the same.
  A statement that computes or returns rows stops at once. A server-side cancel (`ALTER SYSTEM CANCEL SQL`)
  through a second connection is the planned remedy.
- Every statement is committed as it runs; there are no transaction controls yet.
- Bind variables (`:name`) in a script are not asked for; such a statement fails with ORA-01008.
- `&substitution` variables, `@file` includes and other SQL*Plus features are not interpreted.
- Authentication is user name and password only: no wallet, no Kerberos, no OS authentication, no TNS alias
  from `tnsnames.ora`.
- Package bodies are not listed separately in the explorer; object source can be read by the provider but
  nothing in the UI opens it for Oracle until the PL/SQL language service exists.
- DBMS_OUTPUT is read one line per round trip, at most 5,000 lines per statement.
