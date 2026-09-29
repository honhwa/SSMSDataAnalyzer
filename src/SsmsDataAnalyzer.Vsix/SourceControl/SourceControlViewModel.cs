using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Microsoft.SqlServer.Management.Smo.RegSvrEnum;
using Microsoft.VisualStudio.Shell;
using SsmsDataAnalyzer.Core.History;
using SsmsDataAnalyzer.Core.SourceControl;

namespace SsmsDataAnalyzer.Vsix.SourceControl
{
    /// <summary>One entry of the window-filter dropdown (docs/source-control-sync-plan.md §9
    /// Phase 1 item 2: Today / 7 / 15 (default) / 30 days).</summary>
    internal sealed class WindowOption
    {
        public string Text { get; }
        public int Days { get; }
        public WindowOption(string text, int days) { Text = text; Days = days; }
        public override string ToString() => Text;
    }

    /// <summary>Where the check looks for changes (plan §13b.1).</summary>
    internal enum CheckSourceMode { ServerAndHistory, History, Server }

    /// <summary>One entry of the check-source dropdown (plan §13b.1).</summary>
    internal sealed class CheckSourceOption
    {
        public string Text { get; }
        public CheckSourceMode Mode { get; }
        public CheckSourceOption(string text, CheckSourceMode mode) { Text = text; Mode = mode; }
        public override string ToString() => Text;
    }

    /// <summary>
    /// Backs SourceControlView: runs the read-only check (docs/source-control-sync-plan.md §9
    /// Phase 1) and exposes the row actions. Orchestration only -- the actual candidate/verdict
    /// logic lives in Core (SyncChecker et al.); this class gathers what Core needs (query
    /// history, server reads, repo files) and never re-implements any of §6/§7/§13.7's rules.
    /// </summary>
    internal sealed class SourceControlViewModel : INotifyPropertyChanged, IDisposable
    {
        public event PropertyChangedEventHandler PropertyChanged;

        public WindowOption[] WindowOptions { get; } =
        {
            new WindowOption("Today", 1),
            new WindowOption("Last 7 days", 7),
            new WindowOption("Last 15 days", 15),
            new WindowOption("Last 30 days", 30),
        };

        /// <summary>User request (2026-09-29): choose how changes are found. Server + history is
        /// the default because it is what the check did before the choice existed.</summary>
        public CheckSourceOption[] SourceOptions { get; } =
        {
            new CheckSourceOption("Server + history", CheckSourceMode.ServerAndHistory),
            new CheckSourceOption("History", CheckSourceMode.History),
            new CheckSourceOption("Server", CheckSourceMode.Server),
        };

        private CheckSourceOption _selectedSource;
        public CheckSourceOption SelectedSource
        {
            get => _selectedSource;
            set { if (ReferenceEquals(_selectedSource, value)) return; _selectedSource = value; OnPropertyChanged(); }
        }

        private WindowOption _selectedWindow;
        public WindowOption SelectedWindow
        {
            get => _selectedWindow;
            set { if (ReferenceEquals(_selectedWindow, value)) return; _selectedWindow = value; OnPropertyChanged(); }
        }

        private bool _showAll;
        public bool ShowAll
        {
            get => _showAll;
            set
            {
                if (_showAll == value) return;
                _showAll = value;
                OnPropertyChanged();
                ThreadHelper.ThrowIfNotOnUIThread();
                RebuildGroups();
            }
        }

        private bool _isBusy;
        public bool IsBusy
        {
            get => _isBusy;
            private set { _isBusy = value; OnPropertyChanged(); }
        }

        private string _statusText = "Not checked yet.";
        public string StatusText
        {
            get => _statusText;
            private set { _statusText = value; OnPropertyChanged(); }
        }

        private readonly ObservableCollection<FindingItem> _visibleFindings = new ObservableCollection<FindingItem>();

        /// <summary>
        /// Every visible finding in ONE list, grouped by WPF rather than by nesting one grid per
        /// group. Field report: each nested grid had to be capped (MaxHeight 260) because a grid
        /// inside a scrolling list gets unlimited height and stops virtualising, so the grid sat
        /// at a fixed size however the window was resized or docked. One grid in the window's
        /// star row sizes with the window and keeps virtualisation even with grouping on.
        /// </summary>
        public System.Windows.Data.ListCollectionView FindingsView { get; }

