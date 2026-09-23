using System;
using System.Collections.Generic;
using System.Threading;
using EnvDTE;
using Microsoft.SqlServer.Management.Smo.RegSvrEnum;
using Microsoft.VisualStudio.Shell;
using SsmsDataAnalyzer.Core.History;
using SsmsDataAnalyzer.Vsix.Options;

namespace SsmsDataAnalyzer.Vsix.History
{
    /// <summary>
    /// docs/query-history-plan.md §4 Phase 1 item 1: called from
    /// <see cref="ResultsGrid.ExecutedQueryTextTracker"/>'s existing <c>BeforeExecute</c> hook,
    /// right after it records the executed text for Go to source. Builds the connection/document
    /// context, respects the "Enable query history"/excluded-servers options, and hands the
    /// entry to <see cref="QueryHistoryService"/> -- either immediately (Duration/Outcome/RowCount
    /// left empty) or, when <see cref="QueryHistoryCompletionListener"/> attaches successfully,
    /// after a short bounded wait for that signal.
    ///
    /// Every path here is wrapped so a failure can never interfere with the user's Execute --
    /// same rule ExecutedQueryTextTracker itself already follows. Nothing here ever logs query
    /// text, a server/database/login name, or any literal through OeDiagnostics.
    /// </summary>
    internal static class QueryHistoryCapture
    {
        private sealed class PendingCapture
        {
            public HistoryEntry Entry;
            public Timer FallbackTimer;
            public Action Detach;
            public int Finished; // 0 = pending, 1 = finished (Interlocked guard)
        }

        // One pending capture per document at a time, keyed by the document's full name -- a
        // second Execute in the same window before the first's signal arrives supersedes it
        // (the superseded one is still recorded, just without Duration/Outcome).
        private static readonly Dictionary<string, PendingCapture> Pending =
            new Dictionary<string, PendingCapture>(StringComparer.OrdinalIgnoreCase);
        private static readonly object PendingLock = new object();

        // How long an entry waits for the optional completion signal before being recorded
        // without duration/outcome.
        //
        // Kept deliberately short: the entry does not exist in the history until this elapses,
        // and "I ran a query and the history is empty" is a far worse outcome than a missing
        // duration on a slow query. Queries that take longer than this are still recorded in
        // full -- they simply show no duration. (A later change could record immediately and
        // fill the duration in afterwards; that needs the edits sidecar to carry more than
        // starred/deleted, so it is not a Phase 1 change.)
        private static readonly TimeSpan FallbackDelay = TimeSpan.FromSeconds(5);

        public static void OnBeforeExecute(Document document, string executedText)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                if (string.IsNullOrWhiteSpace(executedText)) return;
                if (!OptionsAccessor.GetEnableQueryHistory()) return;

                var editor = DataAnalyzerPackage.FindSqlScriptEditorControl(document);
                UIConnectionInfo ci = editor?.Connection;
                string server = ci?.ServerName;
                if (string.IsNullOrEmpty(server)) return; // nothing usable to record against

                if (IsExcludedServer(server)) return;

                string database = ci.AdvancedOptions != null ? ci.AdvancedOptions["DATABASE"] : null;
                string login = ci.UserName;
                HistoryAuthKind authKind = ClassifyAuthKind(ci);

                var entry = new HistoryEntry
                {
                    Id = Guid.NewGuid(),
                    StartedUtc = DateTime.UtcNow,
                    Server = server,
                    Database = database,
                    Login = login,
                    AuthKind = authKind,
                    DocumentName = document?.Name,
                    Text = executedText
                };

                string key = document?.FullName ?? entry.Id.ToString();

                // Supersede any still-pending capture for this same document (e.g. a second
                // Execute fired before the first's completion signal arrived) -- finish it now,
                // without duration/outcome, rather than losing it.
                FinishAnyPending(key);

                var pending = new PendingCapture { Entry = entry };
                lock (PendingLock) { Pending[key] = pending; }

                pending.Detach = QueryHistoryCompletionListener.TryAttach(editor, (outcome, durationMs, rowCount) =>
                {
                    FinishPending(key, pending, outcome, durationMs, rowCount);
                });

