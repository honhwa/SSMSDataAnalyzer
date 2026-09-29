# Source Control Check — development plan

Status: **in development (started 2026-09-28).** Branch `source-control-sync-feature`. Interface
frozen in §13; wave 1 is the spike (§11) plus the Core work. Decided with the user
(2026-09-28): **narrow scope** — a "forgot to commit" alarm — and **read-only**: it tells you
what is missing and never writes to the repository or the database.

## 1. The problem, in the user's words

> I work in SSMS and have the project locally under source control. After a lot of changes in
> SSMS I forget to put some of them in source control.

The databases live as SSDT projects (`*.sqlproj`), one folder per project, one sub-folder per
schema, one per object type:

```
C:\CISA\APPRRR-Agriculture\Database\
  KingICT.Demeter.Database.Finances\
    KingICT.Database.Finances.sqlproj
    ABB\
      Stored Procedures\ABB.ChangeStatus.sql      -> CREATE PROCEDURE [ABB].[ABB.ChangeStatus]
      Tables\ABB.DCCategory.sql
      Views\  Functions\  User Defined Types\
    dbo\Tables\Zasticeni racuni.sql
```

The same database exists on several servers (DEV, TEST, …), all built from the one project.

## 2. What it is — and what it deliberately is not

**It is an alarm:** *"You changed 7 objects in the last week. 3 are not in source control, 1
differs from the file."*

**It is not a schema compare.** The repository already has Visual Studio Schema Compare (`.scmp`
files sit in every project folder). Re-building that would be a losing race. This answers a much
smaller question — *what did I change and forget?* — which is the one that actually bites.

Read-only means:
- No writes to the repository. No files created, changed or added to a `.sqlproj`.
- No writes to any database. Only `SELECT`s on `sys.objects`, `sys.sql_modules` and friends.
- No commits, no pushes, no deploys. The repository carries both TFVC bindings (`*.vssscc`) and
  a `.gitignore`, so nothing may assume which system is in use.

## 3. What the real repository taught us (checked, 2026-09-25)

Looked at `KingICT.Demeter.Database.Finances` before designing anything:

| Finding | Consequence |
|---|---|
| File names are **inconsistent**: `ABB.ChangeStatus.sql` beside `dbo\Tables\Zasticeni racuni.sql` | **File names cannot identify objects.** Each file's contents are parsed to find the object it defines. |
| Object names contain dots: `CREATE PROCEDURE [ABB].[ABB.ChangeStatus]` | Same problem F12 "Script object" already solves; reuse that parsing. |
| The `.sqlproj` lists files: `<Build Include="Xtable\Tables\Correction.sql" />` | The project file is the real inventory. **A file on disk but missing from the `.sqlproj` is silently ignored by SSDT** — it never deploys and never fails. That is its own finding (§6). |
| Files start with a BOM and an author header, then `CREATE PROCEDURE …`, no `GO` | A text comparison would flag every object. Comparison starts at `CREATE` and ignores formatting (§7). |
| 1,329 `.sql` files in one project; ~40 projects | Nothing may script a whole database or re-read every file per check. The repo index is cached (§8). |

## 4. Where changes come from — two sources that cover each other's blind spots

| Source | Tells us | Blind spot |
|---|---|---|
| **Query History** (already recorded, encrypted, local) | Exactly which objects **you** created, altered or dropped, on which server and database, and when | Only what was executed in this SSMS with the extension installed; not query shortcuts |
| **`sys.objects.modify_date`** on the database you are connected to | Every module changed on that server since a date, **by anyone, with any tool** | Only the database you can query right now; says nothing about who |

Query History **nominates**, the server **confirms**. A DDL statement in history may have failed
or been superseded, so the object's current state is always read from the server before any
verdict is given.

`DdlDetector` scans history text with the existing Core `TsqlLexer` for `CREATE`, `ALTER`,
`CREATE OR ALTER` and `DROP` of `PROCEDURE`, `VIEW`, `FUNCTION`, `TRIGGER` and `TABLE`. A script
may hold several; all are found.

## 5. Mapping a database to a project

