using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
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

        /// <summary>docs/query-history-plan.md §4 Phase 2 item 9 -- off by default, bound to
        /// a checkbox in the window. Core (HistoryStore.Query, §8.6a) collapses on exact text
        /// and fills HistoryEntry.GroupCount once this is on.</summary>
        private bool _groupIdenticalText;
        public bool GroupIdenticalText
        {
            get => _groupIdenticalText;
            set
            {
                if (_groupIdenticalText == value) return;
                _groupIdenticalText = value;
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
                filter.GroupIdenticalText = GroupIdenticalText;

                // docs/query-history-plan.md §4 Phase 2 item 11 / §8.6a: OpenDocumentNames is
                // read from DTE on the UI thread (never in Core) and handed in as plain data.
                // ReloadAsync is already on the main thread at this point (the
                // SwitchToMainThreadAsync above), so this costs nothing extra.
                filter.OpenDocumentNames = await GetOpenDocumentNamesAsync().ConfigureAwait(true);

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
                if (Items.Count == 0 && string.IsNullOrWhiteSpace(SearchText))
                {
                    // Empty with no filter applied is the case people report as "it doesn't
                    // work", so say what the capture path actually did rather than nothing.
                    StatusText = "No entries yet. " + QueryHistoryDiagnostics.Describe();
                }
                else
                {
                    StatusText = Items.Count == 1 ? "1 entry" : Items.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + " entries";
                }
            }
            catch (Exception ex)
            {
                ObjectExplorer.OeDiagnostics.Error("Query history: loading the list failed", ex);
                StatusText = "Could not load query history: " + ex.Message;
            }
        }

        /// <summary>docs/query-history-plan.md §4 Phase 2 item 11 / §8.6a: "Names of the query
        /// documents currently open in SSMS ... read on the UI thread and handed in -- Core
        /// never touches DTE." Never throws -- a failure here must degrade to "unknown" (an
        /// empty set, which makes closed:/doc: terms match nothing rather than guessing), not
        /// break the whole reload.</summary>
        private async Task<System.Collections.Generic.ISet<string>> GetOpenDocumentNamesAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var names = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (Package == null) return names;

            try
            {
                var dte = await Package.GetServiceAsync(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
                if (dte == null) return names;

                var documents = dte.Documents;
                if (documents == null) return names;

                foreach (EnvDTE.Document doc in documents)
                {
                    // Same field QueryHistoryCapture records as DocumentName (document.Name,
                    // e.g. "SQLQuery1.sql") -- must match for closed:/doc: to mean anything.
                    if (!string.IsNullOrEmpty(doc?.Name)) names.Add(doc.Name);
                }
            }
            catch (Exception ex)
            {
                ObjectExplorer.OeDiagnostics.Warn("Query history: could not read open document names (" + ex.GetType().Name + "); closed:/doc: search may be incomplete this reload.");
            }

            return names;
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

        /// <summary>One or many entries as clipboard text. Entry text is always copied
        /// verbatim -- a history you cannot trust to be what you ran is worthless.</summary>
        private static string JoinTexts(IEnumerable<QueryHistoryEntryItem> items, bool withHeader)
        {
            if (items == null) return string.Empty;

            var builder = new System.Text.StringBuilder();
            bool first = true;

            foreach (var item in items)
            {
                if (item == null || string.IsNullOrEmpty(item.FullText)) continue;

                if (!first)
                {
                    builder.AppendLine().AppendLine("GO").AppendLine();
                }
                first = false;

                if (withHeader)
                {
                    var entry = item.Entry;
                    builder.Append("-- Server: ").Append(string.IsNullOrEmpty(entry.Server) ? "(unknown)" : entry.Server).AppendLine();
                    builder.Append("-- Database: ").Append(string.IsNullOrEmpty(entry.Database) ? "(unknown)" : entry.Database).AppendLine();
                    builder.Append("-- Time: ").Append(entry.StartedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).AppendLine();
                }

                builder.Append(item.FullText);
            }

            return builder.ToString();
        }

        public void Delete(QueryHistoryEntryItem item)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (item == null) return;
            Items.Remove(item); // optimistic UI update
            if (ReferenceEquals(SelectedItem, item)) SelectedItem = null;
            QueryHistoryService.Delete(item.Id);
        }

        /// <summary>Copies every selected entry (plan §4 Phase 1 item 6: "the selected
        /// entries' text"), newest-to-oldest as shown, separated by a blank line and GO so the
        /// result pastes into a query window as runnable batches.</summary>
        public void Copy(IEnumerable<QueryHistoryEntryItem> items)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            string text = JoinTexts(items, withHeader: false);
            if (string.IsNullOrEmpty(text)) return;
            try
            {
                System.Windows.Clipboard.SetText(text);
            }
            catch (Exception ex)
            {
                // Clipboard can be held by another app -- never a crash, and never log the text.
                ObjectExplorer.OeDiagnostics.Error("Query history: copy to clipboard failed (clipboard may be in use)", ex);
            }
        }

        /// <summary>docs/query-history-plan.md §4 Phase 2 item 10 -- "Copy with header": server
        /// / database / time as a leading SQL comment, then the query, exactly as recorded.</summary>
        public void CopyWithHeader(IEnumerable<QueryHistoryEntryItem> items)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            string text = JoinTexts(items, withHeader: true);
            if (string.IsNullOrEmpty(text)) return;

            try
            {
                System.Windows.Clipboard.SetText(text);
            }
            catch (Exception ex)
            {
                // Same "clipboard can be held by another app -- never a crash, never log the
                // text" rule as Copy.
                ObjectExplorer.OeDiagnostics.Error("Query history: copy with header to clipboard failed (clipboard may be in use)", ex);
            }
        }

        /// <summary>docs/query-history-plan.md §4 Phase 2 item 10 -- "Find entries for this
        /// database": puts db:&lt;that database&gt; in the search box (quoted when it contains
        /// whitespace, so HistoryFilter's tokenizer keeps it as one term).</summary>
        public void FindEntriesForThisDatabase(QueryHistoryEntryItem item)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (item == null || string.IsNullOrEmpty(item.Database)) return;

            string value = item.Database.IndexOf(' ') >= 0 ? "\"" + item.Database + "\"" : item.Database;
            SearchText = "db:" + value;
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
        /// <summary>
        /// docs/query-history-plan.md §4 Phase 3 item 13: writes the list exactly as it is
        /// currently filtered to a .sql file. The caller confirms first -- this is the one
        /// operation that takes history OUT of the encrypted store, and the resulting file is
        /// plain text with the queries (passwords included) exactly as they were run.
        /// </summary>
        public bool ExportToFile(string path)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var entries = new List<HistoryEntry>(Items.Count);
                foreach (var item in Items) entries.Add(item.Entry);

                string script = HistoryExport.ToSqlScript(entries, DateTime.Now, DescribeFilter());

                // UTF-8 with BOM: SSMS opens .sql files as ANSI without one, which mangles any
                // non-ASCII literal in an exported query.
                System.IO.File.WriteAllText(path, script, new System.Text.UTF8Encoding(true));

                StatusText = entries.Count == 1
                    ? "Exported 1 entry."
                    : "Exported " + entries.Count.ToString(CultureInfo.InvariantCulture) + " entries.";
                return true;
            }
            catch (Exception ex)
            {
                // Never the path or the script in the log -- both can carry sensitive text.
                ObjectExplorer.OeDiagnostics.Error("Query history: export failed (" + ex.GetType().Name + ").");
                StatusText = "Export failed: " + ex.Message;
                return false;
            }
        }

        /// <summary>What the exported file says it contains. Search text is the user's own
        /// words and can name a database or server, which is fine in a file they chose to
        /// write -- it is never logged.</summary>
        private string DescribeFilter()
        {
            string range = SelectedDateRange?.Text ?? "All";
            string search = string.IsNullOrWhiteSpace(SearchText) ? "(no search)" : SearchText;
            return search + "   |   " + range + (GroupIdenticalText ? "   |   grouped by identical text" : string.Empty);
        }

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
