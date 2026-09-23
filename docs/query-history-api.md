# Query History — API spike report (Agent S3)

Target verified: **SSMS 22.9.12105.275**,
`C:\Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE`. All binary
findings come from `spikes/OeProbe` (pure `System.Reflection.Metadata`/IL read — nothing was
loaded or executed) against the shipped SSMS assemblies. Two things were added to OeProbe for
this spike: a `listres` verb that reads the *contents* of a standalone `.resources` file
(`docs/oe-api.md` and `docs/resultsgrid-api.md`'s `res` verb only pulls the outer manifest-resource
blob out of a PE file; `listres` goes one level deeper, into the named entries inside that blob —
this is how the `Menus.ctmenu` binary command table was located below). Where I could not settle a
question from static evidence and there is no way to drive a live SSMS session from this
environment, I say so and give a short manual check for the user, in the same spirit as the two
prior spikes.

No SSMS install file was modified. No query ran against any database except read-only
`sys.dm_exec_describe_first_result_set` calls that were not needed for this spike (S-1..S-4 are
all static/IL questions); nothing was executed against `SsmsDataAnalyzerTest` or any other
database.

---

## S-1 — Completion signal (most important)

> **Answer: a real completion signal with outcome + row count EXISTS, but it is internal-only —
> reachable only through two hops of reflection on private/internal implementation details, not
> a public API.** The cheap public alternatives (DTE `AfterExecute`, results grid appearing,
> status bar text) each give a weaker signal than they look like they should.
>
> **Confidence: HIGH that the internal event exists and carries outcome + row count** (read
> directly from IL, corroborated by three independent code paths agreeing with each other).
> **MEDIUM on how reliable hooking it would be in practice** — it has not been tried against a
> running SSMS process, and reflection onto a private field one hop deep is more fragile than
> anything the OE or results-grid spikes needed.

### 1.1 What exists, and where

`SQLEditors.dll` → `Microsoft.SqlServer.Management.QueryExecution.IQueryExecutionHandler`
(**internal** interface, but every member is `public`):

```csharp
internal interface IQueryExecutionHandler
{
    event ScriptExecutionCompletedEventHandler ScriptExecutionCompleted;
    event EventHandler ScriptExecutionStarted;
    bool PrepareForExecution(bool);
    bool Run(ITextSpan, IDbConnection, UIConnectionInfo, bool);
    void Cancel(bool);
    ...
}
```

`ScriptExecutionCompletedEventArgs` (internal class, public members):

```csharp
public ScriptExecutionResult ExecutionResult { get; }   // Success=1, Failure=2, Cancel=4,
                                                          // Timeout=8, Halted=16 (all read from
                                                          // the enum's own .cctor, not guessed)
public bool WithEstimatedPlan { get; }
```

That `ExecutionResult` enum is exactly "did it finish, and did it error/get cancelled" — the
first two-thirds of S-1's question, in one field.

Row count is one hop further in, on the internal `QESQLBatch` class (public members):

```csharp
public long RowsAffected { get; }        // per-batch — a script with several GO-separated
                                          // batches has one QESQLBatch per batch
```

`DisplaySQLResultsControl` (the internal class that actually implements
`IQueryExecutionHandler`, confirmed by `types --all`) additionally carries a private
`long numberOfAffectedRows` field that looks like the script-wide total, and a
`QESQLBatchExecutedEventHandler` event (`private QESQLBatchExecutedEventHandler
m_OnBatchExecutionCompleted`) that fires per batch with a `QESQLBatchExecutedEventArgs`
(`public QESQLBatch Batch { get; }` on top of the `ExecutionResult` it inherits from
`ScriptExecutionCompletedEventArgs`) — so a per-batch row count is available too, if the history
entry ever needs "how many rows did batch 3 return" rather than just a total.

Duration is **not** tracked anywhere I found under a `Duration`/`Elapsed` name. Plan §2 already
expects the caller (our own `BeforeExecute` hook) to stamp the start time; pairing that with the
moment `ScriptExecutionCompleted` fires gives duration without SSMS's help — no gap here.

### 1.2 How you would actually reach it — and why that's the catch

`SqlScriptEditorControl.GetQueryExecutionHandler()` — **internal** method, IL-confirmed to be a
one-line return of a private field:

```
IL_0000: ldarg.0
IL_0001: ldfld  SqlScriptEditorControl::m_sqlResultsControl   // DisplaySQLResultsControl, internal type
IL_0006: ret
```

