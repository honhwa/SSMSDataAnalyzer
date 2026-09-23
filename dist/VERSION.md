# Current build

**`SsmsDataAnalyzer.vsix` — version 0.26.0**

Install: download the `.vsix` in this folder, close SSMS, double-click the file, reopen SSMS.
Full instructions in the [main README](../README.md#installing-it).

Requires **SSMS 22**. Every feature — Analyze Data, Find in Results, Go to source — works on
every SSMS 22 build. (v0.7.6 briefly needed a newer 22.x build for the results-grid features;
v0.8.0 moved them onto a results-grid API confirmed present as far back as SSMS 21, so that
requirement is gone. The graceful-degradation safety net from v0.7.6 — a hidden menu item and
a plain-language status-bar message instead of an error dialog — is kept in case some other,
still-unknown SSMS build surprise ever turns up.)

A handful of value types are still declined on "Go to source" for a results-grid cell, in
trade for that portability — see the version history entry below for exactly which ones and
why.

---

## What's in this build

| Feature | Where to find it |
|---|---|
| Analyze a table | Object Explorer → right-click a table → **Analyze Data…** |
| Search the analysis results | Click the panel → **Ctrl+F** |
| Search inside query results | Right-click the results grid → **Find…** |
| Jump to a linked record | Right-click a cell or column → **Go to source…** |
| Paste a list as an IN clause | In a query window, right-click → **Paste as SQL IN (...)** |
| Compare rows side by side | Select rows in the results grid → right-click → **Pivot selected rows…** |
| See what you ran earlier | **Tools → Query History…**, or the *SSMS Data Analyzer* toolbar |
| Settings | **Tools → Options… → SSMS Data Analyzer** |

## Version history

**0.26.0** — **In a Peek window, clicking a link icon now peeks one level deeper instead of opening a new query tab.** With **← Back** (0.24.0) you can walk down a chain of foreign keys and back out again without ever leaving the window — which is the point of a peek. The icon there is a magnifier rather than "open in new window", since that is now what it does. Right-click → **Go to source…** still opens a query tab when that is what you want, and pivot windows are unchanged.

**0.25.0** — **Every feature now has a keyboard shortcut out of the box.** They share one chord: hold **Ctrl+Alt** and press **Q**, then a letter — **H** history, **F** find, **G** go to source, **P** peek, **V** pivot, **A** aggregate, **D** analyze data, **I**/**N** paste as IN, **S** script object. Press Ctrl+Alt+Q and SSMS lists them. One chord is claimed instead of ten separate keys, so there is one chance of clashing with something you use rather than ten. **Nothing you have configured is overwritten:** the defaults are applied once, only to commands with no shortcut at all, and never onto a key already used by something else — so an upgrade cannot reset your own bindings, and a default you delete stays deleted.

**0.24.0** — **The Peek window can go back.** Following a foreign key inside a peek replaced the record with no way to return, so you could walk forward through links and end up stuck on the last one. There is now a **← Back** button (also **Alt+Left** or **Backspace**), stepping back one record at a time through everything that window has shown. Closing the peek ends the trail, so the next one starts fresh.

**0.23.0** — **Go to source and Peek now work on grids produced by SSMS's query shortcuts.** Select a table name, press **Ctrl+3** (`SELECT TOP(100) * FROM`), and the columns get source links — even though the query SSMS ran was composed by SSMS and never appeared in your editor. The selected name is read as `SELECT * FROM <name>` and checked against the grid: only if the table's real columns match the grid's headers exactly is anything reported. Shortcuts with a different shape, like **Ctrl+4** (`SELECT COUNT(1) FROM`) and **Alt+F1** (`sp_help`), therefore get no links rather than wrong ones.

**0.22.0** — **`SELECT FA.*` now gets source links on queries SQL Server can't describe** (the #temp case). Until now a star made the whole grid decline — "none produced a result matching this grid's 87 columns" — because the column count isn't knowable from the query text. It is knowable from the catalog, so the starred table's real column list is read and expanded. This stays safe: the expanded list must match the grid's headers one-for-one, by count and by name, or it declines exactly as before. A bare `*` is expanded only for a single-table query; across a join the column order is the server's business, not ours.

**0.21.2** — **The toolbar button has an icon.** It was declared two contradictory ways at once — "this icon is a built-in Visual Studio image" plus a pointer to a file resource — so SSMS looked for an image that did not exist and drew nothing. Both it and **Analyze Data...** now use Visual Studio's own icons (a clock with an arrow for history, a table for Analyze Data), which are themed and sharp at any display scaling, and add nothing to the download.

**0.21.1** — Export finished off: **Export selected…** on the right-click menu exports just the rows you picked; the confirmation says how many entries it is about to write; a grouped row's block now states how many runs it stands for (it used to export one block and say nothing about the rest); and the file notes it when the list hit the 5,000-entry display cap.

**0.21.0** — Query History: **Export…** saves the list exactly as you have it filtered to a `.sql` file, one block per query with the server, database and time as a comment above it. It warns first, because the exported file is **not** encrypted — unlike the history itself, it holds the queries in plain text, passwords included.

**0.20.0** — **Query History, part 2.** A **toolbar**: switch on *SSMS Data Analyzer* under **View → Toolbars** for a one-click Query History button. **Group identical** (a checkbox, off by default) collapses repeated runs of the same text into one row showing how many times you ran it (×3). New **right-click menu** on a row: Open in new query window, Insert at cursor, Copy, **Copy with header** (server, database and time as comment lines above the query), Star/unstar, Delete, and **Find entries for this database**. Copy and Copy with header now take **every selected row**, separated by `GO`. New **Document** column, plus two search prefixes: `doc:SQLQuery3` and **`closed:true`** — the queries you ran in a tab you have since closed.

**0.19.4** — **Query History actually records now.** Opening the history file read the retention setting, and reading a setting is only allowed on SSMS's UI thread — but the history file opens on a background thread, so it threw (`COMException`) every time. The key file had already been written by then, which is why the folder existed but never got any entries. The setting is now read on the UI thread and cached. Also hardened: our own diagnostic logging can no longer throw from a background thread, which would have killed the writer and stopped recording for the session.

**0.19.3** — Query History diagnostics now distinguish **queued** from **written**, and report whether the history file could be opened at all. 0.19.2 said "5 recorded" while nothing had reached the disk, which made a broken setup look healthy. The line under an empty list now reads, for example, *"5 executions seen, 5 queued, 0 written. History file: could not be opened (CryptographicException)."* Also: a retention setting of 0 days is now treated as "keep everything" rather than deleting the whole history at startup.

**0.19.2** — **Query History now says why it is empty**, instead of just showing nothing: open Tools → Query History… and the line under the list reports how many executions were seen, how many were recorded, and the reason for the last one that wasn't (never any query text, server or database name — the reasons are value-free). Also, an execution whose connection cannot be read is now still recorded, with the server left blank, rather than being dropped — except when an Excluded servers list is set, where not knowing the server means the exclusion can't be honoured, so it is deliberately skipped and says so.

**0.19.1** — **Fixes Query History recording nothing at all in 0.19.0.** The history folder was never created before the encryption key was written to it, so setting up history failed on the first run and every execution went unrecorded, silently. Nothing was lost that had been saved — nothing had been saved. Also: an entry now appears within a few seconds of running the query instead of waiting up to 30 seconds for the optional duration signal, and **Clear all history** now creates a fresh key for the rest of the session (previously, queries run after a Clear would have been unreadable the next time SSMS started).

**0.19.0** — New: **Query History** (Tools → Query History…). Every query you execute is recorded locally — when, which server and database, and the text that ran — so you can find that query you ran an hour ago. Search it (`sql:`, `server:`, `db:`, `starred:true`, `error:true`, or just type words), filter by date, preview the text, star what you want to keep, and reopen an entry in a new query window (it never runs by itself). **Your history is encrypted on disk** with a key tied to your Windows account, so a copied file is useless on another PC or under another account; queries are stored exactly as you ran them, passwords included, and **Clear all history** destroys the key first so everything old becomes unreadable at once. Off-switch, excluded-servers list and a 30-day retention are in Tools → Options → SSMS Data Analyzer. Assign a shortcut under Environment → Keyboard (`SsmsDataAnalyzer.QueryHistory`).

**0.19.0 fixes** — **Script object as ALTER is no longer slow, and the script no longer walks off to the right.** The script was being typed into the new query window line by line, so SSMS's editor re-indented every line as it arrived. It is now written in one go, exactly as SQL Server produced it. (This also speeds up the query window that **Go to source** opens.) **Ctrl+click now responds immediately**: the popup opens at once showing "Scripting …" with a progress bar while the server is asked, instead of nothing happening for a few seconds.

**0.18.0** — **Go to source, Peek and pivot links now work on queries that use a #temp table** (or anything else SQL Server can't describe from outside your session). The extension reads your query's own `alias.Column` list and FROM clause to find which real table each column comes from; temp-table columns, expressions and `SELECT *` still get no link. The status bar says when an answer came from the query text.

**0.17.0** — New: **script an object from the query editor.** Put the cursor on a table, view, procedure or function name and press **F12** — its ALTER script (CREATE for tables and other objects without ALTER) opens in a new query window, not executed. **Ctrl+click** a name to see its CREATE script in a popup with **Copy** and **Open in new query window**; **Esc** closes it. Names with dots inside brackets and three-part names work.

**0.16.0** — **Go to source, Peek and pivot links now work on every grid when several statements run together** (e.g. three highlighted SELECTs → three grids). Each statement is matched to its own grid using SQL Server's own T-SQL parser. Still safe: if two statements return the same columns from different tables, it declines instead of guessing — which also fixes a case where the second grid could previously point at the first statement's table.

**0.15.2** — Aggregate selection looks cleaner: thin lines between rows, values right-aligned and emphasised so numbers line up, labels in lighter text, and "—" (not available) greyed out.

**0.15.1** — Settings: the "Enable right-click Analyze Data" option is no longer labelled "(experimental)".

**0.15.0** — New: **Aggregate selection…** on the results grid's right-click menu (also under Tools). Select cells and get **Count, Distinct, Sum, Average, Min, Max** in a small popup, formatted with your Windows regional settings (thousands separators included). NULLs are left out like in SQL; if some values aren't numbers, Sum/Average show "—" and say how many. Ctrl+C copies the results; **Esc** closes it.

**0.14.3** — Closing **Find in Results** (Esc or X) now really removes the yellow highlights from the results grid. 0.14.2 closed the window but left the colours behind.

**0.14.2** — **Esc** now closes the **Find in Results** window too. Closing it (Esc or X) also clears the yellow match highlights from the results grid.

**0.14.1** — **Esc** now closes the Peek window. Before, SSMS used Esc to jump back to the query and the window stayed open.

**0.14.0** — New: **Peek source for this value**, next to Go to source on the results grid's right-click menu (and **Peek source…** in pivot windows). Instead of opening a new query tab, it shows the linked record right away in a small floating window — glance at it, then close it with **X** (or **Esc**). Foreign-key icons inside the peek window work too, so you can follow links further. Go to source is unchanged; pick whichever you need each time.

**0.13.3** — A keyboard shortcut assigned to **Go to source for this value** now works: it uses the grid's current cell (the one you last clicked or moved to with the arrow keys). Previously it looked for a cell under the mouse pointer, so a shortcut did nothing unless the pointer happened to be over the right cell. Go to source always follows one value; when several cells are selected, the status bar says which row it used.

**0.13.2** — "Go to source" and pivot FK links now use the query text that was actually **run**, not whatever the query window holds when you right-click. Previously, editing or pasting into the window after running a query (for example pasting copied rows above it) made them decline with a syntax error.

**0.13.1** — When "Go to source" or pivot FK links decline because SQL Server couldn't describe the query, the status bar now shows SQL Server's actual error number and message instead of a generic guess, so the cause can be found. (Shown on screen only, never written to logs.)

**0.13.0** — Pivot extras: a **Header** dropdown labels each pivot column by a chosen column's value (e.g. `ID = 4522`) instead of `Row 7`; every pivot now opens in its own tab (Pivot 1, Pivot 2, …) instead of replacing the previous one; right-click → **Copy as Markdown table** copies what's currently shown, ready for Jira or Confluence. Also: "Go to source" no longer writes clicked cell values into SSMS's ActivityLog — the full message is still shown on the status bar.

**0.12.1** — Pivot look: cells now have vertical grid lines and the link icon has space around it, so it clearly belongs to its own value instead of seeming to point at the next column. The icon is now "open in new window" rather than an arrow.

**0.12.0** — Pivot: foreign-key columns now get a small arrow inside each cell (like DBeaver). Click it to open the linked record in a new, connected query window — the same as **Go to source…**. FK column names show a link marker, and hovering the arrow shows where it goes. Right-click a pivot cell → **Go to source…** does the same from the keyboard. If the query can't be traced back to its tables, the pivot says why and simply shows no arrows.

**0.11.1** — Pivot: right-clicking a row that is **not** part of your selection now pivots just that row. Previously it pivoted the old selection, because SSMS keeps the selection when you right-click elsewhere. Right-clicking inside the selection still pivots all selected rows.

**0.11.0** — New: **Pivot selected rows…** on the results grid's right-click menu (also under Tools). Turns the selected rows sideways — column names down the left, one column per row — so wide rows can be read and compared. Columns whose values differ between the rows are highlighted; toggles show only differing columns or hide all-NULL ones, and a filter box narrows columns by name. Ctrl+C copies selected cells for Excel. Shows at most 100 rows (change it in Options → Pivot); anything beyond that is named in the banner. The values are a snapshot taken when you open it.

**0.10.0** — The **Type** column in Analyze Data now shows the declared size: `nvarchar(50)`, `decimal(18,2)`, `datetime2(7)`, `nvarchar(max)` — instead of just the bare type name.

**0.9.4** — "Paste as SQL IN" now actually appears on the query editor's right-click menu. Earlier builds attached it to the generic Visual Studio editor menu; SSMS's query editor uses its own.

**0.9.3** — You can now assign your own keyboard shortcuts to these commands: **Tools → Options → Environment → Keyboard**, search for `SsmsDataAnalyzer`.

**0.9.2** — "Paste as SQL IN" should now appear on the query editor's right-click menu as well as under Tools.

**0.9.1** — Fixed the new "Paste as SQL IN" items not appearing on the query editor's right-click menu. Also added them to the **Tools** menu.

**0.9.0** — New: **Paste as SQL IN (...)** and **Paste as numeric SQL IN (...)** on the query editor's right-click menu. Turns a list of values from the clipboard (a spreadsheet column, an email) into a ready-to-use IN list — de-duplicated, apostrophes escaped, `N` prefix added automatically for accented text. The numeric variant omits the quotes, and refuses (naming the offending value) if something isn't a number.

**0.8.2** — Fixed "Analyze Data..." missing from the right-click menu on the very first right-click after connecting to a server (it appeared from the second click onward).

**0.8.1** — Fixed "Go to source" refusing to work on query windows using Windows Authentication (it mistook them for Entra sign-ins).

**0.8.0** — "Find in Results" and "Go to source" now work on every SSMS 22 build (previously some builds needed 22.9+ — see 0.7.6). The trade-off: "Go to source" reads a cell's on-screen text now, not its raw stored value, so it declines rather than guess for a few cases where that text can't be trusted to round-trip exactly: `float`/`real` values (shown rounded), `binary`/`varbinary`/`timestamp` values (shown as hex, with no way to confirm nothing was cut off), very long text or `xml` values (same truncation risk), and a cell that displays exactly "NULL" (indistinguishable from the literal word "NULL" stored in a text column). Every other type — whole numbers, `decimal`/`money`, dates and times, GUIDs, ordinary bounded text — still works exactly as before.

**0.7.6** — "Find in Results" and "Go to source" no longer crash SSMS with a raw .NET error dialog on an older SSMS 22 build that lacks the results-grid API they need; the menu items just don't appear, and if triggered anyway, the status bar says why. Analyze Data is unaffected either way.

**0.7.5** — When "Go to source" declines because the query and the grid disagree, it now says exactly how they disagree (both column counts, and the first column that differs).

**0.7.4** — "Go to source" now works with multi-statement queries (`USE ... GO ... SELECT`), with a selection, and when a tab shows more than one result grid.

**0.7.3** — Find in Results moved into a proper dockable panel; F3 / Shift+F3 now work there;
fixed the panel's layout.

**0.7.0** — Added Find for SSMS's own query results grid, which SSMS itself has no feature for.

**0.6.0** — "Go to source" queries now open connected and run automatically, using the
connection you were already working in.

**0.5.0** — Added "Go to source" to SSMS's query results grid, so any cell holding an ID can
jump to its parent record.

**0.4.0** — Added foreign-key navigation to the analysis panel.

**0.3.0** — Added search within the analysis panel, made the settings page functional, and added
a confirmation prompt before analysing very large tables.

**0.2.0** — Analysis is driven entirely from Object Explorer; removed the manual server/database
entry form.

**0.1.x** — First working version: right-click a table, get per-column fill rates, exact distinct
counts and last-fill dates.

---

## Note for maintainers

This file and the `.vsix` beside it are updated by hand when a build is released. If the version
above and the version inside the `.vsix` ever disagree, the `.vsix` is the truth — check it with:

```
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::OpenRead("SsmsDataAnalyzer.vsix").Entries |
  Where-Object Name -eq "extension.vsixmanifest"
```

A GitHub **Release** with the `.vsix` attached would be the tidier long-term home for builds —
it gives a proper download page, release notes and version history without binaries accumulating
in the repository's history. Worth switching to if this gets more than a handful of users.
