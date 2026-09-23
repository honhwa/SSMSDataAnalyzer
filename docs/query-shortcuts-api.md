# Query Shortcuts — API spike report

Target verified: **SSMS 22.9.12105.275**,
`C:\Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE`. All binary
findings come from `spikes/OeProbe` (pure `System.Reflection.Metadata`/IL read — nothing was
loaded or executed) against `Extensions\Application\SQLEditors.dll` and
`Microsoft.SqlServer.Management.UnifiedSettings.dll`. One finding (Q2) is corroborated by a
**live, read-only file read** of this machine's actual SSMS 22 settings file — not a query, not
an SSMS launch, just `cat` on a JSON file already on disk. No SSMS install file was modified, no
SSMS process was started or driven, and no database was queried at all for this spike.

Because SSMS cannot be driven from this environment, **nothing here was confirmed by watching a
keypress actually fire**. Every answer is inferred from IL and from the on-disk settings file.
Where that distinction matters, it's called out with a concrete manual check.

---

## Q1 — What command runs? (most important)

> **Answer: yes, a real command executes — but it is a private SQL-editor command, not
> `Query.Execute`.** Every query shortcut (built-in `sp_help`/`sp_who`/`sp_lock` *and* the 9
> user-configurable slots) is wired as a `System.ComponentModel.Design.MenuCommand` under command
> set GUID **`{52692960-56BC-4989-B5D3-94C47A513E8D}`** with a small, fixed set of command IDs.
> Its handler calls straight into the query-execution engine, **bypassing `OnExecScript` (the
> handler for the real Execute command, same GUID, id `1`) entirely.**
>
> **Confidence: HIGH that this is the command and these are the exact GUID/IDs (read directly
> from IL, constructor arguments literal).** **LOW/MEDIUM on whether `EnvDTE.Events.CommandEvents`
> actually fires `BeforeExecute`/`AfterExecute` for this GUID/ID pair** — that depends on VS's
> command-routing internals, which is architecture I know generally but have not watched happen
> in this SSMS build. See "what would settle this" below.

### 1.1 The command IDs (IL-verified, `SqlScriptEditorControl.AddOptionalMenuCommands()`)

```
GUID_SQLEditorCommandSet = {52692960-56BC-4989-B5D3-94C47A513E8D}   // SQLWorkbenchCommands..cctor

cmdid 104  → OnSpHelp      → ExecuteSpMacroHelper("sp_help")     // hardcoded Alt+F1
cmdid 105  → OnSpWho       → ExecuteSpMacroHelper("sp_who")
cmdid 106  → OnSpLock      → ExecuteSpMacroHelper("sp_lock")
cmdid 107  → OnCustomMacro → looks up shortcut key "-1"  (customShortcutIdToSpIndexMap[107] = -1)
cmdid 108  → OnCustomMacro → looks up shortcut key "3"   (= -1..9  are the JSON "key" values, §Q2)
cmdid 109  → OnCustomMacro → looks up shortcut key "4"
cmdid 110  → OnCustomMacro → looks up shortcut key "5"
cmdid 111  → OnCustomMacro → looks up shortcut key "6"
cmdid 112  → OnCustomMacro → looks up shortcut key "7"
cmdid 113  → OnCustomMacro → looks up shortcut key "8"
cmdid 114  → OnCustomMacro → looks up shortcut key "9"
cmdid 115  → OnCustomMacro → looks up shortcut key "0"

(contrast) cmdid 1 → OnExecScript  — this is the REAL "Execute" command, same GUID
```

IL for `OnCustomMacro` (verbatim):