So the *cleanest available path*, in order of directness:

1. Get the active `SqlScriptEditorControl` (public class — this part is already solved: it's
   exactly what `QueryWindowAccessor`/`ResultsGridSourceCommand` already obtain today for
   Go to source).
2. Reflect the **private** field `m_sqlResultsControl` off it (skips the internal method
   entirely — same IL, one hop instead of two) to get the internal
   `DisplaySQLResultsControl` instance.
3. Reflect `GetEvent("ScriptExecutionCompleted", BindingFlags.NonPublic | Instance)` on that
   instance's concrete runtime type and `AddEventHandler` a delegate matching
   `ScriptExecutionCompletedEventHandler` (itself an internal delegate type, so the handler
   must be built with `Delegate.CreateDelegate` against a `MethodInfo`, not a normal C# lambda
   assignment).
4. In the handler, read `args.ExecutionResult` via `GetProperty(...).GetValue(args)` (the
   `ScriptExecutionCompletedEventArgs` type is also internal, same story).

Every one of those four steps is private/internal reflection on implementation detail, not the
kind of "public interface meant as an extension point" that made Tier A of `docs/oe-api.md`
low-risk. This is a materially different risk profile from that spike's `IWinformsMenuHandler`
finding (a documented-by-behavior public interface) — it is closer to Red Gate's private
`subMenus`/`menuItemsInOrder` reflection that `docs/oe-api.md` flagged as "where their fragility
lives." An SSMS point release renaming `m_sqlResultsControl`, or changing `DisplaySQLResultsControl`
to implement the event explicitly (`IQueryExecutionHandler.ScriptExecutionCompleted`, which
changes the reflected member name to
`Microsoft.SqlServer.Management.QueryExecution.IQueryExecutionHandler.ScriptExecutionCompleted`),
breaks this silently. **I did not attempt this against a live, running SSMS process** — there is
no tool in this environment that can launch and drive the SSMS desktop app, so the four-step chain
above is read off IL, not exercised. That is the honest gap; see 1.4 for what would close it.

### 1.3 The cheap public alternatives — evaluated, not assumed

**DTE `CommandEvents.AfterExecute` on `Query.Execute`.** I read `DoScriptExec`'s IL (the method
the existing `OnExecScript` handler calls, which is what `Query.Execute` ultimately invokes):

```
IL_0001: callvirt  ...GetQueryExecutionHandler
...
IL_0021: callvirt  IQueryExecutionHandler::Run     // returns bool: "did execution START ok"
IL_0026: brtrue.s  17                              // if true: RETURN — nothing else happens here
IL_0028: ...                                       // if false: fire OnExecutionCompleted(Failure) NOW
```

`Run()`'s return value only tells you whether execution *started*; when it returns `true`,
`DoScriptExec` returns immediately with no further processing, which is only consistent with
`ScriptExecutionCompleted` being raised later, asynchronously, from wherever the actual batch
execution happens (matches `QESQLBatch.Execute` being callable off the UI thread, and
`SqlScriptEditorControl.m_dtExecBegan`/`ProcessElapsedTimeTick` existing at all — SSMS itself
needs to poll/tick while a query is still running).

**Consequence: `AfterExecute` fires when the Execute *command dispatch* returns, not when the
query finishes.** For anything but a near-instant query, `AfterExecute` and `BeforeExecute` will
fire back-to-back, both well before the grid has any data. It cannot tell you duration, outcome,
or row count — it is functionally a duplicate of `BeforeExecute`, which the plan already uses.
**Do not build on it for completion.**

