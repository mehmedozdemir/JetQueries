# Changelog

All notable changes to this project are documented here.
Format: [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), versioning: [SemVer](https://semver.org/).

## [Unreleased]

### Added
- Repository foundation: CLAUDE.md, product analysis, build props.
- Solution skeleton (`JetQueries.slnx`): Desktop, Core, Sdk, Editor, DataGrid, SQL Server and Oracle provider projects, Core and Sdk test projects.
- Desktop shell window with light/dark design tokens (`Styles/Tokens.axaml`), explorer / documents / results placeholders, generic host and DI.
- Core job queue on `System.Threading.Channels` with priority, progress and cancellation.
- SDK v0 (preview) contracts: `IDatabaseProvider`, `IMetadataProvider`, `IQueryExecutor`, `ISqlLanguageService`.
- Spike: virtualized results grid (`JetQueries.DataGrid.VirtualGrid`) over one million rows, with headless and on-screen frame benchmarks. Findings in `docs/spikes/virtual-grid.md`.