```
ldarg.1 ; isinst System.ComponentModel.Design.MenuCommand
ldstr   "environment.keyboard.queryShortcuts.shortcuts"
call    Microsoft.SqlServer.Management.UnifiedSettings.UnifiedSettingsService::GetArray
ldsfld  SqlScriptEditorControl::customShortcutIdToSpIndexMap
ldloc.0 ; callvirt MenuCommand::get_CommandID ; callvirt CommandID::get_ID
callvirt Dictionary<int,int>::get_Item                    // cmdID -> shortcut "key" (as int)
call    QueryShortcutsExtensions::TryGetValue              // out fragment text
brfalse.s <skip if slot unconfigured>
call    SqlScriptEditorControl::ExecuteSpMacroHelper(fragment)
```

The commands are registered on a plain `System.ComponentModel.Design.MenuCommand` /
`Microsoft.SqlServer.Management.UI.VSIntegration.MenuCommandsService` (the window pane's own
command service — `ShellWindowPaneUserControl.menuCommands` field), exactly the same mechanism
used for every other SQL-editor command including the real `OnExecScript` (id `1`),
`OnCancelExec` (id `3`), etc. (`ScriptAndResultsEditorControl.FillMenuCommands` /
`SqlScriptEditorControl.AddOptionalMenuCommands`, both IL-dumped).

### 1.2 What does NOT happen

`ExecuteSpMacroHelper` (private, on `SqlScriptEditorControl`) composes the final text (§Q3) and
calls `ScriptAndResultsEditorControl.DoScriptExec(bool,bool,bool,bool,string,ITextSpan)` directly.
`DoScriptExec` calls `GetQueryExecutionHandler().Run(textSpan, connection, connInfo, isSynapseDw)`
— the exact same `IQueryExecutionHandler.Run` entry point the Query History spike
(`docs/query-history-api.md`) already documents. **It never touches `OnExecScript`, and it never
writes anything into the editor's text buffer** — the composed text travels purely as a
constructor argument to a `TextSpan` object (§Q4), never appearing on screen, which matches the
task's own premise exactly.

### 1.3 Whether DTE sees it — what would settle the LOW/MEDIUM point

I could not run SSMS from this environment, so I can't watch `CommandEvents` fire. What's true by
construction: this is a bona fide `MenuCommand` reached via the shell's OLE command-routing chain
when the bound key is pressed (same chain every other SQL-editor command uses), and `EnvDTE`'s
`Events.CommandEvents` indexer accepts an arbitrary `(Guid, ID)` pair — it does not require the
command to have a name in the `Commands` collection. That makes it architecturally very likely
`BeforeExecute`/`AfterExecute` fire for `{52692960-56BC-4989-B5D3-94C47A513E8D}` / `104`-`115`
exactly as they do for `Query.Execute` today. **This has not been observed.**

**Manual check for the user:** in a running SSMS, add a `CommandEvents` hook (VSIX or
`EnvDTE.Events.CommandEvents["{52692960-56BC-4989-B5D3-94C47A513E8D}", 108]`, i.e. Ctrl+3 by
default) with a breakpoint/log in `BeforeExecute`, then press the bound key over a selection.
If it fires, `Guid`/`ID` on the event args should read back exactly this GUID and `108`.

---

## Q2 — Where are the shortcut texts stored?

> **Answer: SSMS 22's "Unified Settings" store — a per-user JSON file, not the registry at all.**
> Concrete path on this machine, **read live, read-only**:
>
> `C:\Users\mario.tusek\AppData\Local\Microsoft\SSMS\22.0_e0ae1525\settings.json`
>
> **Confidence: HIGH.** This isn't inferred — it's the actual file, and it contains this
> machine's real, currently-configured shortcuts, matching the task's own example exactly
> (`Ctrl+3` → `SELECT TOP(100) * FROM `, `Ctrl+4` → `SELECT COUNT(1) FROM `).

### 2.1 The JSON key and shape

