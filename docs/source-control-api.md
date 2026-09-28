# Source Control Check — API spike report (Agent S4)

Target verified: SSMS 22 installed at
`C:\Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE`. S-1 is static
evidence read from the shipped `.pkgdef` registration files and DLL metadata strings — **SSMS was
never launched**, there is no tool in this environment that can drive the SSMS desktop app. S-2
through S-4 are live evidence: a local SQL Server instance was reached and scratch objects were
created, altered and dropped **only in `tempdb`**, then dropped at the end of the script. Nothing
was created, altered or read in any user database or in `SsmsDataAnalyzerTest`. S-5 is a live
timing run of the project's own `TsqlLexer` (`spikes/SourceControlProbe`) against the real
project at `C:\CISA\APPRRR-Agriculture\Database\KingICT.Demeter.Database.Finances` — **read-only**,
nothing under `C:\CISA` was written, renamed or touched; only file names were enumerated, never
printed.

---

## S-1 — Diff viewer (`IVsDifferenceService` / `SVsDifferenceService`)

> **Answer: present and actively used in this SSMS 22 install — not a leftover registration.**
> **Confidence: HIGH that the service is registered and the interface ships; MEDIUM on “usable
> from a third-party VSIX exactly like Visual Studio’s own docs describe”** — SSMS was never
> actually launched, so no call was made through it.

### 1.1 Service registration — hard evidence, read from the `.pkgdef`

`CommonExtensions\Microsoft\Editor\Microsoft.VisualStudio.Editor.pkgdef`:

```
[$RootKey$\Services\{77115e75-ef9e-4f30-92f2-3fe78bcaf6cf}]
@="{e269b994-ef71-4ce0-8bcd-581c217372e8}"
"Name"="SVsDifferenceService"
```

and the owning package:

```
[$RootKey$\Packages\{e269b994-ef71-4ce0-8bcd-581c217372e8}]
@="Microsoft.VisualStudio.Editor.Implementation.EditorPackage"
"Assembly"="Microsoft.VisualStudio.Editor.Implementation,version=18.0.0.0,publicKeyToken=b03f5f7f11d50a3a,culture=neutral"
"AllowsBackgroundLoad"=dword:00000001
```

So `SVsDifferenceService` is registered against SSMS's own `EditorPackage`
(`AllowsBackgroundLoad=1` — the package that owns the service loads without blocking the UI
thread), and the same `.pkgdef` registers a built-in "Diff Editor" editor factory
(`{79d52ddf-52bc-43f1-9663-b3e85cdca912}`) and two UI-context rules
(`DiffSingleFileSelectedUIContext`, `DiffDualFilesSelectedUIContext`) plus a `/Diff` command-line
switch — this is a fully wired-up feature, not a stub registration.

### 1.2 The interface itself, and who else in this install uses it

`IVsDifferenceService` (the string, confirmed present as metadata in the assembly) lives in
`PublicAssemblies\Microsoft.VisualStudio.Shell.Interop.11.0.dll` — a **PublicAssemblies** DLL,
i.e. one meant to be referenced by third-party packages, same tier as the assemblies the Query
History and OE spikes already used. A full-install scan for the literal string
`IVsDifferenceService` also hit it in:

- `Microsoft.VisualStudio.Editor.Implementation.dll` (the package above — the implementer)
- `Microsoft.VisualStudio.Interop.dll` (`PublicAssemblies` and both Remote Debugger copies)
- `CommonExtensions\Microsoft\TeamFoundation\Team Explorer\Microsoft.TeamFoundation.Git.Provider.dll`
- `CommonExtensions\Microsoft\TeamFoundation\Team Explorer\Microsoft.VisualStudio.TeamFoundation.VersionControl.dll`
- `CommonExtensions\Microsoft\TeamFoundation\Team Explorer\Microsoft.TeamFoundation.CodeReview.Components.dll`
- `Extensions\Microsoft\Copilot\Microsoft.VisualStudio.Copilot.UI.dll`
- `Automation\msenv110p.dll`

The Git provider, version control and Copilot UI assemblies are real, active parts of this SSMS
install (SSMS 22 ships basic Team Explorer/Git integration) — them consuming
`IVsDifferenceService` for their own diff views is strong circumstantial evidence the service is
live and exercised by code paths that actually run in this product, not dead surface kept only
for API compatibility.

