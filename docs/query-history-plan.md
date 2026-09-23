# Query History — development plan

Status: **Phase 1 shipped and confirmed working in SSMS (v0.19.4, 2026-09-23).** The interface
is in §8. Phase 2 (§4) has not been started. Open decisions are in §9.

**Live-fix log — what Phase 1 cost after "it builds":** four releases, none of which could record
a single query, and none of which any unit test could have caught.

| Build | Symptom | Cause |
|---|---|---|
| 0.19.0 | No folder at all | `HistoryKeyStore` wrote `key.bin` before anything created the directory. The in-memory test fake treated paths as opaque strings, so 353 green tests never saw it. The fake now throws the way Windows does, and `RealDiskHistoryTests` runs the real path against real files. |
| 0.19.1 | Folder and key, no entries | Not diagnosable from outside: the only signal went to an ActivityLog SSMS does not write unless started with `/log`. |
| 0.19.2 | "5 executions seen, 5 recorded", list empty | "Recorded" meant *handed to the writer queue*, not written — the diagnostics themselves made a broken store look healthy. Queued and written are now counted separately. |
| 0.19.3 | "3 queued, 0 written. History file: could not be opened (COMException)" | **The real bug.** `LoadCore` runs on a background thread and read the retention option through `GetDialogPage`, which is UI-thread-only. It threw *after* `key.bin` was written, and `_initFailed` then stuck for the session. Options are now snapshotted on the UI thread. |

**Rules taken from this, for anything that writes to disk from a VS package:**
1. **Never read an option (or any `GetDialogPage`/shell service) off the UI thread.** Snapshot it
   on the UI thread and hand the value to the background work.
2. **A test double must not be more permissive than the real thing.** A fake with no directory
   semantics hides exactly the bug it exists to catch.
3. **Diagnostics must distinguish "accepted" from "done".** Counting the queue is not counting
   the disk.
4. **A silent feature needs a visible reason.** The window now states why it is empty; that
   turned two rounds of guessing into two exact diagnoses.
5. **Diagnostics must never throw.** `OeDiagnostics` reaches a shell service, and an exception
   escaping the writer thread's catch block would have killed recording outright.

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

### 6.1 Spike answers and the lead's decisions (2026-09-23)

Full report: [docs/query-history-api.md](query-history-api.md). Summary and what we do about it:

| # | Answer | Confidence | Decision |
|---|---|---|---|
| S-1 | A real completion signal exists (`ScriptExecutionCompleted` with an outcome enum, and a batch row count), but only through two hops of reflection onto private fields of internal types. DTE `AfterExecute` is proven useless: IL shows execution is started asynchronously, so it fires before any result exists. | MEDIUM | **Optional, feature-detected.** Phase 1 records the entry at execute time with duration/outcome empty, and a separate guarded listener fills them in if — and only if — the reflection succeeds on this build. One failure disables it for the session, logs a single value-free line, and nothing else changes. No feature may depend on it. |
| S-2 | The SQL Editor toolbar's group ID lives in the compressed `CFCT` menu blob and cannot be read, the same wall the results-grid spike hit. | LOW for the caption | **Our own toolbar only** (View → Toolbars → SSMS Data Analyzer), which is the supported route and cannot break. The `CommandBars["SQL Editor"]` by-name attempt is Phase 2 and strictly best-effort. |
| S-3 | Already solved: `QueryWindowAccessor.TryOpenAsync` / `TryOpenNewQueryWindowAsync` is exactly the recommended Option A, shipped since v0.5.0. | HIGH | Reuse it as-is. Only a "is there already an open window on this server and login" helper is new. |
| S-4 | Plain `Query.Execute` is confirmed to run selection-or-whole-document, as the existing tracker assumes. For `ExecuteCurrentStatement` no statement-splitting code path could be found, and the search was inconclusive. | HIGH / LOW | **Don't guess.** Capture what the tracker already captures. When the command was `ExecuteCurrentStatement` and we cannot prove which statement ran, the entry is still recorded — it may hold more text than actually ran, which is honest and harmless for a history. Revisit after a live check. |

The two LOW/MEDIUM answers are both things this environment cannot verify without a running
SSMS, so the standing rule applies with extra force: build the `.vsix` and check live before
anything is built on top of them.

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

## 8. Frozen interface (frozen by the lead, 2026-09-23)

