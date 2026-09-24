# SSMS Data Analyzer

**Find out which columns in a database table are actually being used — and when each one was last filled in.**

An extension for SQL Server Management Studio 22. You don't need to write any SQL to use it:
right-click a table, and it tells you what's in it.

Useful if you need to answer questions like:

- *Is anyone still filling in this field, or is it dead?*
- *When did we stop using this column?*
- *How many different values does this field actually have?*
- *This ID points at another table — what's the actual record behind it?*
- *These two rows look the same — what's actually different between them?*
- *What was that query I ran an hour ago, in the tab I've since closed?*

It also adds a few things SSMS itself doesn't have: a searchable history of the queries you have
run, searching query results, peeking at a linked record, comparing rows side by side, adding up
a selection, turning a list of values into a SQL `IN (...)` clause, and scripting the object
under your cursor with **F12** or **Ctrl+click**.

## Everything it adds, at a glance

| What | Where to click |
|---|---|
| **Analyze a table** — fill rates, distinct counts, last-fill dates | Object Explorer → right-click a table → **Analyze Data…** |
| **Search the analysis** | Click the Analyze Data panel → **Ctrl+F** |
| **Search query results** | Right-click the results grid → **Find…** |
| **Jump to a linked record** | Right-click a cell in the results grid → **Go to source for this value** |
| **Peek at a linked record without leaving your tab** | Right-click a cell in the results grid → **Peek source for this value** |
| **Compare rows side by side** | Select rows in the results grid → right-click → **Pivot selected rows…** |
| **Aggregate a selection** — COUNT, DISTINCT, SUM, AVERAGE, MIN, MAX | Select cells in the results grid → right-click → **Aggregate selection…** |
| **Paste a list as `IN (...)`** | In a query window, right-click → **Paste as SQL IN (...)** |
| **Script an object** — ALTER into a new window, or peek at its CREATE | In a query window, cursor on a table/view/procedure name → **F12**, or **Ctrl+click** the name |
| **See what you ran earlier** — searchable history of every query you execute | **Tools → Query History…** |
| **Settings** | **Tools → Options… → SSMS Data Analyzer** |
| **Keyboard shortcuts** | **Ctrl+Alt+Q** then a letter — see [Keyboard shortcuts](#keyboard-shortcuts) |

---

## Installing it

**You don't need to build anything.** The ready-to-install file is in this repository.

1. Open the **[`dist`](dist)** folder above → click **`SsmsDataAnalyzer.vsix`** →
   click the **Download** button (or the ⤓ icon) to save it.
2. **Close SSMS** if it's open. *(The installer can't replace files while SSMS is running.)*
3. **Double-click the downloaded file** and click through the installer.
4. **Open SSMS again.**

That's it — you'll find **Analyze Data…** when you right-click a table.

Requires **SSMS 22.3 or newer**. It will not install on an older SSMS 22 build, on SSMS 21 or
older, or on Visual Studio — the installer refuses rather than installing something that misbehaves.
(SSMS 22.0 was reported to show an error when SSMS starts. 22.3 is the oldest build every
feature has been confirmed working on. **Help → About** shows your version.)

<details>
<summary>If double-clicking doesn't work</summary>

Run this instead, replacing the path at the end with wherever you saved the file:

```
"C:\Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE\VSIXInstaller.exe" "%USERPROFILE%\Downloads\SsmsDataAnalyzer.vsix"
```

To remove it:

```
"C:\Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE\VSIXInstaller.exe" /uninstall:SsmsDataAnalyzer.6f2b6e2a-6c2a-4e3a-9c9a-2f6b0c8a1a4d
```
</details>

### Updating to a newer version

Same steps — download the new file and install over the top. No need to uninstall first.
The current version and what changed in each one are listed in
[`dist/VERSION.md`](dist/VERSION.md).

---

## Feature 1 — Analyze a table

**Where:** Object Explorer (the tree on the left) → expand your database → **Tables** →
**right-click any table** → **Analyze Data…**

It uses the connection you're already signed in with — no passwords to re-enter.

A panel opens and fills in after a few seconds, one row per column of the table:

