# Manual checklist — Phase 1 · Oracle SQL and PL/SQL language service

Branch `feature/plsql-language-service`. Builds on the Oracle provider ([checklist](phase1-oracle-provider.md));
use the same Oracle test database. Create this first:

```sql
CREATE TABLE jq_cards (id NUMBER(18) PRIMARY KEY, card_no VARCHAR2(19), balance NUMBER(12,2));
CREATE OR REPLACE PACKAGE jq_ops AS
  PROCEDURE open_card(p_id IN NUMBER);
  FUNCTION balance_of(p_id IN NUMBER) RETURN NUMBER;
END jq_ops;
/
```

## 1. Automated checks

- [ ] `dotnet test` reports Provider.Oracle 71 passed and 7 skipped; with `$env:JETQUERIES_ORACLE_TESTS = 'Y'`,
      78 passed (a throwaway Oracle container from the local image).
- [ ] Provider.SqlServer still reports 124 passed and 11 skipped: T-SQL completion now runs on the same engine.

## 2. Colouring

With an Oracle connection chosen in the query's connection box:

- [ ] PL/SQL words (`BEGIN`, `EXCEPTION`, `LOOP`, `ELSIF`), functions (`NVL`, `TO_CHAR`), types (`VARCHAR2`,
      `PLS_INTEGER`), `:bind` and `&substitution` variables each have their colour.
- [ ] `q'[it's]'` is one string; a `/* … */` comment and a string that continue on the next line stay coloured.
- [ ] `FOR i IN 1..10 LOOP` — `1` and `10` are numbers.

## 3. Completion

- [ ] `SELECT * FROM ` — tables and views of your schema. Names appear as the dictionary has them (upper case);
      a name created in quotes with lower case or spaces is inserted in double quotes.
- [ ] `SELECT c. FROM jq_cards c` (caret after `c.`) — the columns with their types, whatever case you typed
      the table name in.
- [ ] `BEGIN jq_ops.` — `BALANCE_OF` and `OPEN_CARD`; an overloaded routine appears once.
- [ ] `SELECT * FROM <another schema>.` — that schema's objects.
- [ ] Inside a procedure or function body, where a value is expected (after `:=`, `WHERE x =`, `INTO`): its
      parameters and local variables first, with their declared types, then package-level declarations and
      the package's own routines; another routine's locals are not offered.
- [ ] `SELECT COUNT(*) INTO ` offers variables, not tables.
- [ ] Nothing is offered inside strings (including `q'[…]'`) and comments.

## 4. Structural errors

- [ ] Delete an `END IF;` in a procedure body: after a pause the next `END` gets a red wavy line, "END IF
      expected". Removing a `;`, a `THEN` or a `LOOP` is reported in the same way.
- [ ] A wrong expression (`x := := 1;`) is **not** underlined here. Run the `CREATE` and the compiler's error
      appears in Messages with its line.

## 5. Go to definition (Ctrl+Click or F12)

- [ ] On `jq_ops` or on `open_card` in `jq_ops.open_card(1)` — the package specification opens in a new tab.
- [ ] On a table name or alias — its DDL as Oracle writes it; on a view, procedure or function — its source.

## Not covered yet

- Going to the place of a member inside the package source, or to the package **body**: the specification
  opens at the top.
- Go to definition on a local variable or parameter.
- Completion of: record fields and `%ROWTYPE` variables' columns, cursor columns, CTE names, synonyms'
  targets, parameter hints when calling a routine.
- Names are inserted in upper case as stored; there is no setting for lower case yet.
- Squiggles from the compiler (`ALL_ERRORS`) in the editor: errors are shown in Messages only.
