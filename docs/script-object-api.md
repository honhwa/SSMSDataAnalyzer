# Script object (F12 / Ctrl+click) — SSMS 22 API spike

Spike for Feature 7. Everything below comes from SSMS 22 install metadata (pkgdefs, `spikes/OeProbe`
IL and member dumps) plus a scratch net472 harness that ran our real scripting code against the
local server using SSMS's own SMO DLLs. Nothing here was verified inside a live SSMS session yet.
The last section lists what still needs a live check.

## a. F12 in the SQL editor — what it is and how we take it over

- **Today:** F12 is VS's `Edit.GoToDefinition` (VSStd97 `cmdidGotoDefn` 935, Global/Text Editor
  scope). The T-SQL editor's language service is `RadLangSvc` (legacy MPF, `Languages\Language Services\SQL`
  → `{c4d96929-…}`). MPF's ViewFilter routes GotoDefn to `AuthoringScope.Goto`. The IL of
  `RadLangSvc.AuthoringScope::Goto` is `initobj; ldnull; ret`, so **F12 does nothing in a query
  window today.** *High confidence.*
- **Scope:** `Extensions\Application\SQLEditors.pkgdef` registers
  `[$RootKey$\Editors\{B5A506EB-11BE-4782-9A18-21265C2CA0B4}] @="SQL Query Editor"`. That is the
  key-binding scope SSMS itself uses for query-window shortcuts. SSMS 22's copy of SQL Prompt 11
  registers only its package and menus in its pkgdef, with no editor scope of its own.
- **Chosen:** a `.vsct` `<KeyBinding editor="{B5A506EB-…}" key1="VK_F12">` for our command
  `SsmsDataAnalyzer.ScriptObjectAsAlter` (0x0302). Editor-scoped bindings outrank Global/Text
  Editor ones while that editor is active. This is independent of the keyboard scheme (default
  `.vsct` bindings are merged into every scheme), is visible and removable under Tools > Options >
  Keyboard, and leaves F12 alone everywhere else. *Medium-high confidence (the standard VS
  mechanism; not yet exercised in SSMS).*
- **Fallback:** DTE `CommandEvents["Edit.GoToDefinition"].BeforeExecute` sets `CancelDefault`
  only when `DTE.ActiveDocument` walks to a `SqlScriptEditorControl`, which is the same pattern as
  `ExecutedQueryTextTracker`. Because MPF reports GotoDefn as enabled, the event fires. It is
  harmless because the default action is a no-op there. We did not use an `IOleCommandTarget`
  filter: it would need per-view attach/detach and adds nothing over the two mechanisms above.
- The command is also on the query-editor context menu (`GUID_SQLEditorGroup:0x0050`, same group
  as Paste as SQL IN) and on Tools.

## b. Ctrl+click — editor type and hook

- `SqlScriptEditorControl` → `ScriptEditorControl` implements `IVsCodeWindow`, so the text area
  is the standard VS WPF editor (`IWpfTextView` via `IVsEditorAdaptersFactoryService`). SQLEditors.dll
  is declared as a `MefComponent` in `Extensions\Application\extension.vsixmanifest`. It consumes
  `ContentTypeAttribute`/`DropFormatAttribute` (IDropHandlerProvider), and the only custom-attribute
  string in it that is a content-type name is **`"SQL"`**. That matches the language-service
  name. *Content type "SQL": medium-high confidence.*
- **Chosen:** `IMouseProcessorProvider` with `[ContentType("text")]` and
  `[TextViewRole(Document)]`. At click time we check `ContentType.IsOfType("SQL")`, or an
  `ITextDocument` path ending in `.sql`, and log the real content-type name once. A wrong guess
  about the name therefore can't disable the feature. We only take a single Ctrl+left-click whose
  position lexes to a scriptable name. We mark down and up as `Handled`, so there's no word
  selection and no caret jump. Everything else falls through.
