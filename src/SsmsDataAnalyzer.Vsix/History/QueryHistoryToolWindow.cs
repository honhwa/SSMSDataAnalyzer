using System.ComponentModel.Design;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;

namespace SsmsDataAnalyzer.Vsix.History
{
    /// <summary>
    /// "Query History…" (docs/query-history-plan.md §4 Phase 1 item 3) -- a normal, dockable,
    /// NON-transient <see cref="ToolWindowPane"/> (unlike PeekToolWindow/GridFindToolWindow: this
    /// is a panel the user comes back to, the same shape as ProfileToolWindow, so VS persists
    /// its docked position across sessions). Single shared instance for the whole SSMS session
    /// (id 0) -- QueryHistoryCommand always shows/reuses it.
    ///
    /// Ctrl+C registered on this pane's own local command service, same pattern as
    /// PivotToolWindow/GridFindToolWindow: VS's own command routing claims Ctrl+C globally
    /// before a plain WPF KeyDown handler would ever see it, so it must be claimed here,
    /// scoped to "while this pane has focus" only.
    /// </summary>
    [Guid(PackageGuids.QueryHistoryToolWindowPersistenceGuidString)]
    public sealed class QueryHistoryToolWindow : ToolWindowPane
    {
        private readonly QueryHistoryView _view;

        public QueryHistoryToolWindow() : base(null)
        {
            Caption = "Query History";
            _view = new QueryHistoryView();
            Content = _view;

            if (GetService(typeof(IMenuCommandService)) is OleMenuCommandService commandService)
            {
                commandService.AddCommand(new MenuCommand(
                    (s, e) => { ThreadHelper.ThrowIfNotOnUIThread(); _view.CopyCommand(); },
                    new CommandID(VSConstants.GUID_VSStandardCommandSet97, (int)VSConstants.VSStd97CmdID.Copy)));
            }
        }

        /// <summary>The single authoritative way QueryHistoryCommand wires this pane to its
        /// package (for EnvDTE/QueryWindowAccessor/the status bar) and starts its first load.</summary>
        internal void Initialize(AsyncPackage package)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _view.Initialize(package);
        }

        protected override void Dispose(bool disposing)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (disposing)
            {
                _view.Detach();
            }
            base.Dispose(disposing);
        }
    }
}
