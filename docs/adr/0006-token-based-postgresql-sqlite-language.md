# 0006 — Token-based language support for PostgreSQL and SQLite

- Status: accepted (2026-11-23)
- Supersedes, for Phase 1: the "PostgreSQL, SQLite → ANTLR4 (grammars-v4)" line in the original technology choice

## Context

Phase 1 asks for *basic* PostgreSQL and SQLite support next to deep SQL Server and Oracle support. The
completion engine that T-SQL and PL/SQL already share (`src/Providers/Shared/Language`) works on tokens and
metadata only: it never needs a parse tree, because half-typed statements are the normal case. ADR 0003 showed
that a grammars-v4 ANTLR grammar can cost seconds and gigabytes on large inputs; the PostgreSQL grammar is of
the same size class. What the two dialects need from a parser in Phase 1 is little: colouring, statement
splitting, and knowing where strings and comments end.

## Decision

Both providers use one **generic, configurable tokenizer** (`src/Providers/Shared/Generic`) with each dialect's
lexical rules (PostgreSQL: dollar quotes, nested comments, `E''` strings, `::` casts, `$1`; SQLite: `[name]`,
`` `name` ``, `X''` blobs, `?`/`:`/`@`/`$` parameters), a **statement splitter** that knows the two bodies that
contain semicolons (`BEGIN ATOMIC … END`, `CREATE TRIGGER … BEGIN … END`), and the **shared completion engine**.
Diagnostics are limited to strings, comments and dollar quotes that are never closed; everything else is
reported by the database when the statement runs. No ANTLR runtime or grammar is added.

## Consequences

- No new dependency, no warm-up, no retained memory; the same budgets as the other dialects hold.
- Syntax errors (a missing comma, a misspelt keyword) appear only after running, with the server's position
  turned into a line and column of the statement. SQL Server and Oracle keep their richer diagnostics.
- PL/pgSQL bodies inside dollar quotes are a string to the editor: no completion of their variables yet.
- A parser can still be added later behind the same `ISqlLanguageService`, when a feature needs a tree (for
  example a PL/pgSQL structure view or rename refactoring). That decision would be a new ADR.
