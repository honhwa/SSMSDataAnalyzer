using System;
using Microsoft.SqlServer.Management.Common;
using Microsoft.SqlServer.Management.Sdk.Sfc;
using Microsoft.SqlServer.Management.UI.VSIntegration.ObjectExplorer;
using Microsoft.VisualStudio.Shell;
using SsmsDataAnalyzer.Vsix.ObjectExplorer;

namespace SsmsDataAnalyzer.Vsix.SourceControl
{
    /// <summary>
    /// Object Explorer as a connection source for the source-control check.
    ///
    /// Field report (v0.28.0 preview): every query window was disconnected while Object Explorer
    /// was connected to the very server the history named, so all 523 candidates came back
    /// "no open connection" and the modify_date scan was skipped entirely. Keeping Object
    /// Explorer connected all day and query windows closed is an ordinary way to work, so it has
    /// to count.
    ///
    /// Same reuse rule as everywhere else in the extension: the connection SSMS already holds
    /// (including a SQL login's in-memory password) builds the connection string, through the
    /// same <see cref="OeTableInfo.TryBuildConnectionString"/> Analyze Data uses. Nothing is
    /// stored and nobody is asked for a password.
    ///
    /// UI thread only: every call here goes through the Object Explorer service.
    /// </summary>
    internal static class ObjectExplorerConnectionFinder
    {
        /// <summary>
        /// A connection string for <paramref name="database"/> on <paramref name="server"/>,
        /// built from a connected Object Explorer server node, or null.
        ///
        /// Looks the server up by URN, which uses the REAL server name — in the field report
        /// Object Explorer displayed the connection as "Test8 - OLTP" while the server is
        /// SQLTEST8, so matching on display text would have missed it. Falls back to the node
        /// currently selected in Object Explorer when that node is on the same server.
        /// </summary>
        public static string TryBuildForServer(IServiceProvider serviceProvider, string server, string database)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (string.IsNullOrEmpty(server)) return null;

            var explorer = serviceProvider?.GetService(typeof(IObjectExplorerService)) as IObjectExplorerService;
            if (explorer == null) return null;

            INodeInformation node = null;
            try
            {
                node = explorer.FindNode("Server[@Name='" + server.Replace("'", "''") + "']");
            }
            catch (Exception ex)
            {
                // Not connected, or not expanded yet: try the selection instead.
                OeDiagnostics.Warn("Source control: Object Explorer server lookup failed (" + ex.GetType().Name + ").");
            }

            if (node == null || !IsOnServer(node, server))
            {
                INodeInformation selected = TryGetSelected(explorer);
                node = selected != null && IsOnServer(selected, server) ? selected : null;
            }

            if (node == null) return null;

            return OeTableInfo.TryBuildConnectionString(node, database, out string connectionString, out _)
                ? connectionString
                : null;
        }

        /// <summary>
        /// The database selected in Object Explorer, when a single node inside a database is
        /// selected on a connected server: the scope the modify_date scan uses when no query
        /// window is connected. In the field report that was exactly AgricultureFinances.
        /// </summary>
        public static bool TryGetSelectedDatabase(IServiceProvider serviceProvider,
            out string server, out string database, out string connectionString)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            server = null;
            database = null;
            connectionString = null;

            var explorer = serviceProvider?.GetService(typeof(IObjectExplorerService)) as IObjectExplorerService;
            INodeInformation node = explorer == null ? null : TryGetSelected(explorer);
            if (node?.Context == null) return false;

            try
            {
                database = new Urn(node.Context).GetAttribute("Name", "Database");
            }
            catch (Exception ex)
            {
                OeDiagnostics.Warn("Source control: could not read the selected Object Explorer node (" + ex.GetType().Name + ").");
                return false;
            }

            server = (node.Connection as SqlConnectionInfo)?.ServerName;
            if (string.IsNullOrEmpty(server) || string.IsNullOrEmpty(database)) return false;

            return OeTableInfo.TryBuildConnectionString(node, database, out connectionString, out _)
                && connectionString != null;
        }

        private static bool IsOnServer(INodeInformation node, string server)
        {
            string nodeServer = (node.Connection as SqlConnectionInfo)?.ServerName;
            return string.Equals(nodeServer, server, StringComparison.OrdinalIgnoreCase);
        }

        private static INodeInformation TryGetSelected(IObjectExplorerService explorer)
        {
            try
            {
                explorer.GetSelectedNodes(out int count, out INodeInformation[] nodes);
                return count == 1 && nodes != null && nodes.Length == 1 ? nodes[0] : null;
            }
            catch (Exception ex)
            {
                OeDiagnostics.Warn("Source control: reading the Object Explorer selection failed (" + ex.GetType().Name + ").");
                return null;
            }
        }
    }
}