                if (pending.Detach == null)
                {
                    // No completion signal on this build (or it was disabled after a failure):
                    // there is nothing to wait for, so record the entry now. Waiting would mean
                    // a query you just ran is missing from the window for 30 seconds.
                    FinishPending(key, pending, null, null, null);
                    return;
                }

                pending.FallbackTimer = new Timer(
                    _ => FinishPending(key, pending, null, null, null),
                    null, FallbackDelay, System.Threading.Timeout.InfiniteTimeSpan);
            }
            catch (Exception ex)
            {
                // Never interfere with the user's Execute -- no query text/server/database in
                // the message.
                ObjectExplorer.OeDiagnostics.Error("Query history: capturing an execution failed", ex);
            }
        }

        /// <summary>Records every entry still waiting for a completion signal. Called when the
        /// package shuts down: without it, closing SSMS within 30 seconds of running a query
        /// would lose that query from the history entirely.</summary>
        public static void FlushPending()
        {
            List<PendingCapture> pending;
            lock (PendingLock)
            {
                pending = new List<PendingCapture>(Pending.Values);
                Pending.Clear();
            }

            foreach (var item in pending)
            {
                try { FinishCore(item, null, null, null); }
                catch { /* shutdown path: never throw */ }
            }
        }

        private static void FinishAnyPending(string key)
        {
            PendingCapture old;
            lock (PendingLock)
            {
                if (!Pending.TryGetValue(key, out old)) return;
                Pending.Remove(key);
            }
            FinishCore(old, null, null, null);
        }

        private static void FinishPending(string key, PendingCapture pending, HistoryOutcome? outcome, int? durationMs, long? rowCount)
        {
            lock (PendingLock)
            {
                if (!Pending.TryGetValue(key, out var current) || !ReferenceEquals(current, pending)) return; // superseded or already finished
                Pending.Remove(key);
            }
            FinishCore(pending, outcome, durationMs, rowCount);
        }

        private static void FinishCore(PendingCapture pending, HistoryOutcome? outcome, int? durationMs, long? rowCount)
        {
            if (pending == null) return;
            if (Interlocked.Exchange(ref pending.Finished, 1) != 0) return; // already finished once

            try { pending.FallbackTimer?.Dispose(); } catch { /* best-effort cleanup */ }
            try { pending.Detach?.Invoke(); } catch { /* best-effort cleanup */ }

            pending.Entry.Outcome = outcome;
            pending.Entry.DurationMs = durationMs;
            pending.Entry.RowCount = rowCount;

            QueryHistoryService.Capture(pending.Entry);
        }

        /// <summary>Same reliable rule ResultsGrid.GridConnectionInfo uses: a non-null
        /// RenewableToken is a genuine Entra sign-in; otherwise a UserName + a known password
        /// (Password or InMemoryPassword having ANY content -- the actual secret is never read
        /// here, only whether one exists) means a SQL login; everything else is Windows/
        /// integrated, whether or not UserName happens to be populated (SSMS fills it in for
        /// Windows auth too).</summary>
        private static HistoryAuthKind ClassifyAuthKind(UIConnectionInfo ci)
        {
            if (ci.RenewableToken != null) return HistoryAuthKind.Entra;

            bool hasPassword = !string.IsNullOrEmpty(ci.Password) ||
                (ci.InMemoryPassword != null && ci.InMemoryPassword.Length > 0);

            if (!string.IsNullOrEmpty(ci.UserName) && hasPassword) return HistoryAuthKind.SqlLogin;

            return HistoryAuthKind.Windows;
        }

        private static bool IsExcludedServer(string server)
        {
            string excludedList = OptionsAccessor.GetQueryHistoryExcludedServers();
            if (string.IsNullOrWhiteSpace(excludedList)) return false;

            foreach (var part in excludedList.Split(','))
            {
                var trimmed = part.Trim();
                if (trimmed.Length == 0) continue;
                if (string.Equals(trimmed, server, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
    }
}
