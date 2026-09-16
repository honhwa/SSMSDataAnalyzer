using System;
using System.Windows;
using System.Windows.Controls;
using SsmsDataAnalyzer.Vsix.ObjectExplorer;

namespace SsmsDataAnalyzer.Vsix.ScriptObject
{
    /// <summary>Read-only monospace script with Copy and "Open in new query window".</summary>
    public partial class ScriptObjectView : UserControl
    {
        public ScriptObjectView()
        {
            InitializeComponent();
        }

        /// <summary>Raised by the "Open in new query window" button; the pane owns the connection.</summary>
        internal event EventHandler OpenRequested;

        internal void Bind(string script, string info)
        {
            ScriptTextBox.Text = script ?? string.Empty;
            ScriptTextBox.ScrollToHome();
            ScriptTextBox.CaretIndex = 0;
            InfoText.Text = string.IsNullOrEmpty(info) ? "Esc to close" : info + "   ·   Esc to close";
            OpenButton.IsEnabled = true;
        }

        internal void FocusScript() => ScriptTextBox.Focus();

        internal void SetOpenEnabled(bool enabled) => OpenButton.IsEnabled = enabled;

        internal void SelectAllCommand()
        {
            ScriptTextBox.Focus();
            ScriptTextBox.SelectAll();
        }

        /// <summary>Ctrl+C: the selection when there is one, otherwise the whole script.</summary>
        internal void CopyCommand()
        {
            string text = ScriptTextBox.SelectionLength > 0 ? ScriptTextBox.SelectedText : ScriptTextBox.Text;
            SetClipboard(text);
        }

        private void CopyButton_Click(object sender, RoutedEventArgs e) => SetClipboard(ScriptTextBox.Text);

        private void OpenButton_Click(object sender, RoutedEventArgs e) => OpenRequested?.Invoke(this, EventArgs.Empty);

        private static void SetClipboard(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            try
            {
                Clipboard.SetDataObject(text, true);
            }
            catch (Exception ex)
            {
                // Another process can hold the clipboard open; never log the script itself.
                OeDiagnostics.Error("Script object: copying to the clipboard failed", ex);
            }
        }
    }
}
