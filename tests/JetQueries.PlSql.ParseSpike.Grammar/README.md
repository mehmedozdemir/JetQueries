# PL/SQL grammar (Phase 0 spike)

| File | Origin |
| --- | --- |
| `PlSqlLexer.g4`, `PlSqlParser.g4` | [antlr/grammars-v4](https://github.com/antlr/grammars-v4), `sql/plsql`, commit `c306207ac870e7c5a95763b518ba3e3a1ca61409` (2026-11-07), unmodified |
| `PlSqlLexerBase.cs`, `PlSqlParserBase.cs` | same commit, `sql/plsql/CSharp`, unmodified |
| `Generated/PlSqlLexer.cs`, `Generated/PlSqlParser.cs` | ANTLR 4.13.1 output, checked in so that building needs no Java |

The grammar is licensed under the Apache License 2.0 (see the header of each `.g4` file). Keep those headers,
and list the grammar in the third-party notices once the project licence is chosen.

## Regenerating

Needs a Java runtime (11+) and `antlr-4.13.1-complete.jar` from <https://www.antlr.org/download/>. The tool
version must match the `Antlr4.Runtime.Standard` package version.

```
java -jar antlr-4.13.1-complete.jar -Dlanguage=CSharp -no-listener -no-visitor -o Generated PlSqlLexer.g4 PlSqlParser.g4
```

Delete the `.interp` and `.tokens` files it also writes; they are not needed at run time.
