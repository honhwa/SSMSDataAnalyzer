using System;
using EnvDTE;
using Microsoft.SqlServer.Management.Smo.RegSvrEnum;
using Microsoft.VisualStudio.Shell;

namespace SsmsDataAnalyzer.Vsix.History
{
    /// <summary>
    /// docs/query-history-api.md S-3 §3.3's noted gap: "finding an open window with the same
    /// server+login... doesn't exist yet as a reusable function". Walks every open DTE document,
    /// reads its <see cref="Microsoft.SqlServer.Management.UI.VSIntegration.Editors.SqlScriptEditorControl.Connection"/>
    /// (the same public surface DataAnalyzerPackage.FindSqlScriptEditorControl already exposes),
    /// and returns the first whose server (and, when given, login) match -- so "Open in new
    /// query window" can copy a REAL, already-authenticated <see cref="UIConnectionInfo"/>
    /// (CONTRACT.md Amendment 13: never reconstruct credentials, only inherit) instead of
    /// building one with no password.
    /// </summary>
    internal static class QueryHistoryConnectionFinder
    {
        /// <param name="server">Required -- matched case-insensitively.</param>
        /// <param name="login">Optional. When non-empty, only a window authenticated as this
        /// exact login (case-insensitive) matches -- a SQL-login history entry must never be
        /// reopened under a different login's session. When empty (Windows/unknown auth), any
        /// live connection to the server matches -- the current Windows identity is what will
        /// actually be used either way.</param>
        public static UIConnectionInfo TryFind(EnvDTE.DTE dte, string server, string login)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (dte == null || string.IsNullOrEmpty(server)) return null;

            try
            {
                foreach (Document document in dte.Documents)
                {
                    UIConnectionInfo ci;
                    try
                    {
                        var editor = DataAnalyzerPackage.FindSqlScriptEditorControl(document);
                        ci = editor?.Connection;
                    }
                    catch
                    {
                        continue;
                    }

                    if (ci == null || string.IsNullOrEmpty(ci.ServerName)) continue;
                    if (!string.Equals(ci.ServerName, server, StringComparison.OrdinalIgnoreCase)) continue;

                    if (!string.IsNullOrEmpty(login) &&
                        !string.Equals(ci.UserName, login, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    return ci;
                }
            }
            catch (Exception ex)
            {
                ObjectExplorer.OeDiagnostics.Warn("Query history: looking for a matching open query window failed (" + ex.GetType().Name + ") -- will open disconnected/prompt instead.");
            }

            return null;
        }
    }
}