### 1.3 What could not be established

Nobody instantiated `SVsDifferenceService` inside a running SSMS process and called
`OpenComparisonWindow`/`OpenComparisonWindow2` — there is no way to launch and drive the SSMS
desktop app from this environment, so "does `Package.GetGlobalService(typeof(SVsDifferenceService))`
actually return a working comparer inside *our own* VSIX's service provider" is inferred, not
observed. The usual failure mode for this kind of service is a version mismatch on the
`Microsoft.VisualStudio.Shell.Interop.*` assembly the VSIX references vs. the one SSMS loads —
worth pinning to `11.0` explicitly given that's the version that carries the interface here.

### 1.4 Recommended path

Primary: `IVsDifferenceService.OpenComparisonWindow2(leftFile, rightFile, caption, tooltip,
leftLabel, rightLabel, inlineLabel, roles, grfDiffOptions)` from `Package.GetGlobalService`,
called on the UI thread (per the VS options lesson already in project memory — never assume a
shell/DTE-adjacent call is safe off it). Both sides need to be either real temp files or backed
by `IVsTextBufferProvider`; the simplest correct implementation for two in-memory strings (server
definition vs. repo file text) is to write the server side to a temp `.sql` file next to the real
repo file's path pattern (never inside `C:\CISA` itself — a scratch temp folder) and diff that
against the actual repo file, so **Compare** never needs to fabricate a second in-memory buffer
type.

**Fallback if it turns out unusable from a third-party package at runtime** (the MEDIUM-confidence
gap above): open two ordinary query windows side by side, one titled `<object> — server` and one
`<object> — repo file`, both non-executable/read-only content. Strictly worse (no inline diff
highlighting) but needs nothing beyond APIs this project already uses (`DTE`, new query window),
so it is a safe two-hour fallback if S-1's MEDIUM-confidence part turns out wrong.

---

## S-2 — What moves `sys.objects.modify_date` for a module

> **Answer: `ALTER`, `CREATE OR ALTER` and `sp_refreshsqlmodule` all move it.
> `GRANT`, `DENY`, adding an extended property, and (mid-name) `sp_rename` do NOT move it for
> GRANT/DENY/extended-property — but `sp_rename` DOES move it.**
> **Confidence: HIGH — live evidence, run once against `tempdb` on the local instance
> (`ABYLAP245`, SQL Server 2022 16.0.1190.2 Developer Edition).**

Single scratch procedure `tempdb.dbo.SCProbe_P1`, `WAITFOR DELAY '00:00:02'` between steps so
`modify_date`'s change is unambiguous, definition body **unchanged** at every step:

| Step | Operation | `modify_date` |
|---|---|---|
| 1 | `CREATE PROCEDURE` | `15:07:43.883` (== `create_date`) |
| 2 | `ALTER PROCEDURE` (identical body) | `15:07:45.910` — **moved** |
| 3 | `CREATE OR ALTER` (identical body) | `15:07:47.940` — **moved** |
| 4 | `EXEC sp_refreshsqlmodule` | `15:07:50.030` — **moved** |
| 5 | `GRANT EXECUTE ... TO PUBLIC` | `15:07:50.030` — **unchanged** |
| 6 | `DENY EXECUTE ... TO PUBLIC` | `15:07:50.030` — **unchanged** |
| 7 | `sp_addextendedproperty` (`MS_Description`) | `15:07:50.030` — **unchanged** |
| 8 | `sp_rename` (object renamed, renamed back) | `15:07:58.557` — **moved** |

`create_date` (`15:07:43.883`) never changed across any step.

**This is the single most important false-alarm risk in the plan, and it is worse than plan §7
assumed:** `sp_refreshsqlmodule` — a pure metadata-refresh call that changes nothing about a
module's text — moves `modify_date` exactly like a real edit. Anyone (SSDT deployment, a
maintenance script, another developer's tooling) running `sp_refreshsqlmodule` against a module
would make `SyncChecker` nominate it as changed even though nothing changed. Since
`sys.sql_modules.definition` is unaffected (confirmed separately — the text is byte-identical
before and after step 4), **the existing §13.7 pipeline already self-corrects**: the candidate
gets nominated, but step 10 (`AreEquivalent`) compares the unchanged definition against the repo
file and correctly reports `Matches`. So this is not a design gap, but it is worth stating
explicitly since Query History's `DdlDetector` (§13.2) does not recognise `sp_refreshsqlmodule` as
a DDL statement at all — a `modify_date`-only nomination (no history match) is exactly the
`ChangeSource.ServerModifyDate`-only path in `ChangeCandidates.Merge`, and it will produce a
`NotCompared`/`Matches` verdict rather than a false positive, provided the comparison step
actually runs. Recommend the plan/tests explicitly cover this case (`modify_date` moved,
definition unchanged → `Matches`), since it is the one operation most likely to be run by
automated tooling outside the user's own hands.