| Column in the panel | What it tells you |
|---|---|
| **Column** | The field name |
| **Type** | What kind of data it holds, with its declared size — `nvarchar(50)`, `decimal(18,2)`, `nvarchar(max)` |
| **Filled** | How many rows actually have a value here |
| **Fill %** | The same as a percentage — **the quickest thing to scan** |
| **Blank** | Rows containing empty text (counted separately from "no value at all") |
| **Distinct** | How many *different* values exist (always an exact count) |
| **Last Fill** | **When this column was last filled in** — see below |
| **Min / Max** | The smallest and largest values |
| **Flags** | A plain-English summary — see below |

### How to read it

**Fill %** is the fastest signal. A column at `0.4%` is filled in for 4 rows in every 1,000 —
almost certainly abandoned, or only used for one rare case.

**Last Fill** is the most useful column and the reason this tool exists. It answers
*"when did anyone last put something in this field?"* If a column shows `2019-03-11` and the
table has rows from last week, **people stopped using that field in 2019**. That's the evidence
you need to retire it.

It works from the table's creation-date column: `DateCreated`, or if there isn't one, the first
of `CreatedDate`, `CreatedOn`, `Created`, `InsertDate`, … that exists (the list can be changed in
[Settings](#settings)). If the table has none of them, Last Fill shows `n/a` and everything else
still works.

**Flags** call out the interesting cases automatically:

| Flag | Meaning |
|---|---|
| `DEAD` | Never filled in. Not once. |
| `SPARSE` | Filled in less than 5% of the time |
| `CONSTANT` | Every row has the *same* value — so it isn't telling you anything |
| `UNIQUE` | Every row has a *different* value — it's an identifier |

### In the panel

- **Copy as Markdown** / **Copy as CSV** (buttons at the top) — paste straight into a ticket, a
  document, or Excel.
- **Ctrl+F** — search the panel. Type, and matching cells are highlighted; **Enter** / **F3** for
  the next match, **Shift+Enter** / **Shift+F3** for the previous, **Esc** to close. Handy when a
  table has 150 columns and you want every `…Date` column or everything flagged `DEAD`.
- **Right-click a row** → **Go to source table** or **Go to source for this value** — see
  [Feature 3](#feature-3--jump-to-a-linked-record-go-to-source).
- **Cancel** stops a long analysis and keeps whatever was already worked out.

---

## Feature 2 — Search inside query results

SSMS has no way to search the results of a query. This adds one.

**Where:** run any query → **right-click anywhere in the results grid** → **Find…**

A **Find in Results** panel opens:

1. Type what you're looking for.
2. Press **Enter** or click **Find**.
3. **Enter** / **F3** for the next match, **Shift+Enter** / **Shift+F3** for the previous.
4. **Esc** closes the panel and clears the highlights.

It searches **every row**, not just the ones on screen, and jumps to each match in turn.

> **Note:** Ctrl+F won't open this one — in query windows that shortcut belongs to SSMS and opens
> its own Find dialog. Use the right-click menu (or [assign your own shortcut](#keyboard-shortcuts)).

---

## Feature 3 — Jump to a linked record ("Go to source")

When a column holds an ID pointing at another table (a foreign key), this opens that other table
for you, already filtered to the matching record.

**Three places you can do it:**

| From | How |
|---|---|
| **Query results** | Right-click a cell containing an ID → **Go to source for this value** |
| **The Analyze Data panel** | Right-click a column's row → **Go to source table**, or right-click its **Min** / **Max** cell → **Go to source for this value** |
| **A pivot window** | Click the small icon inside a linked cell (see [Feature 4](#feature-4--compare-rows-side-by-side-pivot)) |

A new query tab opens, connected with your current sign-in and **already run**, showing the
record. (To review the query before it runs, turn off *Automatically execute the generated
query* in [Settings](#settings).)

**Just want a quick look, not a new tab?** Right-click the same cell and pick **Peek source for
this value** instead. It shows the referenced record in a small floating window right away —
no new query tab, nothing to clean up. Close it with **Esc** or its own **X** the moment you've
seen what you needed. In a pivot window, the equivalent is **Peek source…** on a linked cell's
right-click menu. Either way, if the peeked record itself has a linked column, the 🔗 icon
inside the peek window works too: clicking it follows the link **in the same window**, one level
deeper, and **← Back** (or **Alt+Left**, or **Backspace**) walks back out again. So you can
explore a chain of foreign keys without opening a single query tab, and without losing your way.
The icon is a magnifier there rather than the "open in new window" one, because that is what it
does. If you do want a query tab, right-click the cell → **Go to source…**.

Go to source and Peek source are just two ways to look at the same result — picking one never
changes what the other does.

**It never guesses.** The option is only offered when the link is certain. It is not offered for:

- columns that aren't a declared foreign key, or that point at several tables,
- foreign keys made of more than one column,
- calculated columns (e.g. `Price * 2`),
- values it can't safely turn back into SQL (`NULL` cells, `float` numbers, binary data, very
  long text).

In those cases the status bar at the bottom of SSMS says why.

Works with queries that use `USE`, `GO`, joins, aliases and several result grids. Finding the
link reads only the declared table structure; Go to source and Peek then select just the linked
record.

**Good to know:**

- **It uses the query as it was run.** After running a query you can keep typing, pasting or
  highlighting other text in the window — Go to source and Peek still follow the query that
  produced the grid.
- **With a keyboard shortcut** it uses the grid's current cell (the one you last clicked or
  moved to with the arrow keys). If several cells are selected, the status bar says which row's
  value it used.
- **Queries with #temp tables** work too: when SQL Server can't describe the query (it can't see
  your session's temp tables), the extension reads the query itself. Columns written as
  `alias.Column` from a real table get links, and **`SELECT FA.*` works as well** — the table's
  real column list is read from the catalog and expanded. That expansion is only used when the
  resulting names match the grid's headers exactly, so a table that has changed since you ran
  the query makes it decline rather than mislabel a column. Temp-table columns and expressions
  still get no link, and a bare `*` needs a single-table query. The status bar says "resolved
  from the query text".
- **SSMS's query shortcuts** work too: select a table name, press **Ctrl+3**
  (`SELECT TOP(100) * FROM`), and the grid's columns get source links even though the query SSMS
  ran never appeared in your editor. Shortcuts that produce a different shape — **Ctrl+4**
  (`SELECT COUNT(1) FROM`), **Alt+F1** (`sp_help`) — simply get no links, because their columns
  are not the table's.
- **Several statements run together** work too — each grid is matched to the statement that
  produced it. If two of them return the same columns from *different* tables, it declines
  rather than guess; run just the statement you want.

---

## Feature 4 — Compare rows side by side ("Pivot")

Turns selected rows sideways: column names down the left, one column per row. Great for wide
tables, and for seeing exactly what differs between rows that look the same.

**Where:** select rows in the results grid → **right-click** → **Pivot selected rows…**
(also under the **Tools** menu)

```
Column        Row 4    Row 7    Row 9
ID            4        7        9
TestAKey 🔗    1   ⧉    2   ⧉    1   ⧉     ← foreign key: click ⧉ to open the linked record
Name          bbbb     ttt      gfhd     ← highlighted: values differ
```

### Selecting the rows

| To pivot… | Do this |
|---|---|
| One row | Right-click any cell in it |
| A range of rows | Click the first row number, **Shift+click** the last, then right-click inside the selection |
| Rows far apart | **Ctrl+click one cell in each row**, then right-click one of them. *(Ctrl+click on row numbers doesn't work — SSMS itself replaces the selection.)* |

Right-clicking **inside** your selection pivots the whole selection; right-clicking a row
**outside** it pivots just that row. All columns are always shown, however many cells you
selected.

### In the pivot window

| | |
|---|---|
| **Highlighted rows** | Columns whose values differ between the rows |
| **Show only differing columns** | Hides everything that's the same — often turns 150 columns into 3 |
| **Hide all-NULL columns** | Hides columns that are empty in every row |
| **Filter** | Narrows columns by name |
| **Header** | Label each row by a column's value (e.g. `ID = 4522`) instead of `Row 7` |
| **🔗 and ⧉** | Foreign-key column; in a pivot, click ⧉ in a cell to open the linked record ([Feature 3](#feature-3--jump-to-a-linked-record-go-to-source)). Hover it to see where it goes. Right-click a cell → **Go to source…** does the same, or **Peek source…** to see it in a small closable window without leaving the pivot. |
| **Ctrl+C** / **Ctrl+A** | Copy selected cells (pastes into Excel) / select all |
| **Right-click → Copy as Markdown table** | Copies what's currently shown, for Jira or Confluence |

Each pivot opens in **its own tab** (Pivot 1, Pivot 2, …), so you can keep several open.

### Good to know

- **Up to 100 rows** per pivot by default. If you select more, a highlighted banner says
  "Showing 100 of N selected rows". Change the limit in [Settings](#settings) (1–500).
- **It's a snapshot.** Re-running the query doesn't change an open pivot — pivot again to
  refresh. It keeps working even after you close the query tab.
- **Values are exactly what the grid shows** — very long text is cut off the same way, and a
  real `NULL` looks the same as the text `NULL`.
- If no ⧉ icons appear, the line under the banner says why (for example, the query couldn't be
  matched to its tables).

---

## Feature 5 — Aggregate a selection

Select some cells in the results grid and get their COUNT, DISTINCT, SUM, AVERAGE, MIN and MAX
at a glance in a small popup.

**Where:** select cells in the results grid → **right-click** → **Aggregate selection…**
(also under the **Tools** menu)

```
Count      2
Distinct   2
Sum        252
Average    126
Min        125
Max        127
```

- **Numbers use your Windows regional settings**, with thousands separators — `1.234.567,5` on
  a Croatian machine, `1,234,567.5` on a US one.
- **NULLs are left out** of every number — Count, Distinct, Sum and Average all behave like
  their SQL equivalents (`NULL` here means a real NULL or the literal text `NULL`, same
  caveat as everywhere else in the extension).
- **Sum and Average need every non-NULL value to be a number.** If even one selected cell
  isn't (mixed text and numbers), Sum and Average show **—**, and the small line under the
  numbers says how many values weren't numeric. Min and Max still work in that case — by date
  if every value is one, otherwise alphabetically.
- **Up to 1,000,000 cells** per selection. Select more, and the status bar tells you how many
  you picked instead of computing anything.
- **Copy what you need:** Ctrl+C copies every row (pastes into Excel as two columns);
  double-click a row, or right-click it → **Copy value**, to copy just that one number.
- **It's a snapshot** of the cells selected when you opened it. Select other cells and run it
  again to update.
- **Esc** closes the popup, same as Peek and Find.

---

## Feature 6 — Paste a list as `IN (...)`

You have a list of values in a spreadsheet or an email, and you need them as a SQL `IN` list.

**Where:** in a query window, put the cursor where you want them → **right-click** →
**Paste as SQL IN (...)** (or **Paste as numeric SQL IN (...)** for numbers).
Both are also under the **Tools** menu.

Copy this:

```
aba
baba
dagate
```

Right-click → **Paste as SQL IN (...)**, and you get:

```sql
(
'aba',
'baba',
'dagate'
)
```

The **numeric** variant leaves the quotes off, for ID columns and other numbers:

```sql
(
10,
20,
30
)
```

It handles the awkward bits for you:

- **Duplicates are removed** (the status bar tells you how many).
- **Apostrophes are escaped** — `O'Brien` becomes `'O''Brien'`, which is valid SQL.
- **Accented text gets the `N` prefix** automatically, so Croatian characters survive.
- **Values already in quotes** are not double-quoted.
- **Excel columns work** — tabs and line breaks are both treated as separators.
- If you pick the **numeric** variant and something isn't a number, it **tells you which value**
  and pastes nothing, rather than producing SQL that doesn't run.

---

## Feature 7 — Script an object

You're reading a query and want to see — or change — the view, procedure or table it uses,
without hunting for it in Object Explorer.

**Where:** in a query window, on any object name:

| Do this | You get |
|---|---|
| Put the cursor on the name → **F12** | The object scripted **as ALTER** in a **new query window**, connected to the same server and database — ready to edit. It is **not run**. (Also: right-click → **Script object as ALTER**, or the **Tools** menu.) |
| **Ctrl+click** the name | A small popup with the object's **CREATE** script. **Copy** puts the whole script on the clipboard; **Open in new query window** opens it (again, not run); **Esc** closes it. |

It works like SSMS's own *Script … as* menu in Object Explorer, but straight from the name in
your query:

- **Any way the name is written** — `Orders`, `dbo.Orders`, `[Finances].[Accounting.Compensation.Correction.Item]`
  (dots inside brackets are part of the name), `OtherDb.dbo.Orders`, `"quoted"` names. The cursor
  can be anywhere in the name, including on the schema or a dot.
- **Tables, types, synonyms and sequences have no ALTER** — F12 shows their CREATE script
  instead, with a first line saying so: `-- Table has no ALTER form; CREATE script shown.`
- **Tables include their keys, constraints, indexes and triggers**, like SSMS's *Script Table as
  CREATE*. Permissions and dependent objects are left out.
- **Which object is meant** is decided by SQL Server itself: a name without a schema finds the same
  object your query would (your default schema first, then `dbo`). The status bar names the object
  it scripted, so you can tell. A three-part name (`OtherDb.dbo.Orders`) looks in that database.
- **Nothing to script** — a `@variable`, a `#temp` table, a keyword, or a name inside a comment or
  a string — just gets a short explanation in the status bar. The same goes for an object that
  doesn't exist (e.g. a column or alias name).
- **Ctrl+click anywhere else** (blank space, keywords) and plain clicks behave exactly as before.
- **F12 only changes inside query windows.** Everywhere else in SSMS it keeps its usual meaning.
  To use a different key, see [Keyboard shortcuts](#keyboard-shortcuts).
- Needs a **connected** query window. Microsoft Entra (MFA / token) connections aren't supported
  yet — the status bar says so.

---

## Feature 8 — Query History

*"What was that query I ran an hour ago?"* — the tab is closed, the text is gone. SSMS keeps no
record. This does.

**Where:** **Tools → Query History…** (or give it your own shortcut — see
[Keyboard shortcuts](#keyboard-shortcuts)).

Every query you **execute** is recorded: the text that ran, the server and database, and when.
(One exception, in "Good to know" below: queries run from a query shortcut such as Ctrl+3.)
The window lists them newest first, with a preview of the full text below the list.

### Finding one

Type in the search box and the list narrows as you type. Plain words match inside the query text,
and you can be specific:

| Type this | To find |
|---|---|
| `Accounting` | every query mentioning Accounting |
| `"create view"` | that exact phrase |
| `server:ISUPT` | queries run on servers whose name contains ISUPT |
| `db:Finances` | queries run against that database (`database:` works too) |
| `starred:true` | only the ones you starred |
| `error:true` | only the ones that failed |
| `doc:SQLQuery3` | queries run from that query tab |
| `closed:true` | queries you ran in a tab that is no longer open |

Terms combine, so `db:Finances create view` finds both. There's also a date filter — Today, Last 7
days, Last 30 days, All.

### Using one

| Action | What it does |
|---|---|
| **Open in new query window** (or double-click) | Opens the query on the same server and database. **It is never run for you.** |
| **Insert at cursor** | Drops the text into the query window you're already in |
| **Copy** | The query text to the clipboard (Ctrl+C works too) |
| **☆ Star** | Keeps an entry forever — starred entries are never removed by the retention period |
| **Delete** / **Clear all history** | Removes one entry, or everything |

Select several rows and **Copy** takes them all, separated by `GO`. **Copy with header** adds the
server, database and time as comment lines above each query. Right-click a row for all of these,
plus **Find entries for this database**.

**Group identical** (the checkbox above the list) collapses repeated runs of the same text into a
single row, showing the most recent one and how many times you ran it (×3). It is off by default.

**The toolbar:** switch on **SSMS Data Analyzer** under **View → Toolbars** for a Query History
button you can reach in one click.

**Export…** writes the list, exactly as you have it filtered, to a `.sql` file — one block per
query with the server, database and time as a comment above it, separated by `GO`. To export just
a few, select them and use **Export selected…** on the right-click menu. If **Group identical** is
on, each block says how many runs it stands for, so the export never understates what you ran.

It asks first: that file is **not** encrypted, so unlike the history itself it holds the queries
in plain text, passwords included. Put it somewhere you would put a password.

### Your history is encrypted

DBAs type passwords into queries — `CREATE LOGIN … WITH PASSWORD = '…'` and the like. A plain
history file would hand all of them to anyone who copied your profile folder.

So the file isn't plain. **Every entry is encrypted before it reaches the disk**, with a key tied
to your Windows account (via Windows' own DPAPI), kept in
`%LOCALAPPDATA%\SsmsDataAnalyzer\QueryHistory\`. Copy that folder to another PC, or open it
under another Windows account, and it is unreadable — even to a local administrator. Server and
database names are encrypted too, not just the query text.

Queries are stored **exactly as you ran them**, passwords included. Nothing is rewritten or
silently skipped, so what you see in the history is genuinely what you ran.

**What this does not protect against:** someone using your PC while you are logged in, or malware
running as you. In both cases your open query windows are just as exposed. Encryption protects the
*file*, not a session someone is already sitting in front of.

If you'd rather not record something in the first place:

- **Excluded servers** — list production servers in Options and nothing from them is ever recorded.
- **Enable query history** — turn it off entirely; existing history is left alone.
- **Clear all history** — destroys the key first, so every existing file becomes unreadable
  instantly, and then deletes them.

### Good to know

- It records **executions**, not keystrokes — one entry each time you press Execute.
- **Queries run from a query shortcut (Ctrl+3, Alt+F1, …) are not recorded.** SSMS runs those
  through a different internal command that an extension cannot observe, and it composes the
  text itself, so there is nothing for us to record. Deliberate: an entry holding only the table
  name you had selected would be misleading, and guessing which shortcut you pressed could put a
  query in your history that you never ran. (**Go to source** and **Peek** do work on those
  grids — see [Feature 3](#feature-3--jump-to-a-linked-record-go-to-source).)
- Recording happens in the background; it never slows down running a query.
- **Duration** and **success/error** are filled in when SSMS tells us a query finished. On builds
  where that signal isn't available those two columns stay empty — everything else still works.
- Entries older than the retention period (**30 days** by default) are removed when SSMS starts.
  Starred ones are kept forever.
- Very long scripts are stored up to 1 MB, then truncated with a marker.

---

## Keyboard shortcuts

Everything has a shortcut out of the box. They all start with the same chord — hold
**Ctrl+Alt** and press **Q**, let go, then press one letter:

| Press | Does |
|---|---|
| **Ctrl+Alt+Q**, then **H** | **Query History…** |
| **Ctrl+Alt+Q**, then **F** | **Find** in query results |
| **Ctrl+Alt+Q**, then **G** | **Go to source** for the selected cell |
| **Ctrl+Alt+Q**, then **P** | **Peek source** for the selected cell |
| **Ctrl+Alt+Q**, then **V** | **Pivot** the selected rows |
| **Ctrl+Alt+Q**, then **A** | **Aggregate** the selected cells |
| **Ctrl+Alt+Q**, then **D** | **Analyze Data** for the table selected in Object Explorer |
| **Ctrl+Alt+Q**, then **I** | **Paste as SQL IN (...)** |
| **Ctrl+Alt+Q**, then **N** | **Paste as numeric SQL IN (...)** |
| **Ctrl+Alt+Q**, then **S** | **Script object as ALTER** (same as F12) |
| **F12** | **Script object as ALTER**, in query windows only |

You don't have to memorise the letters: press **Ctrl+Alt+Q** and SSMS lists what can follow it.

**Why one chord instead of ten separate keys?** A default that quietly steals a key you already
use is worse than no default at all. This way the extension claims exactly **one** combination —
`Ctrl+Alt+Q` — rather than ten chances to collide with something of yours. F12 is the one
exception, and it applies only inside query windows, so it keeps its usual meaning everywhere
else.

**It never overrides a shortcut you have set.** The defaults are applied once, the first time a
version with them starts, and only to commands that have **no** shortcut at all:

- A shortcut you assigned yourself is left exactly as it is — upgrading never resets it.
- A key already used by SSMS or another extension is left alone too, and that command simply
  gets no default.
- If you **delete** one of these defaults, it stays deleted; it is not put back on the next
  start.

(Check what actually happened in **Tools → Options… → Environment → Keyboard** — search
`SsmsDataAnalyzer`.)

Press one in the wrong place and nothing breaks: the status bar just says what it needed (a
selected cell, a connected query window, and so on).

### Changing them

1. **Tools → Options… → Environment → Keyboard**
2. Type `SsmsDataAnalyzer` in *Show commands containing* and pick a command.
3. Click into *Press shortcut keys*, press the combination you want, then **Assign**.
   (*Remove* clears a default you don't want.)

To give a command you use constantly a single keystroke instead of the chord, assign it here —
SSMS warns you if the key is already used, which is the check this extension can't do for you.

| Command | Name in the Keyboard list |
|---|---|
| Query History… | `SsmsDataAnalyzer.QueryHistory` |
| Go to source for this value | `SsmsDataAnalyzer.GoToSourceForValue` — uses the cell selected in the results grid |
| Peek source for this value | `SsmsDataAnalyzer.PeekSourceForValue` — uses the cell selected in the results grid |
| Find… (in query results) | `SsmsDataAnalyzer.FindInResults` |
| Pivot selected rows… | `SsmsDataAnalyzer.PivotRows` |
| Aggregate selection… | `SsmsDataAnalyzer.AggregateSelection` — uses the cells selected in the results grid |
| Analyze Data… | `SsmsDataAnalyzer.AnalyzeData` — uses the table selected in Object Explorer |
| Paste as SQL IN (...) | `SsmsDataAnalyzer.PasteAsSqlIn` |
| Paste as numeric SQL IN (...) | `SsmsDataAnalyzer.PasteAsNumericSqlIn` |
| Script object as ALTER | `SsmsDataAnalyzer.ScriptObjectAsAlter` — **F12** in query windows; uses the name at the cursor |

---

## Settings

**Where:** **Tools** menu → **Options…** → **SSMS Data Analyzer** (in the list on the left)

You can leave every one of these alone. Changes apply the next time you use the feature — no
restart needed.

| Setting | What it does | Default |
|---|---|---|
| **Automatically execute the generated query** | Whether "Go to source" runs the query for you, or opens it for you to review first | On |
| **Pivot row limit** | Most rows one pivot (or Peek window) shows (1–500) | 100 |
| **Query timeout (seconds)** | How long Analyze Data waits before giving up on a slow table — raise it if a big table times out | 120 |
| **Large table threshold (rows)** | Above this size, Analyze Data warns you before starting a long analysis | 10,000,000 |
| **DateCreated candidate columns** | Fallback column names used for **Last Fill** when a table has no `DateCreated` | `CreatedDate, CreatedOn, …` |
| **Enable query history** | Whether executed queries are recorded at all | On |
| **Excluded servers** | Comma-separated server names that are never recorded — e.g. production | (empty) |
| **Retention (days)** | History older than this is removed at SSMS startup; starred entries are kept | 30 |
| **Enable right-click Analyze Data** | Turn off only if a future SSMS update breaks the Object Explorer menu | On |
| **Distinct batch size**, **Max grant percent**, **MAXDOP** | Advanced — how Analyze Data spreads its work and caps its server memory use | 8, 25, 0 |

---

## Is it safe to run on a production database?

It only ever **reads**. It never writes, updates or deletes anything.

- **Analyze Data** stays out of other users' way: it reads without blocking anyone else's work,
  caps how much server memory it can take, gives up rather than running forever, and warns you
  before starting on a very large table. **Cancel** at any point keeps what it has so far.
- **Go to source**, **Peek** and pivot links only read the table structure to find the link; the
  query they run selects just the linked record (or at most 1,000 rows for **Go to source table**).
- **Script an object** only reads the object's definition (the same way SSMS's *Script as* does);
  the script it opens is never run for you.
- **Find**, **Pivot**, **Aggregate selection** and **Paste as SQL IN** work on what's already on
  your screen or clipboard — they don't query the database at all.
- Your password is never stored or written anywhere, and cell values are never written to SSMS's
  log files.
- **Query History** records only what you executed, locally and encrypted; it never runs anything
  by itself and never sends anything anywhere.

---

## If something doesn't work

- **"Analyze Data…" isn't in the right-click menu** — make sure you right-clicked a *table*
  (Databases → *your database* → Tables). Try right-clicking again; if it still doesn't appear,
  restart SSMS, and check *Enable right-click Analyze Data* is on in [Settings](#settings).
- **The Analyze Data panel shows an error** — it always says what went wrong rather than sitting
  blank. Send that message along when reporting the problem.
- **A large table is slow** — that's the exact counting doing its work. Press **Cancel** to keep
  partial results, or raise the timeout in [Settings](#settings).
- **"Go to source" isn't offered, or nothing happens** — look at the status bar at the bottom of
  SSMS; it says why (see [Feature 3](#feature-3--jump-to-a-linked-record-go-to-source) for the
  cases it deliberately refuses). Sign-ins with Microsoft Entra aren't supported for this yet.
  If you ran the query right after starting SSMS, run it once more — the extension may not have
  finished loading yet.
- **A pivot shows fewer rows than you selected** — you hit the pivot row limit; the banner says
  so. Raise it in [Settings](#settings).
- **Query History is empty** — open it and read the line under the list: it says how many
  executions were seen, how many were written, and the reason for the last one that was not.
  Queries run from a query shortcut (Ctrl+3, Alt+F1, …) are never recorded — see
  [Feature 8](#feature-8--query-history).
- **A keyboard shortcut does nothing** — check it is actually assigned in **Tools → Options… →
  Environment → Keyboard** (search `SsmsDataAnalyzer`). A default is skipped when the key was
  already in use on your machine. Commands that act on a cell or a selection say what they
  needed on the status bar.

---

<details>
<summary><b>For developers</b> — building, testing, design decisions</summary>

### Repository layout

```
src/SsmsDataAnalyzer.Core/   netstandard2.0 — profiling engine, pivot, aggregate and result-shape logic, zero VS dependencies
src/SsmsDataAnalyzer.Cli/    net8.0 — same engine, scriptable from a terminal
src/SsmsDataAnalyzer.Vsix/   net472 — the SSMS 22 extension
tests/                       xUnit — ~390 tests, unit + integration
tools/seed/                  seeded test database + verified ground truth
docs/                        reverse-engineering notes on SSMS's internals, and one plan per feature
                             (pivot-plan.md, query-history-plan.md, *-api.md spike reports)
spikes/OeProbe/              metadata/IL inspector used to produce those notes
dist/                        the released .vsix and VERSION.md
```

`Core` has no dependency on Visual Studio or WPF, which is why the engine is testable and the
CLI exists.

### Building

Requires MSBuild from a Visual Studio 2022 or newer install. The VS "extension development"
workload is **not** required — the VSSDK NuGet package supplies the build targets, but they must
be imported explicitly (see `src/SsmsDataAnalyzer.Vsix/README-BUILD.md`).

```
msbuild src/SsmsDataAnalyzer.Vsix/SsmsDataAnalyzer.Vsix.csproj -restore -p:Configuration=Release
```

### Tests

```
dotnet test tests/SsmsDataAnalyzer.Tests/SsmsDataAnalyzer.Tests.csproj
```

Integration tests need a local SQL Server and the seeded database:

```
sqlcmd -S . -E -C -i tools/seed/seed.sql
```

`dotnet test --filter "Speed!=Slow"` skips the one deliberately slow timeout test.

### Design decisions

**Exact distinct counts, always.** `APPROX_COUNT_DISTINCT` is banned from the codebase and a
test enforces it. Approximate cardinality is fine for query planning and useless for deciding
whether a column is a de-facto key.

**One scan for everything else.** Pass 1 computes fill counts, last-fill dates, min/max and
average length for *every* column in a single table scan. Distinct counts get their own pass,
using index-backed queries where an index exists and batching the rest under a capped memory
grant.

**Report, never guess.** Distinct counts are collation-dependent and reported as the database
computes them. A composite foreign key offers a table jump but *not* a value jump, because
filtering on half a composite key returns plausible-but-wrong rows. Results-grid jumps describe
every `GO` batch and require them to agree on the source column before offering a link.

**The grid's own headers are the judge.** When SQL Server cannot describe a query (a `#temp`
table it cannot see from our connection), the source of each column is read from the query text
instead — including expanding `SELECT alias.*` from the catalog. None of that is trusted on its
own: the reconstructed column list must match the grid's actual headers one for one, by count
and by name, or the whole thing declines. That is what makes it safe to read a bare table name
left over from a query shortcut as `SELECT * FROM <name>`: if the guess is wrong, the headers
disagree and nothing is offered.

**Encrypted at rest, honest in content.** Query history is encrypted before it touches the disk,
under a key held by Windows for the current account. Queries are stored exactly as they ran,
passwords included — a history you cannot trust to be what you ran would be worth less than no
history — and the README says plainly what that does not protect against.

**Nothing is silently partial.** Cancellation keeps completed work. A pass-1 timeout returns the
metadata it has plus a warning rather than discarding the profile. A capped search reports
`10000+`, and a capped pivot says how many rows it left out.

**No secrets or data in logs.** SSMS's own connection object is reused rather than rebuilt from
a password, and nothing that can contain a cell value is written to the ActivityLog.

`CONTRACT.md` holds the frozen interfaces and the amendment history behind each of these
decisions; `PLAN.md` has the roadmap. Each larger feature has its own plan under `docs/` —
`pivot-plan.md` and `query-history-plan.md` — and the `*-api.md` files are spike reports on
SSMS's internals, each one marked with how confident it is and what it could not establish.

</details>