H1 owns everything under `src/SsmsDataAnalyzer.Core/History/`. H2 codes against these names and
may not change them. Core is `netstandard2.0` and references nothing but
`Microsoft.Data.SqlClient`, so: **no Json.NET, no SQLite, no `ProtectedData`** — the JSON writer
is hand-rolled for this one fixed schema, and DPAPI lives behind `IKeyProtector`, implemented in
the Vsix.

### 8.1 Model

```csharp
namespace SsmsDataAnalyzer.Core.History
{
    public enum HistoryAuthKind { Unknown = 0, Windows = 1, SqlLogin = 2, Entra = 3 }
    public enum HistoryOutcome  { Unknown = 0, Success = 1, Error = 2, Cancelled = 3 }

    public sealed class HistoryEntry
    {
        public Guid            Id            { get; set; }   // required, unique
        public DateTime        StartedUtc    { get; set; }   // always UTC, Kind=Utc
        public string          Server        { get; set; }
        public string          Database      { get; set; }
        public string          Login         { get; set; }   // never a password
        public HistoryAuthKind AuthKind      { get; set; }
        public string          DocumentName  { get; set; }
        public string          Text          { get; set; }   // verbatim, secrets included
        public bool            TextTruncated { get; set; }
        public int?            DurationMs    { get; set; }
        public HistoryOutcome? Outcome       { get; set; }
        public long?           RowCount      { get; set; }
        public bool            Starred       { get; set; }   // from the edits sidecar, not the month file
    }
}
```

`Starred` is never written to a month file; month files are append-only and immutable.

### 8.2 Serialization — `HistoryJson`

```csharp
public static class HistoryJson
{
    public const int MaxTextBytes = 1024 * 1024;              // 1 MB, then truncate + flag

    public static string Write(HistoryEntry entry);           // one line, no newline, no BOM
    public static bool   TryParse(string line, out HistoryEntry entry);  // false = skip the line
    public static string WriteEdit(HistoryEdit edit);
    public static bool   TryParseEdit(string line, out HistoryEdit edit);
}

public sealed class HistoryEdit
{
    public Guid Id      { get; set; }
    public bool? Starred { get; set; }   // null = not changed by this edit
    public bool Deleted  { get; set; }
}
```

`TryParse` returns false — never throws — for a torn, empty or unknown-shape line. Unknown JSON
fields are ignored, so a newer build's files stay loadable. Escaping must round-trip `"` `\`
control characters, newlines/tabs and non-BMP characters; there is a unit test per case.

### 8.3 Encryption — `HistoryCipher`

```csharp
public interface IKeyProtector       // DPAPI in the Vsix, a fake in tests
{
    byte[] Protect(byte[] plaintextKey);
    byte[] Unprotect(byte[] protectedKey);   // throws if it can't
}

public sealed class HistoryCipher
{
    public const string LinePrefix = "v1:";

    public HistoryCipher(byte[] key);                  // exactly 64 bytes: 32 AES + 32 HMAC
    public static byte[] NewKey();                     // RandomNumberGenerator
    public string EncryptLine(string plaintext);       // "v1:" + base64(IV | ciphertext | MAC)
    public bool   TryDecryptLine(string line, out string plaintext);  // false = skip
}
```

AES-256-CBC, PKCS7, a fresh random 16-byte IV per line; HMAC-SHA256 over `IV | ciphertext`,
compared in constant time. Encrypt-then-MAC: a line whose MAC fails is skipped, never decrypted.
`TryDecryptLine` never throws.

`HistoryKeyStore` (Core) reads/creates `key.bin` through `IHistoryFileSystem` + `IKeyProtector`:
`byte[] LoadOrCreate()`, `void Destroy()` (crypto-shred, called first by Clear all).

### 8.4 Files — `IHistoryFileSystem`

```csharp
public interface IHistoryFileSystem
{
    void   EnsureDirectory(string path);           // + the user-only ACL in the Vsix impl
    bool   FileExists(string path);
    string[] ReadAllLines(string path);            // empty array when missing
    void   AppendLines(string path, IEnumerable<string> lines);
    byte[] ReadAllBytes(string path);
    void   WriteAllBytes(string path, byte[] bytes);
    void   DeleteFile(string path);
    string[] ListFiles(string directory, string searchPattern);
    void   ReplaceFile(string sourcePath, string destinationPath);   // trim writes a new file, then swaps
}
```