**The results grid appearing.** `GridResultsTabPage`/`DisplaySQLResultsControl` build grids
per-result-set as the query streams rows (`QESQLBatchNewResultSetEventHandler NewResultSet`,
per `docs/resultsgrid-api.md`'s object graph) — a grid can appear well before the *script*
finishes (multiple batches, multiple result sets per batch). Watching for "a grid now exists"
tells you a result set arrived, not that execution is over, and tells you nothing about errors
(an error-only batch never produces a grid at all). Weaker than the internal event, not a
substitute for it.

**The SSMS status bar text.** Genuinely useful as a fallback, and public-ish:
`Microsoft.SqlServer.Management.UI.VSIntegration.Editors.QEStatusBarKnownStates` is a **public**
enum with exactly the outcomes we want:

```csharp
public enum QEStatusBarKnownStates
{
    Unknown = 0, Offline = 1, Connecting = 2, Connected = 3, Executing = 4, Debugging = 5,
    Parsing = 6, ExecutionOk = 7, ExecutionFailed = 8, ExecutionCancelled = 9,
    ExecutionTimedOut = 10, CancelingExecution = 11
}
```

But the only place I found this enum *consumed* is
`internal void OnWindowStatusTextChanged(QEStatusBarKnownStates)` on `SqlScriptEditorControl` —
an internal method, not an event we can subscribe to. The public route to the same information
would be polling `IVsStatusbar.GetText` (a standard, public, documented VS Shell COM interface)
on a timer and diffing the text — but that reads whatever *localized, freeform* string SSMS put
there, not this enum value directly, so it needs string-matching against SSMS's own (possibly
localized, possibly changed between builds) status messages. It also cannot distinguish "the
status bar says this because of query A" from "...because the user alt-tabbed to query window
B" without also tracking window focus. Usable as a last-resort visual fallback, not as the
primary signal.

### 1.4 Recommendation to the lead

1. **Try the internal-event route first, but behind a `try/catch` that degrades cleanly to
   "record only the start time"** — exactly the fallback plan §6 already accepts. Given the
   reflection depth, treat any failure (missing field, wrong event signature, exception during
   `AddEventHandler`) as "this SSMS build doesn't support it," not as a bug to chase.
2. **Do the one thing this spike could not do: try it against a real, running SSMS 22 session**
   before committing to it for Phase 1. A 20-line throwaway probe (reflect `m_sqlResultsControl`
   off the currently active `SqlScriptEditorControl`, hook `ScriptExecutionCompleted`, log
   `ExecutionResult` and `QESQLBatch.RowsAffected` to `OeDiagnostics`) run once, live, in SSMS
   would upgrade this from MEDIUM to HIGH confidence or kill it outright. This is exactly the
   kind of check `docs/oe-api.md` and `docs/resultsgrid-api.md` left as "manual check for the
   user" — recommend the same here before H2 builds on it.
3. **If it works:** wire it from `DataAnalyzerPackage`/`ExecutedQueryTextTracker`'s existing
   `SqlScriptEditorControl` access point (same object the Go to source code already holds),
   store `DurationMs` as `(completedUtc - startedUtc)`, map `ScriptExecutionResult` →
   `HistoryOutcome` (`Success`→`Success`, `Failure`→`Error`, everything else→`Cancelled`), and
   `RowCount` from summing `QESQLBatch.RowsAffected` across `QESQLBatchExecutedEventArgs` if you
   hook the per-batch event too, or from `numberOfAffectedRows` if that field alone is reachable
   and sufficient.
4. **If it doesn't survive a live try, or the lead judges the reflection risk too high for a
   feature this size:** Phase 1 records only `StartedUtc`, exactly as plan §6 already allows.
   That is a legitimate, low-risk outcome of this spike, not a failure of it.

---

## S-2 — Toolbar

> **Answer: the SQL Editor toolbar's group GUID/ID could not be read from static evidence — it
> is compiled into the same compressed `Menus.ctmenu` blob that blocked `docs/resultsgrid-api.md`
> from reading group placements. This is a genuine dead end for the `.vsct`-declares-a-group
> approach.** The practical way to add a button to it is the **DTE `CommandBars` route**, found
> by name at runtime rather than by GUID/ID — the same mechanism `docs/resultsgrid-api.md` §3.3
> documented (and warned against) for SQL Lizard, but here it is the *only* option, not a
> fallback of last resort.
>
> **Confidence: HIGH that the GUID/ID is unreadable by static means** (I extracted and inspected
> the actual bytes, below). **LOW on the toolbar's exact display name** — I could not verify it
> live; "SQL Editor" is the name PLAN.md and general SSMS familiarity suggest, not something
> I read out of a string table.

### 2.1 What I checked, and what blocked it

Same as `docs/resultsgrid-api.md` §3's finding: SSMS's own command table lives as a **named
sub-entry inside a `.resources` file**, not as loose strings. Using the new `listres` verb to
open that sub-entry (rather than just noting its existence, as the prior spike did):

```
spikes/OeProbe listres <extracted>Microsoft.SqlServer.Management.UI.VSIntegration.Editors.Resources.resources
  ...
  [Byte[]] Menus.ctmenu  (7236)
  ...
```

