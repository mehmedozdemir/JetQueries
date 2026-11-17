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

## 3. Run it on real packages — the part only you can do

- [ ] Export package specs and bodies from one or two real schemas into a folder **outside the repository**
      (`.sql`, `.pks`, `.pkb`, `.pls`, `.plb`, `.prc`, `.fnc` are picked up, recursively).
- [ ] `dotnet run -c Release --project tests/JetQueries.PlSql.ParseSpike -- --corpus <that folder> --report real.txt`
- [ ] Send back from `real.txt`: files / lines / largest file, "parsed without syntax errors", the FAILED lines
      (file name and first error only — check they contain nothing confidential), the "warm" line, and the
      recovery block for "corpus files that parse cleanly".
- [ ] For any FAILED file, check whether the code is valid Oracle PL/SQL; if so, it is a grammar gap.

## 4. Decisions needed

- [ ] Confirm or change the thresholds: at least 98 % of valid files parse; a 5,000-line package parses in
      under 200 ms warm.
- [ ] Set a memory target for the language service and a target for first-parse latency; the spike fails any
      reasonable one today.
- [ ] The grammar and example files are Apache 2.0. Confirm that is acceptable alongside the project licence
      (MIT is suggested and compatible, with attribution).
