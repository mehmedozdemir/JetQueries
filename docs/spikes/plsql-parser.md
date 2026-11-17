# Spike findings — PL/SQL parsing (Phase 0)

- Branch: `spike/plsql-parser`
- Date: 2026-11-17
- Gate being tested: "PL/SQL test corpus parses" (ANALIZ.md §13), and the risk "PL/SQL parser cannot recover on
  broken code" (§14)
- Suggested thresholds (not yet confirmed): ≥ 98 % of valid files parse; < 200 ms for a 5,000-line package

## Verdict

**Decision (ADR 0003): the Oracle language service is built on a hand-written structural parser, not on ANTLR.**
The owner asked for the best-performing option. Measured on the same 869 open-source files (section 8):

| | ANTLR + grammars-v4 | Hand-written structural parser |
| --- | ---: | ---: |
| Valid files with no reported errors | 643 of 869 (74 %); packages and types 90 % | 869 of 869 |
| Whole corpus, warm (251,368 lines) | 15.7 s | 0.07–0.12 s |
| 5,000-line package, warm | 107–250 ms synthetic, 1–2 s real | 4.5–5.4 ms |
| First parse in the process | 6–23 s for a large package | 30 ms |
| Memory kept between parses | 3.7 GB | none (12 MB heap growth after all runs) |
| Routines found, where ANTLR parses the file | 7,121 | 7,121, all identical |

The trade-off is deliberate: the hand-written parser reads structure only. It does not check expressions or the
inside of SQL statements, so it cannot report every syntax error; those come from the database when the unit is
compiled. With that parser the measurable parts of the gate are met on public code. The owner's own packages
are still untested.

What follows is the record of how the ANTLR grammar was measured and why it was dropped.

### ANTLR grammar (superseded)

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

Profiling (section 7) shows why: valid packages fall through to ANTLR's slow full-LL stage, and 91–97 % of the
prediction time and most of the cache sit in about seven ambiguous expression rules. That makes a repaired
fork worth a two-day experiment before ANTLR is dropped; see "Alternatives compared".

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
- The alternatives comparison is a desk study: the hand-written parser and tree-sitter columns are estimates,
  not measurements, and no grammar repair has been attempted yet.

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

### 7. Where the time and the cache go

`--profile <file>` runs ANTLR's own profiler over one warm parse. Three large packages from section 6, two runs
(the laptop was in use; timings moved by up to 1.7× between runs, the shape did not):

| | oos_util_crypto.pkb | as_pdf3.pkb | ut_utils.pkb |
| --- | ---: | ---: | ---: |
| Lines / tokens | 2,417 / 20,999 | 3,432 / 18,809 | 1,171 / 6,014 |
| First parse in the process | 10–14 s | 17–23 s | 6–7 s |
| Warm two-stage parse | 1.5–2.4 s | 1.3–1.4 s | 0.2–0.4 s |
| One SLL pass with recovery, warm | 281 ms (13 false errors) | 170 ms (19 false errors) | 37 ms (11 false errors) |
| Lexer only | 2–6 ms | 6–9 ms | 1–2 ms |
| Full-LL fallbacks / true ambiguities | 3,202 / 1,119 | 2,506 / 872 | 800 / 341 |
| Decisions used, of 3,054 | 102 | 141 | 164 |
| Prediction time in the top 8 decisions | 97 % | 96 % | 91–94 % |

- **The slow part is the full-LL stage, not parsing as such.** All three files are valid, yet SLL fails on
  them, so every parse pays for a failed SLL pass plus a full-LL pass. Full-LL predictions are recomputed on
  every parse (they are not cached the way SLL predictions are), which is why a warm cache does not help. The
  single SLL pass is 5–8× faster.
- **The cost is concentrated.** The same handful of expression rules lead every table:
  `unary_expression_core`, `general_element_part`, `atom`, `routine_name`, `type_spec`, `statement`,
  `concatenation`. They are where the grammar is genuinely ambiguous (ANTLR reports 341–1,119 ambiguities per
  file).
- **So is the cache.** One decision in `general_element_part` (is `a.b(c)` a call, an indexed element, a
  cursor expression …) looks ahead up to **31,211 tokens** and owns 47–72 % of all cached states (20,155 of
  28,121 after three files).
- The lexer is not a problem: it can serve syntax colouring as it is.

