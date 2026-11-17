# 0005 — Where connections, passwords and other user data live

- Status: accepted (2026-11-17)
- Relates to: CLAUDE.md hard rule 5 (never persist secrets in plain text)

## Context

Saved connections must survive updates and an uninstall, must be shareable later (Phase 2 sync), and must
never carry a password. Velopack installs the application into `%LocalAppData%\JetQueries` and removes that
folder on uninstall, which is where CLAUDE.md originally placed settings and history.

## Decision

- **Connection definitions** are one JSON file, `%AppData%\JetQueries\connections.json` (roaming profile). The
  file format has no field for a password. Writes replace the file atomically; an unreadable file is copied
  aside (`.unreadable`) instead of being overwritten.
- **Passwords** go to the operating system's protected store, keyed by the connection's id: Windows Credential
  Manager today (generic credential `JetQueries/connection/<id>`, local machine persistence, not roaming).
  Where no supported store exists yet (macOS, Linux) passwords are held in memory for the session only; there
  is no encrypted-file fallback.
- **User data goes to `%AppData%\JetQueries`**; only data that can be rebuilt (metadata cache, logs) stays in
  `%LocalAppData%\JetQueries`, next to the installed application. Settings, query history and user extensions
  follow this rule as they are built.
- A provider describes its own connection settings through the SDK (`IConnectionSettingsProvider`); the host
  renders the form from that description and never knows a provider's keys.
- Before the driver connects, the host opens a plain TCP connection to the endpoint the provider derives from
  the settings (4-second limit), so an unreachable server — typically a VPN that is not connected — is
  reported quickly and in those words.

## Consequences

- Uninstalling the application keeps the user's connections; their passwords stay in Credential Manager until
  the connection is deleted in JetQueries or the entry is removed by hand.
- Definitions roam with the profile but passwords do not: on a second machine the user is asked once.
- CLAUDE.md's "Settings and history: `%LocalAppData%\JetQueries\`" is superseded for user data.
- The extensions folder for user-installed extensions is still under `%LocalAppData%` and should move with the
  extension catalog work.
