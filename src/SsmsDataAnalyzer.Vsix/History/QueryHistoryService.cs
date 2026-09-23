using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SsmsDataAnalyzer.Core.History;
using SsmsDataAnalyzer.Vsix.Options;

namespace SsmsDataAnalyzer.Vsix.History
{
    /// <summary>
    /// The single owner of the Core <see cref="HistoryStore"/> for this SSMS process
    /// (docs/query-history-plan.md §4 Phase 1 item 1 / §5). Everything here is safe to call
    /// from the UI thread without blocking it:
    /// <list type="bullet">
    /// <item><see cref="Capture"/> only enqueues onto a single background writer thread --
    /// Execute never waits on disk.</item>
    /// <item><see cref="QueryAsync"/> offloads the (lazy, once-per-process) load plus the
    /// in-memory search onto the thread pool.</item>
    /// <item><see cref="SetStarred"/>/<see cref="Delete"/>/<see cref="ClearAll"/> all enqueue
    /// onto the same writer thread as <see cref="Capture"/>, so mutations are strictly
    /// ordered with appends.</item>
    /// </list>
    /// The store itself (Core) is already one-lock thread-safe; the writer thread here exists
    /// only so a burst of Execute calls never makes the UI thread wait on disk I/O, not because
    /// the store needs external serialization.
    /// </summary>
    internal static class QueryHistoryService
    {
        private static readonly object InitLock = new object();
        private static readonly BlockingCollection<Action> WriteQueue = new BlockingCollection<Action>();

        // Options are read through GetDialogPage, which is UI-thread-only: calling it from the
        // writer thread or the thread pool throws COMException. LoadCore runs on exactly those
        // threads, and until v0.19.4 it read the retention option directly -- so initialization
        // threw AFTER key.bin had been written, _initFailed stuck for the session, and every
        // append was silently dropped while the window showed an empty list (field report:
        // "3 executions seen, 3 queued, 0 written. History file: could not be opened
        // (COMException)"). The value is now snapshotted on the UI thread instead.
        private static volatile int _retentionDaysSnapshot = 30;

        private static Thread _writerThread;
        private static HistoryStore _store;
        private static HistoryKeyStore _keyStore;
        private static bool _loaded;
        private static bool _initFailed;
        private static string _rootDirectory;

        /// <summary>Forwards HistoryStore.Changed -- raised on whichever thread the mutation
        /// happened on (the writer thread, almost always). Subscribers (the tool window view
        /// model) must marshal to the UI thread themselves before touching WPF state.</summary>
        public static event EventHandler Changed;

        /// <summary>
        /// Enqueues one execution for persistence. Never blocks -- <see cref="BlockingCollection{T}.Add"/>
        /// on this unbounded queue is O(1). The entry is expected to already carry everything
        /// the caller knows (QueryHistoryCapture builds it, including Duration/Outcome/RowCount
        /// when the optional completion listener supplied them in time).
        /// </summary>
        /// <summary>
        /// Reads the option values that background work needs, on the UI thread, and caches
        /// them. Call it from the UI thread whenever the options may have changed -- package
        /// initialization and opening the Query History window both do. Never call
        /// OptionsAccessor from the writer thread or the thread pool instead: GetDialogPage is
        /// UI-thread-only and throws COMException elsewhere.
        /// </summary>
        public static void RefreshOptionsSnapshot()
        {
            try
            {
                _retentionDaysSnapshot = OptionsAccessor.GetQueryHistoryRetentionDays();
            }
            catch (Exception ex)
            {
                // Keep the previous/default value; a bad options read must never break history.
                ObjectExplorer.OeDiagnostics.Warn("Query history: could not read the retention option (" + ex.GetType().Name + "); keeping the previous value.");
            }
        }

        public static void Capture(HistoryEntry entry)
        {
            if (entry == null) return;
            EnsureWriterThreadStarted();
            WriteQueue.Add(() =>
            {
                try
                {
                    EnsureLoadedOnWriterThread();
                    if (_initFailed)
                    {
                        QueryHistoryDiagnostics.WriteFailed("the history file could not be opened");
                        return;
                    }
                    _store.Append(entry);
                    QueryHistoryDiagnostics.Written();
                }
                catch (Exception ex)
                {
                    // Never the entry's Text/Server/Database -- OeDiagnostics must never see
                    // query text or literal values (project rule).
                    QueryHistoryDiagnostics.WriteFailed(ex.GetType().Name);
                    ObjectExplorer.OeDiagnostics.Error("Query history: recording an execution failed", ex);
                }
            });
        }

        public static void SetStarred(Guid id, bool starred)
        {
            EnsureWriterThreadStarted();
            WriteQueue.Add(() =>
            {
                try
                {
                    EnsureLoadedOnWriterThread();
                    if (_initFailed) return;
                    _store.SetStarred(id, starred);
                }
                catch (Exception ex)
                {
                    ObjectExplorer.OeDiagnostics.Error("Query history: star/unstar failed", ex);
                }
            });
        }

        public static void Delete(Guid id)
        {
            EnsureWriterThreadStarted();
            WriteQueue.Add(() =>
            {
                try
                {
                    EnsureLoadedOnWriterThread();
                    if (_initFailed) return;
                    _store.Delete(id);
                }
                catch (Exception ex)
                {
                    ObjectExplorer.OeDiagnostics.Error("Query history: delete failed", ex);
                }
            });
        }

