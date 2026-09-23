using System;
using System.ComponentModel.Design;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

namespace SsmsDataAnalyzer.Vsix.History
{
    /// <summary>
    /// "Query History…" on the Tools menu (docs/query-history-plan.md §4 Phase 1 item 2 / §8.7:
    /// CanonicalName "SsmsDataAnalyzer.QueryHistory", no default key). Same shape as
    /// AnalyzeDataCommand -- shows/creates the single shared QueryHistoryToolWindow instance.
    /// </summary>
    internal sealed class QueryHistoryCommand
    {
        private readonly AsyncPackage _package;

        public static QueryHistoryCommand Instance { get; private set; }

        public static async Task InitializeAsync(AsyncPackage package)
        {
            await package.JoinableTaskFactory.SwitchToMainThreadAsync();

            var commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            if (commandService == null)
            {
                ObjectExplorer.OeDiagnostics.Error("'Query History' command could not be registered: no IMenuCommandService.");
                return;
            }
            Instance = new QueryHistoryCommand(package, commandService);
        }

        private QueryHistoryCommand(AsyncPackage package, OleMenuCommandService commandService)
        {
            _package = package ?? throw new ArgumentNullException(nameof(package));
            if (commandService == null) throw new ArgumentNullException(nameof(commandService));

            var commandId = new CommandID(PackageGuids.CommandSetGuid, PackageIds.QueryHistoryCommandId);
            var menuItem = new MenuCommand(Execute, commandId);
            commandService.AddCommand(menuItem);
        }

        private void Execute(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            _package.JoinableTaskFactory.RunAsync(async () =>
            {
                var pane = await _package.ShowToolWindowAsync(
                    typeof(QueryHistoryToolWindow),
                    id: 0,
                    create: true,
                    cancellationToken: _package.DisposalToken) as QueryHistoryToolWindow;

                if (pane?.Frame == null)
                {
                    ObjectExplorer.OeDiagnostics.Error("'Query History' could not create/show its tool window.");
                    return;
                }

                pane.Initialize(_package);
            }).FileAndForget("SsmsDataAnalyzer/QueryHistory/QueryHistoryCommand/Execute");
        }
    }
}
