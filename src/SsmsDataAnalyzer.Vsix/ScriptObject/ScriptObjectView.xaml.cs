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
            CopyButton.IsEnabled = true;
            OpenButton.IsEnabled = true;
            Spinner.Visibility = Visibility.Collapsed;
        }

        /// <summary>
        /// Shown the moment Ctrl+click is recognised, before the script has been fetched.
        /// v0.19.0 field report: "on Ctrl + left click I don't know if anything is happening in
        /// the background or not, the window shows after a few seconds". Resolving the object and
        /// asking SMO for its script is a round trip to the server, so the popup now appears at
        /// once in this waiting state and fills in when the script arrives.
        /// </summary>
        internal void BindPending(string objectName)
        {
            ScriptTextBox.Text = string.Empty;
            InfoText.Text = "Scripting " + objectName + "…";
            CopyButton.IsEnabled = false;
            OpenButton.IsEnabled = false;
            Spinner.Visibility = Visibility.Visible;
        }

        /// <summary>The request finished without a script: say so in the popup rather than
        /// leaving an empty window behind (the status bar carries the detail).</summary>
        internal void BindFailed(string message)
        {
            ScriptTextBox.Text = string.Empty;
            InfoText.Text = message;
            CopyButton.IsEnabled = false;
            OpenButton.IsEnabled = false;
            Spinner.Visibility = Visibility.Collapsed;
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
