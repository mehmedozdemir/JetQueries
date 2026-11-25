# 0013 — Health check: provider rules, host texts, fixes never run

- Status: accepted (2026-11-25)
- Relates to: ANALIZ item 10 (DBA paneli, Faz 3), 0012 (DBA menu and explanations)

## Context

Many servers run with settings nobody chose: unlimited memory, the 1990s parallelism threshold, auto shrink,
percentage file growth, databases in FULL recovery without log backups, no integrity checks. A developer who is not a
DBA cannot spot these, and must not be handed a button that changes a production server.

## Decision

- **`IHealthCheck`** (SDK, preview) is an optional interface of `IDatabaseConnection`: `CheckAsync` returns one
  `HealthFinding` per rule (passing rules as `Ok`), with a stable `RuleId`, a category (configuration, databases,
  backups, security, performance), a severity (ok, info, warning, critical), the current and recommended value, the
  objects it was found on and an optional fix script quoted by the provider.
- **Texts belong to the host**: `Rule_<RuleId>_Title`, `_Why`, `_Fix` in Turkish and English; an unknown rule shows
  the provider's English title. A test checks every SQL Server rule has its texts in both languages. Values the
  provider writes are language-neutral (numbers, dates, "—").
- **Fixes are never run**: one finding's script, or all shown problems' scripts under a review header, open in a
  new query tab.
- **The check runs when the tab opens and on "Yeniden kontrol et"**, on a channel of its own; it only reads.
  Backups and suspect pages come from msdb; without access the backup rules say "could not be read" instead of
  reporting missing backups.
- **SQL Server rules** (22): max server memory, MAXDOP, cost threshold, optimize for ad hoc, priority boost, auto
  shrink, auto close, page verify, automatic statistics, TRUSTWORTHY, compatibility level, file growth, log that
  cannot be reused, tempdb files, backups readable, full backup age, log backup age, last good CHECKDB, suspect
  pages, sa enabled, sysadmin count, uptime. Recommendations are computed for the server (memory leaves the OS 10 %
  / 4 GB; MAXDOP = cores per NUMA node up to 8; tempdb files = CPUs up to 8).
- Per ADR 0012 the screen has its "i" (the screen and the meaning of each severity); each finding's own explanation
  is in its side panel.

## Consequences

- Oracle and PostgreSQL add their own rules (e.g. archivelog without backups, autovacuum off) behind the same
  contract with their own `Rule_` texts.
- Thresholds are conventional advice; a team can disagree with a rule, so the screen explains rather than enforces.