        private FindingItem _selectedFinding;
        public FindingItem SelectedFinding
        {
            get => _selectedFinding;
            set { _selectedFinding = value; OnPropertyChanged(); }
        }

        public AsyncPackage Package { get; set; }

        private List<FindingItem> _allFindings = new List<FindingItem>();

        /// <summary>
        /// Every (server, database) the server-side scan has been pointed at this session.
        ///
        /// Field report: select AgricultureFinances on SQLTEST7 and check, then select it on
        /// SQLTEST8 and check — SQLTEST7's group vanished, while SQLTEST8's never did. SQLTEST8's
        /// rows came from the user's own Query History, which is read for every server on every
        /// check; SQLTEST7's came from the modify_date scan, which only covered the database
        /// selected at that moment. Moving the selection silently dropped a server the user had
        /// just checked. Now each database you check joins this list and every check rescans
        /// all of them, so results only ever grow while you move around; "Check selected only"
        /// resets it. Session-only: connections are too.
        /// </summary>
        private readonly List<(string Server, string Database)> _scannedScopes = new List<(string Server, string Database)>();

        private bool _checkSelectedOnly;

        /// <summary>"Check selected only": forget the other databases, scan just the selected one.</summary>
        public Task RunCheckSelectedOnlyAsync()
        {
            _checkSelectedOnly = true;
            return RunCheckAsync();
        }

        private static bool SameScope((string Server, string Database) scope, string server, string database) =>
            string.Equals(scope.Server, server, StringComparison.OrdinalIgnoreCase)
            && string.Equals(scope.Database, database, StringComparison.OrdinalIgnoreCase);
        private bool _disposed;

        public SourceControlViewModel()
        {
            FindingsView = new System.Windows.Data.ListCollectionView(_visibleFindings);
            FindingsView.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription(nameof(FindingItem.GroupHeader)));
            FindingsView.SortDescriptions.Add(new SortDescription(nameof(FindingItem.GroupSortKey), ListSortDirection.Ascending));
            FindingsView.SortDescriptions.Add(new SortDescription(nameof(FindingItem.Object), ListSortDirection.Ascending));

            _selectedWindow = WindowOptions[2]; // Last 15 days -- the default (§9 Phase 1 item 2).
            _selectedSource = SourceOptions[0]; // Server + history -- the default (§13b.1).
        }

        // ---- the check itself ----------------------------------------------------------------

        /// <param name="startedInQueryWindow">True only when the check was started while typing
        /// in a query window. Otherwise — Object Explorer, the Tools menu used from there, or this
        /// panel's own buttons — the database selected in Object Explorer is the scope, and the
        /// active query window is only the fallback when nothing is selected there.</param>
        public async Task RunCheckAsync(bool startedInQueryWindow = false)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (_disposed || Package == null) return;

