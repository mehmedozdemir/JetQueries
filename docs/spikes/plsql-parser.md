# Spike findings — PL/SQL parsing with ANTLR4 (Phase 0)

- Branch: `spike/plsql-parser`
- Date: 2026-11-17
- Gate being tested: "PL/SQL test corpus parses" (ANALIZ.md §13), and the risk "PL/SQL parser cannot recover on
  broken code" (§14)
- Suggested thresholds (not yet confirmed): ≥ 98 % of valid files parse; < 200 ms for a 5,000-line package

## Verdict

**Usable for structure, not sufficient for completion, and too expensive as it stands.**

- Coverage is good on the grammar's own tests (every valid file parses) but has gaps on realistic code: of 12
  hand-written files, 4 fail, on 8 distinct constructs — mostly Oracle 12c–21c syntax (section 5).
- Warm speed is borderline: a 5,000-line package takes 107–250 ms depending on the run.
- Two things fail outright: the prediction cache grows to **~880 MB** after the corpus (budget: 200 MB idle for
  the whole app), and a cold cache makes the first parse of each new construct take hundreds of milliseconds to
  seconds (28–36 s for 7,400 lines of varied SQL).
- On broken text the parse tree keeps almost everything *before* the damage, but it knows which clause the
  caret is in only 32–49 % of the time. The token-based fallback gets 93–100 %.

**The gate is not passed.** On real open-source code (section 6: 869 files, 251,000 lines) 90 % of package and
type files parse, large packages take about a second each even warm, and the prediction cache reaches 3.7 GB.
The grammar cannot be used unmodified. The user's own production packages are still untested.

## What was built

- `tests/JetQueries.PlSql.ParseSpike.Grammar` — the grammars-v4 PL/SQL grammar (Apache 2.0, unmodified, pinned
  commit) and the C# parser ANTLR 4.13.1 generates from it. The generated code is checked in, so building needs
  no Java. It is 9 MB of C# and takes about two minutes to compile.
- `tests/JetQueries.PlSql.ParseSpike` — a console harness: corpus run, size scaling, damage experiments, and a
  ~100-line token-based context finder (`TokenContext`).

Run it (Release, 5–8 minutes; `--skip-recovery` for a 1-minute run of sections 1, 2 and 4):

```
dotnet run -c Release --project tests/JetQueries.PlSql.ParseSpike -- --report report.txt
dotnet run -c Release --project tests/JetQueries.PlSql.ParseSpike -- --corpus D:\path\to\real\packages
```

## Limits of this spike

- **No real packages.** The main corpus is the grammar's own regression suite: 478 short files, 7,391 lines,
  the largest 487 lines. It exercises breadth of syntax, not production PL/SQL. The second corpus (section 5)
  is realistic in style but was written by the same author who ran the test, and was not compiled. A hand-written, realistic package
  body repeated to 1,000 / 5,000 / 20,000 lines stands in for a large package; it parses, but it is one coding
  style.
- Timings are from one laptop that was in use; repeated runs differ by up to 2× (ranges are given).
- The token fallback was corrected once after looking at its failures on this same corpus, so its numbers are
  optimistic for unseen code. It was evaluated only for carets inside a `SELECT`.
- Not measured: the lexer as an incremental line tokenizer, PL/SQL-only subsetting of the grammar, antlr4-c3.

## Measurements

### 1. Coverage

| Corpus | Files | Parse without syntax errors |
| --- | ---: | ---: |
| grammars-v4 `examples` + `more-examples` | 478 | 470 (98.3 %) |
| — of which intended to be valid | 470 | 470 (100 %) |
| synthetic package, 1,006 / 5,065 / 20,014 lines | 3 | 3 |

The 8 failures are negative tests (their names end in `_invalid` or `_not_allowed`). 16 files needed the
slower full-LL second stage.

### 2. Speed

| Input | Lex only | Parse, warm cache | Parse, first time |
| --- | ---: | ---: | ---: |
| trivial `SELECT` (first parse in the process, loads the ATN) | – | – | 390–570 ms |
| package, 1,006 lines | 5–8 ms | 26–56 ms | 1.3–1.7 s |
| package, 5,065 lines | 10–12 ms | 107–250 ms | 207–288 ms (cache already warm from the 1,000-line run) |
| package, 20,014 lines | 64–119 ms | 0.75–1.66 s | – |
| whole corpus, 7,391 lines | 16–20 ms | 239–361 ms | **28–36 s** |