        /// <summary>"Clear all history" (docs/query-history-plan.md §3 "Layer 2"): destroys the
        /// encryption key FIRST -- crypto-shred, every existing file becomes unreadable
        /// instantly even if the file deletion that follows is interrupted -- then deletes the
        /// files themselves. Runs on the writer queue, same as every other mutation.</summary>
        public static void ClearAll()
        {
            EnsureWriterThreadStarted();
            WriteQueue.Add(() =>
            {
                try
                {
                    EnsureLoadedOnWriterThread();
                    if (_initFailed) return;
                    _keyStore.Destroy();
                    _store.ClearAll();

                    // Crypto-shred done: every file written before this moment is unreadable
                    // forever. But the session must not carry on encrypting with a key that no
                    // longer exists on disk -- the next SSMS start would create a fresh key.bin
                    // and everything recorded after this Clear would be undecryptable, i.e.
                    // silently lost. So mint a new key now and rebuild the store around it.
                    byte[] newKey = _keyStore.LoadOrCreate();
                    var newStore = new HistoryStore(new HistoryFileSystem(), _rootDirectory, new HistoryCipher(newKey), () => DateTime.UtcNow);
                    newStore.Changed += (s2, e2) => Changed?.Invoke(null, EventArgs.Empty);
                    newStore.Load();
                    _store = newStore;

                    ObjectExplorer.OeDiagnostics.Info("Query history: cleared (key destroyed, then files deleted; a new key was created for this session).");
                }
                catch (Exception ex)
                {
                    ObjectExplorer.OeDiagnostics.Error("Query history: Clear all history failed", ex);
                }
            });
        }

        /// <summary>Used by the tool window to load/refresh its list. Offloads the (lazy,
        /// once-per-process) load and the in-memory search onto the thread pool so the UI
        /// thread is never blocked by disk I/O or the one-time DPAPI unwrap.</summary>
        public static Task<IReadOnlyList<HistoryEntry>> QueryAsync(HistoryFilter filter, int max)
        {
            return Task.Run(() =>
            {
                EnsureLoadedBlocking();
                if (_initFailed) return (IReadOnlyList<HistoryEntry>)Array.Empty<HistoryEntry>();
                return _store.Query(filter, max);
            });
        }

        private static void EnsureWriterThreadStarted()
        {
            if (_writerThread != null) return;
            lock (InitLock)
            {
                if (_writerThread != null) return;
                _writerThread = new Thread(WriterLoop)
                {
                    IsBackground = true,
                    Name = "SsmsDataAnalyzer.QueryHistoryWriter"
                };
                _writerThread.Start();
            }
        }

        private static void WriterLoop()
        {
            foreach (var action in WriteQueue.GetConsumingEnumerable())
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    ObjectExplorer.OeDiagnostics.Error("Query history: background writer task failed", ex);
                }
            }
        }

        /// <summary>Called only from the writer thread -- mutations are already serialized by
        /// the queue, so a plain (non-locking) check is enough here; <see cref="EnsureLoadedBlocking"/>
        /// below is the version used by <see cref="QueryAsync"/>, which can race with the writer
        /// thread and does take the lock.</summary>
        private static void EnsureLoadedOnWriterThread()
        {
            if (_loaded || _initFailed) return;
            lock (InitLock)
            {
                if (_loaded || _initFailed) return;
                LoadCore();
            }
        }

        private static void EnsureLoadedBlocking()
        {
            if (_loaded || _initFailed) return;
            lock (InitLock)
            {
                if (_loaded || _initFailed) return;
                LoadCore();
            }
        }

        private static void LoadCore()
        {
            try
            {
                _rootDirectory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "SsmsDataAnalyzer", "QueryHistory");

                var fileSystem = new HistoryFileSystem();

                // Create (and ACL) the folder before anything writes into it. HistoryKeyStore
                // does this for itself too; doing it here as well means the very first run on a
                // clean machine cannot fail on a missing directory.
                fileSystem.EnsureDirectory(_rootDirectory);

                var protector = new DpapiKeyProtector();
                _keyStore = new HistoryKeyStore(fileSystem, protector, Path.Combine(_rootDirectory, "key.bin"));

                byte[] key = _keyStore.LoadOrCreate();
                var cipher = new HistoryCipher(key);

                _store = new HistoryStore(fileSystem, _rootDirectory, cipher, () => DateTime.UtcNow);
                _store.Changed += (s, e) => Changed?.Invoke(null, EventArgs.Empty);
                _store.Load();

                // Snapshot only -- never OptionsAccessor from here (see _retentionDaysSnapshot).
                int retentionDays = _retentionDaysSnapshot;

                // A retention of 0 (or less) would mean "everything older than right now", i.e.
                // delete the entire history on startup. That is never what someone means by it,
                // and it would be unrecoverable, so treat it as "keep everything" instead.
                int removed = retentionDays > 0 ? _store.Trim(retentionDays) : 0;
                if (removed > 0)
                {
                    ObjectExplorer.OeDiagnostics.Info("Query history: startup retention trim removed " + removed.ToString(System.Globalization.CultureInfo.InvariantCulture) + " expired entr" + (removed == 1 ? "y" : "ies") + ".");
                }

                _loaded = true;
                QueryHistoryDiagnostics.StoreState("open");
            }
            catch (Exception ex)
            {
                _initFailed = true;
                QueryHistoryDiagnostics.StoreState("could not be opened (" + ex.GetType().Name + ")");
                ObjectExplorer.OeDiagnostics.Error("Query history: failed to initialize (history is unavailable this session)", ex);
            }
        }
    }
}
