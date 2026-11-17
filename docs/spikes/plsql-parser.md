# Spike findings — PL/SQL parsing with ANTLR4 (Phase 0)

- Branch: `spike/plsql-parser`
- Date: 2026-11-17
- Gate being tested: "PL/SQL test corpus parses" (ANALIZ.md §13), and the risk "PL/SQL parser cannot recover on
  broken code" (§14)
- Suggested thresholds (not yet confirmed): ≥ 98 % of valid files parse; < 200 ms for a 5,000-line package

## Verdict

**Usable for structure, not sufficient for completion, and too expensive as it stands.**

- Coverage is good: every valid file in the public corpus and the synthetic package parse without errors.
- Warm speed is borderline: a 5,000-line package takes 107–250 ms depending on the run.
- Two things fail outright: the prediction cache grows to **~880 MB** after the corpus (budget: 200 MB idle for
  the whole app), and a cold cache makes the first parse of each new construct take hundreds of milliseconds to
  seconds (28–36 s for 7,400 lines of varied SQL).
- On broken text the parse tree keeps almost everything *before* the damage, but it knows which clause the
  caret is in only 32–49 % of the time. The token-based fallback gets 93–100 %.

**The gate is not passed**, and could not have been: there is no real-package corpus yet (see Limits).

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

- **No real packages.** The corpus is the grammar's own regression suite: 478 short files, 7,391 lines, the
  largest 487 lines. It exercises breadth of syntax, not production PL/SQL. A hand-written, realistic package
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

## Conclusions

1. **Keep the grammar for structure, in the background.** Outline, folding, Ctrl+Click targets, diagnostics and
   "which routine am I in" can come from the tree: everything before the damage survives, and most of what
   follows a missing `END`.
2. **Do not build completion on the parse tree.** Use tokens for the caret context (clause, tables and aliases
   in scope) and the tree only for what lies outside the statement being typed. The fallback in the risk table
   should be the primary mechanism. antlr4-c3 (candidates computed from the ATN at the caret) is the other
   known approach and was not evaluated.
3. **Parse broken text with one SLL pass plus recovery**; keep two-stage only for a final, precise diagnostics
   pass on text that SLL accepts.
4. **The cache has to be tamed before this ships.** Options, none measured yet: clear or cap the DFA cache
   (trading CPU for memory), parse one routine at a time instead of the whole package, warm the cache on a
   background job at start-up, and cut the grammar down to what an editor needs — most of its 10,000 lines
   cover administrative DDL.

## Proposed next steps

| # | Step | Why |
| --- | --- | --- |
| 1 | Get a real corpus (package specs and bodies from production schemas, kept outside the repo) and rerun with `--corpus` | The gate asks for it; coverage and speed on real code are unknown |
| 2 | Measure cache size and warm speed with a PL/SQL-only subset of the grammar, and with per-routine parsing | Decides whether ANTLR + this grammar fits the memory budget at all |
| 3 | Extend the token context finder to DML, PL/SQL blocks and aliases, with tests; compare with antlr4-c3 | It is the completion engine's core |
| 4 | Confirm thresholds: ≥ 98 % valid files, < 200 ms per 5,000 lines warm, and add one for memory and first-parse latency | The two failing dimensions have no agreed target yet |
