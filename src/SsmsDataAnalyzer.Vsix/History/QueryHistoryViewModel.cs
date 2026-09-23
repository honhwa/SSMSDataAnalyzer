using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Threading;
using Microsoft.VisualStudio.Shell;
using SsmsDataAnalyzer.Core.History;

namespace SsmsDataAnalyzer.Vsix.History
{
    /// <summary>One entry of the "date range" dropdown (docs/query-history-plan.md §4 Phase 1
    /// item 5: Today / Last 7 days / Last 30 days / All).</summary>
    internal sealed class DateRangeOption
    {
        public string Text { get; }
        public HistoryDateRange Value { get; }
        public DateRangeOption(string text, HistoryDateRange value) { Text = text; Value = value; }
        public override string ToString() => Text;
    }

    /// <summary>
    /// Backs QueryHistoryView: loads/filters/re-loads from <see cref="QueryHistoryService"/>,
    /// and exposes the Phase 1 actions (docs/query-history-plan.md §4 item 7) as plain methods
    /// the code-behind wires to button clicks / context-menu items / double-click, matching this
    /// codebase's existing convention (Pivot/GridFind) rather than an ICommand-per-action shape.
    /// </summary>
    internal sealed class QueryHistoryViewModel : INotifyPropertyChanged, IDisposable
    {
        private const int MaxResults = 5000;
        private static readonly TimeSpan SearchDebounce = TimeSpan.FromMilliseconds(250);

        public event PropertyChangedEventHandler PropertyChanged;

        public ObservableCollection<QueryHistoryEntryItem> Items { get; } = new ObservableCollection<QueryHistoryEntryItem>();

        public DateRangeOption[] DateRangeOptions { get; } =
        {
            new DateRangeOption("All", HistoryDateRange.All),
            new DateRangeOption("Today", HistoryDateRange.Today),
            new DateRangeOption("Last 7 days", HistoryDateRange.Last7Days),
            new DateRangeOption("Last 30 days", HistoryDateRange.Last30Days)
        };

        private DateRangeOption _selectedDateRange;
        public DateRangeOption SelectedDateRange
        {
            get => _selectedDateRange;
            set
            {
                if (ReferenceEquals(_selectedDateRange, value)) return;
                _selectedDateRange = value;
                OnPropertyChanged();
                ScheduleReload();
            }
        }

        private string _searchText = string.Empty;
        public string SearchText
        {
            get => _searchText;
            set
            {
                _searchText = value ?? string.Empty;
                OnPropertyChanged();
                ScheduleReload();
            }
        }

        private QueryHistoryEntryItem _selectedItem;
        public QueryHistoryEntryItem SelectedItem
        {
            get => _selectedItem;
            set
            {
                if (ReferenceEquals(_selectedItem, value)) return;
                _selectedItem = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(PreviewText));
                OnPropertyChanged(nameof(HasSelection));
            }
        }

        public string PreviewText => SelectedItem?.FullText ?? string.Empty;

        public bool HasSelection => SelectedItem != null;

        private string _statusText = string.Empty;
        public string StatusText
        {
            get => _statusText;
            private set { _statusText = value; OnPropertyChanged(); }
        }

        private readonly DispatcherTimer _debounceTimer;
        private bool _disposed;

        /// <summary>The active AsyncPackage, wired by QueryHistoryToolWindow right after
        /// construction -- needed for QueryWindowAccessor / EnvDTE / the status bar.</summary>
        public AsyncPackage Package { get; set; }

        public QueryHistoryViewModel()
        {
            _selectedDateRange = DateRangeOptions[0];

            _debounceTimer = new DispatcherTimer { Interval = SearchDebounce };
            _debounceTimer.Tick += (s, e) =>
            {
                _debounceTimer.Stop();
                _ = ReloadAsync();
            };

            QueryHistoryService.Changed += OnServiceChanged;
        }