`sp_rename` also moves `modify_date` even though — for a plain rename with no body change — the
object still exists under a new name; §13.7's decision tree doesn't have a branch for "renamed":
a rename looks exactly like "old name dropped, new name created" to a `(schema, name)`-keyed
scan, which the plan's own equality rule (`ModuleRef` equality is name-based, `Kind` excluded)
already anticipates for cross-kind matches but not for renames — worth a note, out of scope for
this spike to resolve.

**What would raise confidence further:** re-run the same script and confirm the identical GRANT/
DENY/extended-property "unchanged" result on a second instance/edition (only Developer Edition on
one machine was available here); the exact T-SQL is `spikes/../s2s3s4_probe2.sql`-shaped and
reproducible — see script at the end of this section-equivalent in the Core repo history if
needed, or re-derive from the table above (each row is one statement against a throwaway
`tempdb` procedure).

---

## S-3 — What `sys.sql_modules.definition` keeps

> **Answer: comments written before `CREATE`/`ALTER` in the same batch ARE kept; a `SET`
> statement before `CREATE`/`ALTER` in the same batch is not merely stripped — SQL Server refuses
> to compile it at all (Msg 111). Leading blank lines are kept as `\n`; leading spaces on the
> `CREATE` line itself are NOT kept. Line endings are normalised to bare `\n` (LF) — no `\r`
> survives anywhere in the stored definition, even though the source batch was sent with CRLF.
> **LEAD CORRECTION: the two lines above are wrong. SQL Server stores line endings exactly as sent; the probe went through `sqlcmd`, which re-joins lines with LF. See §3.4.**
> After `CREATE OR ALTER`, the definition's opening keyword is stored as `CREATE` with `OR ALTER`
> replaced by blank space of the same width, not literally re-written as `CREATE OR ALTER`.**
> **Confidence: HIGH — live evidence, byte- and character-position exact (`CHARINDEX`,
> `DATALENGTH`, `CAST(... AS VARBINARY)`), same `tempdb` instance as S-2.**

### 3.1 Comment before `CREATE`, same batch — kept

Batch: `/* author header line one\n   line two */\n   CREATE PROCEDURE dbo.SCProbe_A ...`.
Result: `CHARINDEX('author header', definition) = 4` (found — the comment text is in the stored
definition), `41` characters precede the `CREATE` keyword (the comment plus the leading spaces on
its own following line), `DATALENGTH = 214` bytes / `LEN = 107` characters (`definition` is
`nvarchar(max)`, 2 bytes/char, consistent). **This directly confirms plan §7 rule 1 is
necessary** — the raw server definition really does carry pre-`CREATE` comments, so a comparer
that starts at the first `CREATE`/`ALTER` token (as §13.6 already specifies) is required, not
optional.

### 3.2 `SET` statement before `CREATE`, same batch — not just stripped, illegal

Tried as a literal batch and as a one-string dynamic batch via `EXEC('SET ANSI_NULLS ON; CREATE
PROCEDURE ...')`: both fail identically —

```
Msg 111, Level 15, State 1 ... 'CREATE/ALTER PROCEDURE' must be the first statement in a query batch.
```