```json
"environment.keyboard.queryShortcuts.shortcuts": [
  { "key": "-2", "storedProcedure": "sp_help" },
  { "key": "-1", "storedProcedure": "" },
  { "key": "1",  "storedProcedure": "sp_who" },
  { "key": "2",  "storedProcedure": "sp_lock" },
  { "key": "3",  "storedProcedure": "SELECT TOP(100) * FROM " },
  { "key": "4",  "storedProcedure": "SELECT COUNT(1) FROM " },
  { "key": "5",  "storedProcedure": "SELECT ar.replica_server_name AS ServerName, ..." },
  { "key": "6",  "storedProcedure": "SELECT * FROM sys.dm_exec_requests ..." },
  { "key": "7",  "storedProcedure": "SELECT d.name AS DatabaseName, ..." },
  { "key": "8",  "storedProcedure": "EXEC dbo.[ServerLogin.Connections] " },
  { "key": "9",  "storedProcedure": "SELECT session_id, percent_complete, ..." }
],
"environment.keyboard.queryShortcuts.storedProcShortcutExecuteWithNoOptions": false
```

`"key"` is the digit shown in Tools → Options (`-2` = the fixed Alt+F1 slot, `1`-`9` = the
configurable Ctrl+N slots — `-1`/`0` are reserved/unused slots); `"storedProcedure"` is the
literal fragment text despite the misleading field name (it holds arbitrary SQL, not just a proc
name — see `Ctrl+3`/`Ctrl+4`/`Ctrl+5` above, none of which are stored procedures).

**Note this file also has** `environment.keyboard.queryShortcuts.storedProcShortcutExecuteWithNoOptions`,
a bool that suppresses the "additional connection options" prompt SSMS otherwise shows before
running a shortcut — irrelevant to text reconstruction but worth knowing it exists.

### 2.2 The type shape and the code path (IL, `Microsoft.SqlServer.Management.UnifiedSettings.dll`)

```csharp
// public class, public members — usable directly by a package, no reflection needed:
public class QueryShortcut : IEquatable<QueryShortcut> {
    public string Key { get; set; }
    public string StoredProcedure { get; set; }
}
public static class UnifiedSettingsService {
    public static IReadOnlyList<T> GetArray<T>(string key);   // T = QueryShortcut here
    public static T GetValue<T>(string key);
    public static ISettingsReader GetReader();
}
```

`GetArray<T>` IL:
```
ldtoken  Microsoft.Internal.VisualStudio.Shell.Interop.SVsUnifiedSettingsManager
call     Type::GetTypeFromHandle
call     UnifiedSettingsService::GetSettingsManager   // → Package.GetGlobalService(svc)
callvirt Microsoft.VisualStudio.Utilities.UnifiedSettings.ISettingsManager::GetReader
callvirt Microsoft.VisualStudio.Utilities.UnifiedSettings.ISettingsReader::GetArrayOrThrow<T>
```

So the concrete read path for a package (a VSPackage/MEF component with a service provider) is:

```csharp
var mgr = (Microsoft.VisualStudio.Utilities.UnifiedSettings.ISettingsManager)
          Package.GetGlobalService(typeof(Microsoft.Internal.VisualStudio.Shell.Interop.SVsUnifiedSettingsManager));
var shortcuts = mgr.GetReader().GetArrayOrThrow<QueryShortcut>("environment.keyboard.queryShortcuts.shortcuts");
```

`ISettingsManager`/`ISettingsReader`/`SVsUnifiedSettingsManager` live in the VS shell assemblies
under `Common7\IDE` (surveyed hit: `Microsoft.VisualStudio.Shell.UI.Internal.dll`, 609
"UnifiedSettings"-namespace type defs — the core implementation). `QueryShortcut` and
`UnifiedSettingsService` themselves are **public** in `Microsoft.SqlServer.Management.UnifiedSettings.dll`
— a package could reference that assembly directly and call `UnifiedSettingsService.GetArray<QueryShortcut>(...)`
verbatim instead of re-declaring the DTO, or just parse the JSON file directly (simpler, no VS
service dependency, and works even when the extension loads before that service is ready).

