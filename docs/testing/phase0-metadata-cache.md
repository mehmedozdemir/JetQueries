# Manual checklist — Phase 0 metadata cache

There is nothing to click yet: no provider produces metadata until Phase 1. The cache is covered by
`tests/JetQueries.Core.Tests/Metadata` (12 tests).

- [ ] `dotnet test` reports Core 40, Sdk 7, DataGrid 28 passed.
- [ ] Start the application once and close it. No file is created: the cache file
      `%LocalAppData%\JetQueries\cache\metadata.db` appears only when something is first cached.

What to check in Phase 1, when the object explorer uses it:

- [ ] Expanding a schema a second time is instant and makes no database call.
- [ ] After a restart, the tree fills from the cache before the connection is opened.
- [ ] "Refresh" on a connection reloads from the database.
- [ ] Deleting `cache\metadata.db` (application closed) loses nothing but speed.

Known limits: the memory layer has no size limit; two simultaneous first requests for the same list both go to
the database; object definitions (source text) are not cached yet.