**This is a correction to an assumption implicit in plan §3/§7**: a real SSDT-generated file puts
`SET ANSI_NULLS ON` / `SET QUOTED_IDENTIFIER ON` **before a `GO`**, each in its own batch, ahead
of the batch that holds `CREATE`/`ALTER` — it is not possible for the definition SQL Server
actually stores to ever contain a `SET` statement ahead of `CREATE` in the same batch, because
that script could never have deployed in the first place. So §7 rule 1 ("ignore everything before
the first CREATE/ALTER") will never need to strip a `SET` line from the *server* side; it only
ever needs to strip comments/BOM/whitespace from the server side, and comments **and** `SET`
lines from the **file** side (a `.sql` file legitimately has both, separated by `GO`, as one
text blob once `GO` separators are stripped for parsing). Confirms `ModuleFileParser`'s existing
requirement ("must handle... `SET` lines before `CREATE`", §13.3) is correctly scoped to the file
side.

### 3.3 Leading whitespace, no comment, same batch

Batch: two blank lines, then six spaces, then `CREATE PROCEDURE dbo.SCProbe_C ...`. Result: 2
characters precede `CREATE` (the two blank lines' line terminators — LF only, one each), and the
first stored character is `0x000A` (LF) — the six spaces immediately before `CREATE` on its own
line are **not** present in the stored definition at all. This says the client-side/parser layer
that produces the definition trims trailing whitespace on the line immediately preceding
`CREATE`/collapses it, while blank lines earlier stay as bare newlines. Practical consequence for
the normaliser (§13.6): don't assume a fixed run of leading whitespace maps 1:1 between repo file
and server text — rule 3 ("whitespace is ignored") already covers this, and this finding confirms
rule 3 is doing real work, not just defending against formatting-only edits.

### 3.4 Line endings — **LEAD CORRECTION (2026-09-28): CRLF IS preserved**

> **The finding below is wrong, and the cause is the test harness, not SQL Server.** The batch
> was sent with `sqlcmd -i`, and sqlcmd reads its input line by line and re-joins the lines with
> LF when it builds each batch — so the server never received a carriage return.
>
> Re-tested by the lead through `Microsoft.Data.SqlClient` directly, sending a `CREATE PROCEDURE`
> with CRLF line endings, including inside a multi-line string literal:
> `sys.sql_modules.definition` kept **all 5 `\r` characters** (5 CR, 5 LF). **SQL Server stores a
> module's line endings exactly as the client sent them.** Which endings a server holds therefore
> depends on the tool that deployed each module (sqlcmd: LF; SSMS and SqlClient: whatever the
> text had).
>
> **Consequence — the conclusion survives, for a better reason:** line endings must be normalised
> on both sides before comparing, because they genuinely vary by deployment tool. `TsqlLexer`
> already classes `\r` as whitespace (`char.IsWhiteSpace`), so endings *between* tokens were never
> a risk. The real gap is **inside a multi-line string literal**, which §7 rule 5 compares
> exactly. Fixed in the plan as §7 **rule 0**: `\r\n` → `\n`, then lone `\r` → `\n`, on both
> inputs, before lexing.
>
> Lesson for future spikes: **a probe that goes through a client tool tests the tool too.** Verify
> storage behaviour through the same client library the extension uses.

*Original text, kept for the record:*

Test C's definition (`tempdb.dbo.SCProbe_C`), sent to the server via `sqlcmd -i` reading a file
saved with Windows CRLF line endings: `CHARINDEX(CHAR(13)+CHAR(10), definition)` over the whole
definition returns `0` (not found, anywhere), while a straight `LEN(REPLACE(definition,
CHAR(13),''))` equals `LEN(definition)` exactly (no `CHAR(13)` present at all). **`sys.sql_modules
.definition` never contains a carriage return — SQL Server stores/normalises line breaks as bare
LF**, regardless of how the batch was sent. This matters for §7: **any repo `.sql` file saved
with CRLF (the SSDT/Windows norm) will differ from the server text on every line break unless the
normaliser explicitly strips `\r`** — rule 3 says "whitespace... ignored", which is fine *if* the
tokenizer treats `\r` as whitespace and discards it outright (Core's `TsqlLexer` has a
`Whitespace` token kind — confirm `\r` classifies as `Whitespace`, not `Other`, or line-ending
differences will silently defeat rule 3 and produce false `DiffersFromRepo` results on every
single object, which would be exactly the "alarm that fires on formatting" the plan explicitly
tries to avoid in D3). **This is the one finding in this spike most likely to break the feature
silently if missed** — recommend a unit test that feeds the normaliser a CRLF file and an LF
server string with identical content and asserts `AreEquivalent` returns true.

### 3.5 `CREATE OR ALTER` — the stored opening keywords

Fresh object (`SCProbe_P3`, never plain-`CREATE`d), created directly with `CREATE OR ALTER
PROCEDURE ...`. The first 30 characters of `definition`, both as text and as exact
`VARBINARY` bytes: `CREATE   PROCEDURE dbo.SCProbe` — i.e. the literal token `OR ALTER` is gone,
replaced by **blank characters holding its original column width** (`CREATE` + 3 spaces +
`PROCEDURE`, versus `CREATE` + 1 space + `PROCEDURE` for a plain `CREATE`). This is a real,
observed behaviour, not a guess — the exact replacement width was not tried against
`CREATE   OR   ALTER` (extra spaces in the source) to confirm the substitution is truly
whitespace-for-token vs. some other fixed transform, but the net effect for §13.6 is what
matters: **the stored definition never contains the literal substring `"OR ALTER"`** once a
`CREATE OR ALTER` has run, which is exactly why §13.6's rule 2 ("`CREATE`, `ALTER` and `CREATE OR
ALTER` are equivalent") needs to normalise on the *keyword after the optional `OR ALTER`*
(i.e. `PROCEDURE`/`VIEW`/etc.), not on matching the literal three-way string — a normaliser that
literally special-cased the string `"CREATE OR ALTER"` would never fire on the server side, only
on repo files, which would silently break the "equivalent" rule in exactly the direction that
produces false `DiffersFromRepo` alarms after any `CREATE OR ALTER` runs against a file whose repo
copy still says plain `CREATE`.

---

## S-4 — Encrypted and CLR modules

> **Answer: `definition` is `NULL` (not an error, not empty string) for a `WITH ENCRYPTION`
> module, confirmed live. The module still executes normally — encryption blocks reading the text,
> not running it. CLR was not attempted.**
> **Confidence: HIGH on the `WITH ENCRYPTION` case (live evidence). Not established for CLR —
> see below.**

`tempdb.dbo.SCProbe_Enc`, created `WITH ENCRYPTION`:

```
definition IS NULL        = 1   (true)
OBJECTPROPERTY(...,'IsEncrypted') = 1
EXEC dbo.SCProbe_Enc      -> succeeded, "ENC exec succeeded (definition NULL did not block execution)"
```

`sys.sql_modules.definition` is `NULL`, matching §13.7 step 9's expectation
(`Definition == null → NotCompared`) exactly, and no error was raised reading it — a plain
`SELECT definition FROM sys.sql_modules WHERE ...` against an encrypted module returns a `NULL`
row value cleanly, no permission error, no exception. Safe to rely on `Definition == null` as the
sole signal without a separate `OBJECTPROPERTY('IsEncrypted')` check, though keeping that check as
a documented reason string (`"definition not readable: encrypted"` vs `"...: CLR or other"`) would
give a better message than a bare "unreadable" — `OBJECTPROPERTY(..., 'IsEncrypted')` is available
for that even when `definition` is `NULL`, at effectively no extra cost.

**CLR was not attempted** — creating a CLR module needs `clr enabled` (a server-level
configuration change, explicitly out of the "no server-level changes" boundary for this spike),
an assembly to register, and would leave more residue than a same-session `DROP` cleanly
undoes if anything failed partway. Recommend the lead or C1 settle this later with the exact
script below, run only if `clr enabled` is already `1` on a disposable instance (never flip it
for this check):

```sql
-- Run only on a disposable instance where "clr enabled" is already 1. Do not enable it here.
USE tempdb;
GO
IF OBJECT_ID('dbo.SCProbe_Clr') IS NOT NULL DROP PROCEDURE dbo.SCProbe_Clr;
IF EXISTS (SELECT 1 FROM sys.assemblies WHERE name = 'SCProbeClrAsm') DROP ASSEMBLY SCProbeClrAsm;
GO
-- Register any trivial pre-built CLR assembly as SCProbeClrAsm, then:
-- CREATE PROCEDURE dbo.SCProbe_Clr AS EXTERNAL NAME SCProbeClrAsm.[SomeClass].[SomeMethod];
-- GO
SELECT definition, OBJECTPROPERTY(OBJECT_ID('dbo.SCProbe_Clr'), 'IsExecuted') AS is_clr_ish
FROM sys.sql_modules WHERE object_id = OBJECT_ID('dbo.SCProbe_Clr');
-- Expect definition IS NULL for a CLR proc the same way as WITH ENCRYPTION, but this line is
-- inference from Microsoft documentation, not observed here -- confirm before relying on it.
GO
DROP PROCEDURE dbo.SCProbe_Clr;
DROP ASSEMBLY SCProbeClrAsm;
```

Documented (not observed) expectation, **LOW confidence**: CLR procedures also report `definition
= NULL` from `sys.sql_modules` because CLR procs have no T-SQL body to store — `sys.sql_modules`
only has rows at all for T-SQL modules; a CLR proc's row (if any) would come from
`sys.assembly_modules`, not `sys.sql_modules`, so `ServerObjectState.Definition == null` (§13.7)
likely already covers it for free via a plain join miss, not an explicit encrypted/CLR branch —
but this was **not verified live** and should not be treated as settled.

---

## S-5 — Index cost: lexing the real project

> **Answer: cold (first read, includes OS disk I/O) ≈ 1.2 seconds total for all 1,329 files;
> warm (OS file cache primed) ≈ 0.75 seconds; lexing alone (text already resident, no I/O)
> ≈ 25 ms. No progress bar is needed for the lex step itself — disk I/O dominates, and even that
> is sub-1.5-second on a warm or cold local disk.**
> **Confidence: HIGH — live run, single pass, this machine's disk/CPU.**

`spikes/SourceControlProbe` (new console app, `net8.0`, project-references
`src/SsmsDataAnalyzer.Core/SsmsDataAnalyzer.Core.csproj` directly — no copy of `TsqlLexer`) walked
`C:\CISA\APPRRR-Agriculture\Database\KingICT.Demeter.Database.Finances` read-only
(`Directory.EnumerateFiles` + `File.ReadAllBytes`, nothing written, renamed or deleted):

```
File count: 1329
Total bytes read: 8,351,764  (~8.0 MiB)
COLD  read  elapsed: 1167 ms
COLD  lex   elapsed:   71 ms
COLD  total (read+lex): 1238 ms
Total tokens produced: 1,426,901
WARM  read  elapsed:  710 ms
WARM  lex   elapsed:   41 ms
WARM  total (read+lex): 751 ms
LEX-ONLY (text already in memory) elapsed: 25 ms
```

File count (1,329) matches plan §3's own count exactly, confirming this is the same project
snapshot the plan was designed against.

**Reading the file** dominates cost by roughly 15–30x over lexing it — expected, `TsqlLexer` is a
single linear scan with no allocation-heavy parse tree, while `File.ReadAllBytes` +
`Encoding.UTF8.GetString` pays for actual disk I/O the first time and OS-cache-to-managed-heap
copy every time after. The gap between COLD and WARM read (1167 ms → 710 ms) is entirely the
first-touch disk cost — this machine's disk, not a network share; a real network-hosted repo
(common for a shared SSDT project) would likely widen that COLD number further, which the plan
should note as a variable this spike could not test (only a local path was available).

**Recommendation for the plan:** a progress indicator is worth having for the **first** build of
the `RepoIndex` per `.sqlproj` (the COLD number, and potentially worse on a network path) simply
because ~1.2 seconds of a blocked UI thread is noticeable, but the lexing step itself needs no
special treatment — even the full 1,426,901-token, 1,329-file, 8 MiB run lexes in under 30 ms once
memory-resident. §8's caching design (rebuild only for files whose size/mtime changed) means this
COLD cost is paid once per session per project, not per check — confirmed sufficient by these
numbers without needing async/background-thread work for the lex step specifically (the read step
should still go through the async file I/O the Vsix layer already uses elsewhere, per the "VS
options off the UI thread" project rule, since 1.2 seconds on the UI thread would be a visible
stall).

**What could not be established:** only one run was taken (no variance/repeat-run statistics),
and only against a local NTFS path — a real developer's `.sqlproj` on a mapped network drive or
inside OneDrive could look meaningfully different for the COLD number specifically.

---

## Biggest risk to the plan (see also cover message)

`sp_refreshsqlmodule` (S-2) and the CRLF-normalisation gap in the comparer (S-3.4) are the two
findings most likely to cause real false positives or false negatives if not explicitly covered
by tests — both are now spelled out above with the exact behaviour observed.
