using System;
using System.ComponentModel.Composition;
using System.Windows.Input;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Formatting;
using Microsoft.VisualStudio.Utilities;
using SsmsDataAnalyzer.Vsix.ObjectExplorer;

namespace SsmsDataAnalyzer.Vsix.ScriptObject
{
    /// <summary>
    /// Ctrl+left-click on an object name in a SQL query window → CREATE script popup.
    ///
    /// SSMS 22's T-SQL editor is the standard VS WPF editor (SqlScriptEditorControl implements
    /// IVsCodeWindow; SQLEditors.dll itself exports [ContentType("SQL")] editor parts), so a MEF
    /// IMouseProcessorProvider is the supported hook. Registered for "text" + Document role and
    /// filtered at click time (content type "SQL", or a *.sql document) so a wrong guess about the
    /// content type name cannot silently disable the feature — and nothing else is touched:
    /// <list type="bullet">
    /// <item>Only a single Ctrl+left-click (no Shift/Alt, no double-click) on a scriptable name is
    /// taken; anything else (plain clicks, Ctrl+click on whitespace, keywords, variables, comments)
    /// falls through to the editor's normal handling.</item>
    /// <item>A taken click is marked Handled on both down and up, so the editor does not also run
    /// its own Ctrl+click word selection or move the caret.</item>
    /// </list>
    /// </summary>
    [Export(typeof(IMouseProcessorProvider))]
    [Name("SsmsDataAnalyzer.ScriptObjectCtrlClick")]
    [ContentType("text")]
    [TextViewRole(PredefinedTextViewRoles.Document)]
    internal sealed class CtrlClickMouseProcessorProvider : IMouseProcessorProvider
    {
        public IMouseProcessor GetAssociatedProcessor(IWpfTextView wpfTextView) => new CtrlClickMouseProcessor(wpfTextView);
    }

    internal sealed class CtrlClickMouseProcessor : MouseProcessorBase
    {
        private readonly IWpfTextView _view;
        private bool _swallowNextUp;

        public CtrlClickMouseProcessor(IWpfTextView view)
        {
            _view = view;
        }

        public override void PreprocessMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            _swallowNextUp = false;
            try
            {
                if (e.ClickCount != 1 || Keyboard.Modifiers != ModifierKeys.Control) return;
                if (!IsSqlView()) return;

                int? position = PositionFromMouse(e);
                if (position == null) return;

                ITextSnapshot snapshot = _view.TextSnapshot;
                if (!ScriptObjectController.Lookup(snapshot, position.Value).Found) return;

                e.Handled = true;
                _swallowNextUp = true;

                // The editor would have focused itself on this click; do that much so the query
                // window becomes the active document, then act once activation has settled.
                _view.VisualElement.Focus();
                int clicked = position.Value;
                ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
                {
                    // alwaysYield: run after this click (and the focus change) has been processed.
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(alwaysYield: true);
                    if (!EnsurePackageLoaded()) return;
                    ScriptObjectController.ExecuteCtrlClick(_view, snapshot, clicked);
                }).FileAndForget("SsmsDataAnalyzer/ScriptObject/CtrlClick");
            }
            catch (Exception ex)
            {
                // Never break the editor's own click handling because of this feature.
                _swallowNextUp = false;
                OeDiagnostics.Error("Script object: Ctrl+click handling failed", ex);
            }
        }

        public override void PreprocessMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            if (!_swallowNextUp) return;
            _swallowNextUp = false;
            e.Handled = true;
        }

        private bool IsSqlView()
        {
            ITextBuffer buffer = _view.TextBuffer;
            if (buffer.ContentType.IsOfType("SQL")) return true;

            if (buffer.Properties.TryGetProperty(typeof(ITextDocument), out ITextDocument document)
                && document?.FilePath != null
                && document.FilePath.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            {
                OeDiagnostics.InfoOnce("scriptobject-ctype-" + buffer.ContentType.TypeName,
                    "Script object: a .sql document uses content type '" + buffer.ContentType.TypeName + "' (not 'SQL'); Ctrl+click still enabled by file extension.");
                return true;
            }
            return false;
        }

        private int? PositionFromMouse(MouseButtonEventArgs e)
        {
            var point = e.GetPosition(_view.VisualElement);
            ITextViewLine line = _view.TextViewLines?.GetTextViewLineContainingYCoordinate(point.Y + _view.ViewportTop);
            if (line == null) return null;
            SnapshotPoint? bufferPosition = line.GetBufferPositionFromXCoordinate(point.X + _view.ViewportLeft);
            if (bufferPosition == null) return null;
            if (bufferPosition.Value.Snapshot != _view.TextSnapshot) return null;
            return bufferPosition.Value.Position;
        }

        /// <summary>The package auto-loads in the background at startup, but a very early click
        /// could beat it; load it on demand rather than dropping the click.</summary>
        private static bool EnsurePackageLoaded()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (ScriptObjectController.IsInitialized) return true;
            try
            {
                if (Package.GetGlobalService(typeof(SVsShell)) is IVsShell shell)
                {
                    Guid packageGuid = PackageGuids.PackageGuid;
                    shell.LoadPackage(ref packageGuid, out _);
                }
            }
            catch (Exception ex)
            {
                OeDiagnostics.Error("Script object: loading the package for Ctrl+click failed", ex);
            }
            return ScriptObjectController.IsInitialized;
        }
    }
}
