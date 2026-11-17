# Phase 1 · Query history — manual test checklist

What automated tests cover: the store (round trip, search, pruning, redaction, unreadable file), recording of
each run by the query tab, and the panel's view model. What nobody has done yet is click through it.

## Recording
- [ ] Run a `SELECT` on a connection. Open the **History** tab of the left panel: the query is the first line,
      with the connection name, row count, duration and the time it ran.
- [ ] Run a statement that fails (a misspelt table). It appears in red with "failed"; hovering shows the
      database's message above the SQL.
- [ ] Start a long query and cancel it. It appears with "canceled".
- [ ] Select part of the text and run it: only the selection is recorded.
- [ ] On a read-only connection run a `DELETE`: nothing is sent and nothing is added to the history.
- [ ] On a production connection decline the confirmation of a `DROP`: nothing is added.
- [ ] Close the application and start it again: the history is still there.

## Panel
- [ ] Type in the search box: the list narrows as you type; it matches SQL text and connection names, in any
      letter case. `%` and `_` are searched literally.
- [ ] Double-click an entry (or select it and press **Open**): a new query tab opens with its text, on the
      same connection if that connection is open, otherwise on the default one.
- [ ] **Delete** removes the selected entry only.
- [ ] **Clear all** asks first; after confirming the list shows "The queries you run appear here."
- [ ] Both themes: the selected row and a failed (red) row are readable.
- [ ] Turkish UI: tabs read "Gezgin" and "Geçmiş", buttons and the confirmation are translated.

## Secrets
- [ ] Run `ALTER LOGIN x WITH PASSWORD = 'something'` (SQL Server) or `ALTER USER x IDENTIFIED BY something`
      (Oracle) on a throwaway database. In the history the password reads `'***'`.
- [ ] Open `%AppData%\JetQueries\history.db` in any SQLite tool: the password is not in it.

## Known limits
- Only password literals in the statements above are masked (`IDENTIFIED BY`, `PASSWORD`, `OLD_PASSWORD`,
  `PWD`). Other sensitive values typed into a query (card numbers in a `WHERE`, for example) are stored as
  written; the file is in the user profile and is never synced.
- The newest 10,000 runs are kept; a script longer than 100,000 characters is stored cut, with a mark.
- The list shows the newest 200 matches; there is no paging and no filter by connection or date yet.
- A script with several batches is one entry with one outcome.
- The left panel's tabs are fixed; dockable panels come later in Phase 1.
