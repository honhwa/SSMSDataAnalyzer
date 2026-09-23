using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.Shell;

namespace SsmsDataAnalyzer.Vsix.ObjectExplorer
{
    /// <summary>
    /// CONTRACT.md Amendment 13, Bug 2: the right-click "Analyze Data" path had no way to
    /// tell the user (or us) WHY it silently didn't appear — the feature flag, a failed
    /// service lookup, and a never-firing event all looked identical: nothing happens.
    /// Writes to SSMS's own ActivityLog (Help &gt; ... &gt; View Log, or
    /// %AppData%\Microsoft\...\ActivityLog.xml) via the Try* APIs, which never throw, so a
    /// diagnostics call can never itself become a new failure mode. One-shot messages are
    /// deduplicated per process so routine, high-frequency events (every Object Explorer
    /// selection change) don't flood the log; failures always log every time.
    /// </summary>
    internal static class OeDiagnostics
    {
        private const string Source = "SsmsDataAnalyzer.ObjectExplorer";

        private static readonly HashSet<string> LoggedOnce = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Logs unconditionally. Use for state changes worth seeing every time (rare by construction — e.g. once per newly-seen menu handler).</summary>
        public static void Info(string message) => Safe(() => ActivityLog.TryLogInformation(Source, message));

        /// <summary>
        /// The Try* APIs do not throw for logging failures, but reaching the activity-log
        /// service is a shell call, and from a background thread that can throw (COMException)
        /// before any logging is attempted. Diagnostics must never become a failure mode of the
        /// thing they are reporting on: query history's background writer calls these from its
        /// own thread, and an exception escaping here would kill that thread and stop all
        /// recording — the exact class of bug this file exists to make visible.
        /// </summary>
        private static void Safe(Action log)
        {
            try { log(); }
            catch { /* never let diagnostics break the caller */ }
        }

        /// <summary>Logs the first time a given <paramref name="key"/> is seen this session, then never again — for routine/high-frequency events.</summary>
        public static void InfoOnce(string key, string message)
        {
            lock (LoggedOnce)
            {
                if (!LoggedOnce.Add(key)) return;
            }
            Safe(() => ActivityLog.TryLogInformation(Source, message));
        }

        public static void Warn(string message) => Safe(() => ActivityLog.TryLogWarning(Source, message));

        public static void Error(string message, Exception ex = null)
        {
            Safe(() => ActivityLog.TryLogError(Source, ex == null ? message : $"{message}: {ex}"));
        }
    }
}
