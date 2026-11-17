# Corpus: grammars-v4 PL/SQL examples

`examples/` and `more-examples/` are copied unmodified from
[antlr/grammars-v4](https://github.com/antlr/grammars-v4), `sql/plsql`, commit
`c306207ac870e7c5a95763b518ba3e3a1ca61409` (2026-11-07). They are that grammar's regression tests: short files
covering breadth of Oracle syntax, including a few intentionally invalid ones (`*_invalid*`, `*_not_allowed*`).

They are not production PL/SQL. Real packages must not be added here; pass their folder to the spike with
`--corpus <dir>` instead.