- **VS Ctrl+click Go To Definition:** the platform side (`INavigableSymbolSource`,
  `Microsoft.VisualStudio.Platform.VSEditor`) ships in SSMS 22. It needs a navigable-symbol provider
  for the content type. None exists for SQL (Roslyn's lives under VBCSharp and only covers C#/VB).
  In SQL windows, Ctrl+click is therefore the editor's plain word-select, and we preempt it only on
  object names. *Medium confidence: SQL Prompt, if it adds one, could compete.*

## c. Connection and current database of the active editor

- `DTE.ActiveDocument` → `DataAnalyzerPackage.FindSqlScriptEditorControl` (`Window.Object` or
  HWnd walk) → public `SqlScriptEditorControl.Connection` (`UIConnectionInfo`) and `IsConnected`.
- **Live database:** `ScriptAndResultsEditorControl.CurrentDB` is `protected`.
  `SqlScriptEditorControl::get_CurrentDB` IL returns `m_connection.Database` when
  `m_connection.State == Open`, so it follows `USE` and the database picker. We read it by
  reflection and fall back to `AdvancedOptions["DATABASE"]` (the connect-time database). *High
  confidence.*
- The connection string comes from the existing `GridConnectionInfo.TryBuild` (Amendment 13
  pattern). Entra/token connections are declined with a status message. Nothing about the
  connection or the script is logged.

## d. Scripting

- Host assemblies (IDE root), all **18.100.0.0** (file 18.100.11.36): `Microsoft.SqlServer.Smo`,
  `SmoExtended`, `SqlEnum`, `ConnectionInfo`, `Management.Sdk.Sfc`. `Scripter`,
  `ScriptingOptions` and `ScriptMaker` are in `Smo.dll`. `ServerConnection(SqlConnection)` takes
  Microsoft.Data.SqlClient. Smo/ConnectionInfo/Sfc were already `Private=false` references in the
  VSIX project, and the built `.vsix` contains no SMO DLL.
- **Finding (harness, SMO 18.100):** `ScriptingOptions.ScriptForAlter` is not usable directly.
  - `Scripter` ignores it and emits CREATE.
  - `obj.Script(ScriptForAlter)` returns **0 batches**.
  - SSMS's own `ObjectExplorer.ScriptGenerator::ScriptAlter` (IL) calls `Touch()` then
    `ScriptMaker` + `Preferences.ScriptForAlter`. That produces batches, but the stored text
    header still says `CREATE`, unless `TextMode=false`, which regenerates the header from metadata.
- **Chosen:**
  1. Resolve the name with `OBJECT_ID(@name)` / `TYPE_ID(@name)` (parameterized, bracketed
     two-part argument, connection in the target database, so SQL Server's own default-schema
     resolution applies).
  2. Script with `Scripter` as CREATE, using SSMS-like options: headers, schema-qualified, DriAll,
     indexes, triggers, full-text, extended properties, no collation, no permissions, no
     dependencies.
  3. For ALTER, turn off indexes, triggers, DRI and extended properties, then rewrite the first
     batch whose first code token is `CREATE` (or `CREATE OR ALTER`) to `ALTER` by lexer token
     position (`ObjectScriptText.RewriteCreateBatchToAlter`).
  4. If SMO throws, fall back to `sys.sql_modules.definition` with the same rewrite.
- Harness results:
  - CREATE: seeded tables `[dbo].[Intervention.ABB.Request.Change.History]` and `[dbo].[FkChild]`
    (with its keys and indexes).
  - ALTER: msdb views `sysjobs_view` and `sysdtslog90`, proc `sp_help_job`, functions
    `agent_datetime` (scalar) and `managed_backup.fn_get_parameter` (TVF), and trigger
    `trig_targetserver_insert`. All start with `ALTER`, and the fallback agrees.
  - Tables with F12 get the "no ALTER form" first line.
  - The seeded DB has no views, procs or functions, and nothing was created, so msdb supplied the
    module cases.

## Needs live verification in SSMS 22

1. F12 in a query window runs our command. Check that the key-binding scope wins over the
   Global/Text Editor binding and over SQL Prompt, and that F12 in other windows is unchanged.
2. Ctrl+click reaches the mouse processor. Check the content type (the ActivityLog line only
   appears if it isn't "SQL"), and that there's no word selection or caret jump.
3. The new query window connects to the right database, and the popup's Esc and Copy work.
