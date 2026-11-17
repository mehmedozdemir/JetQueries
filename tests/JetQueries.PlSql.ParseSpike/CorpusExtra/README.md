# Extra corpora (not part of the default run)

| Folder | What it is |
| --- | --- |
| `handwritten/` | 12 files, 1,742 lines of PL/SQL written for this spike by Claude: packages, triggers, object types, reporting SQL, dynamic SQL, bulk ETL, DDL, deployment scripts, and one file of Oracle 12c–23ai syntax. Not checked against a database. |
| `grammar-gaps/` | One small file per construct that was suspected to be unsupported, to tell a grammar gap from a mistake in the hand-written code. 10 of the 14 fail; see docs/spikes/plsql-parser.md. |

Run with:

```
dotnet run -c Release --project tests/JetQueries.PlSql.ParseSpike -- --corpus tests/JetQueries.PlSql.ParseSpike/CorpusExtra/handwritten --list-errors
```

These are still not production packages: the author of the corpus also chose what to test.
