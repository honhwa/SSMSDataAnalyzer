using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Microsoft.VisualStudio.Shell;

namespace SsmsDataAnalyzer.Vsix.SourceControl
{
    internal sealed class InverseBoolConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            !(value is bool b && b);

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            !(value is bool b && b);
    }

    /// <summary>Code-behind for SourceControlView -- wires the toolbar/context menu to
    /// SourceControlViewModel's action methods, same shape as History.QueryHistoryView.</summary>
    internal partial class SourceControlView : UserControl
    {
        internal SourceControlViewModel ViewModel { get; }

        public SourceControlView()
        {
            InitializeComponent();
            ViewModel = new SourceControlViewModel();
            DataContext = ViewModel;
        }

        /// <summary>Called once by SourceControlToolWindow right after construction. The check
        /// also runs when the panel opens (docs/source-control-sync-plan.md §9 Phase 1 item 1).</summary>
        internal void Initialize(AsyncPackage package)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            ViewModel.Package = package;
            ThreadHelper.JoinableTaskFactory.RunAsync(() => ViewModel.RunCheckAsync())
                .FileAndForget("SsmsDataAnalyzer/SourceControl/InitialCheck");
        }

        internal void Detach()
        {
            ViewModel.Dispose();
        }

        private void CheckNowButton_Click(object sender, RoutedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            ThreadHelper.JoinableTaskFactory.RunAsync(() => ViewModel.RunCheckAsync())
                .FileAndForget("SsmsDataAnalyzer/SourceControl/CheckNow");
        }

        private FindingItem SelectedRow(object sender)
        {
            var grid = sender as FrameworkElement;
            // ContextMenu items live in a logical tree rooted at the DataGrid's PlacementTarget,
            // not the visual tree -- the simplest reliable source is the view model's own
            // SelectedFinding, kept current by each row's own binding.
            return ViewModel.SelectedFinding;
        }

        private void CompareMenuItem_Click(object sender, RoutedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var item = SelectedRow(sender);
            if (item == null) return;
            ThreadHelper.JoinableTaskFactory.RunAsync(() => ViewModel.CompareAsync(item))
                .FileAndForget("SsmsDataAnalyzer/SourceControl/Compare");
        }

        private void OpenRepoFileMenuItem_Click(object sender, RoutedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var item = SelectedRow(sender);
            if (item == null) return;
            ThreadHelper.JoinableTaskFactory.RunAsync(() => ViewModel.OpenRepoFileAsync(item))
                .FileAndForget("SsmsDataAnalyzer/SourceControl/OpenRepoFile");
        }

        private void ScriptMenuItem_Click(object sender, RoutedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var item = SelectedRow(sender);
            if (item == null) return;
            ThreadHelper.JoinableTaskFactory.RunAsync(() => ViewModel.ScriptServerDefinitionAsync(item))
                .FileAndForget("SsmsDataAnalyzer/SourceControl/Script");
        }

        private void CopyNameMenuItem_Click(object sender, RoutedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var item = SelectedRow(sender);
            if (item != null) ViewModel.CopyObjectName(item);
        }

        private void MapMenuItem_Click(object sender, RoutedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var item = SelectedRow(sender);
            if (item == null) return;
            ThreadHelper.JoinableTaskFactory.RunAsync(() => ViewModel.MapToProjectAsync(item))
                .FileAndForget("SsmsDataAnalyzer/SourceControl/MapToProject");
        }

        private void FindingsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var item = ((FrameworkElement)e.OriginalSource).DataContext as FindingItem ?? ViewModel.SelectedFinding;
            if (item == null) return;

            if (item.IsUnmapped)
            {
                ThreadHelper.JoinableTaskFactory.RunAsync(() => ViewModel.MapToProjectAsync(item))
                    .FileAndForget("SsmsDataAnalyzer/SourceControl/MapToProject");
            }
            else if (item.CanCompare)
            {
                ThreadHelper.JoinableTaskFactory.RunAsync(() => ViewModel.CompareAsync(item))
                    .FileAndForget("SsmsDataAnalyzer/SourceControl/Compare");
            }
            else if (item.CanOpenRepoFile)
            {
                ThreadHelper.JoinableTaskFactory.RunAsync(() => ViewModel.OpenRepoFileAsync(item))
                    .FileAndForget("SsmsDataAnalyzer/SourceControl/OpenRepoFile");
            }
        }
    }
}
