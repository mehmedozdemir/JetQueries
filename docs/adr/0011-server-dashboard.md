# 0011 — Server dashboard: overview and samples through the SDK, charts drawn by the app

- Status: accepted (2026-11-25)
- Relates to: ANALIZ item 10 (DBA paneli, Faz 3), 0009 (activity monitor), 0010 (performance analysis)

## Context

A DBA opening a server first wants it at a glance: what it is, how busy it is (CPU, memory, batches, I/O latency,
blocking), what it waits on, and whether its databases are backed up. The figures come from each database's own
views and counters; most are running totals that only mean something as a rate between two readings. The UI has no
chart control, and CLAUDE.md asks for few dependencies.

## Decision

- **`IServerDashboard`** (SDK, preview) is an optional interface of `IDatabaseConnection`: `GetOverviewAsync`
  (product, version, edition, host, start time, CPUs, memory, databases with data and log size, recovery model and
  last full / log backup, and the CPU history the server keeps) and `GetSampleAsync` (CPU, memory used / target /
  free, page life expectancy, connections, running and blocked requests, and running totals of batches,
  compilations, transactions, bytes read / written, I/O operations and stall time, waits by type). Providers report
  facts; the host makes the rates.
- **The dashboard reads only while shown**, every 5 s by default (2–30 s), on a channel of its own, through the job
  queue; the overview once (and on request). Rate charts keep 10 minutes, the CPU chart an hour (SQL Server records
  CPU once a minute in the scheduler monitor ring buffer, so the chart has history the moment it opens).
- **Waits**: since the dashboard opened once two samples exist, until then since the server started; harmless
  background waits are left out by the provider.
- **Backups**: a database without a full backup, with one older than 7 days, or in FULL recovery without a log
  backup in 24 hours is flagged; tempdb and databases that are not online never are; when msdb cannot be read the
  backups are unknown ("?"), not missing.
- **Charts are drawn by the app** (`LineChart`, `BarList` in `JetQueries.Wpf/Views/Charts.cs`): one axis per chart,
  2px lines, a recessive three-line grid, the legend with each series' latest value, a crosshair with every
  series' value on hover; bars anchored at the start with a rounded data end. Series colours are the dataviz
  reference palette's first two slots, stepped per theme and validated against the UiKit surfaces (colour-vision
  separation and contrast pass in both); text never wears series colours; blocking is shown with an icon and a word
  besides red. No charting package is added.

## Consequences

- Oracle (`V$OSSTAT`, `V$SYSSTAT`, `V$SYSTEM_EVENT`, RMAN views) and PostgreSQL (`pg_stat_database`,
  `pg_stat_bgwriter`, no CPU) fill what they can; empty tiles show "—".
- The chart controls are the app's; if UiKit grows charts, they can move there.
