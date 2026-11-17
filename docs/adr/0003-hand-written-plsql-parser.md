# 0003 — Hand-written structural parser for PL/SQL

- Status: accepted (2026-11-17), pending the owner's check on production packages
- Supersedes: the "PL/SQL → ANTLR4 (grammars-v4)" line in the original technology choice

## Context

The Phase 0 spike measured the grammars-v4 PL/SQL grammar with ANTLR 4.13.1 (`docs/spikes/plsql-parser.md`).
On 869 open-source files it parsed 90 % of package and type files, needed 1–2 s for a large package even with
a warm prediction cache, 6–23 s for the first parse, and kept 3.7 GB of cache. Profiling showed the cost
concentrated in about seven ambiguous expression rules, so a repaired fork might have worked; a hand-written
parser and tree-sitter were the alternatives. The project budgets are < 200 MB idle memory and IntelliSense in
< 100 ms. The owner asked for the best-performing option.

## Decision

The Oracle provider's language service uses a **hand-written tokenizer and recursive-descent parser that reads
structure only**: script splitting as SQL*Plus does it, units, routines, parameters, declarations, blocks and
statement ranges. Expressions and the inside of SQL statements are skipped. Completion context is computed
from tokens. ANTLR and the grammars-v4 grammar are not shipped; they remain under `tests/` as a reference until
the parser has its own test suite.

## Consequences

- 251,368 lines parse in about 0.1 s with no retained memory and no warm-up; re-parsing on every keystroke is
  affordable. All 869 corpus files parse without diagnostics, and routines match the ANTLR tree exactly where
  ANTLR succeeds (7,121 of 7,121).
- The editor cannot report every syntax error. Structural errors (unbalanced `END`, missing `;`, `THEN`,
  `LOOP`, `BEGIN`) are reported while typing; all others come from the database compile and must be surfaced
  from `USER_ERRORS`.
- We own the parser: new Oracle syntax that changes structure (a new block form, a new unit kind) needs a code
  change. Syntax inside expressions and SQL statements needs none, which is where most new syntax lands.
- No third-party grammar, no generated code and no Java in the build for Oracle.
- PostgreSQL and SQLite are still planned on ANTLR grammars and are not covered by this decision.
