# 0012 — DBA menu and built-in explanations on every DBA screen

- Status: accepted (2026-11-25)
- Relates to: 0009 (activity monitor), 0010 (performance analysis), 0011 (server dashboard)

## Context

The DBA screens are also meant for developers who have never been DBAs. Numbers like page life expectancy, wait
types or index fragmentation mean nothing without knowing what they are, what is normal and what to do. The
screens were also spread over the Tools menu and flat explorer menu items.

## Decision

- **Menus**: a top-level **DBA** menu (Sunucu panosu, Etkinlik izleyici, Performans analizi, and the
  "Açıklamalar (i)" switch) and a **DBA** submenu in the explorer's context menu of a connection (and of a
  database, for what applies to one). Every new DBA screen is added to both.
- **Explanations**: a small "i" (`InfoButton`) beside every tile, chart, list and risky action of a DBA screen opens
  a card with four parts: *Ne gösterir* (what it shows, in plain words), *Nasıl okunur* (Normal / Dikkat / Sorun,
  each with concrete values where a rule exists; statuses carry an icon and a word, never colour alone), and
  *Ne yapmalı* (what to do, pointing to the screen that helps next).
- **Content as resources**: topics are listed in `DbaHelp` (Presentation); their texts are `Help_<Topic>_Title`,
  `_What`, `_Good`, `_Watch`, `_Bad`, `_Action` in Turkish and English. `DbaHelpTests` fails when a topic lacks a
  text in either language or the two languages disagree on which levels exist.
- **Optional, on by default**: Settings > Görünüm > "DBA ekranlarında açıklamalar" and DBA > Açıklamalar (i) switch
  the buttons through the `DbaHelp.Visibility` resource, at once.
- **Every DBA screen from now on ships its topics** (this ADR is the rule); the render tests count the buttons of
  each screen.

## Consequences

- Thresholds are general guidance (SQL Server first); provider-specific advice can extend a topic's text.
- About 130 strings per language; they live with the other resources and are translated together.