### 2.3 SSMS 22 defaults (IL, `QueryShortcutsExtensions.get_DefaultQueryShortcuts`)

```
"-2" → "sp_help"
"1"  → "sp_who"
"2"  → "sp_lock"
```
(No default for `3`-`9` — those only exist once a user, or SSMS's first-run seeding, adds them.
This machine's file already has all nine populated, likely from a previous SSMS/Redgate import or
manual configuration — not from a stock install.)

**Confidence on the read API itself (§2.2): MEDIUM** — the call chain is solid IL evidence, but
I have not instantiated `SVsUnifiedSettingsManager` from a real package to confirm
`GetArrayOrThrow<QueryShortcut>` round-trips cleanly outside SSMS's own package context (generic
methods resolved via IL token are usually fine, but service availability timing during VSIX
`InitializeAsync` is the kind of thing that only shows up live). **Confidence on the file + JSON
shape (§2.1): HIGH** — read directly, not inferred.

**What would settle the MEDIUM part:** from the real extension, at package init, try both routes —
(a) call `Package.GetGlobalService(typeof(SVsUnifiedSettingsManager))` and see if it resolves and
returns a non-null reader; (b) as a zero-dependency fallback, just read and JSON-parse
`%LOCALAPPDATA%\Microsoft\SSMS\<version>_<hash>\settings.json` directly (the hash suffix is
per-install; enumerate `%LOCALAPPDATA%\Microsoft\SSMS\` for a `22.*` folder). Given (b) requires
no VS services, no reflection into internal SSMS types, and is already proven to contain the right
data on this machine, **it's the recommended primary path**, with (a) as a nicer alternative if
it turns out to resolve reliably.

---

## Q3 — What exactly gets executed?

> **Answer: usually `fragment + " " + selection`, but with a real exception** — if the fragment
> is a stored procedure that itself takes exactly one string parameter (SQL Server metadata
> lookup, at composition time), the selection is wrapped in quotes instead:
> `fragment + " '" + selection + "'"`-shaped, with some extra care for a selection that's already
> quoted or itself a single bracketed/quoted token. **With no selection, the fragment runs alone,
> completely unmodified** (e.g. `sp_help` by itself, or a bare `SELECT TOP(100) * FROM ` with
> nothing appended if nothing is selected).
>
> **Confidence: HIGH on the general rule and the no-selection case (both read directly from IL).
> MEDIUM on getting the quoting special-case exactly byte-for-byte right** — the branch structure
> is clear but has several sub-cases (already-quoted token, length-1 tokens, etc.) that would need
> a live test matrix to fully pin down.

### 3.1 `ExecuteSpMacroHelper(string fragment)` — IL walk (`SqlScriptEditorControl`, private)

```
if (String.IsNullOrEmpty(fragment)) return false;          // empty shortcut slot: no-op
if (!EnsureConnectionForResults()) return false;
selectedText = m_Editor.SelectedTextOnly;                   // ShellCodeWindowControl.SelectedTextOnly
if (selectedText != null && selectedText.Length > 0) {
    // Only for THIS composition: ask the server whether `fragment` is itself a stored
    // procedure taking a single string argument (via sp_sproc_columns, then a
    // master.dbo fallback) — CheckStoredProcForSingleStringArgument(fragment).
    if (fragment "is a single-string-arg proc") {
        sb = "'" + selectedText (trimmed/already-quoted cases handled) + "'";
        finalText = fragment + " " + sb;                    // e.g. sp_help 'dbo.Foo'
    } else {
        finalText = fragment + " " + selectedText;           // plain case, e.g. SELECT TOP(100) * FROM Foo
    }
} else {
    finalText = fragment;                                    // unchanged — no selection
}
StandardPrepareBeforeExecute(...);
DoScriptExec(false, false, false, false, finalText,
             new TextSpan(0, 0, 0, 0, finalText, m_Editor.CurrentView));   // §Q4
```

The metadata round-trip (`CheckStoredProcForSingleStringArgument`, which runs
`exec dbo.sp_sproc_columns @procedure_name = N'{fragment}', ...` and a `master.dbo` fallback) is
itself a **live query against whatever database the query window is connected to** — so SSMS is
making a real server call during composition, purely to decide the punctuation. This is a notable
wrinkle: **an offline reconstruction of the composed text (Q2 fragment + Q3 rule) cannot always be
done without also replicating this metadata check**, unless the extension is willing to accept
"space-joined" as an approximation and only worry about exact byte-for-byte text for cases where
it actually matters (e.g. Go to source, which just needs the table name, not exact punctuation).

### 3.2 No-selection case, confirmed

The whole composition block is skipped when `selectedText` is null/empty — `finalText` stays as
the original `fragment` argument, unmodified, and that's what reaches `DoScriptExec`. So yes:
`Alt+F1` with nothing selected really does run `sp_help` bare.

---

## Q4 — Is the executed text observable anywhere?

> **Answer: yes, structurally — the composed text is carried as a public `.Text` property on a
> public `ITextSpan` object passed straight into `IQueryExecutionHandler.Run(ITextSpan, ...)`,
> the same entry point `docs/query-history-api.md` already reaches via reflection. But nothing
> caches it anywhere accessible afterward, and the completion/start events carry no text** — so
> observing it means intercepting the `Run` call itself, not just subscribing to an event.
>
> **Confidence: HIGH that `ITextSpan.Text` carries the exact composed string at the moment of the
> call (read directly from IL — `ValidateAndRun` calls `ITextSpan::get_Text()` and passes the
> literal string straight through to `QESQLExec.Execute`). MEDIUM-LOW on whether the interception
> technique below is practical** — it has not been tried against a running SSMS process, and it's
> materially more invasive than the `ScriptExecutionCompleted` hook the Query History spike
> got away with.

### 4.1 The type that carries it

`Microsoft.SqlServer.Management.UI.VSIntegration.TextSpan` (**public class**, implements the
**public** `Microsoft.SqlServer.Management.QueryExecution.ITextSpan`):

```csharp
public class TextSpan : ITextSpan {
    public int AnchorLine { get; }
    public int AnchorCol { get; }
    public int EndLine { get; }
    public int EndCol { get; }
    public string Text { get; set; }              // <-- the composed macro text lives here
    public IVsTextView VsTextView { get; }
    public TextSpan(int, int, int, int, string, IVsTextView);   // the ctor OnCustomMacro's
                                                                  // helper calls, with (0,0,0,0)
                                                                  // coordinates — a "synthetic"
                                                                  // span that carries text, not
                                                                  // a real buffer selection
}
```

`DisplaySQLResultsControl.ValidateAndRun` (the real implementation behind
`IQueryExecutionHandler.Run`) calls `textSpan.Text` directly (not the buffer) to get the string it
hands to the execution engine — confirmed by IL:

```
ldarg.1 ; callvirt ITextSpan::get_Text ; callvirt String::get_Length   // empty-text validation
...
ldarg.0 ; ldarg.1 ; callvirt ITextSpan::get_Text
call     DisplaySQLResultsControl::OutputQueryIntoMessages   // (only if "include query" is on)
...
ldfld    DisplaySQLResultsControl::ScriptExecutionStarted
callvirt EventHandler::Invoke                                 // fires with EventArgs.Empty — no text
...
callvirt QESQLExec::Execute                                    // consumes the span, incl. .Text
```

### 4.2 Why this doesn't turn into a cheap event hook

`ScriptExecutionStarted` is a plain `EventHandler` (`(object, EventArgs.Empty)`) — it fires
*during* `Run`, in the same call, but its args carry nothing. `ScriptExecutionCompletedEventArgs`
(from `docs/query-history-api.md`) has `ExecutionResult`/`WithEstimatedPlan` only. Neither
Query-History spike's reflection hook has a slot for the text. No field on
`DisplaySQLResultsControl` caches the last `ITextSpan` or the last executed string for later
reading either (checked: no `m_currentSpan`/`lastQuery`-shaped field — there is an unrelated
public `executionQuery` field, but nothing wrote into it from this call path in the IL reviewed).

### 4.3 The one real interception route

`ScriptAndResultsEditorControl.GetQueryExecutionHandler()` (internal, one-line: returns
`m_sqlResultsControl` cast to `IQueryExecutionHandler` — same field the Query History spike
already reflects onto) is called **fresh, every time**, right before `.Run(...)` is invoked
(`DoScriptExec` IL: `callvirt GetQueryExecutionHandler(); ...; callvirt IQueryExecutionHandler::Run`).
That means a package could, at query-window init, **reflectively replace the private
`m_sqlResultsControl` field with a decorator** that implements `IQueryExecutionHandler`, forwards
every call to the real `DisplaySQLResultsControl`, and — in its own `Run(ITextSpan span, ...)` —
reads `span.Text` before forwarding. This would give the exact final text for *every* execution
path (shortcuts, Execute, Execute Current Statement, debugger) uniformly, not just shortcuts.

This is strictly more invasive than the existing `CommandEvents` hook
(`ExecutedQueryTextTracker.cs`) or the `ScriptExecutionCompleted` reflection hook already
documented: it requires swapping a private field with a proxy object rather than just subscribing
to an event, and needs to happen early enough (before the first execution) that the swap is in
place. It has **not been tried** here — no SSMS process was driven.

**What would settle this:** in a scratch VSIX, reflect `SqlScriptEditorControl.m_sqlResultsControl`,
wrap it in a `DispatchProxy`/hand-written decorator implementing `IQueryExecutionHandler`, set the
field back, then press a query shortcut over a selection and confirm the decorator's `Run` sees
the exact composed text (including the quoting special-case from §Q3) before the real control does.

---

## Recommendation

1. **Don't try to catch query shortcuts via `Query.Execute`/`CommandEvents` the way the tracker
   does today** — they don't go through that command at all (§Q1). If DTE command hooking is
   still wanted, hook `{52692960-56BC-4989-B5D3-94C47A513E8D}` / ids `104`-`115` instead (§1.1),
   but treat this as unverified until tested live.
2. **For knowing *which* fragment maps to which key, read
   `%LOCALAPPDATA%\Microsoft\SSMS\22.*\settings.json` → `environment.keyboard.queryShortcuts.shortcuts`
   directly** (§Q2) — it's a plain JSON file, no registry hive, no COM service dependency, and
   it's already proven to hold real per-user data on this machine. Treat
   `UnifiedSettingsService`/`SVsUnifiedSettingsManager` as a nicer-if-it-works alternative, not
   the primary plan.
3. **Don't try to byte-for-byte reconstruct the composed text from Q2 fragment + selection**
   (§Q3) — the space-vs-quoted-argument branch depends on a live `sp_sproc_columns` check against
   whatever the query window is connected to at the moment, which a passive extension can't
   replicate without duplicating that same server round-trip. If a feature only needs "what table
   was this," a naive `fragment + " " + selection` (or just the selection alone) is good enough;
   if it needs the literal executed text, that requirement should be downgraded to best-effort.
4. **If exact executed text is truly required** (not just "what table"), the only structurally
   sound route is the `IQueryExecutionHandler` decorator in §4.3 — reflectively wrap
   `m_sqlResultsControl` and read `ITextSpan.Text` inside `Run`. This is more invasive than
   anything the Query History or Results Grid spikes needed, is unverified against a live process,
   and should be treated as a stretch goal, not a first cut. A pragmatic middle ground: combine
   #2 (know the fragment) with #1 (know a shortcut fired, and roughly when) to label history
   entries as "ran via query shortcut (Ctrl+N)" without claiming to know the exact composed SQL.