Extracting `Menus.ctmenu` and inspecting the header:

```
4346 4354 0500 0000 0e51 0000 1400 0000   CFCT.....Q......
```

`CFCT` magic, version `5` (`0500 0000`) — the same compressed "CTC" binary command-table format
`docs/resultsgrid-api.md` already flagged as undecompressable ("I could not decompress them to
read the group placements — that one link is inferred, not read"). I did not find a way to
decompress this format either; it is proprietary to the VSSDK's `.vsct` compiler and I found no
documentation or tooling for it accessible from this environment. **The toolbar's group GUID/ID
is inside this 7236-byte blob and stays unread.**

I also checked every public field on `SQLWorkbenchCommands` (the class that carries every other
known command/menu GUID and ID for this package, dumped in full — see the `cmdid*`/`IDM_*` list)
for anything toolbar-shaped (`IDG_`, `IDR_TB_`, a field literally containing "Toolbar"): there is
none. Every toolbar constant that exists is inside the compressed blob, not as a loose public
field the way the context-menu IDs are.

### 2.2 The practical path anyway

`docs/resultsgrid-api.md` §3.3 already proved, from SQL Lizard's shipping IL, that the DTE
`CommandBars` route works against this exact SSMS 22 install:

```csharp
var bars = ((EnvDTE.DTE)package.GetService(typeof(EnvDTE.DTE))).CommandBars as CommandBars;
CommandBar bar = bars["SQL Editor"];             // by NAME, not GUID/ID — sidesteps the whole
                                                   // CFCT problem, because CommandBars are
                                                   // enumerable/indexable by their live caption
                                                   // at runtime, no compile-time constant needed
var button = (CommandBarButton)bar.Controls.Add(MsoControlType.msoControlButton, Temporary: true);
button.Caption = "Query History";
button.Click += OnQueryHistoryClick;
```

This is weaker than a `.vsct` group in the ways `docs/resultsgrid-api.md` already documented for
SQL Lizard: it must run after the toolbar exists (package autoload timing), items are
`Temporary: true` so they need re-adding every session, there's no per-context
`BeforeQueryStatus` enable/disable, and — the risk specific to *this* toolbar rather than the
results grid one — **the toolbar's actual caption is unverified**. If it is not literally "SQL
Editor" in this build/locale, `bars["SQL Editor"]` throws or returns null; the fallback used by
SQL Lizard (`FindLikelyResultsGridMenu`-style caption heuristics) does not directly transfer,
because a toolbar's *button* captions (Execute, Parse, Cancel, …) are a different search surface
than a *context menu's* item captions.

### 2.3 Recommendation to the lead

1. **Drop the `.vsct`-group-on-the-SQL-Editor-toolbar plan entirely** — there is no GUID/ID to
   target and no realistic way to get one without decompiling a proprietary compressed format.
2. **Use `CommandBars["SQL Editor"]` at runtime**, guarded by `try/catch` and falling back
   silently to "toolbar button not available this session" — exactly the plan's own phase 2
   fallback ("Otherwise, use this feature only [own toolbar]", §6). This matches plan §4 item 8's
   own framing: "if its group ID can be found" — it cannot be, so the always-available own
   toolbar (already planned) is not just a fallback, it is now the *primary* Phase 2 delivery
   surface, with the `CommandBars` route as a nice-to-have if the toolbar name checks out live.
3. **One manual check would settle the LOW-confidence name question**: in SSMS 22, open
   View → Toolbars and read the exact checkbox label for the toolbar carrying Execute/Parse/
   Cancel/Connect — that string is exactly what `bars[...]` needs, and it takes ten seconds to
   confirm live versus guessing from binaries that don't expose it.

---

## S-3 — Reopen with a connection

> **Answer: Option A (reuse an existing `UIConnectionInfo`) is not just feasible, it is
> ALREADY BUILT, shipped, and field-tested in this codebase.** `QueryWindowAccessor.TryOpenAsync`
> plus `DataAnalyzerPackage.TryOpenNewQueryWindowAsync` (the Go to source implementation) is the
> exact mechanism S-3 asks for. Query History's "Open in new query window" action should call
> this existing code path directly, not build a new one.
>
> **Confidence: HIGH.** This isn't a spike inference — it's reading working production code with
> a field-report history (`v0.5.0`, `v0.5.3`, `v0.6.1` comments document real bugs found and
> fixed against a live SSMS).

### 3.1 The mechanism, as already implemented

`src/SsmsDataAnalyzer.Vsix/DataAnalyzerPackage.cs` (`TryOpenNewQueryWindowAsync`, ~line 233) —
two routes, tried in order:

1. **`ServiceCache.ScriptFactory.CreateNewBlankScript(ScriptType, UIConnectionInfo,
   IDbConnection)`** — `Microsoft.SqlServer.Management.UI.VSIntegration.Editors.ScriptFactory`
   is `public sealed class ... : IScriptFactory` in `SQLEditors.dll`; `CreateNewBlankScript` and
   the connection-info-typed `CreateNewScript` overloads are all `public`, confirmed by
   `members --all`. The code creates the new window with a **copy** of a real, already-working
   `UIConnectionInfo` (`sourceConnectionInfo.Copy()`), overriding only `ServerName` and the
   `DATABASE` advanced option — never reconstructing credentials, per the project's Amendment 13
   rule already baked into this file's comments.
2. **EnvDTE fallback**, self-discovered at runtime (never a hardcoded command name, for the same
   CFCT-blob reason documented in S-2/S-4): enumerate `DTE.Commands` for a "new query" command,
   execute it, then read the new window's connection back through the public
   `SqlScriptEditorControl.Connection` property and verify it landed on the right server before
   writing anything into it.

Both routes are already wired into `QueryWindowAccessor` (`src/SsmsDataAnalyzer.Vsix/GoToSource/
QueryWindowAccessor.cs`), which is deliberately decoupled from package/service-provider details
so any other feature (History included) can call `QueryWindowAccessor.TryOpenAsync(sql,
connectionString, sourceConnectionInfo, allowAutoExecute)` without knowing anything about
`ServiceCache`/`IScriptFactory`.

### 3.2 Mapping onto History's three options

- **Option A (reuse an open window's/OE's `UIConnectionInfo`)** — this is what the existing code
  does when `sourceConnectionInfo` is non-null. For History, the caller supplies whichever open
  query window (or Object Explorer node, via `docs/oe-api.md`'s
  `INodeContext.Connection`) currently matches the entry's `Server` + `Login`. **This is the
  recommended path** — it is already built, already tested, and matches plan §4 item 6's own
  wording.
- **Option B (SSMS's connect dialog, pre-filled)** — not needed as a *primary* path since A
  already works when a matching live connection exists. It remains the honest answer for "no
  matching open window and no stored password": `DataAnalyzerPackage.cs`'s hand-built
  `UIConnectionInfo` fallback (used when `sourceConnectionInfo == null`) fills in `ServerName`,
  `ServerType` (the exact GUID literal, read from `RegSvrConnectionInfo.SqlServerTypeGuid`'s own
  `.cctor` — not guessed) and `UserName`/`Password` *only if the connection string already has
  them* (e.g., a currently-open, already-authenticated ADO.NET connection). **For History**
  specifically, the plan already says "never store the password" (§3), so this fallback would
  produce a `UIConnectionInfo` with a server/login but no password when reopening an entry whose
  session has since closed — SSMS's own connect dialog then prompts for the password itself,
  which is the correct, safe behavior for that case. No new code needed; this is the existing
  fallback path doing exactly the right thing by accident of how it already works.
- **Option C (open disconnected)** — not needed; the existing code always attempts a connection
  and only fails with a specific, honest reason (`QueryWindowOpenResult.Fail`) if both routes
  don't produce a connected window.

### 3.3 Recommendation to the lead

**No new API work needed for S-3.** History's "Open in new query window" action is a thin caller
of the existing `QueryWindowAccessor.TryOpenAsync`, passing:
- `sql` = the entry's `Text`
- `connectionString` = built from the entry's `Server`/`Database`/`Login`/`AuthKind`
- `sourceConnectionInfo` = the `UIConnectionInfo` of whichever currently-open query window (if
  any) matches the entry's server + login — found via `DTE.Documents`/existing
  `ScriptAndResultsEditorControl.Connection` reads, the same pattern
  `ExecutedQueryTextTracker`/`ResultsGridSourceCommand` already use elsewhere in this codebase
- `allowAutoExecute` = `false` (plan §4 item 6: "Never runs the query automatically")

The one gap worth flagging to H2: finding "an open window with the same server+login" needs a
small helper that walks open documents' `SqlScriptEditorControl.Connection`/`IsConnected`,
which doesn't exist yet as a reusable function (today's callers always have one specific,
already-known source window). That's ordinary new code, not a spike question.

---

## S-4 — Execute current statement

> **Answer: I could not confirm from static evidence that "Execute current statement" exists in
> this SSMS 22 build as a distinct code path at all, separate from plain `Query.Execute`.** What
> I *can* confirm precisely, from IL, is what plain `Query.Execute` runs when there is no
> selection: **the whole document text**, not a parsed "current statement" — which matches what
> `ExecutedQueryTextTracker` already assumes for that command. I found no statement-boundary
> parsing code anywhere in `SQLEditors.dll`.
>
> **Confidence: LOW on whether `ExecuteCurrentStatement` is a real, separate SSMS 22 command.
> HIGH on the mechanics of `Query.Execute`'s own selection-or-whole-document fallback** (read
> directly from IL, byte for byte). This is the one item where I recommend a live check before
> anyone builds on the assumption either way.

