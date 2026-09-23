# Query History — development plan

Status: **planned, not started.** Open decisions are in §9. The spike (§6) runs before any UI
code.

## 1. What it does

Every query you **execute** in SSMS is recorded locally. **Tools → Query History…** (or your
own shortcut, or a toolbar button) opens a window listing them, newest first. You can search
them, preview the text, and reopen one in a new query window.

```
┌ Query History ─────────────────────────────────────────────────────────────────┐
│ 🔍 [ sql:Accounting server:ISUPT…            ]  [Today ▾]  ☆ Starred only      │
├──────────────────┬──────────────┬────────────────────┬──────────┬──────────────┤
│ When             │ Server       │ Database           │ Duration │ Query        │
│ 14:52:10         │ ISUPTMSSQLD2 │ AgricultureFinances│ 00:00:01 │ SELECT TOP 1…│
│ 14:31:02  ☆      │ ISUPTMSSQLD2 │ AgricultureFinances│ 00:00:00 │ SELECT * FRO…│
│ yesterday 16:05  │ WINDEV…\SQLE…│ TestDB             │ ✖ error  │ UPDATE dbo.…│
├──────────────────┴──────────────┴────────────────────┴──────────┴──────────────┤
│ SELECT TOP 100 * FROM DebtLedger.Debt ORDER BY 1 DESC            (preview)      │
│                                                                                 │
│ [Open in new query window]  [Insert at cursor]  [Copy]  [☆ Star]  [Delete]      │
└─────────────────────────────────────────────────────────────────────────────────┘
```

### How it compares with Redgate SQL History

What we take from Redgate SQL History:
- the searchable history window
- search prefixes (`sql:`, `server:`, `database:`, `date:`, `starred:`)
- starring
- a retention period (their default is 7 days)
- a toolbar button

What we deliberately do differently:
- **We record executions, not keystrokes.** Redgate also records edits and unsaved tab
  versions. That is a much larger feature and a lot more data. Execution history answers "what
  did I run, where, and when".
- **One entry per execution.** Running the same text again adds a new entry, because the
  time, database and duration differ. The list can group identical text (see §9, D4).

## 2. What is recorded (per execution)

| Field | Source | Notes |
|---|---|---|
| Executed text | The existing `ExecutedQueryTextTracker`: the selection, or the whole document | Exactly what ran. |
| Start time | Local clock at Execute | UTC stored, shown in local time |
| Server, database, login name | The editor's `UIConnectionInfo` + current database | **Never the password** (CONTRACT.md Amendment 13) |
| Auth type | Windows / SQL login / Entra | So "open again" can reconnect sensibly |
| Document name / path | DTE active document | For example "SQLQuery1.sql" or a saved file path |
| Duration, outcome (success / error / cancelled), row count | Execution-completed signal | **Spike item S-1.** Leave empty if SSMS gives us no signal. |
| Starred | User | |

## 3. Storage (decision D1)