This is the same pattern others report for community ANTLR SQL grammars: Bytebase cut a T-SQL parse from 143 s
to 2.3 s by removing one ambiguity, and a grammars-v4 maintainer notes that this PL/SQL grammar's "chain of
rules" expressions are a known cause of long lookahead (grammars-v4 issue 4014). It suggests, but does not
prove, that a small number of grammar repairs would remove most of the cost.

## Alternatives compared

Desk comparison plus the profile above; only option A has measurements behind it.

| | A. ANTLR, forked and repaired grammar | B. Hand-written recursive-descent parser | C. tree-sitter + tree-sitter-plsql |
| --- | --- | --- | --- |
| Coverage today | 90 % of real package/type files; 9 known causes | None; has to be built | Unmeasured. One maintainer, 13 stars, "in development"; built for packages, limited for stand-alone routines and triggers |
| Speed | 1–2 s per large package now; a single SLL pass already does 170–281 ms. Unknown after repairs | Linear, typically well under 50 ms for such files; no warm-up | Incremental re-parse in milliseconds by design |
| Memory | 3.7 GB now, about half to three quarters of it in one decision. Unknown after repairs | Tree only | Tree only, native heap |
| First parse | 6–23 s per new kind of file now | None | None |
| Broken code | Recovery keeps what precedes the damage; clause at the caret 43 % | We design it: recover at `;`, `END`, `/` and statement keywords | Built for it, but quality depends on the grammar |
| Effort | Days for a first verdict; then permanent grammar ownership | Weeks for PL/SQL structure, statements and expressions; Oracle SQL in full is far larger | Days to integrate; then the same grammar gaps, in a JavaScript grammar DSL and a C toolchain |
| Dependencies | Managed runtime already in the tree (Apache 2.0); Java only to regenerate | None | Native library per OS and CPU, third-party .NET bindings, C build in CI |
| Main risk | Repairs do not get close enough; cold start stays slow | Scope creep: SQL inside PL/SQL is most of the language | Smallest community, weakest grammar, most build machinery |

Independent of the choice: completion context comes from tokens (100 % against 43 % from the tree in every
corpus), colouring comes from the lexer, and T-SQL uses ScriptDom and is not affected.

**Recommendation.** Rule out C: it adds native binaries on three platforms and a weaker grammar than the one
already measured, to solve a problem (incrementality) we do not yet have. Do not commit to B yet either: it is
the safest end state for speed and memory but the most expensive to reach. First run one **time-boxed
experiment on A (two days)**: fork the grammar, remove the ambiguities in the seven rules above, add the
missing constructs from section 6, and re-run the same corpus. Keep ANTLR only if all of these hold on the
869-file corpus:

1. no valid file needs the full-LL stage (today 271 do), so one SLL pass is also correct for diagnostics;
2. a 5,000-line package parses in under 200 ms warm;
3. the prediction cache stays under 100 MB after the whole corpus;
4. the first parse of a large package takes under 2 s, or can be hidden by a background warm-up at start-up;
5. at least 98 % of package and type files parse.

If any fails, switch to B for structure (declarations, blocks, statement boundaries, with SQL statements kept
as token ranges) and keep ANTLR out of the product.

**Outcome.** The owner chose the best-performing option, so the experiment on A was not run and B was built
directly; section 8 has the measurements.

## 8. Hand-written structural parser

`tests/JetQueries.PlSql.ParseSpike/Structure`: a tokenizer (`PlLexer`, ~420 lines) and a recursive-descent
parser (`PlStructureParser`, ~1,500 lines), no dependencies. Run with `--structure`; add `--compare-antlr` to
check it against the ANTLR tree.

**What it reads.** A script the way SQL*Plus cuts it (SQL*Plus commands, SQL statements up to `;`, PL/SQL units
up to the `/` line); package and type specifications and bodies, stand-alone routines, triggers, anonymous
blocks; routines with parameters; declarations; blocks, `IF`, `CASE`, loops, exception handlers; every
statement as a range, with SQL statements marked. **What it skips.** Expressions and the inside of SQL
statements: it only tracks parentheses and `CASE … END` to find where they stop.

| Corpus | Files without diagnostics | Warm time | Notes |
| --- | ---: | ---: | --- |
| Open-source projects (869 files, 251,368 lines) | 869 / 869 | 73–116 ms (2.2–3.5 million lines/s) | slowest file 3–5 ms |
| grammars-v4 examples (478 files) | 476 / 478 | 2 ms | the two flagged files are invalid: one is named `…_invalid`, the other closes a `CASE` statement with a bare `END` |
| Hand-written corpus (12 files) | 12 / 12 | 1 ms | includes the 21c and 23ai syntax ANTLR rejected |
| Grammar-gap probes (14 files) | 14 / 14 | | all 10 that ANTLR rejects |

