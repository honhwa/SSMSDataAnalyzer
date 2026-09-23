using System.Collections.Generic;
using System.ComponentModel.Design;
using System.Runtime.InteropServices;
using System.Windows.Input;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using SsmsDataAnalyzer.Core.Pivot;
using SsmsDataAnalyzer.Vsix.Pivot;

namespace SsmsDataAnalyzer.Vsix.Peek
{
    /// <summary>
    /// "Peek source for this value" / pivot's "Peek source…" — a small floating tool window
    /// (docs' explicit ask: "like Find in Results", closable immediately) that shows the
    /// referenced record right away instead of opening a new query tab. "Go to source"
    /// (the new-query-tab choice) is completely unchanged; this is a second, additional choice.
    ///
    /// Reuses <see cref="PivotView"/>/PivotViewModel wholesale rather than a new view: a
    /// peeked record is exactly a one-range "pivot" of the rows the peek query returned — same
    /// transposed grid, same FK link icons (chained navigation via a fresh <see cref="PivotFkLinks"/>
    /// per peek), same Ctrl+C / Copy as Markdown. See <see cref="PeekRunner"/> for how the
    /// query is run and formatted before it ever reaches this window.
    ///
    /// Single, floating, TRANSIENT instance (id 0; MultiInstances stays at its ToolWindowPane
    /// default of false) re-targeted on every peek — the same "one shared window, re-pointed"
    /// shape as GridFindToolWindow, deliberately NOT PivotToolWindow's per-invocation
    /// multi-instance tabs: a peek is meant to be glanced at and closed, not accumulated.
    /// </summary>
    [Guid(PackageGuids.PeekToolWindowPersistenceGuidString)]
    public sealed class PeekToolWindow : ToolWindowPane, IVsWindowFrameNotify3
    {
        /// <summary>One record this window has shown, kept so Back can restore it.</summary>
        private sealed class PeekState
        {
            public PivotResult Result;
            public PivotFkLinks FkLinks;
            public string Banner;
            public string Caption;
        }

        /// <summary>
        /// Where this window has been, oldest at the bottom. Following a foreign key inside a
        /// peek used to replace the record outright: you could walk forward through links and
        /// never get back to where you started (field report).
        ///
        /// Bounded, because each state holds a snapshot of a result set and a peek chain has no
        /// natural end. Dropping the OLDEST entry is the right end to drop: Back is used to
        /// retrace recent steps.
        /// </summary>
        private readonly List<PeekState> _history = new List<PeekState>();
        private const int MaxHistory = 30;

        private PeekState _current;

        private readonly PivotView _view;

        public PeekToolWindow() : base(null)
        {
            Caption = "Peek";
            _view = new PivotView();
            Content = _view;
            _view.PreviewKeyDown += OnViewPreviewKeyDown;
            _view.BackRequested += (s, e) => GoBack();

            // A link icon in a peek goes one level deeper in THIS window rather than opening a
            // query tab; Back walks the chain out again.
            _view.FollowLinksInPlace = true;

            // Same pane-local Copy/SelectAll shape as PivotToolWindow (see its doc comment for
            // the decompilation trail): Ctrl+C/Ctrl+A are global VS commands, so registering
            // these standard IDs on this pane's OWN command service is what scopes them to
            // "while this window has focus" instead of stealing them everywhere in SSMS.
            if (GetService(typeof(IMenuCommandService)) is OleMenuCommandService commandService)
            {
                commandService.AddCommand(new MenuCommand(
                    (s, e) => _view.CopyCommand(),
                    new CommandID(VSConstants.GUID_VSStandardCommandSet97, (int)VSConstants.VSStd97CmdID.Copy)));
                commandService.AddCommand(new MenuCommand(
                    (s, e) => _view.SelectAllCommand(),
                    new CommandID(VSConstants.GUID_VSStandardCommandSet97, (int)VSConstants.VSStd97CmdID.SelectAll)));

                // v0.14.1 field report: Esc only returned focus to SSMS. In a tool window VS
                // binds Esc to Window.ActivateDocumentWindow (VSStd97 PaneActivateDocWindow,
                // 289) and runs it before WPF ever sees the key — the same mechanism that
                // swallowed F3 in Find in Results. Claiming it (and the generic Escape, 743) on
                // this pane's own command service scopes the override to the peek window only.
                commandService.AddCommand(new MenuCommand(
                    (s, e) => { ThreadHelper.ThrowIfNotOnUIThread(); CloseWindow(); },
                    new CommandID(VSConstants.GUID_VSStandardCommandSet97, (int)VSConstants.VSStd97CmdID.PaneActivateDocWindow)));
                commandService.AddCommand(new MenuCommand(
                    (s, e) => { ThreadHelper.ThrowIfNotOnUIThread(); CloseWindow(); },
                    new CommandID(VSConstants.GUID_VSStandardCommandSet97, (int)VSConstants.VSStd97CmdID.Escape)));

                // Alt+Left. Same reasoning as Esc above: VS routes the shell's navigate-back
                // command before WPF sees the key, so claiming it on THIS pane's command
                // service is what scopes it to the peek window instead of hijacking it
                // everywhere in SSMS.
                commandService.AddCommand(new MenuCommand(
                    (s, e) => { ThreadHelper.ThrowIfNotOnUIThread(); GoBack(); },
                    new CommandID(VSConstants.GUID_VSStandardCommandSet97, (int)VSConstants.VSStd97CmdID.ShellNavBackward)));
            }
        }