        private void OnServiceChanged(object sender, EventArgs e)
        {
            // Raised from the writer thread almost always -- switch to the UI thread the
            // established (JoinableTaskFactory) way before touching anything WPF-bound, rather
            // than a raw Dispatcher.BeginInvoke.
            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (_disposed) return;
                await ReloadAsync();
            }).FileAndForget("SsmsDataAnalyzer/QueryHistory/ServiceChanged");
        }

        private void ScheduleReload()
        {
            _debounceTimer.Stop();
            _debounceTimer.Start();
        }

        public async Task ReloadAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (_disposed) return;

            try
            {
                var filter = HistoryFilter.Parse(SearchText);
                filter.DateRange = SelectedDateRange?.Value ?? HistoryDateRange.All;

                var selectedId = SelectedItem?.Id;
                var entries = await QueryHistoryService.QueryAsync(filter, MaxResults).ConfigureAwait(true);

                Items.Clear();
                QueryHistoryEntryItem toReselect = null;
                foreach (var entry in entries)
                {
                    var item = new QueryHistoryEntryItem(entry);
                    Items.Add(item);
                    if (selectedId.HasValue && item.Id == selectedId.Value) toReselect = item;
                }

                SelectedItem = toReselect;
                StatusText = Items.Count == 1 ? "1 entry" : Items.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + " entries";
            }
            catch (Exception ex)
            {
                ObjectExplorer.OeDiagnostics.Error("Query history: loading the list failed", ex);
                StatusText = "Could not load query history: " + ex.Message;
            }
        }

        // ---- actions (docs/query-history-plan.md §4 Phase 1 item 7) --------------------------

        public void ToggleStar(QueryHistoryEntryItem item)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (item == null) return;
            bool newValue = !item.Starred;
            item.Starred = newValue; // optimistic UI update
            QueryHistoryService.SetStarred(item.Id, newValue);
        }

        public void Delete(QueryHistoryEntryItem item)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (item == null) return;
            Items.Remove(item); // optimistic UI update
            if (ReferenceEquals(SelectedItem, item)) SelectedItem = null;
            QueryHistoryService.Delete(item.Id);
        }

        public void Copy(QueryHistoryEntryItem item)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (item == null || string.IsNullOrEmpty(item.FullText)) return;
            try
            {
                System.Windows.Clipboard.SetText(item.FullText);
            }
            catch (Exception ex)
            {
                // Clipboard can be held by another app -- never a crash, and never log the text.
                ObjectExplorer.OeDiagnostics.Error("Query history: copy to clipboard failed (clipboard may be in use)", ex);
            }
        }

        public async Task InsertAtCursorAsync(QueryHistoryEntryItem item)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (item == null || string.IsNullOrEmpty(item.FullText) || Package == null) return;

            try
            {
                var dte = await Package.GetServiceAsync(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
                var selection = dte?.ActiveDocument?.Selection as EnvDTE.TextSelection;
                if (selection == null)
                {
                    StatusText = "Insert at cursor: no active query window.";
                    return;
                }

                selection.Insert(item.FullText, (int)EnvDTE.vsInsertFlags.vsInsertFlagsInsertAtStart);
                StatusText = "Inserted at cursor.";
            }
            catch (Exception ex)
            {
                ObjectExplorer.OeDiagnostics.Error("Query history: insert at cursor failed", ex);
                StatusText = "Insert at cursor failed: " + ex.Message;
            }
        }

        /// <summary>docs/query-history-plan.md §4 item 6 "Open in new query window" +
        /// docs/query-history-api.md S-3: reuse an already-open, already-authenticated window's
        /// UIConnectionInfo when one matches this entry's server+login (Option A, the
        /// recommended path). When none matches: a Windows/integrated entry is still attempted
        /// (the current Windows identity may well have access); a SQL-login entry is declined
        /// with a clear message instead of failing with a confusing login error, because no
        /// password is ever stored (CONTRACT.md Amendment 13) and QueryWindowAccessor's shared
        /// open-a-window path needs a connection string it can actually open. Never runs the
        /// query automatically (allowAutoExecute: false).</summary>
        public async Task OpenInNewQueryWindowAsync(QueryHistoryEntryItem item)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (item == null || Package == null) return;

            var entry = item.Entry;
            if (string.IsNullOrEmpty(entry.Server))
            {
                StatusText = "Open in new query window: this entry has no server recorded.";
                return;
            }

            try
            {
                var dte = await Package.GetServiceAsync(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
                var matched = QueryHistoryConnectionFinder.TryFind(dte, entry.Server, entry.Login);

                string connectionString;
                Microsoft.SqlServer.Management.Smo.RegSvrEnum.UIConnectionInfo sourceConnectionInfo = null;

                if (matched != null)
                {
                    sourceConnectionInfo = matched;
                    if (!ResultsGrid.GridConnectionInfo.TryBuild(matched, entry.Database, out connectionString))
                    {
                        StatusText = "Open in new query window: found a matching open window, but could not build a connection string from it (Entra/token-based sign-ins aren't supported here).";
                        return;
                    }
                }
                else if (entry.AuthKind == HistoryAuthKind.SqlLogin)
                {
                    // No live, already-authenticated session for this SQL login -- never
                    // attempt with a passwordless connection string (no password is ever
                    // stored). Decline with a clear reason instead of a confusing login failure.
                    StatusText = "Open in new query window: no open connection found for " + entry.Server +
                        (string.IsNullOrEmpty(entry.Login) ? string.Empty : " as " + entry.Login) +
                        ". Open a new query window on that server first, or connect manually.";
                    return;
                }
                else
                {
                    var csb = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder
                    {
                        DataSource = entry.Server,
                        ApplicationName = "SSMS Data Analyzer",
                        TrustServerCertificate = true,
                        IntegratedSecurity = true
                    };
                    if (!string.IsNullOrEmpty(entry.Database)) csb.InitialCatalog = entry.Database;
                    connectionString = csb.ConnectionString;
                }

                var result = await GoToSource.QueryWindowAccessor.TryOpenAsync(
                    entry.Text, connectionString, sourceConnectionInfo, allowAutoExecute: false).ConfigureAwait(true);

                StatusText = result.Success ? result.Reason : "Open in new query window: " + result.Reason;
            }
            catch (Exception ex)
            {
                ObjectExplorer.OeDiagnostics.Error("Query history: 'Open in new query window' failed", ex);
                StatusText = "Open in new query window failed: " + ex.Message;
            }
        }

        /// <summary>"Clear all history" (docs/query-history-plan.md §3 "Layer 2") -- the
        /// caller (code-behind) is responsible for the confirmation prompt before calling this.</summary>
        public void ClearAll()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            Items.Clear();
            SelectedItem = null;
            QueryHistoryService.ClearAll();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _debounceTimer.Stop();
            QueryHistoryService.Changed -= OnServiceChanged;
        }

        private void OnPropertyChanged([CallerMemberName] string name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