### 4.1 What `Query.Execute` (`cmdidExecuteQuery`) actually does — read from IL

`ScriptAndResultsEditorControl.OnExecScript` (the handler bound to plain Execute) calls
`ScriptEditorControl.GetSelectedTextSpan()`, which delegates to
`ShellCodeWindowControl.GetSelectedTextSpan()` (**public** method). Its IL, decoded in full:

```
IVsTextView::GetSelectedText -> trim -> if length > 0:
    build TextSpan from IVsTextView::GetSelection (the real, on-screen selection range)
else:
    build TextSpan from m_textBuffer.get_Text()    // <-- THE WHOLE DOCUMENT
    with start=(0,0), end = GetLineAndColumn(wholeText.Length)
```

This is an exact, byte-for-byte match for what `ExecutedQueryTextTracker.OnBeforeExecute`
already does in C# (`selection.Text` if non-empty, else the whole `TextDocument`) — **for the
plain `Query.Execute` command, the existing tracker is provably correct**, at HIGH confidence.

### 4.2 Why I doubt `ExecuteCurrentStatement` is a separate thing here

I searched exhaustively for any statement-level parsing entry point:

- `ScriptEditorControl`/`ShellCodeWindowControl`/`ShellTextEditorControl`: the **only**
  span-producing method on any of them is `GetSelectedTextSpan()` above. No
  `GetCurrentStatementSpan`, no `GetStatementAtPosition`, nothing statement-shaped.