Synthetic package: 1,006 lines 1.1–1.2 ms; 5,065 lines 4.5–5.4 ms; 20,014 lines 16–17 ms. A parse allocates
about 0.34 MB per 1,000 lines and keeps nothing afterwards. First parse in the process: 26–30 ms.

**Is the tree right?** "No diagnostics" proves little for a parser that skips most of the text, so the result
was compared with the ANTLR parse tree on the 642 files ANTLR parses cleanly:

- Routines (keyword offset, name, and whether it has a body): **7,121 of 7,121 identical**; neither parser has
  one the other lacks.
- Statements (start offset): 23,964 of ANTLR's 24,025 (99.75 %). All differences are in 13 files and are three
  conventions, not errors: statements in `$ELSE` branches of conditional compilation (only the `$IF` branch is
  kept), `EXEC …` lines (treated as SQL*Plus commands), and labels (ANTLR starts the statement at `<<label>>`).
- For the 226 files ANTLR rejects there is no reference; the parser reports no diagnostics in any of them.

**Damaged text** (757 files, same seed and method as section 4, but compared against its own intact parse):

| | Survival before the damage | After the damage | Diagnostics p50 / max | Parse p95 |
| --- | ---: | ---: | ---: | ---: |
| Text ends at the caret (2,271 samples) | 100.0 % of 68,235 | – | 2 / 9 | 0.1–0.4 ms |
| One `END` or `;` removed (1,878 samples) | 100.0 % of 69,769 | 99.2 % of 60,921 | 1 / 4 | 0.2–0.6 ms |

The routine around the caret is still found in 98.3 % of 846 truncations. 3,476 random cuts and deletions at
character level all returned, the slowest in 17–52 ms (first-call and GC noise; typical is under 1 ms).
ANTLR's corresponding figures were 97–100 % before and 93–95 % after the damage, on a different set of
constructs, so the two are not directly comparable.

**Limits.**

- It is not a syntax checker. A wrong expression or a misspelt SQL clause produces no diagnostic; only missing
  or unbalanced structure does (`END`, `;`, `THEN`, `LOOP`, `BEGIN`, `IS`). Full error reporting has to come from
  compiling on the database (`USER_ERRORS`), which the editor needs anyway.
- Code in `$ELSIF` / `$ELSE` branches is not in the tree.
- The completion context (clause at the caret, tables in scope) is still the token-based prototype from
  section 4 and still runs on ANTLR tokens; it has to be ported to the new tokenizer.
- The damage test measures stability of the parser against itself, not against an independent reference.
- Spike code: no unit tests yet, and it lives under `tests/`.

## Conclusions

1. **Structure comes from the hand-written parser.** Outline, folding, Ctrl+Click targets and "which routine am
   I in" need exactly what it produces, at a cost that allows re-parsing on every keystroke without a debounce.
2. **Completion comes from tokens**, as measured in sections 4–6 (100 % against 43 % for the clause at the
   caret). The structural tree supplies the enclosing routine and its declarations.
3. **Diagnostics are structural only in the editor; the database is the syntax authority.** Squiggles for
   unbalanced blocks appear while typing; everything else appears after compile.
4. **ANTLR is not shipped.** The grammar stays in `tests/` as a reference to compare against and can be deleted
   once the parser has its own test suite. This drops a 9 MB generated parser and the Apache 2.0 grammar from
   the product.
5. **T-SQL is unaffected** (ScriptDom). PostgreSQL and SQLite are still planned on ANTLR grammars; they are
   "basic in MVP", but the same measurement should be repeated before relying on them.

## Proposed next steps

| # | Step | Why |
| --- | --- | --- |
| 1 | Owner runs `--structure --corpus <folder>` on production packages kept outside the repository | The remaining part of the gate; coding style decides whether any construct is misread |
| 2 | Phase 1: move tokenizer and parser into `JetQueries.Provider.Oracle` behind `ISqlLanguageService`, with unit tests built from the corpora and the damage cases | Spike code is not product code |
| 3 | Port the token context finder to the new tokenizer and extend it to DML, PL/SQL blocks and aliases | It is the completion engine's core |
| 4 | Decide how inactive conditional-compilation branches should appear in outline and folding | Today they are invisible |
| 5 | Show database compile errors (`USER_ERRORS`) as editor diagnostics | Replaces the syntax checking the grammar would have given |
