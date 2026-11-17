# Manual checklist — Phase 0 PL/SQL parse spike

Branch `spike/plsql-parser`. There is no UI in this spike; the checks are about reproducing the numbers and
supplying what the spike could not have. Findings: [docs/spikes/plsql-parser.md](../spikes/plsql-parser.md).

## 1. Build

- [ ] `dotnet build JetQueries.slnx` succeeds with 0 warnings. The first build takes about two minutes longer
      than before: `JetQueries.PlSql.ParseSpike.Grammar` compiles a 9 MB generated parser. No Java is needed.
- [ ] `dotnet test` still reports 19 passed (Core 12, Sdk 7).

## 2. Reproduce the public-corpus run

```
dotnet run -c Release --project tests/JetQueries.PlSql.ParseSpike -- --report report.txt
```

- [ ] It finishes in 5–8 minutes and prints four sections (use `--skip-recovery` for a 1-minute run).
- [ ] Section 2 reports 470/478 files without syntax errors, and the 8 listed failures all have `invalid` or
      `not_allowed` in their names.
- [ ] Section 1: note the "warm parse" time of the 5,065-line package on your machine (target: under 200 ms).
- [ ] Sections 2 and 4: note "managed heap growth" (about 880 MB on the development laptop).

## 2b. Check the hand-written corpus against a real database

The 12 files in `tests/JetQueries.PlSql.ParseSpike/CorpusExtra/handwritten` (also in `C:\AI\temp`) were never
compiled. Their value as evidence depends on them being valid Oracle.

- [ ] In a scratch schema, compile the constructs listed as grammar gaps in the findings (the minimal files in
      `CorpusExtra/grammar-gaps`). Any that Oracle itself rejects is my mistake, not a grammar gap — in
      particular gap 1 (`DEFAULT` on an object-type method parameter).
- [ ] Note your database version: gaps 4–7 need 12c, 18c, 19c and 21c respectively.

## 3. Run it on real packages — the part only you can do

- [ ] Export package specs and bodies from one or two real schemas into a folder **outside the repository**
      (`.sql`, `.pks`, `.pkb`, `.pls`, `.plb`, `.prc`, `.fnc` are picked up, recursively).
- [ ] `dotnet run -c Release --project tests/JetQueries.PlSql.ParseSpike -- --corpus <that folder> --report real.txt`
- [ ] Send back from `real.txt`: files / lines / largest file, "parsed without syntax errors", the FAILED lines
      (file name and first error only — check they contain nothing confidential), the "warm" line, and the
      recovery block for "corpus files that parse cleanly".
- [ ] For any FAILED file, check whether the code is valid Oracle PL/SQL; if so, it is a grammar gap.

## 3b. Profile one slow package (optional)

```
dotnet run -c Release --project tests/JetQueries.PlSql.ParseSpike -- --profile <path to a large .pkb>
```

- [ ] It prints warm parse time, the time of a single SLL pass, and the 15 grammar decisions that cost the
      most. On your own packages, check whether the same rules lead the table as in section 7 of the findings
      (`unary_expression_core`, `general_element_part`, `atom`, `routine_name`, `type_spec`).

## 3c. Hand-written structural parser (the chosen approach, ADR 0003)

```
dotnet run -c Release --project tests/JetQueries.PlSql.ParseSpike -- --structure
dotnet run -c Release --project tests/JetQueries.PlSql.ParseSpike -- --structure --list-errors --corpus <folder with your packages>
```

- [ ] The first command finishes in a few seconds and reports 476/478 files without diagnostics; the two
      flagged files are `case_when.sql` and `sqlplus_slash_separated_invalid.sql`.
- [ ] On your own packages (folder **outside the repository**): note "no diagnostics: n/m" and the warm total.
      Every `DIAGNOSTICS` line is either a real structural error in that file or a parser bug — send the file
      name, line and message (check they contain nothing confidential).
- [ ] Optional cross-check, slow and memory-hungry (about 3 minutes, 3–5 GB): add `--compare-antlr`. `DIFFERS`
      lines show routines or statements the two parsers disagree on.
- [ ] Open one large package and check a few routine names and line numbers against what you see in your
      current tool's outline. There is no UI for this yet; it becomes the outline in Phase 1.

## 4. Decisions needed

- [ ] Confirm the trade-off in ADR 0003: the editor reports structural errors only; other syntax errors appear
      after compiling on the database.


- [ ] Confirm or change the thresholds: at least 98 % of valid files parse; a 5,000-line package parses in
      under 200 ms warm.
- [ ] Set a memory target for the language service and a target for first-parse latency; the spike fails any
      reasonable one today.
- [ ] The grammar and example files (Apache 2.0) now stay under `tests/` only and are not shipped. Confirm that
      keeping them in the repository as test material is acceptable, or ask for them to be removed.
