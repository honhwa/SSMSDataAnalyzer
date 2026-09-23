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
        public static void Capture(HistoryEntry entry)
        {
            if (entry == null) return;
            EnsureWriterThreadStarted();
            WriteQueue.Add(() =>
            {
                try
                {
                    EnsureLoadedOnWriterThread();
                    if (_initFailed) return;
                    _store.Append(entry);
                }
                catch (Exception ex)
                {
                    // Never the entry's Text/Server/Database -- OeDiagnostics must never see
                    // query text or literal values (project rule).
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
                    // The in-memory key/cipher this process holds still works for whatever gets
                    // appended for the rest of this session -- only the ON-DISK key file is
                    // gone. A fresh key.bin (and therefore a fresh cipher) only matters the next
                    // time SSMS starts, which is exactly the crypto-shred guarantee: every file
                    // written *before* this moment is unreadable, forever, starting now.
                    ObjectExplorer.OeDiagnostics.Info("Query history: cleared (key destroyed, then files deleted).");
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
                var protector = new DpapiKeyProtector();
                _keyStore = new HistoryKeyStore(fileSystem, protector, Path.Combine(_rootDirectory, "key.bin"));

                byte[] key = _keyStore.LoadOrCreate();
                var cipher = new HistoryCipher(key);

                _store = new HistoryStore(fileSystem, _rootDirectory, cipher, () => DateTime.UtcNow);
                _store.Changed += (s, e) => Changed?.Invoke(null, EventArgs.Empty);
                _store.Load();

                int retentionDays = OptionsAccessor.GetQueryHistoryRetentionDays();
                int removed = _store.Trim(retentionDays);
                if (removed > 0)
                {
                    ObjectExplorer.OeDiagnostics.Info("Query history: startup retention trim removed " + removed.ToString(System.Globalization.CultureInfo.InvariantCulture) + " expired entr" + (removed == 1 ? "y" : "ies") + ".");
                }

                _loaded = true;
            }
            catch (Exception ex)
            {
                _initFailed = true;
                ObjectExplorer.OeDiagnostics.Error("Query history: failed to initialize (history is unavailable this session)", ex);
            }
        }
    }
}