            IsBusy = true;
            StatusText = "Checking…";
            try
            {
                var dte = await Package.GetServiceAsync(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
                if (dte == null)
                {
                    StatusText = "Could not reach EnvDTE -- nothing was checked.";
                    return;
                }

                // 1. Current query window's connection, read on the UI thread only.
                string currentServer = null, currentDatabase = null, currentConnStr = null;
                UIConnectionInfo currentInfo = null;
                try
                {
                    var doc = dte.ActiveDocument;
                    var editor = doc != null ? DataAnalyzerPackage.FindSqlScriptEditorControl(doc) : null;
                    currentInfo = editor?.Connection;
                    if (currentInfo != null && !string.IsNullOrEmpty(currentInfo.ServerName))
                    {
                        currentServer = currentInfo.ServerName;
                        currentDatabase = currentInfo.AdvancedOptions?["DATABASE"];
                        ResultsGrid.GridConnectionInfo.TryBuild(currentInfo, null, out currentConnStr);
                    }
                }
                catch (Exception ex)
                {
                    ObjectExplorer.OeDiagnostics.Warn("Source control check: could not read the current query window's connection (" + ex.GetType().Name + ").");
                }

                // Which database this check is about. Unless it was started from a query window,
                // the Object Explorer selection wins over the last-used query window (field
                // report: AgricultureFinances selected on SQLTEST7, AG1LISTENER scanned instead,
                // because SSMS keeps the last query tab as the "active document" even while you
                // click in Object Explorer). The query window stays the fallback.
                string scopeSource = currentConnStr != null ? "query window" : null;
                bool preferObjectExplorer = !startedInQueryWindow || currentConnStr == null;
                if (preferObjectExplorer
                    && ObjectExplorerConnectionFinder.TryGetSelectedDatabase(Package,
                        out string oeServer, out string oeDatabase, out string oeConnStr))
                {
                    currentServer = oeServer;
                    currentDatabase = oeDatabase;
                    currentConnStr = oeConnStr;
                    scopeSource = "Object Explorer selection";
                }

                int windowDays = SelectedWindow?.Days ?? 15;
                DateTime cutoffUtc = DateTime.UtcNow.AddDays(-windowDays);

                // 2. Query history + DDL scan (background: disk I/O + lexing).
                CheckSourceMode mode = SelectedSource?.Mode ?? CheckSourceMode.ServerAndHistory;

                List<ChangeCandidate> historyCandidates = mode == CheckSourceMode.Server
                    ? new List<ChangeCandidate>()
                    : await Task.Run(async () =>
                        await BuildHistoryCandidatesAsync(cutoffUtc).ConfigureAwait(false)).ConfigureAwait(true);

                // 3. sys.objects.modify_date on EVERY database checked this session (see
                //    _scannedScopes), the current one included.
                bool selectedOnly = _checkSelectedOnly;
                _checkSelectedOnly = false;
                if (selectedOnly) _scannedScopes.Clear();
                if (!string.IsNullOrEmpty(currentServer) && !string.IsNullOrEmpty(currentDatabase)
                    && !_scannedScopes.Any(sc => SameScope(sc, currentServer, currentDatabase)))
                {
                    _scannedScopes.Add((currentServer, currentDatabase));
                }

                List<ChangeCandidate> serverCandidates = new List<ChangeCandidate>();
                var scanNotes = new List<string>();
                foreach (var scope in mode == CheckSourceMode.History ? new List<(string Server, string Database)>() : _scannedScopes.ToList())
                {
                    // The selected database uses the connection already found for it; any other
                    // remembered one goes through the same lookup as the actions (current window,
                    // an open window on that server, then Object Explorer).
                    string scopeConnStr = SameScope(scope, currentServer, currentDatabase) && currentConnStr != null
                        ? currentConnStr
                        : await ResolveConnectionStringAsync(dte, scope.Server, scope.Database).ConfigureAwait(true);

                    string label = scope.Database + " on " + scope.Server;
                    if (scopeConnStr == null)
                    {
                        scanNotes.Add(label + ": no connection");
                        continue;
                    }

                    try
                    {
                        var recent = await ServerObjectReader.ReadRecentlyModifiedAsync(scopeConnStr, cutoffUtc).ConfigureAwait(true);
                        foreach (var (module, changedUtc) in recent)
                        {
                            serverCandidates.Add(new ChangeCandidate(scope.Server, scope.Database, module, changedUtc, ChangeSource.ServerModifyDate, null));
                        }
                        scanNotes.Add(label + ": " + recent.Count);
                    }
                    catch (Exception ex)
                    {
                        scanNotes.Add(label + ": failed (" + ex.GetType().Name + ")");
                        ObjectExplorer.OeDiagnostics.Warn("Source control check: reading sys.objects.modify_date failed (" + ex.GetType().Name + ").");
                    }
                }

                IReadOnlyList<ChangeCandidate> merged = ChangeCandidates.Merge(historyCandidates, serverCandidates);

                // "Check selected only" must leave ONLY the selected database. Clearing the scan
                // list is not enough on its own: Query History names every server you ran DDL on,
                // so without this filter the other groups came straight back and the reset did
                // not reset (user question: "what if I want to remove all and leave only one").
                // The next "Check now" shows everything again.
                if (selectedOnly)
                {
                    merged = merged
                        .Where(c => string.Equals(c.Server, currentServer, StringComparison.OrdinalIgnoreCase)
                                 && string.Equals(c.Database, currentDatabase, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                }

                // 4. Group by (server, database); resolve a connection string per group -- the
                //    current window's own for its own (server, database), otherwise an open
                //    window on that server via QueryHistoryConnectionFinder (UI thread).
                var groups = merged.GroupBy(c => Key(c.Server, c.Database)).ToList();
                var connByGroup = new Dictionary<string, (string ConnStr, string Server, string Database, string SkipReason)>();

                foreach (var group in groups)
                {
                    ChangeCandidate sample = group.First();
                    string server = sample.Server;
                    string database = sample.Database;

                    if (!string.IsNullOrEmpty(currentServer) && !string.IsNullOrEmpty(currentDatabase)
                        && string.Equals(server, currentServer, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(database, currentDatabase, StringComparison.OrdinalIgnoreCase))
                    {
                        connByGroup[group.Key] = (currentConnStr, server, database, currentConnStr == null ? "no active query window connection" : null);
                        continue;
                    }

                    UIConnectionInfo found = string.IsNullOrEmpty(server) ? null : History.QueryHistoryConnectionFinder.TryFind(dte, server, null);
                    if (found == null)
                    {
                        // No query window on that server: Object Explorer's connection counts too.
                        string oeCs = ObjectExplorerConnectionFinder.TryBuildForServer(Package, server, database);
                        connByGroup[group.Key] = oeCs != null
                            ? (oeCs, server, database, (string)null)
                            : (null, server, database, "no open query window or Object Explorer connection to " + server);
                        continue;
                    }

                    ResultsGrid.GridConnectionInfo.TryBuild(found, database, out string cs);
                    connByGroup[group.Key] = (cs, server, database, cs == null ? "could not build a connection string for " + server : null);
                }

                // 5. Server reads + repo index, per group -- background thread (SQL + file I/O).
                SourceControlMap map = await SourceControlMapStore.LoadAsync().ConfigureAwait(true);

                var findings = new List<SyncFinding>();
                var summaryCounts = new Dictionary<SyncFinding, int>();
                int databasesChecked = 0, databasesUnmapped = 0, serversUnreachable = 0;
                var skipNotes = new List<string>();

                foreach (var group in groups)
                {
                    var (connStr, server, database, skipReason) = connByGroup[group.Key];
                    List<ChangeCandidate> candidates = group.ToList();

                    string projectPath = string.IsNullOrEmpty(database) ? null : map.FindProject(server, database);
                    RepoIndex repoIndex = null;
                    if (projectPath != null)
                    {
                        var (index, error) = await RepoIndexCache.GetOrBuildAsync(projectPath).ConfigureAwait(true);
                        repoIndex = index;
                        if (index == null && error != null)
                        {
                            skipNotes.Add(database + ": " + error);
                        }
                    }
                    else
                    {
                        databasesUnmapped++;

                        // ONE row for an unmapped database, not one per object. Field report: 178
                        // identical "database is not mapped to a project" rows for CeresSubsidy
                        // buried everything else. Nothing can be compared without a project, so the
                        // server is not read either; the row carries the count and the
                        // "Map database to project..." action.
                        ChangeCandidate latest = candidates.OrderByDescending(c => c.ChangedUtc).First();
                        SyncFinding unmappedRow = SyncChecker.Check(latest, null, null);
                        summaryCounts[unmappedRow] = candidates.Count;
                        findings.Add(unmappedRow);
                        continue;
                    }

                    Dictionary<ModuleRef, ServerObjectState> states = new Dictionary<ModuleRef, ServerObjectState>();
                    if (connStr != null)
                    {
                        try
                        {
                            var unqualified = candidates.Where(c => c.Module?.Schema == null).Select(c => c.Module).Distinct().ToList();
                            Dictionary<ModuleRef, string> resolvedSchemas = unqualified.Count > 0
                                ? await ServerObjectReader.ResolveSchemasAsync(connStr, database, unqualified).ConfigureAwait(true)
                                : new Dictionary<ModuleRef, string>();

                            // Rebuild the candidate list with resolved schemas applied.
                            candidates = candidates.Select(c =>
                            {
                                if (c.Module?.Schema != null) return c;
                                if (c.Module != null && resolvedSchemas.TryGetValue(c.Module, out string schema))
                                {
                                    return new ChangeCandidate(c.Server, c.Database, c.Module.WithSchema(schema), c.ChangedUtc, c.Source, c.LastAction);
                                }
                                return c;
                            }).ToList();

                            var qualified = candidates.Where(c => c.Module?.Schema != null).Select(c => c.Module).Distinct().ToList();
                            if (qualified.Count > 0)
                            {
                                states = await ServerObjectReader.ReadStatesAsync(connStr, database, qualified).ConfigureAwait(true);

                                // Plan 13b.2: a table YOUR history names is compared by its
                                // columns. One found only by the server scan is not — its
                                // modify_date may only mean a deployment touched an index — and
                                // SyncChecker says so, pointing at the History source.
                                var historyTables = candidates
                                    .Where(c => c.Module?.Schema != null
                                             && (c.Source == ChangeSource.QueryHistory || c.Source == ChangeSource.Both)
                                             && states.TryGetValue(c.Module, out var st) && st.Exists && st.Kind == DbObjectKind.Table)
                                    .Select(c => c.Module).Distinct().ToList();

                                if (historyTables.Count > 0)
                                {
                                    var catalog = await ServerObjectReader.ReadTableColumnsAsync(connStr, database, historyTables).ConfigureAwait(true);
                                    foreach (ModuleRef table in historyTables)
                                    {
                                        if (!catalog.TryGetValue(table, out var raw)) continue;
                                        var columns = raw.Select(c => TableColumn.FromCatalog(c.Name, c.TypeName, c.MaxLength,
                                            c.Precision, c.Scale, c.IsNullable, c.IsIdentity, c.IsComputed)).ToList();
                                        ServerObjectState known = states[table];
                                        states[table] = new ServerObjectState(known.Exists, known.Kind, known.Definition, columns);
                                    }
                                }
                            }
                            databasesChecked++;
                        }
                        catch (Exception ex)
                        {
                            skipNotes.Add(database + ": server read failed (" + ex.GetType().Name + ")");
                        }
                    }
                    else if (skipReason != null)
                    {
                        serversUnreachable++;
                        skipNotes.Add((server ?? "(unknown server)") + "/" + database + ": " + skipReason);
                    }

                    foreach (ChangeCandidate candidate in candidates)
                    {
                        ServerObjectState state = candidate.Module != null && states.TryGetValue(candidate.Module, out var s) ? s : null;
                        findings.Add(SyncChecker.Check(candidate, state, repoIndex));
                    }
                }

                // 6. Back on the UI thread: publish.
                _allFindings = findings
                    .Select(f => new FindingItem(f, summaryCounts.TryGetValue(f, out int count) ? count : 0))
                    .ToList();
                RebuildGroups();

                var summary = new List<string>
                {
                    "source: " + (SelectedSource?.Text ?? "Server + history"),
                    mode == CheckSourceMode.Server
                        ? "history not used (Server source)"
                        : historyCandidates.Count + " candidate(s) from query history",
                    // Every database scanned, and what each gave, so "why did that group vanish"
                    // always has an answer on screen. Names are the user's own, shown to them,
                    // never logged.
                    mode == CheckSourceMode.History
                        ? "server scan not used (History source)"
                    : scanNotes.Count == 0
                        ? "server scan skipped: no connected query window and no database selected in Object Explorer"
                        : "server scan" + (scopeSource != null ? " (" + scopeSource + " + earlier checks)" : " (earlier checks)")
                            + ": " + string.Join(", ", scanNotes),
                    databasesChecked + " database(s) checked against a server",
                };
                if (databasesUnmapped > 0) summary.Add(databasesUnmapped + " database(s) not mapped to a project");
                if (serversUnreachable > 0) summary.Add(serversUnreachable + " server(s)/database(s) had no open connection");
                if (skipNotes.Count > 0) summary.Add("details: " + string.Join("; ", skipNotes.Take(5)));

                if (selectedOnly)
                {
                    summary.Insert(0, string.IsNullOrEmpty(currentDatabase)
                        ? "Showing nothing: select a database in Object Explorer (or use a connected query window), then Check selected only"
                        : "Showing only " + currentDatabase + " on " + currentServer + " (Check now shows everything again)");
                }

                StatusText = string.Join("   |   ", summary);
            }
            catch (Exception ex)
            {
                ObjectExplorer.OeDiagnostics.Error("Source control check failed", ex);
                StatusText = "Check failed: " + ex.Message;
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task<List<ChangeCandidate>> BuildHistoryCandidatesAsync(DateTime cutoffUtc)
        {
            var filter = new HistoryFilter { DateRange = HistoryDateRange.Last30Days };
            IReadOnlyList<HistoryEntry> entries = await History.QueryHistoryService.QueryAsync(filter, 5000).ConfigureAwait(false);

            var candidates = new List<ChangeCandidate>();
            foreach (HistoryEntry entry in entries)
            {
                if (entry.StartedUtc < cutoffUtc) continue; // HistoryFilter only knows Today/7/30 -- filter the window ourselves.
                if (string.IsNullOrEmpty(entry.Server) || string.IsNullOrEmpty(entry.Text)) continue;

                IReadOnlyList<DdlStatement> statements = DdlDetector.Find(entry.Text);
                foreach (DdlStatement statement in statements)
                {
                    if (statement.Target == null) continue;
                    // Lead amendment §13.2: a three-part name's own database wins over the
                    // session database, so a cross-database DROP is attributed correctly.
                    string database = statement.Database ?? entry.Database;
                    if (string.IsNullOrEmpty(database)) continue;

                    candidates.Add(new ChangeCandidate(entry.Server, database, statement.Target, entry.StartedUtc,
                        ChangeSource.QueryHistory, statement.Action));
                }
            }
            return candidates;
        }

        private static string Key(string server, string database) =>
            (server ?? string.Empty).ToUpperInvariant() + "\u0001" + (database ?? string.Empty).ToUpperInvariant();

        private void RebuildGroups()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            // Grouping (per server AND database — never per database alone, 0.28.2) and ordering
            // come from FindingsView's descriptions; this only decides what is visible.
            _visibleFindings.Clear();
            foreach (var item in ShowAll ? _allFindings : _allFindings.Where(f => !f.IsMatch))
            {
                _visibleFindings.Add(item);
            }
        }

        // ---- actions (docs/source-control-sync-plan.md §9 Phase 1 item 5, all read-only) -----

        /// <summary>Resolves a connection string for (server, database), same "current window
        /// first, else reuse an open one" rule the check itself uses (§9 item 5 / §13.7 step 3).</summary>
        private async Task<string> ResolveConnectionStringAsync(EnvDTE.DTE dte, string server, string database)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            try
            {
                var doc = dte?.ActiveDocument;
                var editor = doc != null ? DataAnalyzerPackage.FindSqlScriptEditorControl(doc) : null;
                UIConnectionInfo current = editor?.Connection;
                if (current != null && string.Equals(current.ServerName, server, StringComparison.OrdinalIgnoreCase))
                {
                    string currentDb = current.AdvancedOptions?["DATABASE"];
                    if (string.IsNullOrEmpty(currentDb) || string.Equals(currentDb, database, StringComparison.OrdinalIgnoreCase))
                    {
                        ResultsGrid.GridConnectionInfo.TryBuild(current, database, out string cs0);
                        if (cs0 != null) return cs0;
                    }
                }

                UIConnectionInfo found = History.QueryHistoryConnectionFinder.TryFind(dte, server, null);
                if (found == null) return ObjectExplorerConnectionFinder.TryBuildForServer(Package, server, database);
                ResultsGrid.GridConnectionInfo.TryBuild(found, database, out string cs);
                return cs;
            }
            catch (Exception ex)
            {
                ObjectExplorer.OeDiagnostics.Warn("Source control: resolving a connection for an action failed (" + ex.GetType().Name + ").");
                return null;
            }
        }

        /// <summary>Definitions are never retained on a finding (never logged, never kept
        /// longer than needed) -- an action that needs the server's current text reads it fresh,
        /// same read-only SELECT as the check itself.</summary>
        private async Task<string> RefetchDefinitionAsync(EnvDTE.DTE dte, FindingItem item)
        {
            ChangeCandidate candidate = item.Finding.Candidate;
            if (candidate?.Module == null) return null;

            string connStr = await ResolveConnectionStringAsync(dte, candidate.Server, candidate.Database).ConfigureAwait(true);
            if (connStr == null) return null;

            ModuleRef module = candidate.Module;
            if (module.Schema == null)
            {
                var resolved = await ServerObjectReader.ResolveSchemasAsync(connStr, candidate.Database, new[] { module }).ConfigureAwait(true);
                if (!resolved.TryGetValue(module, out string schema)) return null;
                module = module.WithSchema(schema);
            }

            var states = await ServerObjectReader.ReadStatesAsync(connStr, candidate.Database, new[] { module }).ConfigureAwait(true);
            return states.TryGetValue(module, out ServerObjectState state) ? state.Definition : null;
        }

        public async Task OpenRepoFileAsync(FindingItem item)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (item?.Finding.Candidate == null || string.IsNullOrEmpty(item.RelativePath)) return;

            try
            {
                string fullPath = await ResolveRepoFilePathAsync(item).ConfigureAwait(true);
                if (fullPath == null)
                {
                    StatusText = "Open repo file: could not resolve the file's full path (is the database still mapped?).";
                    return;
                }

                var dte = await Package.GetServiceAsync(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
                dte?.ItemOperations.OpenFile(fullPath, EnvDTE.Constants.vsViewKindTextView);
            }
            catch (Exception ex)
            {
                ObjectExplorer.OeDiagnostics.Error("Source control: opening the repo file failed", ex);
                StatusText = "Open repo file failed: " + ex.Message;
            }
        }

        private async Task<string> ResolveRepoFilePathAsync(FindingItem item)
        {
            ChangeCandidate candidate = item.Finding.Candidate;
            SourceControlMap map = await SourceControlMapStore.LoadAsync().ConfigureAwait(true);
            string projectPath = map.FindProject(candidate.Server, candidate.Database);
            if (projectPath == null) return null;
            string folder = System.IO.Path.GetDirectoryName(projectPath);
            if (folder == null) return null;
            return System.IO.Path.Combine(folder, item.RelativePath);
        }

        public void CopyObjectName(FindingItem item)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (item == null || string.IsNullOrEmpty(item.Object)) return;
            try
            {
                System.Windows.Clipboard.SetText(item.Object);
            }
            catch (Exception ex)
            {
                ObjectExplorer.OeDiagnostics.Error("Source control: copy object name failed (clipboard may be in use)", ex);
            }
        }

        /// <summary>Scripts the CURRENT server definition into a new query window -- never
        /// executed (§9 item 5).</summary>
        public async Task ScriptServerDefinitionAsync(FindingItem item)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (item?.Finding.Candidate == null) return;

            try
            {
                var dte = await Package.GetServiceAsync(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
                string definition = await RefetchDefinitionAsync(dte, item).ConfigureAwait(true);
                if (definition == null)
                {
                    StatusText = "Script server definition: could not read the current definition (no connection, or it is a table/encrypted/CLR module).";
                    return;
                }

                string connStr = await ResolveConnectionStringAsync(dte, item.Finding.Candidate.Server, item.Finding.Candidate.Database).ConfigureAwait(true);
                var result = await GoToSource.QueryWindowAccessor.TryOpenAsync(definition, connStr, null, allowAutoExecute: false).ConfigureAwait(true);
                StatusText = "Script server definition: " + result.Reason;
            }
            catch (Exception ex)
            {
                ObjectExplorer.OeDiagnostics.Error("Source control: scripting the server definition failed", ex);
                StatusText = "Script server definition failed: " + ex.Message;
            }
        }

        /// <summary>Compare: server definition (written to a random-named, flagged-temporary
        /// file) vs the repo file, via IVsDifferenceService (docs/source-control-api.md S-1).
        /// Falls back to two new query windows if the diff service is unavailable, and says so
        /// on the status line either way (§9 item 5 / the "silent result needs a visible
        /// reason" rule).</summary>
        public async Task CompareAsync(FindingItem item)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (item?.Finding.Candidate == null) return;

            try
            {
                var dte = await Package.GetServiceAsync(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
                string definition = await RefetchDefinitionAsync(dte, item).ConfigureAwait(true);
                string repoFile = await ResolveRepoFilePathAsync(item).ConfigureAwait(true);

                if (definition == null || repoFile == null)
                {
                    StatusText = "Compare: could not read " + (definition == null ? "the server definition" : "the repo file") + ".";
                    return;
                }

                string tempFile = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                    "SsmsDataAnalyzer-" + Guid.NewGuid().ToString("N") + ".sql");
                System.IO.File.WriteAllText(tempFile, definition);
                try { System.IO.File.SetAttributes(tempFile, System.IO.FileAttributes.Temporary); } catch { /* best-effort */ }

                var diffService = Microsoft.VisualStudio.Shell.Package.GetGlobalService(typeof(Microsoft.VisualStudio.Shell.Interop.SVsDifferenceService))
                    as Microsoft.VisualStudio.Shell.Interop.IVsDifferenceService;

                if (diffService != null)
                {
                    try
                    {
                        diffService.OpenComparisonWindow2(tempFile, repoFile,
                            "Compare: " + item.Object, "server vs repository",
                            "server", "repository", null, null, 0);
                        StatusText = "Compare: opened the diff view (server vs " + item.RelativePath + ").";
                        return;
                    }
                    catch (Exception ex)
                    {
                        ObjectExplorer.OeDiagnostics.Warn("Source control: IVsDifferenceService failed, falling back to two query windows (" + ex.GetType().Name + ").");
                    }
                }

                // Fallback (docs/source-control-api.md S-1.4): two windows, one per side.
                string connStr = await ResolveConnectionStringAsync(dte, item.Finding.Candidate.Server, item.Finding.Candidate.Database).ConfigureAwait(true);
                var result = await GoToSource.QueryWindowAccessor.TryOpenAsync(
                    "-- Server definition of " + item.Object + " (compare against " + item.RelativePath + ")\n" + definition,
                    connStr, null, allowAutoExecute: false).ConfigureAwait(true);
                dte?.ItemOperations.OpenFile(repoFile, EnvDTE.Constants.vsViewKindTextView);
                StatusText = "Compare: the diff viewer is unavailable this session, so the server definition and the repo file were opened as two windows instead. " + result.Reason;
            }
            catch (Exception ex)
            {
                ObjectExplorer.OeDiagnostics.Error("Source control: Compare failed", ex);
                StatusText = "Compare failed: " + ex.Message;
            }
        }

        /// <summary>"Map to project…" for an unmapped database: a file picker on *.sqlproj,
        /// then SourceControlMap.With + Serialize back to the mapping file (§5 / §9 item 4) --
        /// the only write this feature ever performs. Re-runs the check afterward so the newly
        /// mapped database is reflected immediately.</summary>
        public async Task MapToProjectAsync(FindingItem item)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (item?.Finding.Candidate == null) return;

            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Map " + item.Database + " to a project",
                Filter = "SQL Server Database Project (*.sqlproj)|*.sqlproj|All files (*.*)|*.*",
                CheckFileExists = true,
            };

            if (dialog.ShowDialog() != true) return;

            bool ok = await SourceControlMapStore.AddMappingAsync(item.Database, null, dialog.FileName).ConfigureAwait(true);
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            StatusText = ok
                ? item.Database + " mapped to " + dialog.FileName + ". Re-checking…"
                : "Could not save the mapping (see the ActivityLog).";

            if (ok) await RunCheckAsync().ConfigureAwait(true);
        }

        public void Dispose()
        {
            _disposed = true;
        }

        private void OnPropertyChanged([CallerMemberName] string name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
