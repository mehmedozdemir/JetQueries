# Manual checklist — Phase 0 extension host

Branch `feature/extension-host`. Decision record: [ADR 0004](../adr/0004-extension-host.md).

## 1. Build and tests

- [ ] `dotnet build JetQueries.slnx` succeeds with 0 warnings.
- [ ] `dotnet test` reports Core 28, Sdk 7, DataGrid 28 passed.

## 2. No extensions

- [ ] `dotnet run --project src/JetQueries.Desktop` — the window opens as before; the right end of the status
      bar shows `Extensions: 0` (`Eklentiler: 0` on a Turkish system) within a second.

## 3. One extension

The test extension is the only extension that exists so far. In PowerShell, from the repository root:

```
$target = "$env:LOCALAPPDATA\JetQueries\extensions\sample"
New-Item -ItemType Directory -Force $target | Out-Null
Copy-Item tests\JetQueries.TestExtension\bin\Debug\net10.0\JetQueries.TestExtension.* $target
Copy-Item tests\JetQueries.TestExtension\bin\Debug\net10.0\extension.json $target
```

- [ ] Start the application: the status bar shows `Extensions: 1`.

## 4. A broken extension does not stop the application

- [ ] Create an empty file `throw.marker` in the `sample` folder and restart: the window opens normally and the
      status bar shows `Extensions: 0 loaded, 1 failed`.
- [ ] Replace the content of `extension.json` with `{ not json` and restart: same result, no crash.

## 5. Clean up

- [ ] Delete `%LocalAppData%\JetQueries\extensions\sample` (close the application first; the DLL is locked while
      it runs).

## Not covered yet

- There is no extension list in the UI; the reason for a failure is only in the debug log.
- The SQL Server and Oracle provider projects are still empty and are not yet built into `extensions/`.
