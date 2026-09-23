using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE;
using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.Management.Smo.RegSvrEnum;
using Microsoft.SqlServer.Management.UI.VSIntegration.Editors;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using SsmsDataAnalyzer.Core.ScriptObject;
using SsmsDataAnalyzer.Vsix.GoToSource;
using SsmsDataAnalyzer.Vsix.ObjectExplorer;
using SsmsDataAnalyzer.Vsix.ResultsGrid;
using Task = System.Threading.Tasks.Task;

namespace SsmsDataAnalyzer.Vsix.ScriptObject
{
    /// <summary>
    /// "Script object" in SQL query windows (docs/script-object-api.md):
    /// <list type="bullet">
    /// <item>F12 / "Script object as ALTER" — the object under the caret, as ALTER (CREATE with a
    /// first-line note when it has no ALTER form), in a NEW query window on the same connection
    /// and database. Never executed.</item>
    /// <item>Ctrl+click (<see cref="CtrlClickMouseProcessorProvider"/>) — the clicked object's
    /// CREATE script in the floating <see cref="ScriptObjectToolWindow"/>.</item>
    /// </list>
    ///
    /// F12 reaches <see cref="PackageIds.ScriptObjectAsAlterCommandId"/> through a KeyBinding
    /// scoped to SSMS's "SQL Query Editor" (VSCommandTable.vsct), so it does nothing anywhere else.
    /// As a fallback (e.g. a keyboard scheme that dropped our binding) Edit.GoToDefinition — what
    /// F12 means elsewhere, and a no-op in SSMS's T-SQL editor (RadLangSvc AuthoringScope.Goto
    /// returns null) — is intercepted, but ONLY while the active document is a SQL query window.
    ///
    /// Connection handling is CONTRACT.md Amendment 13's: the editor's own UIConnectionInfo is
    /// inherited (GridConnectionInfo), never logged; neither script text nor credentials are ever
    /// written to the ActivityLog. Database work runs off the UI thread.
    /// </summary>
    internal static class ScriptObjectController
    {
        private const string StatusPrefix = "Script object: ";

        /// <summary>Beyond this, only the caret's line is tokenized (keeps a Ctrl+click on a huge
        /// script instant; a name never spans lines in practice).</summary>
        private const int MaxLookupTextLength = 2_000_000;

        private static AsyncPackage _package;
        private static CommandEvents _goToDefinitionEvents; // keep the COM event sink alive
        private static int _requestVersion;

        public static bool IsInitialized => _package != null;

        public static void Initialize(AsyncPackage package, OleMenuCommandService commandService)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (package == null) return;
            _package = package;

            if (commandService != null)
            {
                var command = new OleMenuCommand(
                    (s, e) => { ThreadHelper.ThrowIfNotOnUIThread(); ExecuteFromActiveEditor(forAlter: true); },
                    new CommandID(PackageGuids.CommandSetGuid, PackageIds.ScriptObjectAsAlterCommandId));
                command.BeforeQueryStatus += (s, e) =>
                {
                    // Enable/disable only, never hide (same reasoning as PasteAsSqlInCommand).
                    if (s is OleMenuCommand c) { c.Visible = true; c.Enabled = true; }
                };
                commandService.AddCommand(command);
            }

            HookGoToDefinition();
        }

