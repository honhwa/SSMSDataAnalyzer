using System;
using System.ComponentModel.Design;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.Management.Smo.RegSvrEnum;
using Microsoft.SqlServer.Management.UI.VSIntegration.Editors;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text.Editor;
using SsmsDataAnalyzer.Core.Model;
using SsmsDataAnalyzer.Core.ScriptObject;
using SsmsDataAnalyzer.Vsix.ObjectExplorer;
using SsmsDataAnalyzer.Vsix.ResultsGrid;
using SsmsDataAnalyzer.Vsix.ScriptObject;
using SsmsDataAnalyzer.Vsix.ToolWindow;
using Task = System.Threading.Tasks.Task;

namespace SsmsDataAnalyzer.Vsix.QueryEditor
{
    /// <summary>
    /// "Analyze Data..." on the query editor's right-click menu (default Ctrl+Alt+Q, T): profiles
    /// the table under the caret, so it does not have to be found in Object Explorer first (user
    /// request). Same window and same profile as Object Explorer's right-click; only where the
    /// table and the connection come from differs.
    ///
    /// The name is found exactly as F12 finds it (<see cref="ScriptObjectController.Lookup"/>,
    /// so [bracketed], schema-qualified and three-part names all work), and the query window's
    /// own connection and current database are reused (CONTRACT.md Amendment 13), never logged.
    /// With text selected, the name at the START of the selection is used, so selecting a table
    /// name and pressing the shortcut does what it looks like.
    ///
    /// The name is resolved on the server before anything runs: Analyze Data profiles TABLES,
    /// and a view, procedure or misspelt name is reported plainly in the window rather than
    /// attempted.
    /// </summary>
    internal static class AnalyzeTableAtCaretCommand
    {
        private const string StatusPrefix = "Analyze Data: ";

        private static DataAnalyzerPackage _package;

        public static void Initialize(DataAnalyzerPackage package, OleMenuCommandService commandService)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (package == null || commandService == null) return;
            _package = package;

            var command = new OleMenuCommand(
                (s, e) => { ThreadHelper.ThrowIfNotOnUIThread(); Execute(); },
                new CommandID(PackageGuids.CommandSetGuid, PackageIds.AnalyzeTableAtCaretCommandId));
            command.BeforeQueryStatus += (s, e) =>
            {
                // Never hidden (same cold-session reasoning as PasteAsSqlInCommand); Execute
                // explains on the status bar when there is no table to analyze.
                if (s is OleMenuCommand c) { c.Visible = true; c.Enabled = true; }
            };
            commandService.AddCommand(command);
        }

        private static void Execute()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                SqlScriptEditorControl editor = ScriptObjectController.TryGetActiveSqlEditor(out IWpfTextView view, out _);
                if (editor == null || view == null)
                {
                    ScriptObjectController.SetStatus(StatusPrefix + "place the caret on a table name in a SQL query window.");
                    return;
                }

                int position = view.Selection.IsEmpty
                    ? view.Caret.Position.BufferPosition.Position
                    : view.Selection.Start.Position.Position;

                SqlNameLookupResult lookup = ScriptObjectController.Lookup(view.TextSnapshot, position);
                if (!lookup.Found)
                {
                    ScriptObjectController.SetStatus(StatusPrefix + lookup.Message);
                    return;
                }

                SqlMultipartName name = lookup.Name;
                if (name.Server != null)
                {
                    ScriptObjectController.SetStatus(StatusPrefix + "four-part (linked server) names are not supported: " + name + ".");
                    return;
                }
                if (name.Name.StartsWith("#", StringComparison.Ordinal) || name.Name.StartsWith("@", StringComparison.Ordinal))
                {
                    ScriptObjectController.SetStatus(StatusPrefix + name + " is a temporary table or table variable; only permanent tables can be analyzed.");
                    return;
                }

                UIConnectionInfo connectionInfo = editor.Connection;
                if (connectionInfo == null || !editor.IsConnected)
                {
                    ScriptObjectController.SetStatus(StatusPrefix + "this query window is not connected.");
                    return;
                }

