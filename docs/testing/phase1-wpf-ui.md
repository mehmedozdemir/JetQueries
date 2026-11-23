# Phase 1 · WPF user interface — manual test checklist

The WPF application (`src/JetQueries.Wpf`) replaces the Avalonia one as the product (ADR 0007). It uses the
same view models, so behaviour should match the earlier checklists (`phase1-*.md`); this list covers what is new
or WPF-specific. Automated: view models (Presentation tests), result grid source, and renderings of the shell,
the history tab and the connection dialog in both themes (`tests/JetQueries.Wpf.Tests/.../screenshots`).

```bash
dotnet run --project src/JetQueries.Wpf
```

## Shell
- [ ] The window opens with the UiKit look: toolbar (New connection, New query, Run, Cancel, theme button on
      the right), Explorer/History card on the left, query tabs on the right, status bar at the bottom.
- [ ] The theme button switches dark/light at once, including the title bar; the choice is remembered at the
      next start (`%AppData%\JetQueries\theme.json`). With nothing saved the Windows setting is followed.
- [ ] The left card can be resized with the splitter; the editor/results split too.
- [ ] Ctrl+N opens a new query tab; closing every tab shows the "No open queries" state with a button.
- [ ] Turkish and English UI texts, including the grid's own menus (right-click a header or a cell).

## Connections and explorer
- [ ] New connection opens the dialog; the provider list shows names (not type names); provider settings,
      sign-in, folder, environment and read-only fields work; Browse picks an SQLite file.
- [ ] The password box is styled like the other inputs; saving with "save password" stores it in Windows
      Credential Manager (as before).
- [ ] The explorer shows folders, environment dots (green / amber / red), the "connected" and "read-only"
      badges; double-click connects; expanding shows databases/schemas/groups/tables/columns with icons.
- [ ] A connection error is shown in red with the message as a tool tip.

## Editor and results
- [ ] Syntax colours follow UiKit's tokens (keywords blue, strings green, numbers orange, comments italic).
- [ ] Completion opens while typing and with Ctrl+Space; the list is styled like a UiKit card; Enter/Tab
      inserts, Esc closes (and does not cancel a running query).
- [ ] A syntax error is underlined; hovering shows the message. Ctrl+Click / F12 opens a definition tab.
- [ ] F5 / Ctrl+Enter runs (the selection if there is one); Esc or the toolbar's Cancel stops a long query.
- [ ] Results stream into the grid while the query runs; numbers right-aligned, NULL muted, booleans
      coloured; click a header to see the sort indicator (sorting itself is not implemented yet); Ctrl+C copies.
- [ ] The Messages tab shows server messages; error lines in red; text can be selected and copied.

## History
- [ ] The History tab lists runs; search filters after a short pause; double-click or Open reopens a run;
      Clear all asks first.

## Known limits
- Grid header sort and filter only show their indicators; results are not re-sorted or filtered yet.
- Idle memory of the WPF application is about 370 MB (budget: 200 MB); not investigated yet.
- The Avalonia application still builds (`JetQueries.Avalonia.exe`) but is no longer packaged or developed.