        /// <summary>Esc closes the peek window immediately — the point of "closable in a few
        /// seconds" (the window's own titlebar X always works regardless). Kept as a fallback
        /// for the case the key does reach WPF; the pane-local commands above are what VS
        /// actually routes Esc to.</summary>
        private void OnViewPreviewKeyDown(object sender, KeyEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            // Backspace and Alt+Left both go back, the two keys people already try. Neither is
            // claimed while a text box has focus (the column filter), where Backspace must
            // still delete a character.
            bool typing = Keyboard.FocusedElement is System.Windows.Controls.TextBox;
            if (!typing && (e.Key == Key.Back
                || (e.Key == Key.Left && (Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt)))
            {
                e.Handled = true;
                GoBack();
                return;
            }

            if (e.Key != Key.Escape) return;
            e.Handled = true;
            CloseWindow();
        }

        /// <summary>Makes this pane the frame's view helper, which is what delivers
        /// <see cref="IVsWindowFrameNotify3"/> notifications -- the same registration
        /// GridFindToolWindow needs, and for the same reason: closing a tool window normally
        /// only hides it, so Dispose is not a reliable "the user closed it" signal.</summary>
        public override void OnToolWindowCreated()
        {
            base.OnToolWindowCreated();
            ThreadHelper.ThrowIfNotOnUIThread();
            if (Frame is IVsWindowFrame frame)
                frame.SetProperty((int)__VSFPROPID.VSFPROPID_ViewHelper, this);
        }

        /// <summary>A real close ends the trail, so the next peek starts fresh instead of
        /// offering Back to a record from an earlier, unrelated look. FRAMESHOW_WinHidden is
        /// deliberately NOT treated as a close: it also fires when a docked tab is switched
        /// away from, and coming back to find Back disabled would be wrong.</summary>
        int IVsWindowFrameNotify3.OnShow(int fShow)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (fShow == (int)__FRAMESHOW.FRAMESHOW_WinClosed) ClearHistory();
            return VSConstants.S_OK;
        }

        int IVsWindowFrameNotify3.OnMove(int x, int y, int w, int h) => VSConstants.S_OK;
        int IVsWindowFrameNotify3.OnSize(int x, int y, int w, int h) => VSConstants.S_OK;
        int IVsWindowFrameNotify3.OnDockableChange(int fDockable, int x, int y, int w, int h) => VSConstants.S_OK;

        int IVsWindowFrameNotify3.OnClose(ref uint pgrfSaveOptions)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            ClearHistory();
            return VSConstants.S_OK;
        }

        private void ClearHistory()
        {
            _history.Clear();
            _current = null;
        }

        /// <summary>Restores the record shown before the current one. Does nothing at the start
        /// of the trail, so the button is simply disabled rather than closing the window.</summary>
        private void GoBack()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_history.Count == 0) return;

            PeekState previous = _history[_history.Count - 1];
            _history.RemoveAt(_history.Count - 1);

            Show(previous);
        }

        private void Show(PeekState state)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _current = state;
            Caption = state.Caption;
            _view.Bind(state.Result, state.FkLinks, state.Banner);
            _view.SetBackAvailable(_history.Count > 0);
        }

        private void CloseWindow()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (Frame is IVsWindowFrame frame)
                frame.CloseFrame((uint)__FRAMECLOSE.FRAMECLOSE_NoSave);
        }

        /// <summary>The single authoritative way <see cref="PeekRunner"/> (re)targets this
        /// window at a freshly peeked record.</summary>
        internal void Bind(PivotResult result, PivotFkLinks fkLinks, string bannerText, string caption)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            // Whatever was on screen becomes the step you can go back to. That is true both of
            // following a foreign key from inside this window and of a fresh peek from the
            // grid: either way "Back" means the record you were looking at a moment ago, which
            // is what someone reaches for.
            if (_current != null)
            {
                _history.Add(_current);
                if (_history.Count > MaxHistory) _history.RemoveAt(0);
            }

            Show(new PeekState { Result = result, FkLinks = fkLinks, Banner = bannerText, Caption = caption });
        }

        /// <summary>Cancels any in-flight FK resolution when this pane is closed — same
        /// reasoning as PivotToolWindow.Dispose.</summary>
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                _view.CancelPendingResolve();

                ClearHistory();
            }
            base.Dispose(disposing);
        }
    }
}