- **Location:** per-user, at `%LOCALAPPDATA%\SsmsDataAnalyzer\QueryHistory\`. Never roaming.
- **Decided: append-only JSON Lines, one file per month.** The file names look like
  `2026-09.jsonl`. Starring and deleting are written to a small `edits.jsonl` sidecar. At
  startup, a background load builds an in-memory index.
  - Zero extra DLLs, and crash-safe: a torn last line is simply skipped on the next load.
  - Easy to delete by hand. Not readable by hand: every line is encrypted (see
    "Protection at rest" below).
  - At the expected volume (tens of thousands of executions), in-memory search is instant.
- **Why not SQLite, as Redgate uses:** SSMS already loads other copies of SQLitePCLRaw
  (Roslyn language services, Copilot). Shipping our own copy into the same process risks
  assembly-version conflicts inside SSMS.
- **Retention:** default **30 days** (Redgate uses 7). It can be changed in Options, and
  starred entries are never trimmed. Old month files are deleted or rewritten in the
  background at startup.
- **Size cap:** texts longer than 1 MB are stored truncated, with a marker. That protects
  against huge scripts.

### Privacy and safety (user requirement, 2026-09-21)

**The risk:** DBAs type passwords into queries, for example `CREATE LOGIN … WITH PASSWORD = '…'`
or `sp_addlinkedsrvlogin`. If someone gets at the PC, or copies the profile folder, a plain
history file would hand them every password and every literal value ever run.

**Decision (user, 2026-09-23): encryption at rest is the protection.** Queries are stored as
they were run, passwords included — no redaction, no skipping. That keeps the history honest
(what you see is what you ran) and the code far simpler. The security rests on Layer 1; Layers
2 and 3 are hygiene around it.

**Layer 1: encrypted at rest.**
- Every entry is encrypted before it touches the disk.
- The algorithm is AES-256-CBC plus HMAC-SHA256 (encrypt-then-MAC). Each entry gets its own
  random IV. A JSONL line is `v1:<base64 IV|ciphertext|MAC>`. There is no plaintext field,
  so even server and database names are encrypted.
- The 64-byte key (32 bytes for AES, 32 for the MAC) is generated once with
  `RandomNumberGenerator`.
- The key is stored only DPAPI-protected: `ProtectedData.Protect`, scope CurrentUser, with
  fixed extra entropy, in `QueryHistory\key.bin`. DPAPI ties it to the Windows user's login
  secret. The key never exists in plaintext on disk.
- **Effect:**
  - A stolen history folder, a copied disk or a backup are all unreadable on another machine
    or under another Windows account.
  - Even a local admin can't read it without the user's Windows credentials (or an attack on
    the logged-in session).
- **Speed:** there is one DPAPI call at load. After that, AES runs per line, which is fast
  (tens of thousands of entries load in well under a second, on a background thread).
- **Tampering:** an entry that fails its MAC check is skipped, not shown. A missing or
  undecryptable key means history is simply empty, and a new key is created.
- Everything used is in .NET Framework 4.7.2 (`System.Security.Cryptography` +
  `System.Security.dll` ProtectedData), so nothing extra is shipped.

**Layer 2: the user decides.**
- *Enable query history*: on by default. When turned off, nothing is written.
- *Excluded servers*: a comma-separated list, for example production servers, that are never
  recorded.
- *Retention (days)*, default 30.
- **Clear all history** in the window, with confirmation. It deletes the key first
  (crypto-shred: every existing file becomes unreadable instantly, even if file deletion is
  interrupted or the disk keeps old sectors), then the files.

**Layer 3: file hygiene.**
- The folder is created with an explicit ACL: only the current user plus SYSTEM, inheritance
  disabled. That holds even if `%LOCALAPPDATA%` was redirected somewhere looser.
- Nothing goes to a temp file, and nothing plaintext is written during trimming. Old entries
  are re-encrypted into a new file, which is then swapped in.
- Decrypted text lives only in memory while SSMS runs. OeDiagnostics never gets query text
  (the unchanged project rule).

**What this does NOT protect against, stated in the README:**
- someone using the PC while you are logged in
- malware running as you

In both cases, the query window itself is just as exposed.

## 4. Features by phase

### Phase 1 — capture + window (MVP)

1. **Capture:** extend `ExecutedQueryTextTracker` (it already hooks `Query.Execute`,
   `ExecuteCurrentStatement` and `ExecuteWithDebugger`) to also append a history entry.
   - Writing happens off the UI thread through a single background writer queue, so executing
     a query never waits on disk.
   - When `ExecuteCurrentStatement` is used, the entry records the statement that actually ran,
     not the whole document.
2. **Command** `SsmsDataAnalyzer.QueryHistory`, "Query History…". It lives in the Tools menu
   (our existing group) and has a CanonicalName, so it can be bound to a shortcut. No default
   key, as for every command in this extension.
3. **Tool window:** a normal dockable `ToolWindowPane`, **not transient**, since it's a panel
   you come back to.
   - It remembers its position, and Esc returns focus to the editor (standard VS behaviour).
   - A virtualized list with columns When, Server, Database, Duration/Outcome and Query (first
     line).
   - A preview pane (read-only, Consolas, whole text), with a splitter between list and preview.
4. **Search box**, filtered as you type with a 250 ms debounce:
   - Plain words match inside the query text, case-insensitively.
   - Prefixes: `sql:`, `server:`, `database:` / `db:`, `starred:true`, `error:true`.
   - Quoted phrases work, for example `"create view"`.
   - The prefix parser is pure Core code with unit tests.
5. **Date filter:** Today / Last 7 days / Last 30 days / All.
6. **Actions:**
   - **Open in new query window:**
     - Same server, database and login as the entry.
     - If an open query window already uses that server and login, reuse its `UIConnectionInfo`
       (no password prompt). Otherwise open the SSMS connect dialog pre-filled (spike S-3).
     - Never runs the query automatically.
   - **Insert at cursor** in the active query window.
   - **Copy:** Ctrl+C copies the selected entries' text.
   - **Star / unstar**, **Delete**.
   - Double-click = Open in new query window.
7. **Options page:** *Enable query history*, *Excluded servers*, *Retention (days)* (see §3 "Privacy and safety").

### Phase 2 — toolbar + polish

8. **Toolbar button:**
   - A "Query History" button on SSMS's **SQL Editor toolbar**, if its group ID can be found
     (spike S-2).
   - Always also our own small **"SSMS Data Analyzer" toolbar**, which the user can switch on
     under View → Toolbars. That is the supported VS way, and it can't break.
9. **Group identical text** toggle: collapse executions whose text is identical. The group shows
   the latest run, the number of runs, and all runs when expanded.
10. **Context menu** on entries: Open, Insert, Copy, Copy with header (server/db/time as a
    comment), Star, Delete, Find entries for this database.
11. **Re-open a closed, unsaved tab:** list the documents whose last execution came from a
    tab that no longer exists. This is limited to executed text, never unsaved edits.

### Phase 3 — optional

12. **Import from Redgate SQL Prompt**
    (`%LOCALAPPDATA%\Red Gate\SQL Prompt 11\SqlHistory.db`, a SQLite database):
    - A one-time import.
    - It needs a SQLite reader. Use an **out-of-process** helper or a read-only file copy
      parsed with a small reader, to avoid the in-process SQLite conflict from §3.
    - Only if you really want it (D6).
13. **Export** the filtered list to a `.sql` file (one block per entry, with a header comment).

## 5. Architecture

| Piece | Where | Tests |
|---|---|---|
| `HistoryEntry` record, JSONL serializer/deserializer (tolerant of torn lines) | Core/History | ✅ unit |
| `HistoryStore`: append queue, month files, edits sidecar, load index, retention trim | Core/History (file I/O through an interface for tests) | ✅ unit, with a temp folder |
| `HistoryQuery`: prefix parser + filter + sort | Core/History | ✅ unit |
| `HistoryCipher`: AES-256-CBC + HMAC-SHA256 per entry; key via an `IKeyProtector` (DPAPI in Vsix, fake in tests) | Core/History (+ DPAPI adapter in Vsix) | ✅ unit (round-trip, tamper → skipped, wrong key → skipped) |
| Capture hook (extends `ExecutedQueryTextTracker`) and the execution-completed listener | Vsix/History | manual |
| `QueryHistoryToolWindow` + view/view model | Vsix/History | manual |
| Commands, toolbar, options | .vsct, PackageGuids, DataAnalyzerPackage, Options | manual |

## 6. Spike (before Phase 1 UI) — Sonnet, IL + live-safe

- **S-1 Completion signal.** How can we learn when a query execution finished, how long it
  took, whether it errored and how many rows it returned?
  - Candidates: `SqlScriptEditorControl` / `QueryExecutor` events (for example
    `ExecutionCompleted`, `StatusBar` updates), or the results control.
  - Report the public/internal event names and which ones are stable.
  - If nothing is usable, Phase 1 records only the start time and leaves duration and outcome
    empty.
- **S-2 Toolbar.** Find the SQL Editor toolbar's command-group GUID/ID in SQLEditors.dll or
  its `.vsct` resources, so a button can be placed on it. Otherwise, use our own toolbar only.
- **S-3 Reopen with a connection.** How do we open a new query window connected to server X,
  database Y, as login Z, without a stored password?
  - Option A: reuse the `UIConnectionInfo` of an open window or Object Explorer connection with
    the same server and login.
  - Option B: SSMS's connect dialog, pre-filled.
  - Option C: open disconnected.
  - The existing `QueryWindowAccessor` / `CreateNewBlankScript` path is the starting point.
- **S-4 ExecuteCurrentStatement.** Confirm which text actually runs for
  "Execute current statement", and how to get it.

## 7. Team (low token use, same model as the pivot)

| Agent | Model | Owns | Why |
|---|---|---|---|
| **Lead** | Opus (this session) | Plan, frozen interface (§8), reviews, release | Needs the project history |
| **S3 spike** | Sonnet | `docs/query-history-api.md`, OeProbe | Bounded IL lookups, like S1 |
| **H1 history-core** | Sonnet | `Core/History/*` + tests | Fully specified pure logic |
| **H2 history-shell** | Sonnet | `Vsix/History/*`, .vsct/Guids/Package/Options additions | Follows existing tool window and command patterns plus the pitfalls list |
| **D2 docs** | Haiku | README section + VERSION entry | Writing only |

Waves:
1. S3 and H1 in parallel. They work on disjoint files.
2. H2, once the spike answers are in.
3. The lead builds and sends the `.vsix` for a live check (standing rule).
4. D2, then release.

## 8. Frozen interface (to be written by the lead after the spike)

Core `SsmsDataAnalyzer.Core.History`:
- `HistoryEntry`: Id (Guid), StartedUtc, Server, Database, Login, AuthKind, DocumentName,
  Text, TextTruncated, DurationMs?, Outcome?, RowCount?, Starred.
- `IHistoryStore`: `Append`, `SetStarred`, `Delete`, `ClearAll`, `LoadAsync`, `Query`, and a
  `Changed` event.
- `HistoryQuery.Parse(string)` → filter object.
- `SecretRedactor.Redact(text, tokens)`.

Exact signatures get fixed in this section before H1/H2 start.

## 9. Open decisions

| # | Question | Recommendation |
|---|---|---|
| D1 | Storage | **Decided (user, 2026-09-21):** JSON Lines per month + in-memory index — lightweight, no .db file, no SQLite in-process |
| D2 | Default retention | 30 days, starred entries kept forever |
| D3 | Record executions only, or also unsaved edits like Redgate? | Executions only |
| D4 | Group identical texts by default? | Off by default, toggle in the window |
| D5 | Password/secret handling | **Decided (user, 2026-09-23):** encryption at rest is enough. Queries are stored verbatim, passwords included — no redaction, no skipping. Plus excluded-servers list, crypto-shred on Clear all, user-only folder ACL |
| D6 | Import Redgate `SqlHistory.db` | Phase 3, only if you want it |
| D7 | Window style | Dockable, persistent tool window (not a popup) |