- `SQLWorkbenchCommands`' full `cmdid*` constant list (dumped in full for S-2) has exactly one
  execute-family id relevant here, `cmdidExecuteQuery = 1` (plus the older
  `cmdIdTeamSystemSqlEditorExecute = 1536` team-system alias) — no distinct "execute current
  statement" id.
- I searched the **entire SSMS 22 IDE tree** (every `.dll`/`.pkgdef`/`.exe`, both ASCII and
  UTF-16LE encodings) for the literal bytes `ExecuteCurrentStatement` and `CurrentStatement`.
  Zero hits anywhere in SQL-editor-related code; the only `CurrentStatement` hits anywhere in the
  whole install are in the **debugger** engine assemblies (`vbdebug.dll`,
  `Microsoft.VisualStudio.Debugger.Engine.dll`, etc. — "Set/Show Next Statement", an unrelated
  debugger feature).

That said, **this negative result is not conclusive**, for the same reason S-2's toolbar name
search wasn't conclusive: every VSCT-defined command's `CanonicalName` string (which is what
`DTE.Commands.Item("Query.ExecuteCurrentStatement", -1)` would be matching against) is compiled
into the same compressed `Menus.ctmenu` CFCT blob that blocked S-2. A command can legitimately
exist and simply have its name hidden inside that blob rather than sitting as a loose string
anywhere else in the binaries. So the honest state is: **no supporting evidence found, but the
search method has a known blind spot that could hide a real command.**

