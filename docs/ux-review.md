# UX review of the WPF application (2026-11-23)

A pass over the whole application as a UX reviewer: the shell, explorer, editor, results, dialogs and windows,
in both themes, from the renderings in `tests/JetQueries.Wpf.Tests/.../screenshots` and the XAML. Items fixed in
the same change are marked **fixed**; the rest are open, ordered by priority inside each area.

Priorities: **P1** hurts daily work or safety · **P2** friction or inconsistency · **P3** polish.

## Fixed in this pass
- **fixed** Submenus never opened: UiKit's `MenuItem` template had no popup, so the explorer's "Script",
  the grid's "Copy as" and the editor's "Insert snippet" did nothing. The template now has submenu, menu bar and
  check states.
- **fixed** The column filter panel closed as soon as the mouse button was released (it opened on mouse down
  with "close on outside click"); it now opens on release.
- **fixed** No main menu: commands were only on toolbars and context menus. File, Connection, Query, View and
  Tools menus now exist, with their shortcuts shown.
- **fixed** History was a tab competing with the explorer for the left panel; it has its own window with a full
  text preview (Tools → Query history, Ctrl+Shift+H).
- **fixed** Connection editor: one long column with 170 px labels and a hint under every field. It is now a
  compact two-column form with labels above inputs, user and password side by side, fine-tuning under "Advanced",
  and the test result next to the actions.
- **fixed** Ctrl+Click only opened a CREATE script. It opens an object details window (properties, columns,
  constraints, foreign keys, references, indexes, triggers, parameters, dependencies, DDL).
- **fixed** Result headers did not say the column's data type; it is a second, smaller line under the name.

## Shell and navigation
- **fixed** (P1) Nothing on a query tab showed which connection and environment it runs against. Tabs now carry
  the environment's colour dot, the query toolbar names the connection, and test/production tabs have a strip
  ("PRODUCTION · name") in the environment's colour above the toolbar. Was: A red/amber strip on the
  tab and above the editor for production/test connections (DataGrip, DBeaver do this) prevents running on the
  wrong server.
- **P2** The toolbar repeats the menu (New connection, New query, Open, Settings). With a menu bar, an icon-only
  toolbar (with tooltips) would free space and look calmer.
- **P2** No command palette / shortcut overview; shortcuts are only discoverable in menus and tooltips.
- **P3** Status text appears twice: "3 rows · 28 ms" on the query toolbar and again in the status bar.
- **P3** Hiding the explorer (View → Explorer) is not remembered at the next start.

## Explorer
- **fixed** (P1) The explorer toolbar was cut off at the default width (280 px). Connect/disconnect is now an
  icon (its text is the tooltip) and the actions wrap when the panel is narrower. Was: cut off: the last icons are clipped. Move rarely
  used actions (Import, Go to object) into the menu or an overflow, or wrap.
- **P2** A connection row carries a type mark, an environment dot, a bold name and a "connected" badge; the badge
  repeats what bold and the expanded tree already say.
- **P2** Script errors appear as red text at the bottom of the explorer, far from where the user clicked; a
  toast or inline message on the node would be seen.
- **P3** Lazy loads show a 36 px progress bar after the name; a spinner in place of the expander arrow is
  quieter.
- **P3** No favourites / pinned objects; no object counts on groups ("Tables (128)").

## Editor and query toolbar
- **P2** Commit / rollback are icon-only (✓ and ↶), told apart by colour; add labels or at least distinct shapes
  and tooltips with shortcuts.
- **P2** The database combo box is wide and visually the heaviest control on the toolbar.
- **P3** The zoom badge in the editor corner is always visible; show it only away from 100 %.

## Results
- **fixed** (P1) Provider option labels, hints and some field labels in the connection editor were English
  (Yes/No, "SQL Server login", Oracle's "Service name"); the host now translates them
  (`SettingOption_<key>_<value>`, `SettingHint_<key>`). The results footer ("3 rows fetched …") was already in
  the resources: it showed English only in the test renderings, where the language is set on one thread and
  follows background work; the application itself never sets a language and uses Windows' on every thread.
- **P2** The page-size box has no label and the footer is a row of unlabelled icons (fetch next, fetch all,
  count, show value); new users cannot tell what "200" means.
- **P2** The SQL filter bar is always shown and uses a long monospace placeholder; collapse it to an icon until
  used, or shorten the placeholder.
- **P2** View modes (grid / text / record) sit in a vertical strip on the left of the grid, an unusual place
  that costs width; a segmented control in the result toolbar would be more familiar.
- **P3** The data-type line under headers makes the header taller; offer it as a setting for users who prefer
  denser grids.

## Dialogs and windows
- **P2** Object details open in a separate window, not as an editor tab as in DBeaver; several details windows
  can pile up. Moving them into the document tabs needs the tab area to host more than query tabs.
- **P2** The history window cannot filter by connection or outcome (failed only), only by text.
- **P3** The environment is a combo box in the connection editor; coloured chips (None / Dev / Test / Prod)
  would be one click and show the colour that will be used.

## Accessibility
- **P2** Several icon-only buttons have tooltips but no `AutomationProperties.Name`, so screen readers announce
  them as "button".
- **P2** Muted caption text (`Brush.TextMuted`) on the light theme is below 4.5:1 contrast for small text.
- **P3** Some custom controls remove the focus visual (`FocusVisualStyle = null`) and rely on a border change
  that is subtle in the dark theme.
