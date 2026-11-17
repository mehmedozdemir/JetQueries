# 0004 — Extension host: one AssemblyLoadContext per extension, manifest-driven

- Status: accepted (2026-11-17)
- Relates to: ADR 0001 (layered architecture), CLAUDE.md hard rules 3 and 4

## Context

Database providers and built-in features are extensions written against `JetQueries.Sdk` only; Core and
Desktop never reference a provider assembly. Providers bring heavy, conflicting dependencies (Oracle, SqlClient,
Npgsql drivers), and a broken extension must not stop the application from starting.

## Decision

- An extension is a directory holding `extension.json` (`id`, `name`, `version`, `assembly`, `sdk`) and its
  assemblies. The host searches `<application>/extensions` (built-in) and then
  `%LocalAppData%/JetQueries/extensions` (user); the first extension with a given id wins.
- Each extension is loaded into its own non-collectible `AssemblyLoadContext` with an
  `AssemblyDependencyResolver`, so its dependencies come from its own directory. `JetQueries.Sdk` is always
  taken from the host, even if the extension ships a copy.
- The entry assembly's public `IExtension` types are created and given an `IExtensionContext` with the
  contribution points (today: database providers). Contributions are staged and committed only if every entry
  point succeeds, so an extension is either fully active or absent.
- While the SDK is in preview, the manifest's `sdk` must equal `SdkContract.Version` exactly.
- Loading runs once, off the calling thread, as a start-up job. Any failure (bad manifest, wrong SDK revision,
  duplicate id, exception in extension code) is recorded per extension and logged; the others still load.

## Consequences

- Two providers can ship different versions of the same library.
- **This is not a sandbox.** Extensions run in-process with the user's rights; the load context isolates
  dependencies, not behaviour. Installing a third-party extension is a trust decision, and the future catalog
  needs signing or review before it is opened to others.
- Extensions cannot be unloaded or updated without restarting the application (non-collectible contexts, and
  loaded files stay locked on Windows). Acceptable for now; revisit with the catalog in Phase 4.
- `Initialize` must be cheap and free of I/O; an extension that blocks there delays every extension after it,
  though never the UI. There is no timeout yet.
- `IExtension`, `IExtensionContext`, `ExtensionManifest` and `SdkContract` are now part of the public SDK
  contract.