### 8.5 Store — `HistoryStore`

```csharp
public sealed class HistoryStore
{
    public HistoryStore(IHistoryFileSystem fileSystem, string rootDirectory,
                        HistoryCipher cipher, Func<DateTime> utcNow);

    public void Load();                                  // month files + edits.jsonl -> in-memory index
    public void Append(HistoryEntry entry);              // writes <yyyy-MM>.jsonl; caller is the writer thread
    public void SetStarred(Guid id, bool starred);       // appends to edits.jsonl
    public void Delete(Guid id);                         // appends to edits.jsonl (tombstone)
    public void ClearAll();                              // key destroyed by the caller first, then every file
    public int  Trim(int retentionDays);                 // returns entries removed; starred are kept
    public IReadOnlyList<HistoryEntry> Query(HistoryFilter filter, int max);  // newest first
    public event EventHandler Changed;
}
```

Every public member is thread-safe (one lock). The store does no threading of its own — the Vsix
owns the background writer queue and calls `Append` from it.

### 8.6 Search — `HistoryFilter`

```csharp
public enum HistoryDateRange { All = 0, Today = 1, Last7Days = 2, Last30Days = 3 }

public sealed class HistoryFilter
{
    public static HistoryFilter Parse(string searchText);   // never throws
    public HistoryDateRange DateRange { get; set; }
    public bool GroupIdenticalText { get; set; }
    public bool Matches(HistoryEntry entry, DateTime utcNow);
}
```

Supported prefixes: `sql:`, `server:`, `database:` / `db:`, `starred:true|false`,
`error:true|false`. Bare words match inside the query text, case-insensitively; `"quoted
phrases"` match as one term; several terms are AND.

### 8.6a Phase 2 additions (frozen 2026-09-23)

`HistoryFilter` gains two prefixes and one piece of caller-supplied context:

```csharp
public sealed class HistoryFilter
{
    // ... 8.6 unchanged ...

    /// <summary>Names of the query documents currently open in SSMS. Supplied by the Vsix,
    /// read on the UI thread and handed in -- Core never touches DTE. Null means "unknown",
    /// and a closed:/open: term then matches nothing rather than guessing.</summary>
    public ISet<string> OpenDocumentNames { get; set; }
}
```

- `doc:<text>` matches `HistoryEntry.DocumentName`, case-insensitively, like `server:`.
- `closed:true` matches entries whose `DocumentName` is **not** in `OpenDocumentNames`
  (`closed:false` the reverse). This is plan item 11: finding what you ran in a tab you have
  since closed. Comparison is `OrdinalIgnoreCase`.
- `GroupIdenticalText` (already in 8.6) collapses on exact `Text`, keeping the newest of each
  group. `HistoryStore.Query` applies it after filtering and before `max`, which is what the
  existing implementation already does -- Phase 2 only exposes it in the UI.

Grouping needs one new piece of data so the UI can say "x3":

```csharp
public sealed class HistoryEntry
{
    // ... 8.1 unchanged ...

    /// <summary>How many executions this row stands for once GroupIdenticalText collapsed
    /// them; 1 when grouping is off. Never serialized -- it is a property of a query result,
    /// not of a stored entry.</summary>
    public int GroupCount { get; set; }
}
```

`Query` sets `GroupCount` on the entries it returns (1 when grouping is off) and must not
mutate the stored index entries -- return copies when grouping, or the count leaks into
later ungrouped queries.

### 8.7 IDs and GUIDs (lead-assigned — do not change)

| Name | Value |
|---|---|
| `PackageIds.QueryHistoryCommandId` | `0x0400` |
| `PackageIds.QueryHistoryToolbar` | `0x1060` |
| `PackageIds.QueryHistoryToolbarGroup` | `0x1061` |
| `PackageGuids.QueryHistoryToolWindowPersistenceGuid` | `5f3a1c72-8d46-4b09-a2e7-6c81d4f9b3e5` |
| Command CanonicalName (for a shortcut) | `SsmsDataAnalyzer.QueryHistory` |
| Options | `EnableQueryHistory` (bool, true), `QueryHistoryExcludedServers` (string, empty), `QueryHistoryRetentionDays` (int, 30) |

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
