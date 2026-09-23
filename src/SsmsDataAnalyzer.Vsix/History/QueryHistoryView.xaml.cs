using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.VisualStudio.Shell;

namespace SsmsDataAnalyzer.Vsix.History
{
    /// <summary>Code-behind for QueryHistoryView -- wires the toolbar/context-menu/double-click
    /// to QueryHistoryViewModel's action methods (docs/query-history-plan.md §4 item 7), plus
    /// the pane-local Ctrl+C handler QueryHistoryToolWindow registers.</summary>
    internal partial class QueryHistoryView : UserControl
    {
        internal QueryHistoryViewModel ViewModel { get; }

        public QueryHistoryView()
        {
            InitializeComponent();
            ViewModel = new QueryHistoryViewModel();
            DataContext = ViewModel;
        }

        /// <summary>Called once by QueryHistoryToolWindow right after construction, then again
        /// is unnecessary -- Package never changes for the lifetime of this single-instance
        /// window.</summary>
        internal void Initialize(AsyncPackage package)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            ViewModel.Package = package;
            ThreadHelper.JoinableTaskFactory.RunAsync(() => ViewModel.ReloadAsync())
                .FileAndForget("SsmsDataAnalyzer/QueryHistory/InitialLoad");
        }

        internal void Detach()
        {
            ViewModel.Dispose();
        }

        /// <summary>Invoked by QueryHistoryToolWindow's pane-local Edit.Copy handler -- same
        /// "claim VSStd97 Copy on this pane's own command service" pattern as
        /// PivotToolWindow/GridFindToolWindow, since Ctrl+C is otherwise a global VS command
        /// that never reaches a plain WPF KeyDown handler here.</summary>
        internal void CopyCommand()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (Keyboard.FocusedElement is TextBox) return; // let the preview/search box's own Ctrl+C win
            CopySelected();
        }

        private void HistoryGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var item = ((FrameworkElement)e.OriginalSource).DataContext as QueryHistoryEntryItem;
            item = item ?? ViewModel.SelectedItem;
            if (item == null) return;
            ThreadHelper.JoinableTaskFactory.RunAsync(() => ViewModel.OpenInNewQueryWindowAsync(item))
                .FileAndForget("SsmsDataAnalyzer/QueryHistory/Open");
        }

        private void StarGlyph_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (sender is FrameworkElement fe && fe.DataContext is QueryHistoryEntryItem item)
            {
                ViewModel.ToggleStar(item);
                e.Handled = true;
            }
        }

        private void OpenButton_Click(object sender, RoutedEventArgs e) { ThreadHelper.ThrowIfNotOnUIThread(); OpenSelected(); }
        private void OpenMenuItem_Click(object sender, RoutedEventArgs e) { ThreadHelper.ThrowIfNotOnUIThread(); OpenSelected(); }

        private void OpenSelected()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var item = ViewModel.SelectedItem;
            if (item == null) return;
            ThreadHelper.JoinableTaskFactory.RunAsync(() => ViewModel.OpenInNewQueryWindowAsync(item))
                .FileAndForget("SsmsDataAnalyzer/QueryHistory/Open");
        }

        private void InsertButton_Click(object sender, RoutedEventArgs e) { ThreadHelper.ThrowIfNotOnUIThread(); InsertSelected(); }
        private void InsertMenuItem_Click(object sender, RoutedEventArgs e) { ThreadHelper.ThrowIfNotOnUIThread(); InsertSelected(); }

        private void InsertSelected()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var item = ViewModel.SelectedItem;
            if (item == null) return;
            ThreadHelper.JoinableTaskFactory.RunAsync(() => ViewModel.InsertAtCursorAsync(item))
                .FileAndForget("SsmsDataAnalyzer/QueryHistory/Insert");
        }

        private void CopyButton_Click(object sender, RoutedEventArgs e) { ThreadHelper.ThrowIfNotOnUIThread(); CopySelected(); }
        private void CopyMenuItem_Click(object sender, RoutedEventArgs e) { ThreadHelper.ThrowIfNotOnUIThread(); CopySelected(); }

        /// <summary>Every selected row, in the order shown -- not just the focused one.</summary>
        private IEnumerable<QueryHistoryEntryItem> Selection()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var selected = HistoryGrid.SelectedItems.Cast<QueryHistoryEntryItem>().ToList();
            if (selected.Count > 0) return selected;
            return ViewModel.SelectedItem == null
                ? Enumerable.Empty<QueryHistoryEntryItem>()
                : new[] { ViewModel.SelectedItem };
        }

        private void CopySelected()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            ViewModel.Copy(Selection());
        }

        private void CopyWithHeaderMenuItem_Click(object sender, RoutedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            ViewModel.CopyWithHeader(Selection());
        }

        private void FindForDatabaseMenuItem_Click(object sender, RoutedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            ViewModel.FindEntriesForThisDatabase(ViewModel.SelectedItem);
        }

        private void StarButton_Click(object sender, RoutedEventArgs e) { ThreadHelper.ThrowIfNotOnUIThread(); StarSelected(); }
        private void StarMenuItem_Click(object sender, RoutedEventArgs e) { ThreadHelper.ThrowIfNotOnUIThread(); StarSelected(); }

        private void StarSelected()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            ViewModel.ToggleStar(ViewModel.SelectedItem);
        }

        private void DeleteButton_Click(object sender, RoutedEventArgs e) { ThreadHelper.ThrowIfNotOnUIThread(); DeleteSelected(); }
        private void DeleteMenuItem_Click(object sender, RoutedEventArgs e) { ThreadHelper.ThrowIfNotOnUIThread(); DeleteSelected(); }

        private void DeleteSelected()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            // Multi-select delete: the plan's action list is per-entry, but Extended selection
            // mode makes several-at-once natural; delete every selected row rather than only
            // the "current" one.
            foreach (var item in HistoryGrid.SelectedItems.Cast<QueryHistoryEntryItem>().ToList())
            {
                ViewModel.Delete(item);
            }
        }

        /// <summary>"Clear all history", with confirmation (docs/query-history-plan.md §3
        /// "Layer 2" -- the crypto-shred is destructive and irreversible).</summary>
        private void ClearAllButton_Click(object sender, RoutedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var result = MessageBox.Show(
                Window.GetWindow(this),
                "This permanently deletes all query history on this machine, including starred entries. This cannot be undone. Continue?",
                "Clear all query history",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);

            if (result == MessageBoxResult.Yes)
            {
                ViewModel.ClearAll();
            }
        }
    }
}
