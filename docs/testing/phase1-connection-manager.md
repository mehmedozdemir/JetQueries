# Manual checklist — Phase 1 · Connection manager

Branch `feature/connection-manager`. Decision record: [ADR 0005](../adr/0005-connection-storage.md).

What exists: saved connections with folders, environment colour and a read-only mark; passwords in Windows
Credential Manager; "Test connection" with a reachability check; connect / disconnect. What does not exist yet:
anything to do with an open connection (object explorer, editor) — connecting only changes the mark in the tree.

## 1. Automated checks

- [ ] `dotnet test` reports Sdk 7, Core 53, Desktop 16, DataGrid 28, Provider.SqlServer 50 passed and 8 skipped.
- [ ] With `$env:JETQUERIES_MSSQL_ACCEPT_EULA = 'Y'`, Provider.SqlServer reports 58 passed (a throwaway SQL Server
      container is started from the local image and removed again).
- [ ] The Desktop tests write real renderings of the views to
      `tests/JetQueries.Desktop.Tests/bin/Debug/net10.0/screenshots/` (light and dark). Look at them: they are what
      Claude checked instead of a screen.

## 2. A database to try with

Any SQL Server you can reach works. For a local one that touches none of your existing containers:

```
docker run --rm -d --name jq-try -e ACCEPT_EULA=Y -e MSSQL_SA_PASSWORD=<a strong password> -p 127.0.0.1:14333:1433 mcr.microsoft.com/mssql/server:2022-latest
```

Server `127.0.0.1,14333`, user `sa`, "Trust server certificate" ticked. Remove it with `docker stop jq-try`.

## 3. Create, test, save

`dotnet run --project src/JetQueries.Desktop`

- [ ] The explorer panel shows "No connections yet…" and the buttons New / Edit / Delete / Connect; Edit, Delete
      and Connect are disabled.
- [ ] New: the dialog lists "SQL Server" as database type and shows its fields (Server, Database,
      Authentication, Encrypt, Trust server certificate, Read-only routing, Connect timeout).
- [ ] Save with an empty form: "Enter a name." appears in red; nothing is saved.
- [ ] Fill it in and press "Test connection": the window stays responsive, and within a few seconds a green
      "Connected in … ms." appears.
- [ ] Wrong password: red "The server refused the connection: Login failed for user …".
- [ ] Wrong port (for example `127.0.0.1,1`): red "The server cannot be reached. Check the address and that the
      VPN is connected." within about 5 seconds, not after the 15-second driver timeout.
- [ ] Authentication "Windows (integrated)": the user name and password fields disappear.
- [ ] Set Folder to `Customer A/Test`, Environment to Test, tick Read-only, Save.

## 4. The tree

- [ ] The connection appears under folders "Customer A" → "Test", with an amber dot and "read-only" next to it.
      Development is green, Production red, "Not set" an empty circle.
- [ ] Double-click it (or select it and press Connect): the name turns bold with "connected"; the button reads
      "Disconnect". Disconnect reverses it.
- [ ] A connection saved without "Save the password": connecting asks for the password; Cancel leaves it
      disconnected without an error.
- [ ] A failed connect turns the name red; hovering shows the reason.
- [ ] Edit: the password box is empty with the hint "leave empty to keep the saved password"; saving without
      typing keeps the password working.
- [ ] Delete asks first; after confirming, the connection is gone.
- [ ] Switch the theme with the top-right button: tree and dialog are readable in both.

## 5. Where things are stored

- [ ] `%AppData%\JetQueries\connections.json` contains the connection and **no password**.
- [ ] Control Panel → Credential Manager → Windows Credentials → Generic Credentials has an entry
      `JetQueries/connection/<id>`. Deleting the connection in JetQueries removes it.
- [ ] Restart the application: the connections are back, and connecting works without asking for the password.

## Not covered yet

- Read-only is stored and shown, but nothing enforces it until there is an editor to run statements from; the
  same goes for confirming destructive statements on Production connections.
- Hints and option names that come from the provider ("host, host,port or host\instance", "SQL Server login")
  are English only; field labels are translated.
- No drag and drop between folders, no duplicate, no import/export, no search.
- A named instance (`host\instance`) skips the reachability check, so an unreachable one still waits for the
  driver's timeout.
- macOS Keychain and libsecret: on those systems passwords are kept for the session only.