        private static void HookGoToDefinition()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var dte = ((IServiceProvider)_package).GetService(typeof(DTE)) as DTE;
                if (dte == null) return;
                Command command = dte.Commands.Item("Edit.GoToDefinition", -1);
                _goToDefinitionEvents = dte.Events.CommandEvents[command.Guid, command.ID];
                _goToDefinitionEvents.BeforeExecute += OnBeforeGoToDefinition;
                OeDiagnostics.Info("Script object: Edit.GoToDefinition fallback hooked for SQL query windows.");
            }
            catch (Exception ex)
            {
                OeDiagnostics.Warn("Script object: could not hook Edit.GoToDefinition (the F12 key binding still applies): " + ex.GetType().Name);
            }
        }

        private static void OnBeforeGoToDefinition(string guid, int id, object customIn, object customOut, ref bool cancelDefault)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                if (TryGetActiveSqlEditor(out _, out _) == null) return; // not a SQL query window: untouched
                cancelDefault = true;
                ExecuteFromActiveEditor(forAlter: true);
            }
            catch (Exception ex)
            {
                OeDiagnostics.Error("Script object: Go To Definition interception failed", ex);
            }
        }

        /// <summary>F12 / menu: the object at the caret of the active SQL query window.</summary>
        private static void ExecuteFromActiveEditor(bool forAlter)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                SqlScriptEditorControl editor = TryGetActiveSqlEditor(out IWpfTextView view, out _);
                if (editor == null || view == null)
                {
                    SetStatus(StatusPrefix + "place the caret on an object name in a SQL query window.");
                    return;
                }

                int caret = view.Caret.Position.BufferPosition.Position;
                Start(editor, view.TextSnapshot, caret, forAlter);
            }
            catch (Exception ex)
            {
                OeDiagnostics.Error("Script object (F12) failed", ex);
                SetStatus(StatusPrefix + "failed (" + ex.GetType().Name + ": " + ex.Message + ").");
            }
        }

        /// <summary>Ctrl+click from <see cref="CtrlClickMouseProcessor"/>, dispatched after the
        /// click so the clicked window is the active one.</summary>
        internal static void ExecuteCtrlClick(IWpfTextView clickedView, ITextSnapshot snapshot, int position)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                SqlScriptEditorControl editor = TryGetActiveSqlEditor(out IWpfTextView activeView, out _);
                if (editor == null || activeView == null || !ReferenceEquals(activeView, clickedView))
                {
                    SetStatus(StatusPrefix + "Ctrl+click works in a connected SQL query window.");
                    return;
                }
                Start(editor, snapshot, position, forAlter: false);
            }
            catch (Exception ex)
            {
                OeDiagnostics.Error("Script object (Ctrl+click) failed", ex);
                SetStatus(StatusPrefix + "failed (" + ex.GetType().Name + ": " + ex.Message + ").");
            }
        }

        /// <summary>Pure name lookup for the mouse processor's "should I take this click" test.</summary>
        internal static SqlNameLookupResult Lookup(ITextSnapshot snapshot, int position)
        {
            string text = GetLookupText(snapshot, position, out int relative);
            return SqlNameAtPosition.Find(text, relative);
        }

        private static void Start(SqlScriptEditorControl editor, ITextSnapshot snapshot, int position, bool forAlter)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            SqlNameLookupResult lookup = Lookup(snapshot, position);
            if (!lookup.Found)
            {
                SetStatus(StatusPrefix + lookup.Message);
                return;
            }

            SqlMultipartName name = lookup.Name;
            if (name.Server != null)
            {
                SetStatus(StatusPrefix + "four-part (linked server) names are not supported: " + name + ".");
                return;
            }

            UIConnectionInfo connectionInfo = editor.Connection;
            if (connectionInfo == null || !editor.IsConnected)
            {
                SetStatus(StatusPrefix + "this query window is not connected.");
                return;
            }

            string database = name.Database ?? GetCurrentDatabase(editor, connectionInfo);
            if (string.IsNullOrEmpty(database))
            {
                SetStatus(StatusPrefix + "could not tell which database this query window is using.");
                return;
            }

            if (!GridConnectionInfo.TryBuild(connectionInfo, database, out string connectionString))
            {
                SetStatus(StatusPrefix + "this connection's sign-in type (e.g. Microsoft Entra token) cannot be reused for scripting yet.");
                return;
            }

            int version = Interlocked.Increment(ref _requestVersion);
            SetStatus(StatusPrefix + "scripting " + name + "...");

            _package.JoinableTaskFactory.RunAsync(async () =>
            {
                // Ctrl+click: open the popup NOW, in its waiting state. Resolving the object and
                // scripting it is a server round trip, and until v0.19.0 nothing at all appeared
                // for those seconds, so the click looked like it had been ignored (field report).
                ScriptObjectToolWindow pending = forAlter ? null : await ShowPendingPopupAsync(name.ToString());

                ScriptOutcome outcome = await Task.Run(() => ScriptAsync(connectionString, database, name, forAlter)).ConfigureAwait(false);
                await _package.JoinableTaskFactory.SwitchToMainThreadAsync();

                if (version != Volatile.Read(ref _requestVersion)) return; // superseded by a newer F12/Ctrl+click

                if (!outcome.Success)
                {
                    SetStatus(StatusPrefix + outcome.Message);
                    pending?.BindFailed(name.ToString(), "Could not script " + name + " — see the status bar.   ·   Esc to close");
                    return;
                }

                string what = outcome.Object.QualifiedName + " (" + SqlObjectKinds.DisplayName(outcome.Object.Kind) + ")";
                if (forAlter)
                {
                    bool alter = SqlObjectKinds.SupportsAlter(outcome.Object.Kind);
                    QueryWindowOpenResult opened = await QueryWindowAccessor
                        .TryOpenAsync(outcome.Script, connectionString, connectionInfo, allowAutoExecute: false)
                        .ConfigureAwait(true);
                    SetStatus(opened.Success
                        ? StatusPrefix + what + " scripted as " + (alter ? "ALTER" : "CREATE (no ALTER form)") + " in a new query window."
                        : StatusPrefix + "could not open a new query window: " + opened.Reason);
                }
                else
                {
                    await ShowPopupAsync(outcome, connectionInfo, pending);
                    SetStatus(StatusPrefix + "CREATE script for " + what + ".");
                }
            }).FileAndForget("SsmsDataAnalyzer/ScriptObject/Start");
        }

        private sealed class ScriptOutcome
        {
            public bool Success;
            public string Message;
            public string Script;
            public ResolvedSqlObject Object;

            public static ScriptOutcome Fail(string message) => new ScriptOutcome { Message = message };
        }

        /// <summary>Background thread. Messages carry object names only — never script text or
        /// connection details.</summary>
        private static async Task<ScriptOutcome> ScriptAsync(string connectionString, string database, SqlMultipartName name, bool forAlter)
        {
            try
            {
                using (var connection = new SqlConnection(connectionString))
                {
                    await connection.OpenAsync().ConfigureAwait(false);

                    ResolvedSqlObject obj = await ScriptObjectQueries.ResolveAsync(connection, name, CancellationToken.None).ConfigureAwait(false);
                    if (obj == null)
                        return ScriptOutcome.Fail(name + " was not found in " + Core.Sql.SqlIdentifier.Bracket(database) + ".");
                    if (obj.Kind == SqlObjectKind.Unsupported)
                        return ScriptOutcome.Fail(obj.QualifiedName + " is a " + obj.TypeDescription + ", which is not scripted on its own.");

                    IReadOnlyList<string> batches;
                    try
                    {
                        batches = SmoObjectScripter.Script(connection, obj, forAlter);
                    }
                    catch (Exception smoError)
                    {
                        OeDiagnostics.Error("Script object: SMO scripting failed; trying the sys.sql_modules fallback", smoError);
                        if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync().ConfigureAwait(false);
                        connection.ChangeDatabase(obj.Database);
                        batches = await ScriptObjectQueries.TryScriptFromDefinitionAsync(connection, obj, forAlter, CancellationToken.None).ConfigureAwait(false);
                        if (batches == null)
                            return ScriptOutcome.Fail("could not script " + obj.QualifiedName + " (" + smoError.GetType().Name + ": " + smoError.Message + ").");
                    }

                    string comment = forAlter && !SqlObjectKinds.SupportsAlter(obj.Kind)
                        ? SqlObjectKinds.NoAlterFormComment(obj.Kind)
                        : null;

                    return new ScriptOutcome
                    {
                        Success = true,
                        Object = obj,
                        Script = ObjectScriptText.Compose(obj.Database, batches, comment)
                    };
                }
            }
            catch (Exception ex)
            {
                OeDiagnostics.Error("Script object: resolving/scripting failed", ex);
                return ScriptOutcome.Fail("failed (" + ex.GetType().Name + ": " + ex.Message + ").");
            }
        }

        /// <summary>Shows the popup immediately, before the script exists, so a Ctrl+click has
        /// visible feedback while the server is queried. Returns null if it can't be shown, in
        /// which case the flow simply behaves as it did before.</summary>
        private static async Task<ScriptObjectToolWindow> ShowPendingPopupAsync(string objectName)
        {
            try
            {
                await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
                var pane = await _package.ShowToolWindowAsync(
                    typeof(ScriptObjectToolWindow), id: 0, create: true, cancellationToken: CancellationToken.None) as ScriptObjectToolWindow;
                pane?.BindPending(objectName);
                return pane;
            }
            catch (Exception ex)
            {
                OeDiagnostics.Warn("Script object: could not show the waiting popup (" + ex.GetType().Name + "); the script will still appear when it is ready.");
                return null;
            }
        }

        private static async Task ShowPopupAsync(ScriptOutcome outcome, UIConnectionInfo connectionInfo, ScriptObjectToolWindow pending = null)
        {
            await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
            var pane = pending ?? await _package.ShowToolWindowAsync(
                typeof(ScriptObjectToolWindow), id: 0, create: true, cancellationToken: CancellationToken.None) as ScriptObjectToolWindow;
            if (pane == null)
            {
                OeDiagnostics.Error("Script object: could not create/show the script popup.");
                return;
            }
            pane.Bind(outcome.Script, outcome.Object, connectionInfo);
        }

        /// <summary>The popup's "Open in new query window": same connection, the object's database,
        /// never executed. The connection string is rebuilt here, not kept by the popup.</summary>
        internal static async Task<bool> OpenInNewQueryWindowAsync(string script, UIConnectionInfo connectionInfo, string database)
        {
            await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (!GridConnectionInfo.TryBuild(connectionInfo, database, out string connectionString))
            {
                SetStatus(StatusPrefix + "this connection's sign-in type cannot be reused to open a query window.");
                return false;
            }
            QueryWindowOpenResult opened = await QueryWindowAccessor
                .TryOpenAsync(script, connectionString, connectionInfo, allowAutoExecute: false)
                .ConfigureAwait(true);
            SetStatus(opened.Success
                ? StatusPrefix + "opened the script in a new query window."
                : StatusPrefix + "could not open a new query window: " + opened.Reason);
            return opened.Success;
        }

        /// <summary>The active document's SQL editor control and its active WPF text view, or null
        /// when the active document is not a SQL query window.</summary>
        private static SqlScriptEditorControl TryGetActiveSqlEditor(out IWpfTextView view, out Document document)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            view = null;
            document = null;

            var serviceProvider = (IServiceProvider)_package;
            var dte = serviceProvider.GetService(typeof(DTE)) as DTE;
            document = dte?.ActiveDocument;
            if (document == null) return null;

            SqlScriptEditorControl editor = DataAnalyzerPackage.FindSqlScriptEditorControl(document);
            if (editor == null) return null;

            if (serviceProvider.GetService(typeof(SVsTextManager)) is IVsTextManager textManager
                && textManager.GetActiveView(1, null, out IVsTextView vsView) == 0
                && vsView != null
                && serviceProvider.GetService(typeof(SComponentModel)) is IComponentModel componentModel)
            {
                view = componentModel.GetService<IVsEditorAdaptersFactoryService>()?.GetWpfTextView(vsView);
            }
            return editor;
        }

        /// <summary>
        /// The window's LIVE database (reflects USE and the toolbar database picker):
        /// SqlScriptEditorControl.CurrentDB, a protected property whose getter is
        /// m_connection.Database while the connection is open (IL, docs/script-object-api.md).
        /// Falls back to the connection's initial database.
        /// </summary>
        private static string GetCurrentDatabase(SqlScriptEditorControl editor, UIConnectionInfo connectionInfo)
        {
            try
            {
                PropertyInfo property = editor.GetType().GetProperty("CurrentDB", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (property?.GetValue(editor) is string live && !string.IsNullOrEmpty(live)) return live;
            }
            catch (Exception ex)
            {
                OeDiagnostics.Warn("Script object: CurrentDB could not be read (" + ex.GetType().Name + "); using the connection's database.");
            }
            return connectionInfo.AdvancedOptions?["DATABASE"];
        }

        private static string GetLookupText(ITextSnapshot snapshot, int position, out int relativePosition)
        {
            if (snapshot.Length <= MaxLookupTextLength)
            {
                relativePosition = position;
                return snapshot.GetText();
            }
            ITextSnapshotLine line = snapshot.GetLineFromPosition(position);
            relativePosition = position - line.Start.Position;
            return line.GetText();
        }

        internal static void SetStatus(string message)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                if (_package == null) return;
                var statusBar = ((IServiceProvider)_package).GetService(typeof(SVsStatusbar)) as IVsStatusbar;
                if (statusBar == null) return;
                statusBar.FreezeOutput(0);
                statusBar.SetText(message);
            }
            catch (Exception ex)
            {
                OeDiagnostics.Error("Script object: setting the status bar text failed", ex);
            }
        }
    }
}