I also cannot rule out that a "current statement" execution, if it exists, is implemented by
*temporarily selecting the statement text and then invoking the same plain-Execute path* — which
would mean `GetSelectedTextSpan()` (and therefore the existing tracker's `BeforeExecute` hook)
already captures it correctly as an ordinary selection, with no code change needed at all. I
found nothing to confirm or deny this either.

### 4.3 Recommendation to the lead

1. **This is the one item in the spike that needs a two-minute live check before H2 writes any
   code for it.** Open a query window with several statements separated by blank lines (no `GO`
   needed), put the caret in the *second* statement with **no text selected**, and:
   - Check whether SSMS's Query menu / right-click menu has an "Execute Current Statement" (or
     similarly named) item at all, and if so, whether it has a keyboard shortcut shown next to
     it (Tools → Options → Environment → Keyboard, search "current statement", would also
     confirm the exact DTE command name if one exists).
   - If it exists: invoke it and watch whether the editor visually highlights the statement
     first (if it does, the existing selection-based tracker already handles it for free; if it
     runs without ever selecting anything, the tracker needs a real fix, and that fix would need
     its own spike into whatever component actually computes the statement boundary, since
     `SQLEditors.dll` does not).
   - If it does not exist as a native SSMS command: the plan's `ExecuteCommandNames` array
     entry for `"Query.ExecuteCurrentStatement"` is simply always skipped today (the existing
     `try/catch (ArgumentException)` in `ExecutedQueryTextTracker.Initialize` already handles a
     missing command name gracefully — check `OeDiagnostics`' hooked-commands log line on
     startup, which already reports exactly which of the three names it found).
2. **Until that check happens, do not assume the tracker is broken for this command** — plan §5
   ("When `ExecuteCurrentStatement` is used, the entry records the statement that actually ran,
   not the whole document") may already be true today with zero extra work, or may need a
   follow-up spike into a component this one did not find. Either outcome is cheap to learn and
   expensive to guess wrong on.

---

## Appendix — reproducing these findings

```
cd spikes/OeProbe && dotnet build -c Release
set IDE=C:\Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE
set P=bin\Release\net8.0\OeProbe.exe

:: S-1 — completion signal
%P% types   "%IDE%\Extensions\Application\SQLEditors.dll" --ns QueryExecution --all
%P% members "%IDE%\Extensions\Application\SQLEditors.dll" --type ScriptExecutionCompletedEventArgs --all
%P% members "%IDE%\Extensions\Application\SQLEditors.dll" --type ".IQueryExecutionHandler" --all
%P% members "%IDE%\Extensions\Application\SQLEditors.dll" --type ".QESQLBatch" --all
%P% il      "%IDE%\Extensions\Application\SQLEditors.dll" --type ScriptAndResultsEditorControl --method DoScriptExec
%P% il      "%IDE%\Extensions\Application\SQLEditors.dll" --type SqlScriptEditorControl --method GetQueryExecutionHandler
%P% members "%IDE%\Extensions\Application\SQLEditors.dll" --type QEStatusBarKnownStates --all

:: S-2 — toolbar (the new `listres` verb — reads INSIDE a .resources blob, not just the PE's
::        manifest-resource list)
%P% res     "%IDE%\Extensions\Application\SQLEditors.dll" --name Resources --out .\res
%P% listres ".\res\Microsoft.SqlServer.Management.UI.VSIntegration.Editors.Resources.resources" --name ctmenu --out .\ctmenu
xxd .\ctmenu\Menus.ctmenu | head -5     :: "CFCT" magic, version 5 — compressed, unreadable

:: S-3 — reopen with a connection (already-shipped code, read for confirmation only)
%P% members "%IDE%\Extensions\Application\SQLEditors.dll" --type ".ScriptFactory" --all
:: cross-reference against src/SsmsDataAnalyzer.Vsix/DataAnalyzerPackage.cs (TryOpenNewQueryWindowAsync)
::                     and src/SsmsDataAnalyzer.Vsix/GoToSource/QueryWindowAccessor.cs

:: S-4 — Execute current statement
%P% il      "%IDE%\Extensions\Application\SQLEditors.dll" --type ".Editors.ScriptEditorControl" --method GetSelectedTextSpan
%P% il      "%IDE%\Extensions\Application\SQLEditors.dll" --type ShellCodeWindowControl --method GetSelectedTextSpan
%P% members "%IDE%\Extensions\Application\SQLEditors.dll" --type ".Editors.ScriptEditorControl" --all
:: whole-tree literal search (ASCII + UTF-16LE) for "ExecuteCurrentStatement" / "CurrentStatement" —
:: zero hits in SQL-editor code anywhere under Common7\IDE
```

`ResReader.cs` (the new `listres` verb) is a small addition to `spikes/OeProbe`: it opens a
standalone `.resources` file with `System.Resources.ResourceReader` and lists/extracts its named
entries — the one piece of tooling the two prior spikes didn't need because they never had to
look *inside* a `.resources` blob, only find it.
