# 0007 — WPF user interface on UiKit; Avalonia paused

- Status: accepted (2026-11-23), at the owner's request
- Changes: the "Avalonia 12" user interface of the original technology choice. The cross-platform pillar is
  postponed: the product is Windows-only until the Avalonia interface is resumed.

## Context

The owner asked for the user interface to be built in WPF from now on, following the design kit in their
UiKit repository (tokens, control styles, a custom-drawn virtual grid, DESIGN.md as the rule book), and for
Avalonia work to stop until it is needed again. Everything below the user interface — Core, SDK, providers,
extension host — never depended on a UI framework. The view models had one tie to Avalonia: the result set
implemented the Avalonia grid's row source.

## Decision

- **`JetQueries.Presentation`** (net10.0, no UI framework) holds the view models, their service contracts
  (`IDialogService`, `IUiDispatcher`) and the user-facing strings. Its tests (`JetQueries.Presentation.Tests`)
  reference no UI framework either.
- **`JetQueries.Wpf`** (net10.0-windows) is the application that is built, tested and packaged as
  `JetQueries.exe`: shell, dialogs, the SQL editor on AvalonEdit (`Editor/SqlEditor.cs`, the same behaviour as
  the Avalonia editor), results in UiKit's `VirtualGrid`, built-in extensions copied as before.
- **`UiKit.Wpf`** is copied from the UiKit repository into `src/UiKit.Wpf` so this repository builds on its own;
  the repository's analyzer rules are not applied to it. Changes made here are taken back to UiKit
  (so far: the ComboBox selection box honours `DisplayMemberPath`; control text sizes are DynamicResource
  tokens — `FontSize.Body`, `.Small`, `.Caption`, `.Section`, `.Subtitle`, `.Title` — so that an application can
  change them at run time). Application additions live in
  `JetQueries.Wpf/Styles/JetQueries.xaml` and use UiKit tokens only.
- **Avalonia** (`JetQueries.Desktop`, now `JetQueries.Avalonia.exe`, `JetQueries.Editor`, `JetQueries.DataGrid`)
  stays in the solution, keeps building and keeps its tests, but gets no new features and is not packaged.

## Consequences

- One set of view models serves both interfaces: when Avalonia is resumed it gets every feature added to
  Presentation in the meantime; only views have to be written.
- The product runs on Windows only for now. CI already builds on Windows.
- Visual checks are rendered PNGs from `JetQueries.Wpf.Tests` (both themes) instead of Avalonia headless.
- AvalonEdit is a new dependency (the WPF original of AvaloniaEdit, MIT).
