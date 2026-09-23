using System;
using System.ComponentModel.Design;
using System.Runtime.InteropServices;
using System.Windows.Input;
using Microsoft.SqlServer.Management.Smo.RegSvrEnum;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using SsmsDataAnalyzer.Core.ScriptObject;
using SsmsDataAnalyzer.Vsix.ObjectExplorer;

namespace SsmsDataAnalyzer.Vsix.ScriptObject
{
    /// <summary>
    /// Ctrl+click "Script object" popup: the clicked object's CREATE script, read-only, with Copy
    /// and "Open in new query window". Single floating transient instance (id 0) re-targeted on
    /// every Ctrl+click — the same shape and the same pane-local Copy/SelectAll/Esc command
    /// wiring as PeekToolWindow (see its comments for why Esc must be claimed via VSStd97
    /// PaneActivateDocWindow + Escape on this pane's own command service).
    ///
    /// Keeps the source editor's UIConnectionInfo (a reference SSMS already holds) and the
    /// database name — never a connection string — for the Open button.
    /// </summary>
    [Guid(PackageGuids.ScriptObjectToolWindowPersistenceGuidString)]
    public sealed class ScriptObjectToolWindow : ToolWindowPane
    {
        private readonly ScriptObjectView _view;
        private string _script;
        private string _database;
        private UIConnectionInfo _connectionInfo;

        public ScriptObjectToolWindow() : base(null)
        {
            Caption = "Script";
            _view = new ScriptObjectView();
            Content = _view;
            _view.PreviewKeyDown += OnViewPreviewKeyDown;
            _view.OpenRequested += OnOpenRequested;

            if (GetService(typeof(IMenuCommandService)) is OleMenuCommandService commandService)
            {
                commandService.AddCommand(new MenuCommand(
                    (s, e) => _view.CopyCommand(),
                    new CommandID(VSConstants.GUID_VSStandardCommandSet97, (int)VSConstants.VSStd97CmdID.Copy)));
                commandService.AddCommand(new MenuCommand(
                    (s, e) => _view.SelectAllCommand(),
                    new CommandID(VSConstants.GUID_VSStandardCommandSet97, (int)VSConstants.VSStd97CmdID.SelectAll)));
                commandService.AddCommand(new MenuCommand(
                    (s, e) => { ThreadHelper.ThrowIfNotOnUIThread(); CloseWindow(); },
                    new CommandID(VSConstants.GUID_VSStandardCommandSet97, (int)VSConstants.VSStd97CmdID.PaneActivateDocWindow)));
                commandService.AddCommand(new MenuCommand(
                    (s, e) => { ThreadHelper.ThrowIfNotOnUIThread(); CloseWindow(); },
                    new CommandID(VSConstants.GUID_VSStandardCommandSet97, (int)VSConstants.VSStd97CmdID.Escape)));
            }
        }

        /// <summary>Opens straight into the waiting state, so a Ctrl+click gives instant
        /// feedback while the object is resolved and scripted on the server.</summary>
        internal void BindPending(string objectName)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _script = null;
            _database = null;
            _connectionInfo = null;
            Caption = "Script: " + objectName;
            _view.BindPending(objectName);
        }

        /// <summary>The scripting request failed; leave the popup open saying why.</summary>
        internal void BindFailed(string objectName, string message)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _script = null;
            Caption = "Script: " + objectName;
            _view.BindFailed(message);
        }

        internal void Bind(string script, ResolvedSqlObject obj, UIConnectionInfo connectionInfo)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _script = script;
            _database = obj.Database;
            _connectionInfo = connectionInfo;
            Caption = "Script: " + obj.QualifiedName;
            _view.Bind(script, SqlObjectKinds.DisplayName(obj.Kind) + " in " + Core.Sql.SqlIdentifier.Bracket(obj.Database));
            _view.FocusScript();
        }

        private void OnOpenRequested(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (string.IsNullOrEmpty(_script) || _connectionInfo == null) return;

            string script = _script;
            string database = _database;
            UIConnectionInfo connectionInfo = _connectionInfo;
            _view.SetOpenEnabled(false);

            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                bool opened = false;
                try
                {
                    opened = await ScriptObjectController.OpenInNewQueryWindowAsync(script, connectionInfo, database);
                }
                catch (Exception ex)
                {
                    OeDiagnostics.Error("Script object: opening the script in a new query window failed", ex);
                }
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (opened) CloseWindow();
                else _view.SetOpenEnabled(true);
            }).FileAndForget("SsmsDataAnalyzer/ScriptObject/OpenInNewQueryWindow");
        }

        private void OnViewPreviewKeyDown(object sender, KeyEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            CloseWindow();
        }

        private void CloseWindow()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (Frame is IVsWindowFrame frame)
                frame.CloseFrame((uint)__FRAMECLOSE.FRAMECLOSE_NoSave);
        }
    }
}