- Warm throughput is 20,000–47,000 lines/s. The 200 ms target for 5,000 lines is met in some runs and missed
  in others.
- The lexer alone is fast enough for colouring (about 500,000 lines/s).
- The cold cost is per *construct*, not per file: single 20–60-line files took 1.1–2.9 s the first time
  (`analytic_query.sql`, `datetime02.sql`, `interval03.sql`). One file stays slow even warm
  (`create_table.sql`, 487 lines: 145–236 ms).

### 3. Memory

| Point in the run | Managed heap growth |
| --- | ---: |
| after parsing only the synthetic package (three sizes) | ~233 MB |
| after one pass over the corpus | **~880 MB** |
| parse tree + tokens of a 5,065-line package (while held) | 18 MB |

Nothing but the generated parser's static prediction (DFA) cache is still referenced at those points. The
cache never shrinks by itself.

### 4. Broken input

Damage was applied to files that parse cleanly: the text is cut right after a random token (what the parser
sees while the user types), or one `END` / `;` is blanked out. Synthetic 1,000-line package: 150 samples each;
corpus: 591 and 315 samples.

**Time.** ANTLR's usual two-stage strategy (fast SLL, then full LL when SLL fails) always pays for both stages
on broken text. One SLL pass with error recovery gave the same recovery numbers at a fraction of the time:

| 1,000-line package | intact | cut at caret, p50 / p95 | missing `END` or `;`, p50 / p95 |
| --- | ---: | ---: | ---: |
| two-stage | 18–29 ms | 215–270 / 370–1,500 ms | 400–460 / 480–1,800 ms |
| SLL with recovery | (same) | 37–43 / 74–82 ms | 65–72 / 134–700 ms |

**What survives in the tree.** Share of constructs from the intact parse found again at the same position:

| | package | corpus |
| --- | ---: | ---: |
| constructs before the damage (statements, declarations, query blocks, table references, routine bodies) | 100 % | 98–99 % |
| constructs after a missing `END` / `;` | 92 % | 69–73 % |

**Does the parser know where the caret is?** For the last token before the cut:

| | package | corpus |
| --- | ---: | ---: |
| identical rule chain as in the intact parse | 42–47 % | 45–47 % |
| clause (select list / INTO / FROM / WHERE / GROUP / ORDER), from the broken tree | 32 % (n=19) | 49 % (n=302) |
| clause, from the token fallback | 100 % | 93 % |
| tables in scope, from the broken tree | 58 % | 81–82 % |
| tables in scope, from the token fallback | 100 % | 87 % |

