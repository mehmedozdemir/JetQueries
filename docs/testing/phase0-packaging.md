# Manual checklist — Phase 0 packaging (Velopack)

Branch `feature/velopack-packaging` (stacked on `feature/extension-host`).

## 1. Build a package

```
pwsh scripts/pack.ps1
```

- [ ] It ends with `Done:` and lists, under `artifacts/releases/win-x64`: `JetQueries-win-stable-Setup.exe`,
      `JetQueries-<version>-win-stable-full.nupkg`, `JetQueries-win-stable-Portable.zip` and
      `releases.win-stable.json`. About 15 seconds after the publish step; the installer is about 64 MB.
- [ ] The version is `0.1.0-alpha.0.<n>` (no git tag yet). After `git tag v0.1.0` it is `0.1.0`.
- [ ] `git status` shows no changes afterwards (`artifacts/` is ignored, lock files are not rewritten).
- [ ] The two "will not be signed" warnings are expected until there is a certificate.

## 2. Install — not done by Claude, it changes your machine

- [ ] Run `JetQueries-win-stable-Setup.exe`. Windows SmartScreen warns because the installer is unsigned
      ("More info" → "Run anyway").
- [ ] JetQueries starts by itself after installing; shortcuts appear on the desktop and in the Start menu.
- [ ] It is installed under `%LocalAppData%\JetQueries` and listed in "Installed apps".
- [ ] Uninstall from "Installed apps": shortcuts and the folder are removed.

Note: settings, logs and user extensions are planned for `%LocalAppData%\JetQueries\` as well — the same folder
Velopack installs into and removes on uninstall. Decide before Phase 1 whether user data should move (for
example to `%AppData%\JetQueries`) so that it survives an uninstall.

## 3. Update flow (optional, local)

- [ ] With the first package installed, run `pwsh scripts/pack.ps1 -Version 0.1.1` and check that a
      `-delta.nupkg` appears next to the full package. Nothing installs it yet: the application has no update
      check until a release feed URL exists.

## 4. Numbers seen on the development laptop (one run, not a benchmark)

- Published folder: 267 files, 225 MB (self-contained, ReadyToRun).
- Start to input-idle: about 0.7 s with a warm disk cache (budget: 1.5 s cold).
- Working set 4 s after start: **207 MB** — at the 200 MB idle budget before any real feature exists. Needs a
  proper measurement (private bytes, with and without the grid spike view) before Phase 1.

## Not covered yet

- Code signing (needs a certificate; `-SignParams` passes parameters to signtool).
- The in-app update check, channels other than a local build, and CI producing the packages.
- macOS and Linux packages.