                string database = name.Database ?? ScriptObjectController.GetCurrentDatabase(editor, connectionInfo);
                if (string.IsNullOrEmpty(database))
                {
                    ScriptObjectController.SetStatus(StatusPrefix + "could not tell which database this query window is using.");
                    return;
                }

                if (!GridConnectionInfo.TryBuild(connectionInfo, database, out string lookupConnectionString))
                {
                    ScriptObjectController.SetStatus(StatusPrefix + "this connection's sign-in type (e.g. Microsoft Entra token) cannot be reused yet. Use Object Explorer → right-click the table → Analyze Data… instead.");
                    return;
                }

                string server = connectionInfo.ServerName;
                ScriptObjectController.SetStatus(StatusPrefix + "looking up " + name + "...");

                _package.JoinableTaskFactory.RunAsync(async () =>
                {
                    // Open the window first, as Object Explorer's path does: every outcome below,
                    // including "that is a view", then has somewhere visible to be reported.
                    ProfileViewModel viewModel = await _package.ShowProfileWindowAsync();
                    if (viewModel == null) return;

                    (ResolvedSqlObject obj, string error) = await Task.Run(() => ResolveAsync(lookupConnectionString, database, name)).ConfigureAwait(false);
                    await _package.JoinableTaskFactory.SwitchToMainThreadAsync();

                    if (obj == null)
                    {
                        viewModel.ReportObjectExplorerFailure(error);
                        ScriptObjectController.SetStatus(StatusPrefix + error);
                        return;
                    }

                    if (obj.Kind != SqlObjectKind.Table)
                    {
                        string message = obj.QualifiedName + " is a " + SqlObjectKinds.DisplayName(obj.Kind).ToLowerInvariant()
                            + ". Analyze Data works on tables.";
                        viewModel.ReportObjectExplorerFailure(message);
                        ScriptObjectController.SetStatus(StatusPrefix + message);
                        return;
                    }

                    // The profile runs in the table's own database (a three-part name can point
                    // somewhere other than the window's current database).
                    if (!GridConnectionInfo.TryBuild(connectionInfo, obj.Database, out string connectionString))
                    {
                        viewModel.ReportObjectExplorerFailure("this query window's connection could not be reused.");
                        return;
                    }

                    var table = new TableRef { Server = server, Database = obj.Database, Schema = obj.Schema, Name = obj.Name };
                    OeDiagnostics.Info("'Analyze Data' started from a query window, using its own connection.");
                    viewModel.LoadFromObjectExplorer(table, connectionString);
                    ScriptObjectController.SetStatus(StatusPrefix + "analyzing " + obj.QualifiedName + ".");
                }).FileAndForget("SsmsDataAnalyzer/AnalyzeTableAtCaret/Execute");
            }
            catch (Exception ex)
            {
                OeDiagnostics.Error("Analyze Data from the query editor failed", ex);
                ScriptObjectController.SetStatus(StatusPrefix + "failed (" + ex.GetType().Name + ").");
            }
        }

        /// <summary>Background thread. Messages carry object names only, never connection
        /// details.</summary>
        private static async Task<(ResolvedSqlObject Object, string Error)> ResolveAsync(
            string connectionString, string database, SqlMultipartName name)
        {
            try
            {
                using (var connection = new SqlConnection(connectionString))
                {
                    await connection.OpenAsync().ConfigureAwait(false);
                    ResolvedSqlObject obj = await ScriptObjectQueries.ResolveAsync(connection, name, CancellationToken.None).ConfigureAwait(false);
                    return obj != null
                        ? (obj, null)
                        : (null, name + " was not found in " + Core.Sql.SqlIdentifier.Bracket(database) + ".");
                }
            }
            catch (Exception ex)
            {
                OeDiagnostics.Error("Analyze Data: resolving the table under the caret failed (" + ex.GetType().Name + ").");
                return (null, "could not look up " + name + " (" + ex.GetType().Name + ": " + ex.Message + ").");
            }
        }
    }
}
