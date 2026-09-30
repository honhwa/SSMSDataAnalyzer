using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;

namespace SsmsDataAnalyzer.Vsix.SourceControl
{
    /// <summary>
    /// "Check source control…" (docs/source-control-sync-plan.md §9 Phase 1 item 2) -- a
    /// normal, dockable, NON-transient <see cref="ToolWindowPane"/>, same shape as
    /// History.QueryHistoryToolWindow: a panel the user comes back to, single shared instance
    /// (id 0) for the whole SSMS session.
    /// </summary>
    [Guid(PackageGuids.SourceControlToolWindowPersistenceGuidString)]
    public sealed class SourceControlToolWindow : ToolWindowPane
    {
        private readonly SourceControlView _view;

        public SourceControlToolWindow() : base(null)
        {
            Caption = "Source Control Check";
            _view = new SourceControlView();
            Content = _view;
        }

        /// <summary>The single authoritative way SourceControlCommand wires this pane to its
        /// package and starts its first (read-only) check.</summary>
        internal void Initialize(AsyncPackage package, bool startedInQueryWindow = false)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _view.Initialize(package, startedInQueryWindow);
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
