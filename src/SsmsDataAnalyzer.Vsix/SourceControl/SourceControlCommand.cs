using System;
using System.ComponentModel.Design;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

namespace SsmsDataAnalyzer.Vsix.SourceControl
{
    /// <summary>
    /// "Check source control…" on the Tools menu (docs/source-control-sync-plan.md §9 Phase 1
    /// item 1 / §13.9: CanonicalName "SsmsDataAnalyzer.CheckSourceControl", id 0x0500). Same
    /// shape as History.QueryHistoryCommand -- shows/creates the single shared
    /// SourceControlToolWindow instance, and the check also runs on open (SourceControlView).
    /// </summary>
    internal sealed class SourceControlCommand
    {
        private readonly AsyncPackage _package;

        public static SourceControlCommand Instance { get; private set; }

        public static async Task InitializeAsync(AsyncPackage package)
        {
            await package.JoinableTaskFactory.SwitchToMainThreadAsync();

            var commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            if (commandService == null)
            {
                ObjectExplorer.OeDiagnostics.Error("'Check source control' command could not be registered: no IMenuCommandService.");
                return;
            }
            Instance = new SourceControlCommand(package, commandService);
        }

        private SourceControlCommand(AsyncPackage package, OleMenuCommandService commandService)
        {
            _package = package ?? throw new ArgumentNullException(nameof(package));
            if (commandService == null) throw new ArgumentNullException(nameof(commandService));

            var commandId = new CommandID(PackageGuids.CommandSetGuid, PackageIds.CheckSourceControlCommandId);
            var menuItem = new MenuCommand(Execute, commandId);
            commandService.AddCommand(menuItem);
        }

        private void Execute(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            _package.JoinableTaskFactory.RunAsync(async () =>
            {
                var pane = await _package.ShowToolWindowAsync(
                    typeof(SourceControlToolWindow),
                    id: 0,
                    create: true,
                    cancellationToken: _package.DisposalToken) as SourceControlToolWindow;

                if (pane?.Frame == null)
                {
                    ObjectExplorer.OeDiagnostics.Error("'Check source control' could not create/show its tool window.");
                    return;
                }

                pane.Initialize(_package);
            }).FileAndForget("SsmsDataAnalyzer/SourceControl/SourceControlCommand/Execute");
        }
    }
}