The tree loses the caret because an unfinished statement usually fails inside prediction ("no viable
alternative") before any rule is entered, so its tokens end up as error nodes high in the tree — exactly the
statement the user is typing.

### 5. Second corpus: hand-written, realistic PL/SQL

No production packages were available, so a second corpus was written for the spike
(`tests/JetQueries.PlSql.ParseSpike/CorpusExtra/handwritten`, 12 files, 1,742 lines): package specs and bodies,
triggers, object types, reporting SQL, dynamic SQL, bulk ETL, DDL, a SQL*Plus deployment script, and one file
of 12c–23ai syntax. It was written by Claude and never compiled against a database, so each failure was
re-tested in isolation (`CorpusExtra/grammar-gaps`) to separate grammar gaps from authoring mistakes.

| | Result |
| --- | --- |
| Files without syntax errors | 8 of 12 |
| First pass, cold cache | 7.3–7.5 s for 1,742 lines |
| Warm | 134–141 ms (about 12,500 lines/s — slower than the grammar's own tests) |
| Valid files that needed the slow full-LL stage | 4 of 8 (grammar tests: 8 of 470) |
| Heap growth after this corpus plus the synthetic package | ~500 MB |
| Caret cut: rule chain identical | 38–42 % |
| Constructs surviving after a missing `END` / `;` | 75 % |

Constructs the grammar rejects (each confirmed with a minimal file):

| # | Construct | Since | Note |
| --- | --- | --- | --- |
| 1 | `DEFAULT` on a parameter of an object-type method | – | Believed valid Oracle; not verified against a database |
| 2 | `(SELF AS supertype).method()` | 11g | |
| 3 | `<<label>>` before a top-level anonymous block | – | |
| 4 | `ACCESSIBLE BY (...)` | 12c | |
| 5 | `SQL_MACRO` on a function inside a package (standalone works) | 19c | |
| 6 | Iteration controls: `BY` step, several ranges, `PAIRS OF` / `VALUES OF`, `WHEN` | 21c | Four separate failures |
| 7 | Qualified expression for an associative array (`t('k' => v)`) | 18c | The record form works |
| 8 | `SET SERVEROUTPUT ON SIZE UNLIMITED` | – | SQL*Plus, not PL/SQL; the plain form works |

Everything else in those files parsed, including compound triggers, `MATCH_RECOGNIZE`, `JSON_TABLE`, PIVOT,
hierarchical queries, `FORALL … SAVE EXCEPTIONS`, conditional compilation, q-quoted strings, partitioned DDL
and the 23ai statements tried (`IF NOT EXISTS`, multi-row `VALUES`, `SELECT` without `FROM`).

Two consequences beyond coverage:

- **Realistic code hits the slow path.** Half of the valid files could not be parsed with SLL prediction alone.
- **SLL with recovery is not safe for diagnostics.** On its own it reports errors in 4 of the 8 valid files
  (8 of 470 in the grammar tests). It is still the right mode for a fast structural tree of broken text, but
  error squiggles must come from a full two-stage parse.

### 6. Third corpus: open-source PL/SQL projects

Real code written by other people, cloned (shallow, not added to this repository) into
`C:\AI\temp\opensource`: utPLSQL `77fc2b4`, OraOpenSource Logger `600f0c4`, oos-utils `b621729`,
alexandria-plsql-utils `ada4875`, pljson `09a54e4`, Oracle db-sample-schemas `6660bad`.
869 files, 251,368 lines (one data file over 512 KB skipped).

**Coverage**

| File type | Files without syntax errors | Lines in those files |
| --- | ---: | ---: |
| package specs `.pks` | 159 / 164 (97.0 %) | 14,340 / 15,209 |
| package bodies `.pkb` | 154 / 162 (95.1 %) | 72,837 / 78,822 |
| type specs `.tps` | 127 / 138 (92.0 %) | 3,669 / 4,125 |
| type bodies `.tpb` | 58 / 87 (66.7 %) | 5,365 / 9,648 |
| scripts `.sql` | 143 / 316 (45.3 %) | 21,447 / 143,375 |
| all | 643 / 869 (74.0 %) | |

Packages and types together: 498 of 551 files (90.4 %) — below the suggested 98 %. By project: oos-utils 22/22,
alexandria 113/114, Logger 2/4, utPLSQL 361/411.

The 53 failing package and type files come down to these causes (first error of each file inspected):

| Cause | Files | Kind |
| --- | ---: | --- |
| `(SELF AS supertype).method()` | 20 | grammar gap (also found in section 5) |
| `DEFAULT` on an object-type method parameter | 9 | grammar gap; confirms section 5, this code runs on Oracle |
| `PIPELINED` on a type method | 5 | grammar gap |
| `FINAL` / `OVERRIDING FINAL` / `NOT INSTANTIABLE` before `MEMBER` | 5 | grammar gap |
| `SQL_MACRO` on a packaged function | 4 | grammar gap (section 5) |
| Conditional compilation (`$IF`) between package members or inside a function header | 4 | grammar gap |
| `&&name` substitution variables in the source | 3 | not PL/SQL: SQL*Plus preprocessing |
| `annotations` used as an identifier | 2 | grammar gap (keyword not allowed as a name) |
| `TREAT(x AS t).method();` as a statement | 1 | grammar gap |

Script files fail mostly on SQL*Plus, not on SQL: the most common first offending tokens are `SET …` (34
files) and `@file` includes (14), followed by substitution variables and `DEFINE`/`ACCEPT`. They were not
classified further. An editor needs its own layer for SQL*Plus commands regardless of the parser.

**Speed and memory**

| | Result |
| --- | --- |
| First pass, cold cache | 86 s for 251,368 lines |
| Warm | 15.7 s (16,000 lines/s) |
| Slowest files warm | `oos_util_crypto.pkb` 2,416 lines: 1.2 s; `as_pdf3.pkb` 3,431 lines: 1.1 s; `xlsx_builder_pkg.pkb` 1,987 lines: 0.8 s |
| Slowest file cold | `as_pdf3.pkb`: 11.1 s |
| Files that needed the full-LL stage | 271 of 869 |
| Valid files with false errors under SLL-with-recovery | 45 of 643 |
| Prediction cache after the corpus | **3.7 GB** (peak working set 5.3 GB) |

Large real packages parse at 2,000–3,000 lines per second warm — an order of magnitude slower than the
synthetic package, and far from the 200 ms target for 5,000 lines.

**Recovery** (Logger, oos-utils and alexandria only, to bound memory: 174 clean files, 522 samples per damage type)

| | two-stage | SLL with recovery |
| --- | ---: | ---: |
| parse time, cut at caret, p50 / p95 / max | 8 / 201 / 990 ms | 3 / 40 / 258 ms |
| parse time, missing `END` or `;`, p50 / p95 / max | 16 / 296 / 2,896 ms | 3 / 48 / 278 ms |
| constructs before the damage | 100 % | 97 % |
| constructs after a missing `END` / `;` | 95 % | 93 % |
| rule chain at the caret identical | 47 % | 54 % |

For carets inside a query (only 23 samples): clause correct from the broken tree 43 %, from the token fallback
100 %; tables in scope 87 % versus 96 %.

## Conclusions

1. **Keep the grammar for structure, in the background.** Outline, folding, Ctrl+Click targets, diagnostics and
   "which routine am I in" can come from the tree: everything before the damage survives, and most of what
   follows a missing `END`.
2. **Do not build completion on the parse tree.** Use tokens for the caret context (clause, tables and aliases
   in scope) and the tree only for what lies outside the statement being typed. The fallback in the risk table
   should be the primary mechanism. antlr4-c3 (candidates computed from the ATN at the caret) is the other
   known approach and was not evaluated.
3. **Use one SLL pass plus recovery for the structural tree, never for diagnostics.** It is 5–7× faster on
   broken text, but it reports false errors in valid code; squiggles need the two-stage parse, debounced.
4. **Plan for grammar maintenance.** Eight gaps turned up in 1,700 lines of ordinary code. The grammar has to
   be forked and extended (the 21c loop forms and `ACCESSIBLE BY` first), and unknown syntax must degrade to
   "no squiggle", not to a wall of errors.
5. **The cache has to be tamed before this ships.** Options, none measured yet: clear or cap the DFA cache
   (trading CPU for memory), parse one routine at a time instead of the whole package, warm the cache on a
   background job at start-up, and cut the grammar down to what an editor needs — most of its 10,000 lines
   cover administrative DDL.

## Proposed next steps

| # | Step | Why |
| --- | --- | --- |
| 0 | Decide whether to keep ANTLR + this grammar at all. Alternatives worth a short comparison: a hand-written recursive-descent parser for the PL/SQL subset an editor needs, or tree-sitter with a PL/SQL grammar | Memory and speed on real code miss the budgets by a wide margin, not by tuning distance |
| 1 | Run the harness on the user's own production packages (kept outside the repo) with `--corpus` | Open-source code is real but not theirs; coding style drives both coverage and speed |
| 2 | Measure cache size and warm speed with a PL/SQL-only subset of the grammar, and with per-routine parsing | Decides whether ANTLR + this grammar fits the memory budget at all |
| 3 | Extend the token context finder to DML, PL/SQL blocks and aliases, with tests; compare with antlr4-c3 | It is the completion engine's core |
| 4 | Confirm thresholds: ≥ 98 % valid files, < 200 ms per 5,000 lines warm, and add one for memory and first-parse latency | The two failing dimensions have no agreed target yet |
