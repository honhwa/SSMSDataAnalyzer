using System;
using System.Globalization;

namespace SsmsDataAnalyzer.Vsix.History
{
    /// <summary>
    /// Why is the history empty? Until now the honest answer was "run SSMS with /log and read
    /// the ActivityLog", which is a poor thing to ask of anyone (v0.19.x field reports: history
    /// stayed empty and nothing on the machine said why). Every decision the capture path makes
    /// leaves a short note here, and the Query History window shows the latest one whenever the
    /// list is empty.
    ///
    /// Notes are value-free by construction: no query text, no server, database or login name,
    /// no literals -- the same rule that applies to OeDiagnostics. They live in memory only.
    /// </summary>
    internal static class QueryHistoryDiagnostics
    {
        private static readonly object Gate = new object();

        private static string _lastNote;
        private static DateTime _lastNoteLocal;
        private static int _executionsSeen;
        private static int _recorded;
        private static string _hookState = "not initialized";
        private static int _written;
        private static string _storeState = "not opened yet";
        private static string _lastWriteError;

        /// <summary>Which of SSMS's Execute commands the tracker managed to hook. Command
        /// NAMES only -- never anything the user typed. If this says none, no execution can
        /// ever reach the history, which is otherwise indistinguishable from a capture bug.
        /// (Go to source does not prove the hook works: it falls back to reading the editor.)</summary>
        public static void HookState(string state)
        {
            lock (Gate) { _hookState = state; }
        }

        /// <summary>An Execute reached the capture hook (whatever happened next).</summary>
        public static void ExecutionSeen()
        {
            lock (Gate) { _executionsSeen++; }
        }

        /// <summary>The execution was handed to the background writer queue. This is NOT the
        /// same as "it is in the history" -- the write can still fail, and saying "recorded"
        /// when nothing reached the disk is what made a broken v0.19.2 look healthy.</summary>
        public static void QueuedForWriting()
        {
            Note("queued for writing");
            lock (Gate) { _recorded++; }
        }

        /// <summary>The entry actually reached the store (and therefore the disk).</summary>
        public static void Written()
        {
            lock (Gate) { _written++; _lastWriteError = null; }
        }

        /// <summary>Writing the entry failed. Exception KIND only, never its message -- an
        /// exception message can quote a path or, from some providers, the data itself.</summary>
        public static void WriteFailed(string exceptionKind)
        {
            lock (Gate) { _lastWriteError = exceptionKind; }
        }

        /// <summary>How opening the encrypted store went. Exception kind only.</summary>
        public static void StoreState(string state)
        {
            lock (Gate) { _storeState = state; }
        }

        /// <summary>A value-free reason this execution was not recorded.</summary>
        public static void Note(string note)
        {
            lock (Gate)
            {
                _lastNote = note;
                _lastNoteLocal = DateTime.Now;
            }
        }

        /// <summary>One line for the tool window when the list is empty.</summary>
        public static string Describe()
        {
            lock (Gate)
            {
                if (_executionsSeen == 0)
                {
                    return "No executed query has reached the history yet. If you have run one since SSMS started, "
                         + "the extension is not seeing SSMS's Execute command in this build. Execute hook: " + _hookState + ".";
                }

                string text = _executionsSeen.ToString(CultureInfo.InvariantCulture)
                            + (_executionsSeen == 1 ? " execution" : " executions")
                            + " seen, " + _recorded.ToString(CultureInfo.InvariantCulture) + " queued, "
                            + _written.ToString(CultureInfo.InvariantCulture) + " written. History file: " + _storeState + ".";

                if (!string.IsNullOrEmpty(_lastWriteError))
                {
                    text += " Last write error: " + _lastWriteError + ".";
                }

                if (!string.IsNullOrEmpty(_lastNote))
                {
                    text += " Last: " + _lastNote + " (" + _lastNoteLocal.ToString("HH:mm:ss", CultureInfo.CurrentCulture) + ").";
                }

                return text;
            }
        }
    }
}
