# Manual checklist — Phase 0 CI

Branch `feature/ci` (stacked on `feature/velopack-packaging`). Workflow: `.github/workflows/ci.yml`.

The workflow has **never run**: there is no GitHub repository yet, and the first public push waits for the
licence decision. Its commands were run locally on Windows; the YAML itself was checked by reading only.

## 1. What CI does

- On every pull request and every push to `main`: restore in locked mode, build Release (warnings are errors),
  run all tests including the grid render budget, upload the test results.
- On pushes to `main` and `v*` tags, after the tests pass: `scripts/pack.ps1` and upload the Velopack files as
  a workflow artifact (kept 14 days). `main` builds use the `nightly` channel, tags use `stable`.
- It publishes nothing: no GitHub release, no update feed, no signing.

## 2. The same commands locally

```
dotnet restore JetQueries.slnx --locked-mode
dotnet build JetQueries.slnx -c Release --no-restore
dotnet test JetQueries.slnx -c Release --no-build
```

- [ ] All three succeed; tests report Sdk 7, Core 28, DataGrid 28.

## 3. After the repository exists on GitHub

- [ ] Open a pull request: the "Build and test (win-x64)" job is green; "Package" is skipped.
- [ ] Merge to `main`: both jobs run; the run has an artifact `jetqueries-win-x64` containing the installer.
- [ ] First build takes noticeably longer than later ones (the 9 MB generated PL/SQL reference parser compiles
      for about two minutes; NuGet packages are cached afterwards).
- [ ] If the grid render test fails on the hosted runner but not locally, send the message: its limit
      (p95 under half a 60 Hz frame of UI-thread time) was only ever measured on one laptop.
- [ ] In the repository settings, protect `main`: require the "Build and test (win-x64)" check.

## Not covered yet

- Actions are referenced by major version tag (`@v4`), not pinned to a commit hash.
- Code signing, publishing releases and the update feed, macOS and Linux jobs, start-up and memory benchmarks.