The mapping key is the **database name**, not the server. The same database on DEV, TEST and
PROD maps to the same project folder, which is exactly "one repo, many environments" without any
per-environment configuration. A per-server override exists for the rare case where two servers
use the same database name for different things.

Stored per user in `%LOCALAPPDATA%\SsmsDataAnalyzer\SourceControlMap.txt`, one mapping per line,
readable and hand-editable:

```
# database[@server] = path to the .sqlproj
AgricultureFinances = C:\CISA\APPRRR-Agriculture\Database\KingICT.Demeter.Database.Finances\KingICT.Database.Finances.sqlproj
AgricultureFarm@SQLTEST7 = C:\CISA\...\KingICT.Database.Farm.sqlproj
```

Set from the panel: an unmapped database shows a **Map to project…** action that opens a file
picker on `*.sqlproj`. There is no automatic guessing: database `AgricultureFinances` is built
from `KingICT.Database.Finances.sqlproj` in folder `KingICT.Demeter.Database.Finances`, and no
rule relates those three names.

## 6. Verdicts

| Status | Meaning | Shown as |
|---|---|---|
| `Matches` | Server definition and repository file say the same thing | hidden by default ("show all") |
| `DiffersFromRepo` | The file exists but the server holds something else | ⚠ with **Compare** |
| `MissingFromRepo` | No file in the project defines this object | ❌ — the headline case |
| `OnDiskNotInProject` | A file defines it, but the `.sqlproj` does not list that file | ❌ — SSDT will silently ignore it |
| `DroppedButInRepo` | History shows a `DROP`, the object is gone from the server, the file is still in the project | ⚠ |
| `NotCompared` | Changed, but we cannot compare it: a table (Phase 1 compares modules only), an encrypted module, a database not mapped, a server with no usable connection | ℹ with the reason |

`NotCompared` still raises the alarm. A table altered in history is reported as *"Table changed —
not compared yet, check `ABB\Tables\ABB.DCCategory.sql` by hand"*. The job is to remind; the
comparison is a bonus.

## 7. "Did it really change?" — the comparison rules

`sys.sql_modules.definition` holds a module's text as the server stores it; the file holds what
SSDT keeps. Both are passed through the same normaliser before comparing. These rules **define**
what counts as a difference, so they are frozen here and each has a unit test:

0. **Line endings are normalised first**, on both inputs and before lexing: `\r\n` → `\n`, then
   any lone `\r` → `\n`. SQL Server stores a module's line endings exactly as the client sent
   them (lead-verified: CRLF kept intact), so the server and the file can differ only in line
   endings, depending on which tool deployed the module. Between tokens that is already harmless;
   this rule exists for **multi-line string literals**, which rule 5 otherwise compares exactly.
1. Everything **before** the first `CREATE`/`ALTER` token is ignored — author headers, BOMs,
   `SET ANSI_NULLS` lines.
2. `CREATE`, `ALTER` and `CREATE OR ALTER` are equivalent.
3. Whitespace and comments (`--` and `/* */`) are ignored.
4. Keywords and identifiers compare **case-insensitively**; `[ABB]`, `"ABB"` and `ABB` are the
   same name.
5. **String literals compare exactly**, case and spaces included — a changed message text is a
   real change.
6. A trailing `GO` is ignored.

Rule 3 means a change that only edits a comment is **not** reported. That is deliberate — the
alternative is an alarm that fires on formatting — and is open decision D3.

## 8. Repository index

Built on demand, cached in memory per `.sqlproj`, rebuilt only for files whose size or
modification time changed:

1. Read the `.sqlproj`, collect every `<Build Include="…"/>`.
2. Parse each `.sql` under the project folder with `ModuleFileParser` to find the one object it
   defines (the first `CREATE`/`ALTER` statement's target).
3. Index `(schema, name, kind) → file`, remembering whether the file is in the Build list.

A file that defines no recognisable object is simply not indexed. Two files defining the same
object is reported, not guessed between.

## 9. Features by phase

### Phase 1 — the read-only alarm (MVP)

1. **Command** "Check source control…" on the Tools menu, `SsmsDataAnalyzer.CheckSourceControl`,
   default shortcut **Ctrl+Alt+Q, C** (applied the usual way: once, never over an existing
   binding).
2. **Panel** (dockable tool window) listing findings: object, kind, database, server, when it
   changed, where the change came from (history / server), status, file.
   - Window filter: Today / Last 7 days / **Last 15 days (default)** / Last 30 days (history
     retention is 30 days).
   - Findings grouped by database; `Matches` hidden unless "Show all" is ticked.
   - The line under the list says what was checked and why anything was skipped — the lesson of
     Query History: **a silent result needs a visible reason**.
3. **Actions, all read-only:** open the repo file in SSMS; **Compare** — server definition vs file
   in the shell's own diff viewer (spike S-1); copy the object name; script the current server
   definition into a new query window (never executed).
4. **Mapping** as in §5.
5. **Connections:** `modify_date` and definitions are read on the connection of the query window
   you are in. For other servers named in history, an open window on that server is reused (the
   existing `QueryHistoryConnectionFinder`); otherwise the finding is `NotCompared — no open
   connection to <server>`. No password is ever stored or asked for.

### Phase 2 — later, only if Phase 1 earns it

- A reminder: an InfoBar or a status-bar count when uncommitted changes exist.
- The multi-environment view: one object across DEV/TEST/PROD against the repo.
- Table comparison.

### Phase 3 — explicitly deferred

- **Capture** (writing the server definition into the file and adding it to the `.sqlproj`).
  Read-only must earn trust first.

## 10. Architecture

| Piece | Where | Tests |
|---|---|---|
| `DdlDetector` — DDL targets in executed text (lexer-based) | Core/SourceControl | ✅ unit |
| `ModuleFileParser` — which object a `.sql` file defines | Core/SourceControl | ✅ unit, incl. dotted names and BOM |
| `SqlProjectReader` — `Build Include` list from a `.sqlproj` | Core/SourceControl | ✅ unit |
| `RepoIndex` — `(schema, name, kind) → file`, in-project flag | Core/SourceControl | ✅ unit |
| `ModuleDefinitionNormalizer` / comparer — the §7 rules | Core/SourceControl | ✅ unit, **one per rule** |
| `SourceControlMap` — the §5 file format | Core/SourceControl | ✅ unit |
| `SyncChecker` — candidates + server state + index → findings | Core/SourceControl | ✅ unit |
| Server reads (`modify_date`, `sql_modules`), connection reuse | Vsix/SourceControl | manual + local test DB |
| Panel, command, mapping picker, diff action | Vsix/SourceControl | manual |

Core stays `netstandard2.0` with no new packages, reusing `TsqlLexer` and `SqlMultipartName`.
ScriptDom is **not** used here: it is host-owned and Vsix-only, and the lexer is enough for these
bounded patterns — which keeps every rule unit-testable.

## 11. Spike (before any UI) — Sonnet, read-only

- **S-1 Diff viewer.** Is `IVsDifferenceService` present and usable in SSMS 22 to show two texts
  side by side? If not: open both in query windows, or a simple built-in view.
- **S-2 `modify_date`.** Which operations move it for a module (`ALTER`, `CREATE OR ALTER`,
  `sp_refreshsqlmodule`, `GRANT`, `sp_rename`)? A `GRANT` moving it would create false alarms.
- **S-3 `sql_modules.definition`.** Does it keep comments written *before* `CREATE` in the same
  batch? Line endings? This decides whether §7 rule 1 is enough on the server side.
- **S-4 Encrypted and CLR modules.** Confirm `definition` is `NULL` (→ `NotCompared`), not an
  error.
- **S-5 Index cost.** Time to lex all 1,329 files of the Finances project. Decides whether the
  first check needs a progress bar.

S-2 to S-4 need modules to be created and altered, so they run in **`tempdb`** on the local
instance — scratch objects that vanish on restart. Never on the seeded test database (read-only
by project rule), never on any user database.

## 12. Team (low token use, same shape as before)

| Agent | Model | Owns |
|---|---|---|
| **Lead** | this session | Plan, frozen interface (§13), review, release |
| **S4 spike** | Sonnet | `docs/source-control-api.md` |
| **C1 core** | Sonnet | `Core/SourceControl/*` + tests |
| **V1 shell** | Sonnet | `Vsix/SourceControl/*`, `.vsct`/Guids/Package additions |

Waves: S4 + C1 in parallel; V1 after the spike; lead verifies and builds the `.vsix` before
anything is called working. The Query History rules apply in full: nothing reads options or
touches DTE off the UI thread; test doubles fail where the real thing would; diagnostics count
"done", not "accepted".

## 13. Frozen interface (frozen by the lead, 2026-09-28)

C1 owns everything under `src/SsmsDataAnalyzer.Core/SourceControl/`. V1 codes against these
names and may not change them. Namespace `SsmsDataAnalyzer.Core.SourceControl`. Core stays
`netstandard2.0`: no new packages, no ScriptDom, no file or database I/O — the Vsix reads files
and servers and hands Core plain strings. Reuse `SsmsDataAnalyzer.Core.ScriptObject.TsqlLexer`.
Nothing here throws on any input text; bad input produces "not recognised", never an exception.

### 13.1 Identity

```csharp
public enum DbObjectKind { Procedure, View, Function, Trigger, Table }

public sealed class ModuleRef : IEquatable<ModuleRef>
{
    public ModuleRef(string schema, string name, DbObjectKind kind);
    public string       Schema { get; }   // null = the text did not say (unqualified)
    public string       Name   { get; }   // e.g. "ABB.ChangeStatus" — dots are part of the name
    public DbObjectKind Kind   { get; }
    public ModuleRef WithSchema(string schema);
    public override string ToString();    // "[ABB].[ABB.ChangeStatus]", ']' doubled inside
}
```

**Equality is `Schema` + `Name`, both `OrdinalIgnoreCase`; `Kind` is NOT part of it.**
Procedures, views, functions, tables and triggers share one namespace per schema in
`sys.objects`, so the name alone identifies the object — and a history entry saying `ALTER
FUNCTION` must still find a file that says `CREATE FUNCTION`. An unqualified ref (`Schema ==
null`) never equals a qualified one: Core never assumes `dbo`. The Vsix resolves the schema on
the server (`OBJECT_SCHEMA_NAME(OBJECT_ID(...))` in that database) and calls `WithSchema`.

### 13.2 What executed text changed — `DdlDetector`

```csharp
public enum DdlAction { Create, Alter, CreateOrAlter, Drop }

public sealed class DdlStatement
{
    public DdlAction Action { get; }
    public ModuleRef Target { get; }
    public string    Database { get; }   // lead amendment, see below; null = not named
}

public static class DdlDetector
{
    public static IReadOnlyList<DdlStatement> Find(string sqlText);   // in order of appearance
}
```

Recognises `CREATE | ALTER | CREATE OR ALTER | DROP` of `PROC | PROCEDURE | VIEW | FUNCTION |
TRIGGER | TABLE`, including `DROP … IF EXISTS` and a comma-separated `DROP` list (one statement
per target). Skipped, not reported: `#temp` / `##global` tables, `@table` variables, anything
inside a string or a comment (the lexer already says so — so `EXEC('ALTER PROCEDURE …')` is
knowingly missed), and `DROP`/`CREATE` of any other object type.

**Lead amendment (2026-09-28, on review of C1):** `DdlStatement.Database` carries the database a
three-part name states (`DROP TABLE OtherDb.dbo.Orders` → `OtherDb`). C1 had kept only the last
two parts, so such a change was attributed to whichever database the query window was using — a
false `DroppedButInRepo` against the wrong project. The Vsix uses `Database` over the session
database whenever it is set. Four-part (linked-server) names are skipped: no DDL can target one
and we could not attribute it. Covered by three tests; the key one fails with the fix removed.

### 13.3 What a file defines — `ModuleFileParser`

```csharp
public static class ModuleFileParser
{
    public static ModuleRef TryIdentify(string fileText);   // null = defines nothing we recognise
}
```

The target of the **first** `CREATE`/`ALTER` of a §13.2 kind in the file. Must handle a leading
BOM, an author header comment, `SET` lines before `CREATE`, and bracketed names containing dots.

**Lead amendment (2026-09-29, field report): a file can define several objects.**
`ModuleFileParser.IdentifyAll(fileText)` returns every object in the file, one per `GO` batch,
each with its **own batch text**, and `RepoIndex` indexes all of them. SSDT keeps DML triggers in
their table's file (`DebtLedger\Tables\Debt.sql` = table, indexes, two triggers). Measured on the
real Finances project: the first-object-only rule found **0 of its 22 triggers**, so every changed
trigger was reported "missing from repo"; procedures, functions and views were unaffected
(737 / 58 / 27 either way). Each object compares against its own batch, so a trigger is never
compared with its whole table file. `TryIdentify` keeps its meaning (the first object).

### 13.4 What the project includes — `SqlProjectReader`

```csharp
public sealed class SqlProjectFiles
{
    public bool IncludesAreImplicit { get; }       // SDK-style or wildcard project: every .sql counts
    public bool Contains(string relativePath);     // '/' and '\' equivalent, OrdinalIgnoreCase
}

public static class SqlProjectReader
{
    public static SqlProjectFiles Read(string sqlprojXml);   // unreadable XML -> implicit, see below
}
```

Explicit `<Build Include="…"/>` items (the classic SSDT format this repository uses) are the
inventory. A project with **no** `Build` items, or any `Include` containing `*`, is treated as
implicit: every `.sql` counts as included, so `OnDiskNotInProject` can never be reported for it.
**Unreadable XML is also treated as implicit** — reporting 1,329 files as "not in project" on a
parse error would be a false alarm of exactly the kind this feature must not produce.

### 13.5 The index — `RepoIndex`

```csharp
public sealed class RepoFile
{
    public RepoFile(string relativePath, string text);
    public string RelativePath { get; }
    public string Text { get; }
}

public sealed class RepoEntry
{
    public string    RelativePath { get; }
    public string    Text { get; }
    public ModuleRef Module { get; }
    public bool      InProject { get; }
}

public sealed class RepoIndex
{
    public static RepoIndex Build(IEnumerable<RepoFile> files, SqlProjectFiles project);
    public IReadOnlyList<RepoEntry> Find(ModuleRef module);   // 0 = not in repo, >1 = defined twice
    public int FileCount { get; }
    public int UnrecognisedFileCount { get; }                  // .sql files defining nothing we know
}
```

### 13.6 "Did it really change?" — `ModuleDefinitionNormalizer`

```csharp
public static class ModuleDefinitionNormalizer
{
    public static string Normalize(string definition);   // canonical form of the §7 rules
    public static bool   AreEquivalent(string a, string b);
}
```

Implements §7 exactly (rules 0–6), **one unit test per rule**. The canonical form is a token sequence joined
with a separator that cannot occur in T-SQL text (`\u0001`), so that `[ABB].[ABB.ChangeStatus]`
(identifier, dot, identifier) and `ABB.ABB.ChangeStatus` (three identifiers) stay different.
Identifiers and keywords are compared lower-cased (invariant); strings, numbers and punctuation
exactly. Known and accepted: in a case-sensitive database a case-only rename is not reported.

### 13.7 The verdict — `SyncChecker`

```csharp
public enum ChangeSource { QueryHistory, ServerModifyDate, Both }

public enum SyncStatus
{
    Matches, DiffersFromRepo, MissingFromRepo, OnDiskNotInProject, DroppedButInRepo, NotCompared
}

public sealed class ChangeCandidate
{
    public ChangeCandidate(string server, string database, ModuleRef module,
                           DateTime changedUtc, ChangeSource source, DdlAction? lastAction);
    public string Server { get; }
    public string Database { get; }
    public ModuleRef Module { get; }
    public DateTime ChangedUtc { get; }
    public ChangeSource Source { get; }
    public DdlAction? LastAction { get; }        // from history; null for a server-only candidate
}

public static class ChangeCandidates
{
    // One candidate per (server, database, module); server and database OrdinalIgnoreCase.
    // Present in both inputs -> Source = Both, ChangedUtc = the later, LastAction from history.
    public static IReadOnlyList<ChangeCandidate> Merge(
        IEnumerable<ChangeCandidate> fromHistory, IEnumerable<ChangeCandidate> fromServer);
}

public sealed class ServerObjectState
{
    public ServerObjectState(bool exists, DbObjectKind? kind, string definition);
    public bool Exists { get; }
    public DbObjectKind? Kind { get; }
    public string Definition { get; }            // null = table, encrypted, CLR, or unreadable
}

public sealed class SyncFinding
{
    public ChangeCandidate Candidate { get; }
    public SyncStatus Status { get; }
    public string RelativePath { get; }          // null when no file is involved
    public string Reason { get; }                // always set; object and file names only
}

public static class SyncChecker
{
    /// <param name="server">null = could not reach that server</param>
    /// <param name="repo">null = that database is not mapped to a project</param>
    public static SyncFinding Check(ChangeCandidate candidate, ServerObjectState server, RepoIndex repo);
}
```

Decision order — **frozen, and each branch has a test**:

1. `repo == null` → `NotCompared` ("database is not mapped to a project").
2. `candidate.Module.Schema == null` → `NotCompared` ("could not tell which schema").
3. `server == null` → `NotCompared` ("no open connection to <server>").
4. Object **gone** from the server: a file defines it → `DroppedButInRepo`; no file →
   `Matches` (dropped and gone from the repo too: consistent).
5. `repo.Find` returns more than one file → `NotCompared` ("defined in N files: …").
6. No file → `MissingFromRepo`.
7. The one file is not in the project → `OnDiskNotInProject`. **Checked before comparing**: a
   file SSDT ignores is the bigger problem, and comparing it would suggest it matters.
8. It is a table → `NotCompared` ("tables are not compared yet — check <file> by hand").
9. `Definition == null` → `NotCompared` ("definition not readable: encrypted or CLR").
10. `AreEquivalent` → `Matches`, otherwise `DiffersFromRepo`.

### 13.8 The map — `SourceControlMap`

```csharp
public sealed class SourceControlMap
{
    public static SourceControlMap Parse(string text);           // never throws
    public string FindProject(string server, string database);    // null = not mapped
    public SourceControlMap With(string database, string server, string projectPath); // server null = any
    public string Serialize();                                    // keeps comments and line order
    public IReadOnlyList<string> Problems { get; }                // "line 4: no '='", …
}
```

Format as §5: `database[@server] = path`. `#` starts a comment; blank lines ignored; the key is
split on the **first** `=` and then on the **last** `@`; keys compare `OrdinalIgnoreCase`. A
server-specific line wins over a database-only one. `With` replaces the matching line in place
or appends one; `Serialize` round-trips everything else untouched.

### 13.9 IDs (lead-assigned — do not change)

| Name | Value |
|---|---|
| `PackageIds.CheckSourceControlCommandId` | `0x0500` |
| `PackageGuids.SourceControlToolWindowPersistenceGuid` | `c3e8a1f4-6b2d-4e97-8a53-0f1d9b7e2c46` |
| CanonicalName | `SsmsDataAnalyzer.CheckSourceControl` |
| Default shortcut | `Global::Ctrl+Alt+Q, C` — see the correction below |

**Lead correction (2026-09-29): do NOT bump `ShortcutSetVersion`.** A set-version bump re-runs
the whole default list, and a command whose default the user had *deleted* has no binding, so it
would be given that default again — breaking the promise that a deleted default stays deleted.
`DefaultShortcuts` changes to track **which commands have been offered**, one by one: each
command gets exactly one chance at its default, ever, and new commands in later versions get
theirs. Migration: the v0.25 stamp `DefaultShortcutsApplied = "1"` means the ten original
commands have already been offered. This fix ships with this feature because this is the first
release that adds a command to the list.

## 14. Open decisions

| # | Question | Recommendation |
|---|---|---|
| D1 | Scope | **Decided (user, 2026-09-28):** the narrow "forgot to commit" alarm |
| D2 | Write-back | **Decided (user, 2026-09-28):** read-only first; capture is Phase 3 |
| D3 | Report comment-only changes? | No — otherwise formatting noise buries real changes. Revisit if a missed comment ever matters |
| D4 | Default window | **Decided (user, 2026-09-28):** last 15 days |
| D5 | Mapping stored where | Plain text per user (§5): no query text, no secrets, hand-editable |
| D6 | Tables in Phase 1 | Detected from history and reported as `NotCompared`; compared in Phase 2 |
